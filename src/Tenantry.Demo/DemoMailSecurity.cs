using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.MailFlow;

namespace Tenantry.Demo;

/// <summary>
/// The demo's quarantine and organization-wide message trace: made-up spam, phishing and malware
/// from example domains to the demo's people. Releasing and deleting change it in memory.
/// </summary>
public sealed partial class DemoUserRecords : IMailSecurity
{
    static readonly (string Subject, string Reason, string Policy)[] Held =
    [
        ("Your mailbox is almost full: verify now", "Phish", "Default anti-phishing"),
        ("Invoice 4471 overdue", "HighConfPhish", "Default anti-phishing"),
        ("You've won a gift card!", "Spam", "Default"),
        ("Weekly deals just for you", "Bulk", "Default"),
        ("Scanned document", "Malware", "Default anti-malware"),
        ("RE: Wire transfer details", "HighConfPhish", "Default anti-phishing"),
        ("Conference speaker invitation", "Spam", "Default"),
        ("Password expires today", "Phish", "Default anti-phishing"),
        ("Shipment notification", "Malware", "Default anti-malware"),
        ("Exclusive offer ends tonight", "HighConfSpam", "Default"),
    ];

    static readonly string[] Senders = ["alerts@secure-mail.example", "billing@invoices.example", "promo@deals.example", "no-reply@shipping.example", "events@conference.example"];

    List<QuarantinedMessage>? _quarantine;

    async Task<List<QuarantinedMessage>> QuarantineAsync(CancellationToken ct)
    {
        if (_quarantine is not null)
            return _quarantine;
        var people = (await directory.GetUsersAsync(ct)).Where(u => u.Mailbox is not null).Take(40).ToArray();
        var random = new Random(7);
        var now = DateTimeOffset.UtcNow;
        _quarantine = Enumerable.Range(0, 64).Select(i =>
        {
            var (subject, reason, policy) = Held[random.Next(Held.Length)];
            var received = now.AddMinutes(-random.Next(30 * 24 * 60));
            var to = people[random.Next(people.Length)].Mailbox!.PrimaryAddress ?? "";
            return new QuarantinedMessage($"demo-q-{i}", received, Senders[random.Next(Senders.Length)], [to], subject, random.Next(8_000, 400_000),
                reason, policy, received.AddDays(30), random.NextDouble() < 0.1 ? "REQUESTED" : random.NextDouble() < 0.1 ? "RELEASED" : "NOTRELEASED", "Inbound");
        }).ToList();
        return _quarantine;
    }

    public async Task<IReadOnlyList<QuarantinedMessage>> GetQuarantineAsync(int days, CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        return (await QuarantineAsync(cancellationToken)).Where(m => m.Received >= since).OrderByDescending(m => m.Received).ToArray();
    }

    public async Task<QuarantinePreview> PreviewQuarantinedAsync(string identity, CancellationToken cancellationToken = default)
    {
        var m = (await QuarantineAsync(cancellationToken)).First(x => x.Identity == identity);
        var body = m.Reason switch
        {
            "Phish" or "HighConfPhish" => "<p>Dear user,</p><p>Your account will be suspended. <a href=\"https://login-verify.example/reset\">Verify your details</a> within 24 hours.</p>",
            "Malware" => "<p>Please see the attached scanned document.</p>",
            _ => "<p>Don't miss out! Click <a href=\"https://deals.example/offer\">here</a> for today's offer.</p>",
        };
        return new QuarantinePreview(m.Subject, m.Sender, string.Join(", ", m.Recipients), m.Received, body, true,
            m.Reason == "Malware" ? ["Scan_0423.zip"] : []);
    }

