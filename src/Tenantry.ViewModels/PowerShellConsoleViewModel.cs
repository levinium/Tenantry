using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.PowerShell;

namespace Tenantry.ViewModels;

/// <summary>A run of lines from one stream (output, errors, warnings), shown in one colour.</summary>
public sealed partial class PsChunk(PsStream stream) : ObservableObject
{
    readonly StringBuilder _text = new();

    public PsStream Stream { get; } = stream;

    public bool IsError => Stream == PsStream.Error;

    public bool IsWarning => Stream == PsStream.Warning;

    public bool IsQuiet => Stream == PsStream.Verbose;

    public string Text => _text.ToString();

    internal void Add(string line)
    {
        if (_text.Length > 0)
            _text.Append('\n');
        _text.Append(line);
    }

    internal void Changed() => OnPropertyChanged(nameof(Text));
}

/// <summary>One command and what it wrote.</summary>
public sealed partial class PsBlock(string command, Action<string> editAgain) : ObservableObject
{
    /// <summary>More than this and the rest is cut (a huge table would make the window crawl).</summary>
    public const int MaxLines = 5000;

    int _lines;
    // Format-Table and friends start and end with blank lines; leading ones are dropped and
    // trailing ones held back until something follows them.
    int _heldBlanks;

    public string Command { get; } = command;

    public ObservableCollection<PsChunk> Chunks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Outcome))]
    public partial bool IsRunning { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Outcome))]
    public partial PsRunResult? Result { get; set; }

    public bool Failed => Result is { Succeeded: false };

    public string Outcome => IsRunning ? "Running…" : Result switch
    {
        null => "",
        { Stopped: true } => $"Stopped after {Seconds(Result.Elapsed)}",
        { ErrorCount: > 0 } => $"{Plural(Result.ErrorCount, "error")} · {Seconds(Result.Elapsed)}",
        { Succeeded: false } => $"Didn't run · {Seconds(Result.Elapsed)}",
        _ => $"Done in {Seconds(Result.Elapsed)}",
    };

    public bool IsCut { get; private set; }

    internal void Add(IEnumerable<PsLine> lines)
    {
        var touched = new HashSet<PsChunk>();
        foreach (var line in lines)
        {
            if (IsCut)
                break;
            if (line.Stream == PsStream.Output && string.IsNullOrWhiteSpace(line.Text))
            {
                if (_lines > 0)
                    _heldBlanks++;
                continue;
            }
            for (; _heldBlanks > 0; _heldBlanks--)
                touched.Add(Append(PsStream.Output, ""));
            touched.Add(Append(line.Stream, line.Text.TrimEnd()));
            if (++_lines >= MaxLines)
            {
                IsCut = true;
                touched.Add(Append(PsStream.Warning, $"Only the first {MaxLines:N0} lines are shown. To see everything, send it to a file: … | Export-Csv results.csv"));
                OnPropertyChanged(nameof(IsCut));
            }
        }
        foreach (var chunk in touched)
            chunk.Changed();
    }

    PsChunk Append(PsStream stream, string text)
    {
        if (Chunks.Count == 0 || Chunks[^1].Stream != stream)
            Chunks.Add(new PsChunk(stream));
        Chunks[^1].Add(text);
        _lines++;
        return Chunks[^1];
    }

    [RelayCommand]
    void CopyOutput() => Platform.CopyText(string.Join('\n', Chunks.Select(c => c.Text)));

    /// <summary>Puts the command back in the box, to change and run again.</summary>
    [RelayCommand]
    void EditAgain() => editAgain(Command);

    static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds < 10 ? $"{elapsed.TotalSeconds:0.0} s" : $"{elapsed.TotalSeconds:0} s";

    static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>One service's connection, as a chip with (when it helps) a button to fix it.</summary>
public sealed record PsConnectionRow(string Name, string State, BadgeKind Kind, string? Detail, string? ActionText, IRelayCommand? Action)
{
    public bool HasAction => Action is not null;
}

/// <summary>
/// The PowerShell console: the computer's own PowerShell 7, signed in as the admin to Graph,
/// Exchange, Purview and SharePoint. Commands go straight to Microsoft 365 (not through Pending
/// changes or Test mode, as the banner says); each one is recorded in the change log.
/// </summary>
public sealed partial class PowerShellConsoleViewModel : TabViewModel, IDisposable
{
    readonly IPowerShellSession _session;
    readonly ChangePipeline? _pipeline;
    readonly string _tenantName;
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly ConcurrentQueue<(PsBlock Block, PsLine Line)> _incoming = new();
    readonly List<string> _history = [];
    int _historyAt;
    int _flushQueued;
    CancellationTokenSource? _running;
    (string Before, int Start, int Length, IReadOnlyList<string> Matches, int Next)? _completing;

