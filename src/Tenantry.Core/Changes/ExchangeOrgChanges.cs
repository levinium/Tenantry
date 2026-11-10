using System.Text.Json.Nodes;

namespace Tenantry.Core.Changes;

/// <summary>
/// One Exchange setting changed (12e): an organization setting, a remote domain's, a connector's
/// or an accepted domain's. <see cref="ExchangeValue"/> is what Exchange is sent; the texts are
/// what the tray shows. The target names what it belongs to, so Test mode allows only test-named
/// objects (organization settings never).
/// </summary>
/// <param name="Identity">The object it belongs to; null for an organization-wide setting.</param>
public sealed record SetExchangeSetting(ChangeTarget Owner, string SetCmdlet, string GetCmdlet, string? Identity, string Parameter, string SettingLabel,
    JsonNode? ExchangeValue, string OldText, string NewText, bool Caution = false) : ChangeOp
{
    /// <summary>The organization itself, for its settings.</summary>
    public static ChangeTarget Organization(string label) => new("orgsetting", "organization", label, null);

    public override ChangeTarget Target => Owner;
    public override string Key => $"{SetCmdlet}:{Parameter}";
    public override string Label => SettingLabel;
    public override string Before => OldText;
    public override string After => NewText;
    public override bool IsNoChange => OldText == NewText;
    public override bool IsCaution => Caution;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetExchangeSetting e ? this with { OldText = e.OldText } : this;
}

/// <summary>Adds a remote domain: its own settings for mail to that domain.</summary>
public sealed record CreateRemoteDomain(ChangeTarget Domain, string DomainName) : ChangeOp
{
    public override ChangeTarget Target => Domain;
    public override string Key => "remotedomain";
    public override string Label => "Remote Domain";
    public override string Before => "None";
    public override string After => $"Added for {DomainName} (with the default settings)";
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

public sealed record RemoveRemoteDomain(ChangeTarget Domain, string DomainName) : ChangeOp
{
    public override ChangeTarget Target => Domain;
    public override string Key => "remotedomain";
    public override string Label => "Remote Domain";
    public override string Before => $"For {DomainName}";
    public override string After => "Removed (the default settings apply)";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Turns a journal rule on or off.</summary>
public sealed record SetJournalRuleEnabled(ChangeTarget Rule, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => Rule;
    public override string Key => "journal";
    public override string Label => "Journal Rule";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsCaution => !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetJournalRuleEnabled e ? this with { Old = e.Old } : this;
}

/// <summary>A new journal rule: copies of mail in its scope (and for one person or list, if given) go to an outside archive address.</summary>
/// <param name="Scope">"Global" (all mail), "Internal" or "External".</param>
public sealed record CreateJournalRule(ChangeTarget Rule, string Scope, string? Recipient, string JournalAddress) : ChangeOp
{
    public override ChangeTarget Target => Rule;
    public override string Key => "journal";
    public override string Label => "Journal Rule";
    public override string Before => "None";
    public override string After => $"{(Scope switch { "Internal" => "Mail inside the organization", "External" => "Mail to or from outside", _ => "All mail" })}"
                                    + $"{(Recipient is { Length: > 0 } r ? $" for {r}" : "")} copied to {JournalAddress}";
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

public sealed record RemoveJournalRule(ChangeTarget Rule) : ChangeOp
{
    public override ChangeTarget Target => Rule;
    public override string Key => "journal";
    public override string Label => "Journal Rule";
    public override string Before => "Copying mail";
    public override string After => "Removed";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}
