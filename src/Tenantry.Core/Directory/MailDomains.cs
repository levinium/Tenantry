namespace Tenantry.Core.Directory;

/// <summary>A sender Microsoft stopped from sending because it sent spam (Exchange's restricted entities).</summary>
public sealed record BlockedSender(string Address, string? Reason, DateTimeOffset? BlockedAt, DateTimeOffset? Expires, bool Temporary);

/// <summary>A domain's DKIM signing in Exchange: whether it signs, and the two CNAME records it needs in DNS.</summary>
/// <param name="Status">Exchange's own check ("Valid", "CnameMissing"…); null when the domain has no DKIM setup yet.</param>
public sealed record DkimSetup(bool Enabled, string? Status, string? Selector1Target, string? Selector2Target, int? KeySize,
    DateTimeOffset? RotateOnDate, DateTimeOffset? KeyCreated)
{
    public bool CnamesMissing => string.Equals(Status, "CnameMissing", StringComparison.OrdinalIgnoreCase);
}

/// <summary>What DNS says for a domain's mail: where mail goes (MX) and the SPF, DMARC and DKIM records.</summary>
/// <param name="Error">Why DNS couldn't be read (offline, a timeout); the rest is then empty.</param>
public sealed record MailDns(IReadOnlyList<string> Mx, IReadOnlyList<string> Spf, IReadOnlyList<string> Dmarc,
    string? Selector1, string? Selector2, string? Error = null);

/// <summary>How one check on a domain came out.</summary>
public enum CheckState { Good, Warning, Problem, Info }

/// <summary>One check on a domain, in words, with the record to publish when there is one.</summary>
public sealed record DomainCheck(string Check, CheckState State, string Summary, string Detail, string? Record = null, string? RecordName = null, string? RecordType = null);

/// <summary>An accepted domain with its DKIM setup and DNS, and what Tenantry makes of them.</summary>
public sealed record MailDomain(string Name, bool IsDefault, bool IsInitial, DkimSetup? Dkim, MailDns Dns)
{
    /// <summary>Mail for the domain comes to Microsoft 365 (its MX is Exchange Online Protection).</summary>
    public bool ReceivesHere => Dns.Mx.Any(m => m.EndsWith(".mail.protection.outlook.com", StringComparison.OrdinalIgnoreCase)
                                                || m.EndsWith(".mx.microsoft", StringComparison.OrdinalIgnoreCase));

    /// <summary>The domain gets no mail at all (no MX record): a parked domain that should say it sends none.</summary>
    public bool Parked => Dns.Mx.Count == 0 && Dns.Error is null;

    public IReadOnlyList<DomainCheck> Checks => MailDomainChecks.For(this);

    /// <summary>The worst of its checks: a problem, else a warning, else good.</summary>
    public CheckState Worst
    {
        get
        {
            var checks = Checks;
            return checks.Any(c => c.State == CheckState.Problem) ? CheckState.Problem
                : checks.Any(c => c.State == CheckState.Warning) ? CheckState.Warning
                : CheckState.Good;
        }
    }
}

/// <summary>
/// Reads a domain's mail setup the way a receiving mail server would, and says what's wrong in
/// words: SPF (who may send), DKIM (signing), DMARC (what receivers do when those fail) and MX
/// (where mail arrives). The initial onmicrosoft.com domain is Microsoft's to look after.
/// </summary>
public static class MailDomainChecks
{
    const string MicrosoftSpf = "include:spf.protection.outlook.com";

    public static IReadOnlyList<DomainCheck> For(MailDomain d)
    {
        if (d.IsInitial)
            return [new DomainCheck("Microsoft's Domain", CheckState.Good, "Looked after by Microsoft",
                "The initial onmicrosoft.com domain is signed and published by Microsoft; there's nothing to set up.")];
        if (d.Dns.Error is { } error)
            return [new DomainCheck("DNS", CheckState.Warning, "Couldn't read DNS", error)];
        return [Mx(d), Spf(d), Dkim(d), Dmarc(d)];
    }

