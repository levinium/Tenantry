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
/// Where Outlook's Report button sends reports (12d): the organization-wide report settings. Its
/// target is the organization's setting, so Test mode refuses it.
/// </summary>
public sealed record SetReportRouting(ChangeTarget Settings, Directory.ReportRouting Old, Directory.ReportRouting New) : ChangeOp
{
    public static ChangeTarget TargetFor() => new("reportsettings", "reportsubmission", "Where Reports Go", null);

    public override ChangeTarget Target => Settings;
    public override string Key => "routing";
    public override string Label => "Reported Messages Go To";
    public override string Before => Old.Describe();
    public override string After => New.Describe();
    public override bool IsNoChange => Old with { PolicyExists = false, RuleExists = false } == New with { PolicyExists = false, RuleExists = false };
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetReportRouting e ? this with { Old = e.Old } : this;
}

/// <summary>
/// An admin's answer to a person's report (Graph review): marks it and, when the report settings
/// say so, emails the person the result. The target is the reporter, so Test mode allows only
/// test people.
/// </summary>
/// <param name="Verdict">"notSpam", "junk", "phishing" or "malware".</param>
public sealed record ReviewSubmission(ChangeTarget Reporter, string SubmissionId, string Subject, string Verdict) : ChangeOp
{
    public override ChangeTarget Target => Reporter;
    public override string Key => "review:" + SubmissionId;
    public override string Label => $"Report of \"{Subject}\"";
    public override string Before => "Not reviewed";
    public override string After => "Reviewed: " + Verdict switch { "notSpam" => "no threat found", "junk" => "spam", _ => Verdict };
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
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
