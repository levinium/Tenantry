namespace Tenantry.Core.Changes;

/// <summary>
/// A domain's DKIM signing (12c). The target is the domain itself (kind "maildomain"), so in Test
/// mode it's refused unless the domain's name starts with the test marker: a real domain is
/// changed only with Test mode off.
/// </summary>
public abstract record DkimChange(ChangeTarget Domain) : ChangeOp
{
    public static ChangeTarget TargetFor(string domain) => new("maildomain", domain, domain, null);

    public override ChangeTarget Target => Domain;
    public override string Label => "DKIM Signing";
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Creates the domain's DKIM keys (signing stays off until its CNAME records are published).</summary>
public sealed record CreateDkimKeys(ChangeTarget Domain) : DkimChange(Domain)
{
    public override string Key => "dkim";
    public override string Before => "Not set up";
    public override string After => "Keys created (publish the CNAME records, then turn signing on)";
    public override bool IsNoChange => false;
}

/// <summary>Turns DKIM signing on or off for the domain.</summary>
public sealed record SetDkimSigning(ChangeTarget Domain, bool Old, bool New) : DkimChange(Domain)
{
    public override string Key => "dkim";
    public override string Before => Old ? "Signing" : "Off";
    public override string After => New ? "Signing" : "Off";
    public override bool IsNoChange => Old == New;
    public override bool IsCaution => !New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetDkimSigning e ? this with { Old = e.Old } : this;
}

/// <summary>Switches the domain to its other key (new mail is signed with it after about four days), at 2048 bits.</summary>
public sealed record RotateDkimKeys(ChangeTarget Domain, int KeySize = 2048) : DkimChange(Domain)
{
    public override string Key => "dkimrotate";
    public override string Label => "DKIM Keys";
    public override string Before => "Current key";
    public override string After => $"New {KeySize}-bit key (in use after about 4 days)";
    public override bool IsNoChange => false;
}

/// <summary>
/// Lets a sender Microsoft blocked for sending spam send again (Remove-BlockedSenderAddress). The
/// target is the person's address, so Test mode allows only test people.
/// </summary>
public sealed record UnblockSender(ChangeTarget Sender, string? Reason) : ChangeOp
{
    public static ChangeTarget TargetFor(string address) => new("mailbox", address, address, address);

    public override ChangeTarget Target => Sender;
    public override string Key => "blockedsender";
    public override string Label => "Blocked from Sending";
    public override string Before => Reason is { Length: > 0 } r ? $"Blocked ({r})" : "Blocked";
    public override string After => "Can send again";
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}