    static DomainCheck Mx(MailDomain d) => d switch
    {
        { ReceivesHere: true } => new("Where Mail Arrives (MX)", CheckState.Good, "Microsoft 365", $"Mail for {d.Name} comes straight to Exchange Online."),
        { Parked: true } => new("Where Mail Arrives (MX)", CheckState.Info, "Nowhere (no MX record)",
            $"{d.Name} doesn't receive mail. If it doesn't send any either, its SPF and DMARC should say so, so nobody can send as it."),
        _ => new("Where Mail Arrives (MX)", CheckState.Info, "Somewhere else first",
            $"Mail for {d.Name} goes to {string.Join(", ", d.Dns.Mx)} first (a filtering service, or another mail system). That's fine if it's meant to."),
    };

    static DomainCheck Spf(MailDomain d)
    {
        var name = d.Name;
        var suggested = d.Parked ? "v=spf1 -all" : $"v=spf1 {MicrosoftSpf} -all";
        if (d.Dns.Spf.Count == 0)
            return new("Who May Send (SPF)", CheckState.Problem, "Missing",
                $"{name} has no SPF record, so receivers can't tell which servers may send for it and may treat its mail as spam.", suggested, name, "TXT");
        if (d.Dns.Spf.Count > 1)
            return new("Who May Send (SPF)", CheckState.Problem, $"{d.Dns.Spf.Count} records (only one is allowed)",
                "With more than one SPF record every check fails. Merge them into one record.", suggested, name, "TXT");
        var spf = d.Dns.Spf[0];
        var lower = spf.ToLowerInvariant();
        if (!d.Parked && !lower.Contains(MicrosoftSpf))
            return new("Who May Send (SPF)", CheckState.Problem, "Doesn't include Microsoft 365",
                $"The record ({spf}) doesn't list Microsoft 365, so mail sent from Exchange Online fails SPF. Add {MicrosoftSpf} to it.", suggested, name, "TXT");
        if (lower.Contains("+all"))
            return new("Who May Send (SPF)", CheckState.Problem, "Lets anyone send", $"\"+all\" lets every server in the world pass. Use -all or ~all instead. ({spf})", suggested, name, "TXT");
        if (lower.Contains("?all") || !lower.Contains("all"))
            return new("Who May Send (SPF)", CheckState.Warning, "Doesn't say what to do with others",
                $"The record ends without -all or ~all, so servers it doesn't list aren't marked as failing. ({spf})");
        return lower.Contains("~all")
            ? new("Who May Send (SPF)", CheckState.Good, "Set (soft fail for others)", $"{spf}. Servers it doesn't list are marked suspicious (~all); -all is stricter.")
            : new("Who May Send (SPF)", CheckState.Good, "Set", spf);
    }

    static DomainCheck Dkim(MailDomain d)
    {
        var k = d.Dkim;
        if (d.Parked && k is not { Enabled: true })
            return new("Signing (DKIM)", CheckState.Info, "Not needed", $"{d.Name} doesn't receive mail and needn't sign any; SPF and DMARC saying it sends none is enough.");
        if (k is null)
            return new("Signing (DKIM)", CheckState.Problem, "Not set up",
                $"Mail from {d.Name} isn't signed with its own domain. Set up DKIM: Tenantry creates the keys, then you publish two CNAME records.");
        if (k.Enabled && !k.CnamesMissing)
            return k.KeySize is { } size and < 2048
                ? new("Signing (DKIM)", CheckState.Warning, $"Signing ({size}-bit key)",
                    $"Mail from {d.Name} is signed, with a {size}-bit key. Microsoft recommends 2048 bits: Rotate to a 2048-Bit Key (the new key takes over after about four days).")
                : new("Signing (DKIM)", CheckState.Good, "Signing", $"Mail from {d.Name} is signed with its own domain.");
        var missing = DnsMissing(d);
        return missing.Count > 0
            ? new("Signing (DKIM)", CheckState.Problem, "Off: CNAME records missing",
                $"Publish the {(missing.Count == 1 ? "CNAME record" : "two CNAME records")} below in {d.Name}'s DNS, wait for them to show (up to a day), then turn signing on.",
                string.Join("\n", missing.Select(m => $"{m.Name}  CNAME  {m.Target}")), null, "CNAME")
            : new("Signing (DKIM)", CheckState.Warning, "Off (records published)", "The CNAME records are in DNS: signing can be turned on.");
    }

