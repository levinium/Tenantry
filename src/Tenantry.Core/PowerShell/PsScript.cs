using Tenantry.Core.Changes;

namespace Tenantry.Core.PowerShell;

/// <summary>What a script needs to be connected to.</summary>
[Flags]
public enum PsService
{
    None = 0,

    /// <summary>Microsoft Graph (Connect-MgGraph, module Microsoft.Graph.Authentication).</summary>
    Graph = 1,

    /// <summary>Exchange Online (Connect-ExchangeOnline, module ExchangeOnlineManagement).</summary>
    Exchange = 2,

    /// <summary>Purview, Security &amp; Compliance (Connect-IPPSSession, module ExchangeOnlineManagement).</summary>
    Purview = 4,

    /// <summary>The SharePoint admin site (Connect-PnPOnline, module PnP.PowerShell).</summary>
    SharePoint = 8,
}

/// <summary>PowerShell that does what a change does, and what it must be connected to.</summary>
/// <param name="Note">Something worth knowing that the script itself can't say (shown above it, as comments).</param>
public sealed record PsScript(string Text, PsService Needs, string? Note = null)
{
    /// <summary>The script with its note as comments on top.</summary>
    public string Full => Note is null ? Text : Ps.Comment(Note) + "\n" + Text;

    public static PsScript Graph(string text, string? note = null) => new(text, PsService.Graph, note);

    public static PsScript Exchange(string text, string? note = null) => new(text, PsService.Exchange, note);

    public static PsScript Purview(string text, string? note = null) => new(text, PsService.Purview, note);

    public static PsScript SharePoint(string text, string? note = null) => new(text, PsService.SharePoint, note);
}

/// <summary>Turns a change into the PowerShell that does the same thing.</summary>
public interface IChangeScripts
{
    /// <summary>The PowerShell for <paramref name="change"/>, or null when it has none (a Tenantry setting, say).</summary>
    PsScript? For(ChangeOp change);
}

public static class ChangeScripts
{
    /// <summary>The services, as a person reads them ("Microsoft Graph and Exchange Online").</summary>
    public static string ServiceNames(PsService needs)
    {
        var names = new List<string>();
        if (needs.HasFlag(PsService.Graph))
            names.Add("Microsoft Graph");
        if (needs.HasFlag(PsService.Exchange))
            names.Add("Exchange Online");
        if (needs.HasFlag(PsService.Purview))
            names.Add("Purview");
        if (needs.HasFlag(PsService.SharePoint))
            names.Add("SharePoint");
        return names.Count switch
        {
            0 => "nothing",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1],
        };
    }

    /// <summary>The connect commands a script needs outside Tenantry's console (where they're already run).</summary>
    public static IReadOnlyList<string> ConnectCommands(PsService needs)
    {
        var lines = new List<string>();
        if (needs.HasFlag(PsService.Graph))
            lines.Add("Connect-MgGraph");
        if (needs.HasFlag(PsService.Exchange))
            lines.Add("Connect-ExchangeOnline");
        if (needs.HasFlag(PsService.Purview))
            lines.Add("Connect-IPPSSession");
        if (needs.HasFlag(PsService.SharePoint))
            lines.Add("Connect-PnPOnline -Url https://<tenant>-admin.sharepoint.com");
        return lines;
    }

    /// <summary>
    /// One script for several changes, in order, each under a comment saying what it does.
    /// Changes with no PowerShell are listed as such, so nothing silently goes missing.
    /// </summary>
    public static PsScript Batch(IChangeScripts scripts, IEnumerable<ChangeOp> changes)
    {
        var parts = new List<string>();
        var needs = PsService.None;
        var n = 0;
        foreach (var change in changes)
        {
            n++;
            var heading = Ps.Comment($"{n}. {change.Target.Name}: {change.Summary}");
            if (scripts.For(change) is { } script)
            {
                needs |= script.Needs;
                parts.Add(heading + "\n" + script.Full);
            }
            else
                parts.Add(heading + "\n" + Ps.Comment("(No PowerShell for this one: it's a Tenantry record, not a Microsoft 365 change.)"));
        }
        return new PsScript(string.Join("\n\n", parts), needs);
    }
}
