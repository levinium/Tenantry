namespace Tenantry.Core.PowerShell;

public enum PsStream
{
    Output,
    Error,
    Warning,
    Information,
    Verbose,
}

/// <summary>One line a command wrote.</summary>
public sealed record PsLine(PsStream Stream, string Text);

public enum PsSessionStatus
{
    NotStarted,

    /// <summary>Starting PowerShell, checking modules and connecting.</summary>
    Starting,

    Ready,

    /// <summary>A command is running.</summary>
    Running,

    /// <summary>PowerShell 7 isn't installed (or couldn't start); <see cref="IPowerShellSession.StatusDetail"/> says which.</summary>
    Unavailable,
}

public enum PsConnectionStatus
{
    Waiting,
    Connecting,
    Connected,

    /// <summary>The module isn't installed; Install Modules fixes it.</summary>
    ModuleMissing,

    /// <summary>Needs a sign-in first (Purview's second sign-in).</summary>
    NeedsSignIn,

    Failed,
}

/// <summary>How one service's connection stands.</summary>
public sealed record PsConnection(PsService Service, PsConnectionStatus Status, string? Detail = null);

/// <summary>How a command ended.</summary>
/// <param name="Stopped">Stopped by the admin before it finished.</param>
public sealed record PsRunResult(bool Succeeded, int ErrorCount, TimeSpan Elapsed, bool Stopped = false);

/// <summary>Tab completion: replace <see cref="Length"/> characters at <see cref="Start"/> with one of <see cref="Matches"/>.</summary>
public sealed record PsCompletion(int Start, int Length, IReadOnlyList<string> Matches);

/// <summary>
/// A PowerShell 7 process on this computer, signed in to the tenant with Tenantry's own sign-in
/// (Graph, Exchange, Purview and SharePoint), for the console. Commands run one at a time; the
/// variables they set stay until the session restarts. Events may arrive on any thread.
/// </summary>
public interface IPowerShellSession : IAsyncDisposable
{
    PsSessionStatus Status { get; }

    /// <summary>The PowerShell version once started, or why it can't start.</summary>
    string? StatusDetail { get; }

    IReadOnlyList<PsConnection> Connections { get; }

    /// <summary>Status or a connection changed.</summary>
    event EventHandler? Changed;

    /// <summary>Starts PowerShell and connects to everything it can (no browser sign-in).</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a command; cancelling stops it (PowerShell restarts and reconnects, so variables are lost).</summary>
    Task<PsRunResult> RunAsync(string script, Action<PsLine> output, CancellationToken cancellationToken = default);

    Task<PsCompletion?> CompleteAsync(string input, int cursor, CancellationToken cancellationToken = default);

    /// <summary>Installs the missing modules for this user (from the PowerShell Gallery), then reconnects.</summary>
    Task InstallModulesAsync(Action<PsLine> output, CancellationToken cancellationToken = default);

    /// <summary>Signs in to Purview in the browser (its second sign-in), then connects it.</summary>
    Task SignInToPurviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a fresh PowerShell and reconnects (variables are lost).</summary>
    Task RestartAsync(CancellationToken cancellationToken = default);
}
