namespace Tenantry.Core.Changes;

/// <summary>
/// A command run in the PowerShell console, logged once it finished (never staged): it went
/// straight to Microsoft 365, outside Pending changes and Test mode. The output isn't kept.
/// </summary>
/// <param name="Script">What was run, as typed.</param>
public sealed record PowerShellCommand(ChangeTarget Tenant, string Script) : ChangeOp
{
    public override ChangeTarget Target => Tenant;
    public override string Key => "powershell";
    public override string Label => "PowerShell Command";
    public override string Before => "Console";

    /// <summary>The command on one line, shortened (the full text is in <see cref="Script"/>).</summary>
    public override string After
    {
        get
        {
            var line = string.Join(" ", Script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return line.Length <= 200 ? line : line[..199] + "…";
        }
    }

    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}
