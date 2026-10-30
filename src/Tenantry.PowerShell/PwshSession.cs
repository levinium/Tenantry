using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.PowerShell;

namespace Tenantry.PowerShell;

/// <summary>What the console signs in with: Tenantry's own tokens, handed to pwsh over stdin.</summary>
/// <param name="Organization">The tenant's initial (onmicrosoft) domain, for Connect-ExchangeOnline.</param>
/// <param name="PurviewToken">Null when Purview's second sign-in would need the browser.</param>
/// <param name="SharePointAdminUrl">https://contoso-admin.sharepoint.com, or null when unknown.</param>
public sealed record PsSignIn(string Organization, string Account, string GraphToken, string ExchangeToken,
    string? PurviewToken, string? SharePointAdminUrl, string? SharePointToken);

/// <summary>
/// The console's PowerShell: pwsh running <c>Host.ps1</c>, one JSON line per request and reply.
/// One request runs at a time. Tokens last about an hour, so a command after 45 minutes first
/// reconnects with fresh ones. Stopping a command ends the process and starts a new one.
/// </summary>
/// <param name="signIn">Gets the tokens; true allows the browser for Purview's sign-in.</param>
/// <param name="locate">Finds pwsh (default <see cref="PwshLocator.Find"/>).</param>
public sealed class PwshSession(Func<bool, CancellationToken, Task<PsSignIn>> signIn, Func<string?>? locate = null) : IPowerShellSession
{
    static readonly TimeSpan ReconnectAfter = TimeSpan.FromMinutes(45);
    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(3);
    static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);
    static readonly UTF8Encoding Utf8 = new(false);

    static readonly (PsService Service, string Key)[] Services =
        [(PsService.Graph, "graph"), (PsService.Exchange, "exchange"), (PsService.Purview, "purview"), (PsService.SharePoint, "sharepoint")];

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _lock = new();
    Process? _process;
    TaskCompletionSource<JsonObject>? _reply;
    string? _replyKind;
    Action<PsLine>? _output;
    DateTimeOffset _connectedAt;
    bool _disposed;

    public PsSessionStatus Status { get; private set; }

    public string? StatusDetail { get; private set; }

    public IReadOnlyList<PsConnection> Connections { get; private set; } = Services.Select(s => new PsConnection(s.Service, PsConnectionStatus.Waiting)).ToArray();

    public event EventHandler? Changed;

    void SetStatus(PsSessionStatus status, string? detail = null)
    {
        Status = status;
        if (detail is not null)
            StatusDetail = detail;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void SetConnection(PsService service, PsConnectionStatus status, string? detail)
    {
        Connections = Connections.Select(c => c.Service == service ? new PsConnection(service, status, string.IsNullOrEmpty(detail) ? null : detail) : c).ToArray();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status is PsSessionStatus.NotStarted or PsSessionStatus.Unavailable)
                await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        Connections = Services.Select(s => new PsConnection(s.Service, PsConnectionStatus.Waiting)).ToArray();
        SetStatus(PsSessionStatus.Starting, "Starting PowerShell…");
        if ((locate ?? PwshLocator.Find)() is not { } path)
        {
            SetStatus(PsSessionStatus.Unavailable, "PowerShell 7 isn't installed on this computer.");
            return;
        }
        JsonObject hello;
        try
        {
            hello = await LaunchAsync(path, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Kill();
            SetStatus(PsSessionStatus.Unavailable, $"PowerShell couldn't start: {e.Message}");
            return;
        }
        var version = hello["version"]?.GetValue<string>() ?? "";
        if (!Version.TryParse(version.Split('-')[0], out var parsed) || parsed.Major < 7)
        {
            Kill();
            SetStatus(PsSessionStatus.Unavailable, $"Tenantry needs PowerShell 7; this computer's pwsh is {version}.");
            return;
        }
        StatusDetail = $"PowerShell {version}";
        await ConnectAsync(PsService.Graph | PsService.Exchange | PsService.Purview | PsService.SharePoint, interactive: false, cancellationToken);
        SetStatus(PsSessionStatus.Ready);
    }

    /// <summary>Starts pwsh and waits for the host's hello.</summary>
    async Task<JsonObject> LaunchAsync(string path, CancellationToken cancellationToken)
    {
        using var stream = typeof(PwshSession).Assembly.GetManifestResourceStream("Tenantry.PowerShell.Host.ps1")!;
        var script = await new StreamReader(stream).ReadToEndAsync(cancellationToken);
        var info = new ProcessStartInfo(path, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var hello = Expect("hello");
        lock (_lock)
            _process = process;
        process.Start();
        process.Exited += (_, _) => Exited(process);
        _ = Task.Run(() => ReadAsync(process));
        _ = Task.Run(() => ReadErrorsAsync(process));
        return await hello.Task.WaitAsync(StartTimeout, cancellationToken);
    }

    TaskCompletionSource<JsonObject> Expect(string kind)
    {
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _reply = reply;
            _replyKind = kind;
        }
        return reply;
    }

    async Task<JsonObject> RequestAsync(JsonObject request, string replyKind, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var reply = Expect(replyKind);
        Process process;
        lock (_lock)
            process = _process ?? throw new InvalidOperationException("PowerShell isn't running.");
        await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        return timeout is { } limit ? await reply.Task.WaitAsync(limit, cancellationToken) : await reply.Task.WaitAsync(cancellationToken);
    }

    async Task ConnectAsync(PsService services, bool interactive, CancellationToken cancellationToken)
    {
        PsSignIn tokens;
        try
        {
            tokens = await signIn(interactive, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            foreach (var (service, _) in Services.Where(s => services.HasFlag(s.Service)))
                SetConnection(service, PsConnectionStatus.Failed, $"Couldn't get a sign-in for it: {e.Message}");
            return;
        }
        var request = new JsonObject
        {
            ["op"] = "connect",
            ["services"] = new JsonArray(Services.Where(s => services.HasFlag(s.Service)).Select(s => (JsonNode)s.Key).ToArray()),
            ["organization"] = tokens.Organization,
            ["account"] = tokens.Account,
            ["graph"] = tokens.GraphToken,
            ["exchange"] = tokens.ExchangeToken,
            ["purview"] = tokens.PurviewToken,
            ["sharepoint"] = tokens.SharePointToken,
            ["sharePointAdmin"] = tokens.SharePointAdminUrl,
        };
        try
        {
            await RequestAsync(request, "connected", ConnectTimeout, cancellationToken);
            _connectedAt = DateTimeOffset.UtcNow;
        }
        catch (TimeoutException)
        {
            foreach (var c in Connections.Where(c => services.HasFlag(c.Service) && c.Status is PsConnectionStatus.Waiting or PsConnectionStatus.Connecting))
                SetConnection(c.Service, PsConnectionStatus.Failed, "It didn't answer in time.");
        }
    }

    public async Task<PsRunResult> RunAsync(string script, Action<PsLine> output, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken);
        var stopped = false;
        try
        {
            if (Status is PsSessionStatus.NotStarted)
                await StartCoreAsync(cancellationToken);
            if (Status is not PsSessionStatus.Ready)
            {
                output(new PsLine(PsStream.Error, StatusDetail ?? "PowerShell isn't ready."));
                return new PsRunResult(false, 1, watch.Elapsed);
            }
            // Tokens last about an hour: reconnect what was connected before they run out.
            if (DateTimeOffset.UtcNow - _connectedAt > ReconnectAfter)
            {
                var connected = Connections.Where(c => c.Status == PsConnectionStatus.Connected).Aggregate(PsService.None, (all, c) => all | c.Service);
                if (connected != PsService.None)
                {
                    output(new PsLine(PsStream.Verbose, "Signing PowerShell in again (sign-ins last about an hour)…"));
                    await ConnectAsync(connected, interactive: false, cancellationToken);
                }
            }
            SetStatus(PsSessionStatus.Running);
            lock (_lock)
                _output = output;
            try
            {
                var done = await RequestAsync(new JsonObject { ["op"] = "run", ["script"] = script }, "done", null, cancellationToken);
                var errors = done["errors"]?.GetValue<int>() ?? 0;
                return new PsRunResult(errors == 0, errors, watch.Elapsed);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopped = true;
                return new PsRunResult(false, 0, watch.Elapsed, Stopped: true);
            }
            catch (InvalidOperationException e)
            {
                output(new PsLine(PsStream.Error, e.Message));
                return new PsRunResult(false, 1, watch.Elapsed);
            }
        }
        finally
        {
            lock (_lock)
                _output = null;
            if (Status == PsSessionStatus.Running)
                SetStatus(PsSessionStatus.Ready);
            _gate.Release();
            if (stopped)
                _ = RestartAsync();
        }
    }

    public async Task<PsCompletion?> CompleteAsync(string input, int cursor, CancellationToken cancellationToken = default)
    {
        // Busy (a command running): no completion rather than a wait.
        if (!await _gate.WaitAsync(0, cancellationToken))
            return null;
        try
        {
            if (Status != PsSessionStatus.Ready)
                return null;
            var reply = await RequestAsync(new JsonObject { ["op"] = "complete", ["input"] = input, ["cursor"] = cursor }, "completion",
                TimeSpan.FromSeconds(10), cancellationToken);
            var matches = reply["matches"] is JsonArray list ? list.Select(m => m?.GetValue<string>() ?? "").Where(m => m.Length > 0).ToArray() : [];
            return new PsCompletion(reply["start"]?.GetValue<int>() ?? cursor, reply["length"]?.GetValue<int>() ?? 0, matches);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InstallModulesAsync(Action<PsLine> output, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status is not (PsSessionStatus.Ready or PsSessionStatus.Running))
                return;
            SetStatus(PsSessionStatus.Running);
            lock (_lock)
                _output = output;
            await RequestAsync(new JsonObject { ["op"] = "install" }, "installed", InstallTimeout, cancellationToken);
            lock (_lock)
                _output = null;
            // A newly installed module loads only in a fresh PowerShell.
            Kill();
            await StartCoreAsync(cancellationToken);
        }
        finally
        {
            lock (_lock)
                _output = null;
            if (Status == PsSessionStatus.Running)
                SetStatus(PsSessionStatus.Ready);
            _gate.Release();
        }
    }

    public async Task SignInToPurviewAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status == PsSessionStatus.Ready)
                await ConnectAsync(PsService.Purview, interactive: true, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Kill();
            if (!_disposed)
                await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task ReadAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 0 && line[0] == '\u0001')
                    Handle(JsonNode.Parse(line.AsSpan(1).ToString())?.AsObject());
                else
                    Write(PsStream.Output, line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or JsonException or InvalidOperationException)
        {
            // The process ended (or wrote something that isn't the host's); Exited says so.
        }
    }

    async Task ReadErrorsAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                Write(PsStream.Error, line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    void Write(PsStream stream, string text)
    {
        Action<PsLine>? output;
        lock (_lock)
            output = _output;
        if (output is null)
            return;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            output(new PsLine(stream, line));
    }

    void Handle(JsonObject? message)
    {
        var kind = message?["kind"]?.GetValue<string>();
        switch (kind)
        {
            case null:
                return;
            case "line":
                Write(Enum.TryParse<PsStream>(message!["stream"]?.GetValue<string>(), out var stream) ? stream : PsStream.Output,
                    message["text"]?.GetValue<string>() ?? "");
                return;
            case "connection":
                var key = message!["service"]?.GetValue<string>();
                if (Services.FirstOrDefault(s => s.Key == key) is { Key: not null } service
                    && Enum.TryParse<PsConnectionStatus>(message["status"]?.GetValue<string>(), out var status))
                    SetConnection(service.Service, status, message["detail"]?.GetValue<string>());
                return;
        }
        TaskCompletionSource<JsonObject>? reply = null;
        lock (_lock)
        {
            if (kind == _replyKind)
            {
                reply = _reply;
                _reply = null;
                _replyKind = null;
            }
        }
        reply?.TrySetResult(message!);
    }

    void Exited(Process process)
    {
        TaskCompletionSource<JsonObject>? reply;
        lock (_lock)
        {
            if (!ReferenceEquals(process, _process))
                return;
            _process = null;
            reply = _reply;
            _reply = null;
            _replyKind = null;
        }
        reply?.TrySetException(new InvalidOperationException("PowerShell stopped unexpectedly. It starts again with the next command."));
        if (!_disposed)
            SetStatus(PsSessionStatus.NotStarted, "PowerShell stopped unexpectedly. It starts again with the next command.");
    }

    void Kill()
    {
        Process? process;
        TaskCompletionSource<JsonObject>? reply;
        lock (_lock)
        {
            process = _process;
            _process = null;
            reply = _reply;
            _reply = null;
            _replyKind = null;
        }
        reply?.TrySetCanceled();
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        process.Dispose();
        foreach (var c in Connections)
            SetConnection(c.Service, PsConnectionStatus.Waiting, null);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Kill();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