    public async Task<IReadOnlyList<TracedMessage>> TraceAsync(TraceSearch search, CancellationToken cancellationToken = default)
    {
        var people = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Mailbox is not null).Take(60).ToArray();
        var random = new Random(StableSeed($"{search.Start:yyyyMMdd}{search.Sender}{search.Recipient}{search.Subject}"));
        string[] statuses = ["Delivered", "Delivered", "Delivered", "Delivered", "Delivered", "FilteredAsSpam", "Quarantined", "Failed", "Pending"];
        var span = Math.Max(1, (search.End - search.Start).TotalMinutes);
        var messages = Enumerable.Range(0, 300).Select(i =>
        {
            var inside = people[random.Next(people.Length)].Mailbox!.PrimaryAddress ?? "";
            var outside = $"{(char)('a' + random.Next(26))}.{(char)('a' + random.Next(26))}@{(random.NextDouble() < 0.5 ? "partner.example" : "mail.example")}";
            var inbound = random.NextDouble() < 0.6;
            return new TracedMessage($"demo-t-{i}", search.Start.AddMinutes(random.NextDouble() * span),
                search.Sender ?? (inbound ? outside : inside), search.Recipient ?? (inbound ? inside : outside),
                search.Subject is { } s ? $"{s} {i}" : Subjects[random.Next(Subjects.Length)],
                search.Status ?? statuses[random.Next(statuses.Length)], random.Next(4_000, 900_000));
        });
        return messages.OrderByDescending(m => m.Received).ToArray();
    }

    List<ListEntry>? _listEntries;

    List<ListEntry> ListEntries()
    {
        if (_listEntries is not null)
            return _listEntries;
        var now = DateTimeOffset.UtcNow;
        _listEntries =
        [
            new("demo-l-1", ListKind.Sender, "deals.example", true, "Constant marketing", null, null, now.AddDays(-2), now.AddDays(-120), now.AddDays(-120), "admin@northgate.example", "Tenant"),
            new("demo-l-2", ListKind.Sender, "billing@invoices.example", true, "Invoice phishing", now.AddDays(25), null, now.AddDays(-1), now.AddDays(-5), now.AddDays(-5), "admin@northgate.example", "Tenant"),
            new("demo-l-3", ListKind.Sender, "newsletter@partner.example", false, "Partner newsletter kept landing in junk", null, 45, now.AddDays(-3), now.AddDays(-40), now.AddDays(-40), "admin@northgate.example", "Tenant"),
            new("demo-l-4", ListKind.Url, "login-verify.example/*", true, "Credential phishing site", null, null, now.AddDays(-6), now.AddDays(-30), now.AddDays(-30), "admin@northgate.example", "Submission"),
            new("demo-l-5", ListKind.Url, "*.tracking-links.example", true, null, now.AddDays(4), null, null, now.AddDays(-26), now.AddDays(-26), "admin@northgate.example", "Tenant"),
            new("demo-l-6", ListKind.File, "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", true, "Scan_0423.zip", null, null, null, now.AddDays(-9), now.AddDays(-9), "admin@northgate.example", "Tenant"),
            new("demo-l-7", ListKind.IP, "2001:db8:4400::/48", true, "Spam source", now.AddDays(12), null, null, now.AddDays(-18), now.AddDays(-18), "admin@northgate.example", "Tenant"),
            new("demo-l-8", ListKind.Spoof, "*.northgate.example", false, null, null, null, now.AddDays(-1), now.AddDays(-200), now.AddDays(-200), "admin@northgate.example", null,
                "bulkmailer.example", "Internal"),
        ];
        return _listEntries;
    }

    public Task<IReadOnlyList<ListEntry>> GetListEntriesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ListEntry>>(ListEntries().ToArray());

    /// <summary>The demo's rules in the order they run, each held as a full draft (every setting), as Exchange would return it.</summary>
    List<(RuleDraft Draft, DateTimeOffset Changed)>? _flowRules;

    List<(RuleDraft Draft, DateTimeOffset Changed)> FlowRules()
    {
        if (_flowRules is not null)
            return _flowRules;
        var now = DateTimeOffset.UtcNow;
        var catalog = RuleCatalog.Instance;
        RuleLine Line(RulePartKind kind, string parameter, JsonNode value, string? location = null, params (string Extra, JsonNode Value)[] extras)
        {
            var def = catalog.For(kind).First(d => d.Parameter == parameter && (location is null || d.Fixed.GetValueOrDefault("ApplyHtmlDisclaimerLocation") == location));
            var line = new RuleLine(def, kind) { Value = value };
            foreach (var (extra, v) in extras)
                line.Extras[extra] = v;
            return line;
        }
        RuleDraft Rule(int number, string name, bool on, RuleMode mode, RuleLine[] conditions, RuleLine[] exceptions, RuleLine[] actions, string? comments = null)
        {
            var draft = new RuleDraft { Identity = $"demo-r-{number}", Name = name, Enabled = on, Mode = mode };
            draft.Conditions.AddRange(conditions);
            draft.Exceptions.AddRange(exceptions);
            draft.Actions.AddRange(actions);
            if (comments is not null)
                draft.Settings["Comments"] = comments;
            return draft;
        }
        const RulePartKind When = RulePartKind.Condition, Except = RulePartKind.Exception, Then = RulePartKind.Action;
        RuleDraft[] rules =
        [
            Rule(0, "External sender warning", true, RuleMode.Enforce,
                [Line(When, "FromScope", "NotInOrganization")], [Line(Except, "SentTo", new JsonArray("billing@northgate.example"))],
                [Line(Then, "PrependSubject", "[EXTERNAL] "),
                 Line(Then, "ApplyHtmlDisclaimerText", "<p>This message came from outside Northgate.</p>", "Prepend", ("ApplyHtmlDisclaimerFallbackAction", "Wrap"))],
                "Helps staff spot phishing."),
            Rule(1, "Block executable attachments", true, RuleMode.Enforce,
                [Line(When, "AttachmentExtensionMatchesWords", new JsonArray("exe", "js", "vbs", "scr"))], [],
                [Line(Then, "RejectMessageReasonText", "Executable attachments aren't allowed.")]),
            Rule(2, "Copy finance mail to auditors", true, RuleMode.Test,
                [Line(When, "SentToMemberOf", new JsonArray("finance@northgate.example")), Line(When, "SubjectOrBodyContainsWords", new JsonArray("invoice", "wire transfer"))], [],
                [Line(Then, "BlindCopyTo", new JsonArray("auditors@northgate.example"))], "Trial: checking matches before enforcing."),
            Rule(3, "Board confidentiality footer", false, RuleMode.Enforce,
                [Line(When, "SentToMemberOf", new JsonArray("board@northgate.example"))], [],
                [Line(Then, "ApplyHtmlDisclaimerText", "Confidential: board members only.", "Append", ("ApplyHtmlDisclaimerFallbackAction", "Wrap"))]),
            Rule(4, "Bypass spam filtering for the payroll provider", true, RuleMode.Enforce,
                [Line(When, "SenderDomainIs", new JsonArray("payroll.example"))], [],
                [Line(Then, "SetSCL", "-1")]),
        ];
        _flowRules = rules.Select((r, i) => (r, now.AddDays(-i * 17 - 3))).ToList();
        return _flowRules;
    }

    static MailFlowRule Summarize(RuleDraft d, int priority, DateTimeOffset changed) =>
        new(d.Identity!, d.Name, priority, d.Enabled, d.Mode,
            d.Conditions.Select(RuleCodec.Describe).ToArray(), d.Exceptions.Select(RuleCodec.Describe).ToArray(), d.Actions.Select(RuleCodec.Describe).ToArray(),
            d.Settings.GetValueOrDefault("Comments")?.ToString(), null, null, changed, "admin@northgate.example");

    public Task<IReadOnlyList<MailFlowRule>> GetRulesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailFlowRule>>(FlowRules().Select((r, i) => Summarize(r.Draft, i, r.Changed)).ToArray());

    public Task<RuleDraft> GetRuleDraftAsync(string identity, CancellationToken cancellationToken = default) =>
        FlowRules().FirstOrDefault(r => r.Draft.Identity == identity) is { Draft: not null } found
            ? Task.FromResult(found.Draft.Clone())
            : Task.FromException<RuleDraft>(new InvalidOperationException("That rule isn't there any more."));

    ApplyResult? ApplyRule(ChangeOp change)
    {
        var rules = FlowRules();
        var at = change is RuleChange rc ? rules.FindIndex(r => r.Draft.Identity == rc.Rule.Id) : -1;
        var now = DateTimeOffset.UtcNow;
        switch (change)
        {
            case SetRuleEnabled e when at >= 0:
                rules[at].Draft.Enabled = e.New;
                break;
            case SetRuleMode m when at >= 0:
                rules[at].Draft.Mode = m.New;
                break;
            case SetRulePriority p when at >= 0:
                var moving = rules[at];
                rules.RemoveAt(at);
                rules.Insert(Math.Clamp(p.New, 0, rules.Count), moving);
                break;
            case RemoveRule when at >= 0:
                rules.RemoveAt(at);
                break;
            case CreateRule c:
                var parameters = (JsonObject)JsonNode.Parse(c.Parameters)!;
                var priority = parameters["Priority"]?.GetValue<int>() ?? 0;
                parameters["Name"] = c.Rule.Name;
                parameters["Guid"] = $"demo-r-{Guid.NewGuid():N}";
                parameters["State"] = parameters["Enabled"]?.GetValue<bool>() == false ? "Disabled" : "Enabled";
                using (var document = JsonDocument.Parse(parameters.ToJsonString()))
                    rules.Insert(Math.Clamp(priority, 0, rules.Count), (RuleCodec.Read(document.RootElement.Clone()), now));
                break;
            case UpdateRule u when at >= 0:
                var changes = (JsonObject)JsonNode.Parse(u.Changes)!;
                rules[at] = (RuleCodec.Apply(rules[at].Draft, changes), now);
                break;
            case RuleChange:
                return new ApplyResult(ChangeOutcome.Failed, "That rule isn't there any more.");
            default:
                return null;
        }
        return new ApplyResult(ChangeOutcome.Applied);
    }

    /// <summary>Applies a quarantine change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyMailSecurity(ChangeOp change)
    {
        if (change is RuleChange)
            return ApplyRule(change);
        switch (change)
        {
            case ReleaseQuarantined r when _quarantine is not null:
                var i = _quarantine.FindIndex(m => m.Identity == r.Message.Id);
                if (i >= 0)
                    _quarantine[i] = _quarantine[i] with { ReleaseStatus = "RELEASED" };
                return new ApplyResult(ChangeOutcome.Applied);
            case DeleteQuarantined d when _quarantine is not null:
                _quarantine.RemoveAll(m => m.Identity == d.Message.Id);
                return new ApplyResult(ChangeOutcome.Applied);
            case SetSenderListed:
                return new ApplyResult(ChangeOutcome.Applied);
            case AddListEntry a:
                ListEntries().Add(new ListEntry($"demo-l-{Guid.NewGuid():N}", a.Kind, a.Entry.Name, a.Block, a.Notes, a.Expiry.On, a.Expiry.AfterLastUseDays,
                    null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "admin@northgate.example", "Tenant", a.Infrastructure, a.SpoofType));
                return new ApplyResult(ChangeOutcome.Applied);
            case RemoveListEntry r:
                ListEntries().RemoveAll(e => e.Identity == r.Identity);
                return new ApplyResult(ChangeOutcome.Applied);
            case UpdateListEntry u:
                var at = ListEntries().FindIndex(e => e.Identity == u.Identity);
                if (at >= 0)
                    _listEntries![at] = _listEntries[at] with { Notes = u.NewNotes, Expires = u.NewExpiry.On, RemoveAfterDays = u.NewExpiry.AfterLastUseDays };
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }
}