    /// <summary>The DKIM CNAME records DNS doesn't have (or has pointing somewhere else).</summary>
    public static IReadOnlyList<(string Name, string Target)> DnsMissing(MailDomain d)
    {
        var missing = new List<(string, string)>();
        if (d.Dkim?.Selector1Target is { Length: > 0 } t1 && !Same(d.Dns.Selector1, t1))
            missing.Add(($"selector1._domainkey.{d.Name}", t1));
        if (d.Dkim?.Selector2Target is { Length: > 0 } t2 && !Same(d.Dns.Selector2, t2))
            missing.Add(($"selector2._domainkey.{d.Name}", t2));
        return missing;
    }

    static bool Same(string? found, string wanted) =>
        found is not null && string.Equals(found.TrimEnd('.'), wanted.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Both CNAMEs are where Exchange wants them, so turning signing on will be accepted.</summary>
    public static bool CanEnableDkim(MailDomain d) => d.Dkim is { Enabled: false } && DnsMissing(d).Count == 0;

    static DomainCheck Dmarc(MailDomain d)
    {
        var name = $"_dmarc.{d.Name}";
        var suggested = d.Parked ? "v=DMARC1; p=reject;" : "v=DMARC1; p=none; rua=mailto:dmarc-reports@" + d.Name;
        if (d.Dns.Dmarc.Count == 0)
            return new("What Receivers Do (DMARC)", CheckState.Problem, "Missing",
                $"{d.Name} has no DMARC policy, so receivers decide for themselves what to do with mail that fails SPF and DKIM, and big mailbox providers may refuse its bulk mail."
                + " Start with p=none to watch, then move to quarantine or reject.", suggested, name, "TXT");
        var dmarc = d.Dns.Dmarc[0];
        var policy = Tag(dmarc, "p")?.ToLowerInvariant();
        return policy switch
        {
            "reject" => new("What Receivers Do (DMARC)", CheckState.Good, "Reject", $"Mail that fails is refused. ({dmarc})"),
            "quarantine" => new("What Receivers Do (DMARC)", CheckState.Good, "Quarantine", $"Mail that fails goes to junk. ({dmarc})"),
            "none" => new("What Receivers Do (DMARC)", CheckState.Warning, "Watching only (p=none)",
                $"Receivers report failures but deliver them anyway. Once the reports look clean, move to p=quarantine, then p=reject. ({dmarc})"),
            _ => new("What Receivers Do (DMARC)", CheckState.Problem, "Unreadable", $"The record has no valid p= policy. ({dmarc})", suggested, name, "TXT"),
        };
    }

    static string? Tag(string record, string tag) =>
        record.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))?[1].Trim();
}

/// <summary>DNS lookups for mail records (the system's resolvers).</summary>
public interface IMailDnsLookup
{
    Task<MailDns> LookupAsync(string domain, CancellationToken cancellationToken = default);
}

/// <summary>Blocked senders and the organization's mail domains (Exchange plus DNS).</summary>
public interface IMailDomainRecords
{
    Task<IReadOnlyList<BlockedSender>> GetBlockedSendersAsync(CancellationToken cancellationToken = default);

    /// <summary>Every accepted domain, with DKIM and DNS read for each.</summary>
    Task<IReadOnlyList<MailDomain>> GetMailDomainsAsync(CancellationToken cancellationToken = default);

    /// <summary>The accepted domain names only (cheap), to tell the organization's own addresses from others.</summary>
    Task<IReadOnlyList<string>> GetAcceptedDomainNamesAsync(CancellationToken cancellationToken = default);
}
