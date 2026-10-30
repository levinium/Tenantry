using Tenantry.Core.PowerShell;

namespace Tenantry.Demo;

/// <summary>
/// The demo's console: looks connected, runs nothing. A command gets a line saying so (and a
/// made-up error for anything that would change something, so both colours show in screenshots).
/// </summary>
public sealed class DemoPowerShellSession : IPowerShellSession
{
    public PsSessionStatus Status { get; private set; } = PsSessionStatus.NotStarted;

    public string? StatusDetail => "PowerShell 7 (demo)";

    public IReadOnlyList<PsConnection> Connections { get; private set; } =
        [new(PsService.Graph, PsConnectionStatus.Waiting), new(PsService.Exchange, PsConnectionStatus.Waiting),
         new(PsService.Purview, PsConnectionStatus.Waiting), new(PsService.SharePoint, PsConnectionStatus.Waiting)];

    public event EventHandler? Changed;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Connections = [new(PsService.Graph, PsConnectionStatus.Connected, "Microsoft.Graph.Authentication (demo)"),
            new(PsService.Exchange, PsConnectionStatus.Connected, "ExchangeOnlineManagement (demo)"),
            new(PsService.Purview, PsConnectionStatus.NeedsSignIn, "Purview needs its own sign-in first."),
            new(PsService.SharePoint, PsConnectionStatus.ModuleMissing, "The PnP.PowerShell module isn't installed.")];
        Status = PsSessionStatus.Ready;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<PsRunResult> RunAsync(string script, Action<PsLine> output, CancellationToken cancellationToken = default)
    {
        var command = script.TrimStart().Split(' ', '\n')[0];
        var verb = command.Split('-')[0];
        var reads = verb is "Get" or "Test" or "Search" or "Find" or "Measure" or "Select" or "Where";
        output(new PsLine(PsStream.Information, "Demo tenant: nothing was sent anywhere."));
        if (!reads)
            output(new PsLine(PsStream.Error, $"{command}: In your own tenant this would change Microsoft 365 directly."));
        return Task.FromResult(new PsRunResult(reads, reads ? 0 : 1, TimeSpan.FromMilliseconds(120)));
    }

    public Task<PsCompletion?> CompleteAsync(string input, int cursor, CancellationToken cancellationToken = default)
    {
        var start = input.LastIndexOfAny([' ', '\n', '|', ';'], Math.Max(0, cursor - 1)) + 1;
        var word = input[start..cursor];
        var matches = new[] { "Get-Mailbox", "Get-MgUser", "Get-DistributionGroup", "Get-PnPTenantSite", "Set-Mailbox" }
            .Where(c => c.StartsWith(word, StringComparison.OrdinalIgnoreCase)).ToArray();
        return Task.FromResult<PsCompletion?>(word.Length == 0 || matches.Length == 0 ? null : new PsCompletion(start, word.Length, matches));
    }

    public Task InstallModulesAsync(Action<PsLine> output, CancellationToken cancellationToken = default)
    {
        output(new PsLine(PsStream.Information, "Demo tenant: nothing is installed."));
        return Task.CompletedTask;
    }

    public Task SignInToPurviewAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RestartAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