    public PowerShellConsoleViewModel(IPowerShellSession session, ChangePipeline? pipeline, string tenantName, bool isDemo)
    {
        _session = session;
        _pipeline = pipeline;
        _tenantName = tenantName;
        IsDemo = isDemo;
        _session.Changed += (_, _) => OnUi(SessionChanged);
        SessionChanged();
        _ = StartAsync();
    }

    public override string Title => "PowerShell";

    public bool IsDemo { get; }

    public string Banner => IsDemo
        ? "Demo tenant: commands here aren't sent anywhere. In your own tenant they run in this computer's PowerShell 7, signed in as you."
        : $"Commands here go straight to {_tenantName} as you: they don't wait in Pending Changes, and Test Mode doesn't limit them. Each one is recorded in the Change Log.";

    public ObservableCollection<PsBlock> Blocks { get; } = [];

    public bool HasBlocks => Blocks.Count > 0;

    public ObservableCollection<PsConnectionRow> Connections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string Input { get; set; } = "";

    partial void OnInputChanged(string value) => _completing = null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStarting), nameof(IsUnavailable), nameof(IsRunning), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand), nameof(RestartCommand), nameof(InstallModulesCommand))]
    public partial PsSessionStatus Status { get; private set; }

    public bool IsStarting => Status == PsSessionStatus.Starting;

    public bool IsUnavailable => Status == PsSessionStatus.Unavailable;

    public bool IsRunning => Status == PsSessionStatus.Running;

    public string StatusText => Status switch
    {
        PsSessionStatus.Starting => "Starting PowerShell and signing it in…",
        PsSessionStatus.Running => "Running…",
        _ => _session.StatusDetail ?? "",
    };

    public string InstallHelpText => "Install PowerShell 7 (free, from Microsoft), then choose Try Again.";

    /// <summary>Set when this console writes something the user started (a module install).</summary>
    [ObservableProperty]
    public partial string? Notice { get; private set; }

    public bool HasMissingModules => _session.Connections.Any(c => c.Status == PsConnectionStatus.ModuleMissing);

    async Task StartAsync()
    {
        try
        {
            await _session.StartAsync();
        }
        catch (Exception e)
        {
            Notice = $"PowerShell couldn't start: {Friendly.Message(e)}";
        }
    }

    void SessionChanged()
    {
        Status = _session.Status;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasMissingModules));
        InstallModulesCommand.NotifyCanExecuteChanged();
        Connections.Clear();
        foreach (var c in _session.Connections)
            Connections.Add(Row(c));
    }

    PsConnectionRow Row(PsConnection c)
    {
        var name = ChangeScripts.ServiceNames(c.Service);
        return c.Status switch
        {
            PsConnectionStatus.Connected => new(name, "Connected", BadgeKind.Success, c.Detail, null, null),
            PsConnectionStatus.Connecting => new(name, "Connecting…", BadgeKind.Info, null, null, null),
            PsConnectionStatus.ModuleMissing => new(name, "Module Missing", BadgeKind.Warning, c.Detail, "Install Modules", InstallModulesCommand),
            PsConnectionStatus.NeedsSignIn => new(name, "Needs Sign-In", BadgeKind.Neutral, c.Detail, "Sign In", SignInToPurviewCommand),
            PsConnectionStatus.Failed => new(name, "Not Connected", BadgeKind.Danger, c.Detail, null, null),
            _ => new(name, "Waiting", BadgeKind.Neutral, null, null, null),
        };
    }

    bool CanRun => Status == PsSessionStatus.Ready && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Run()
    {
        var script = Input.TrimEnd();
        Input = "";
        if (_history.Count == 0 || _history[^1] != script)
            _history.Add(script);
        _historyAt = _history.Count;
        var block = new PsBlock(script, command => Input = command);
        Blocks.Add(block);
        OnPropertyChanged(nameof(HasBlocks));
        _running = new CancellationTokenSource();
        PsRunResult result;
        try
        {
            result = await _session.RunAsync(script, line => Queue(block, line), _running.Token);
        }
        catch (Exception e)
        {
            Queue(block, new PsLine(PsStream.Error, Friendly.Message(e)));
            result = new PsRunResult(false, 1, TimeSpan.Zero);
        }
        finally
        {
            _running.Dispose();
            _running = null;
        }
        Flush();
        block.IsRunning = false;
        block.Result = result;
        _pipeline?.Record(new PowerShellCommand(ChangePipeline.TenantTarget(_tenantName), script),
            result switch
            {
                { Stopped: true } => "Stopped before it finished.",
                { ErrorCount: 1 } => "It wrote 1 error.",
                { ErrorCount: > 1 } => $"It wrote {result.ErrorCount} errors.",
                _ => null,
            },
            result.Succeeded ? ChangeOutcome.Applied : ChangeOutcome.Failed);
    }

    bool CanStop => Status == PsSessionStatus.Running;

    /// <summary>Stops the running command. PowerShell starts again, so variables are lost.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    void Stop() => _running?.Cancel();

    bool CanRestart => Status is PsSessionStatus.Ready or PsSessionStatus.Unavailable or PsSessionStatus.NotStarted;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    async Task Restart()
    {
        Notice = null;
        try
        {
            await _session.RestartAsync();
        }
        catch (Exception e)
        {
            Notice = $"PowerShell couldn't start: {Friendly.Message(e)}";
        }
    }

    [RelayCommand]
    void Clear()
    {
        Blocks.Clear();
        OnPropertyChanged(nameof(HasBlocks));
    }

    [RelayCommand]
    void OpenInstallHelp() => Platform.OpenUrl("https://learn.microsoft.com/powershell/scripting/install/installing-powershell");

    bool CanInstall => Status == PsSessionStatus.Ready && HasMissingModules;

    /// <summary>Installs the missing modules for this user from the PowerShell Gallery; the output shows as a block.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    async Task InstallModules()
    {
        var block = new PsBlock("Install the missing modules", command => { });
        Blocks.Add(block);
        OnPropertyChanged(nameof(HasBlocks));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _session.InstallModulesAsync(line => Queue(block, line));
            Flush();
            block.Result = new PsRunResult(block.Chunks.All(c => !c.IsError), block.Chunks.Count(c => c.IsError), watch.Elapsed);
        }
        catch (Exception e)
        {
            Flush();
            block.Add([new PsLine(PsStream.Error, Friendly.Message(e))]);
            block.Result = new PsRunResult(false, 1, watch.Elapsed);
        }
        block.IsRunning = false;
    }

    [RelayCommand]
    async Task SignInToPurview()
    {
        try
        {
            await _session.SignInToPurviewAsync();
        }
        catch (Exception e)
        {
            Notice = $"Purview sign-in didn't finish: {Friendly.Message(e)}";
        }
    }

    /// <summary>Puts a script in the box to read, change and run (from Show PowerShell).</summary>
    public void Insert(string script)
    {
        Input = script;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks the view to put the cursor in the command box.</summary>
    public event EventHandler? FocusRequested;

    // ---- History and Tab completion (called by the view's key handling) -------------------

    /// <summary>The previous (older) command, or null when there's none.</summary>
    public string? HistoryBack()
    {
        if (_historyAt == 0)
            return null;
        _historyAt--;
        return _history[_historyAt];
    }

    /// <summary>The next (newer) command, or empty past the newest.</summary>
    public string? HistoryForward()
    {
        if (_historyAt >= _history.Count)
            return null;
        _historyAt++;
        return _historyAt == _history.Count ? "" : _history[_historyAt];
    }

    /// <summary>
    /// Tab: completes the word at the cursor; pressing Tab again (without typing) moves on to
    /// the next match. Returns the new text and cursor, or null when there's nothing to offer.
    /// </summary>
    public async Task<(string Text, int Cursor)?> CompleteAsync(string text, int cursor, bool backwards = false)
    {
        if (_completing is { } c && c.Matches.Count > 0)
        {
            var next = (c.Next + (backwards ? c.Matches.Count - 2 : 0)) % c.Matches.Count;
            return Apply(c.Before, c.Start, c.Length, c.Matches, next);
        }
        var found = await _session.CompleteAsync(text, cursor);
        if (found is null || found.Matches.Count == 0)
            return null;
        return Apply(text, found.Start, found.Length, found.Matches, 0);
    }

    (string, int) Apply(string before, int start, int length, IReadOnlyList<string> matches, int index)
    {
        start = Math.Clamp(start, 0, before.Length);
        length = Math.Clamp(length, 0, before.Length - start);
        var text = before[..start] + matches[index] + before[(start + length)..];
        // Setting Input would clear the cycle; set it, then remember where the cycle is.
        Input = text;
        _completing = (before, start, length, matches, (index + 1) % matches.Count);
        return (text, start + matches[index].Length);
    }

    // ---- Output arrives on PowerShell's reader thread; the window catches up in batches -----

    void Queue(PsBlock block, PsLine line)
    {
        _incoming.Enqueue((block, line));
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            OnUi(Flush);
    }

    void Flush()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        var batch = new List<(PsBlock Block, PsLine Line)>();
        while (_incoming.TryDequeue(out var item))
            batch.Add(item);
        foreach (var group in batch.GroupBy(i => i.Block))
            group.Key.Add(group.Select(i => i.Line));
    }

    void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    public void Dispose()
    {
        _running?.Cancel();
        _ = _session.DisposeAsync().AsTask();
    }
}
