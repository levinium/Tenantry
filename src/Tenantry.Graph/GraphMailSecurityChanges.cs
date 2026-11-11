using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Core.MailFlow;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>Mail &amp; security changes (5a): releasing or deleting quarantined mail, blocking or allowing senders.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>Microsoft lets an allowed sender stay this long after its last use; blocks have no end.</summary>
    public const int AllowedSenderDays = 45;

    Task<IReadOnlyList<JsonElement>> ReadQuarantined(string identity, CancellationToken ct) =>
        Read("Get-QuarantineMessage", new() { ["Identity"] = identity }, ct);

    /// <summary>
    /// Everyone (-ReleaseToAll) or chosen recipients (-User): Exchange refuses both together.
    /// Approving a release request is the same call.
    /// </summary>
    internal static Dictionary<string, object?> ReleaseParameters(ReleaseQuarantined change)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.Message.Id, ["Confirm"] = false };
        if (change.To is { Count: > 0 } to)
            parameters["User"] = to.ToArray();
        else
            parameters["ReleaseToAll"] = true;
        if (change.AllowSender)
            parameters["AllowSender"] = true;
        if (change.ReportFalsePositive)
            parameters["ReportFalsePositive"] = true;
        return parameters;
    }

    Task<ApplyResult> ReleaseQuarantinedAsync(ReleaseQuarantined change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Release-QuarantineMessage", ReleaseParameters(change),
            ct => ReadQuarantined(change.Message.Id, ct),
            rows => rows.Count == 1 && Released(rows[0], change.To),
            cancellationToken);

    /// <summary>Released to everyone, or (for chosen recipients) to each of them.</summary>
    static bool Released(JsonElement row, IReadOnlyList<string>? to)
    {
        var status = row.TryGetProperty("ReleaseStatus", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
        if (to is not { Count: > 0 })
            return status.Equals("RELEASED", StringComparison.OrdinalIgnoreCase);
        var done = row.TryGetProperty("ReleasedUser", out var users) && users.ValueKind == JsonValueKind.Array
            ? users.EnumerateArray().Select(u => u.GetString() ?? "").ToArray() : [];
        return status.Equals("RELEASED", StringComparison.OrdinalIgnoreCase) || to.All(t => done.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    // ---- Mail domains and blocked senders (12c) ------------------------------------------------

    internal static (string Cmdlet, Dictionary<string, object?> Parameters) DkimCall(DkimChange change) => change switch
    {
        CreateDkimKeys c => ("New-DkimSigningConfig", new() { ["DomainName"] = c.Domain.Id, ["Enabled"] = false, ["KeySize"] = 2048 }),
        SetDkimSigning s => ("Set-DkimSigningConfig", new() { ["Identity"] = s.Domain.Id, ["Enabled"] = s.New }),
        RotateDkimKeys r => ("Rotate-DkimSigningConfig", new() { ["Identity"] = r.Domain.Id, ["KeySize"] = r.KeySize }),
        _ => throw new NotSupportedException(change.GetType().Name),
    };

    Task<ApplyResult> DkimAsync(DkimChange change, CancellationToken cancellationToken)
    {
        var (cmdlet, parameters) = DkimCall(change);
        return RunCheckedAsync(cmdlet, parameters, ct => Read("Get-DkimSigningConfig", new() { ["Identity"] = change.Domain.Id }, ct),
            rows => change switch
            {
                SetDkimSigning s => rows.Count == 1 && rows[0].TryGetProperty("Enabled", out var e) && (e.ValueKind == JsonValueKind.True) == s.New,
                _ => rows.Count == 1,
            }, cancellationToken);
    }

    internal static Dictionary<string, object?> UnblockParameters(UnblockSender change)
    {
        var parameters = new Dictionary<string, object?> { ["SenderAddress"] = change.Sender.Id };
        if (change.Reason is { Length: > 0 } reason)
            parameters["Reason"] = reason;
        return parameters;
    }

    Task<ApplyResult> UnblockSenderAsync(UnblockSender change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Remove-BlockedSenderAddress", UnblockParameters(change),
            ct => Read("Get-BlockedSenderAddress", new() { ["SenderAddress"] = change.Sender.Id }, ct),
            rows => rows.Count == 0, cancellationToken);

    // ---- Exchange settings, remote domains, journal rules (12e) ----------------------------------

    internal static Dictionary<string, object?> SettingParameters(SetExchangeSetting change)
    {
        var parameters = new Dictionary<string, object?> { [change.Parameter] = change.ExchangeValue?.DeepClone() };
        if (change.Identity is { } identity)
            parameters["Identity"] = identity;
        // Set-OrganizationConfig and friends take no Identity and ask nothing; the rest need confirming off.
        if (change.SetCmdlet is "Set-RemoteDomain" or "Set-InboundConnector" or "Set-OutboundConnector" or "Set-AcceptedDomain")
            parameters["Confirm"] = false;
        return parameters;
    }

    Task<ApplyResult> SetExchangeSettingAsync(SetExchangeSetting change, CancellationToken cancellationToken) =>
        RunCheckedAsync(change.SetCmdlet, SettingParameters(change),
            ct => Read(change.GetCmdlet, change.Identity is { } id ? new() { ["Identity"] = id } : new(), ct),
            rows => rows.Count >= 1 && rows[0].TryGetProperty(change.Parameter, out var v) && SameValue(v, change.ExchangeValue),
            cancellationToken);

    static bool SameValue(JsonElement read, JsonNode? sent) => sent switch
    {
        null => read.ValueKind == JsonValueKind.Null,
        JsonValue value when value.TryGetValue<bool>(out var b) => read.ValueKind == (b ? JsonValueKind.True : JsonValueKind.False),
        JsonValue value when value.TryGetValue<int>(out var n) => read.ValueKind == JsonValueKind.Number && read.GetInt32() == n,
        JsonValue value when value.TryGetValue<string>(out var s) => read.ValueKind == JsonValueKind.String && string.Equals(read.GetString(), s, StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    Task<ApplyResult> CreateRemoteDomainAsync(CreateRemoteDomain change, CancellationToken cancellationToken) =>
        RunCheckedAsync("New-RemoteDomain", new() { ["Name"] = change.Domain.Name, ["DomainName"] = change.DomainName },
            ct => Read("Get-RemoteDomain", new() { ["Identity"] = change.Domain.Name }, ct), rows => rows.Count == 1, cancellationToken);

    Task<ApplyResult> RemoveRemoteDomainAsync(RemoveRemoteDomain change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Remove-RemoteDomain", new() { ["Identity"] = change.Domain.Name, ["Confirm"] = false },
            ct => Read("Get-RemoteDomain", new(), ct), rows => !rows.Any(r => string.Equals(GraphUserRecords.StrOf(r, "Name"), change.Domain.Name, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);

    Task<ApplyResult> SetJournalRuleEnabledAsync(SetJournalRuleEnabled change, CancellationToken cancellationToken) =>
        RunCheckedAsync(change.New ? "Enable-JournalRule" : "Disable-JournalRule", new() { ["Identity"] = change.Rule.Name, ["Confirm"] = false },
            ct => Read("Get-JournalRule", new() { ["Identity"] = change.Rule.Name }, ct),
            rows => rows.Count == 1 && rows[0].TryGetProperty("Enabled", out var e) && (e.ValueKind == JsonValueKind.True) == change.New, cancellationToken);

    internal static Dictionary<string, object?> JournalParameters(CreateJournalRule change)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Name"] = change.Rule.Name, ["JournalEmailAddress"] = change.JournalAddress, ["Scope"] = change.Scope, ["Enabled"] = true,
        };
        if (change.Recipient is { Length: > 0 } who)
            parameters["Recipient"] = who;
        return parameters;
    }

    Task<ApplyResult> CreateJournalRuleAsync(CreateJournalRule change, CancellationToken cancellationToken) =>
        RunCheckedAsync("New-JournalRule", JournalParameters(change),
            ct => Read("Get-JournalRule", new() { ["Identity"] = change.Rule.Name }, ct), rows => rows.Count == 1, cancellationToken);

    Task<ApplyResult> RemoveJournalRuleAsync(RemoveJournalRule change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Remove-JournalRule", new() { ["Identity"] = change.Rule.Name, ["Confirm"] = false },
            ct => Read("Get-JournalRule", new(), ct), rows => !rows.Any(r => string.Equals(GraphUserRecords.StrOf(r, "Name"), change.Rule.Name, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);

    // ---- Threat policies (12f) ---------------------------------------------------------------------

    /// <summary>The policy, then the rule naming it and who it covers (as the portal makes both).</summary>
    internal static IReadOnlyList<(string Cmdlet, Dictionary<string, object?> Parameters)> CreatePolicyCalls(CreateThreatPolicy change)
    {
        var rule = new Dictionary<string, object?> { ["Name"] = change.Policy.Name, [change.PolicyNoun] = change.Policy.Name };
        foreach (var (parameter, values) in change.WhoParameters.Zip(new[] { change.People, change.Groups, change.Domains }))
            if (values.Count > 0)
                rule[parameter] = values.ToArray();
        return [("New-" + change.PolicyNoun, new() { ["Name"] = change.Policy.Name }), ("New-" + change.RuleNoun, rule)];
    }

    async Task<ApplyResult> CreateThreatPolicyAsync(CreateThreatPolicy change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        var calls = CreatePolicyCalls(change);
        for (var i = 0; i < calls.Count; i++)
        {
            try
            {
                await exchange.InvokeAsync(CmdletEndpoint.Exchange, calls[i].Cmdlet, calls[i].Parameters, cancellationToken);
            }
            catch (CmdletException e)
            {
                return new ApplyResult(i == 0 ? ChangeOutcome.Failed : ChangeOutcome.AppliedNotYetVisible,
                    i == 0 ? $"{e.Explanation}" : $"The policy was made but its rule wasn't: {e.Explanation} Remove the policy, or add who it covers, and try again.");
            }
        }
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            try
            {
                if ((await Read("Get-" + change.RuleNoun, new() { ["Identity"] = change.Policy.Name }, cancellationToken)).Count == 1)
                    return new ApplyResult(ChangeOutcome.Applied);
            }
            catch (CmdletException)
            {
                // Not listed yet: Exchange answers "couldn't be found" until it is.
            }
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Exchange made it but doesn't list it yet; check again in a minute.");
    }

    /// <summary>The rule first, then the policy: Exchange keeps either if only the other goes.</summary>
    internal static IReadOnlyList<(string Cmdlet, Dictionary<string, object?> Parameters)> RemovePolicyCalls(RemoveThreatPolicy change) =>
    [
        .. change.HasRule ? new[] { ("Remove-" + change.RuleNoun, new Dictionary<string, object?> { ["Identity"] = change.Policy.Name, ["Confirm"] = false }) } : [],
        ("Remove-" + change.PolicyNoun, new Dictionary<string, object?> { ["Identity"] = change.Policy.Name, ["Confirm"] = false }),
    ];

    async Task<ApplyResult> RemoveThreatPolicyAsync(RemoveThreatPolicy change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        foreach (var (cmdlet, parameters) in RemovePolicyCalls(change))
        {
            try
            {
                await exchange.InvokeAsync(CmdletEndpoint.Exchange, cmdlet, parameters, cancellationToken);
            }
            catch (CmdletException e)
            {
                return new ApplyResult(ChangeOutcome.Failed, $"{cmdlet} failed: {e.Explanation}");
            }
        }
        var left = await Read("Get-" + change.PolicyNoun, new(), cancellationToken);
        return left.Any(r => string.Equals(GraphUserRecords.StrOf(r, "Name"), change.Policy.Name, StringComparison.OrdinalIgnoreCase))
            ? new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Exchange took it but still lists the policy; check again in a minute.")
            : new ApplyResult(ChangeOutcome.Applied);
    }

    Task<ApplyResult> SetThreatRuleEnabledAsync(SetThreatRuleEnabled change, CancellationToken cancellationToken) =>
        RunCheckedAsync((change.New ? "Enable-" : "Disable-") + change.RuleNoun, new() { ["Identity"] = change.Policy.Name, ["Confirm"] = false },
            ct => Read("Get-" + change.RuleNoun, new() { ["Identity"] = change.Policy.Name }, ct),
            rows => rows.Count == 1 && string.Equals(GraphUserRecords.StrOf(rows[0], "State"), change.New ? "Enabled" : "Disabled", StringComparison.OrdinalIgnoreCase),
            cancellationToken);

    Task<ApplyResult> SetQuarantinePermissionsAsync(SetQuarantinePermissions change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Set-QuarantinePolicy", new() { ["Identity"] = change.QuarantinePolicy.Name, ["EndUserQuarantinePermissionsValue"] = change.New },
            ct => Read("Get-QuarantinePolicy", new() { ["Identity"] = change.QuarantinePolicy.Name }, ct),
            rows => rows.Count == 1 && QuarantinePermissions.Value(QuarantinePermissions.Parse(GraphUserRecords.StrOf(rows[0], "EndUserQuarantinePermissions") ?? "")) == change.New,
            cancellationToken);

    // ---- Alerts ----------------------------------------------------------------------------------

    internal static Dictionary<string, object?> AlertPolicyParameters(SetAlertPolicy change) => new()
    {
        ["Identity"] = change.Identity, [change.Parameter] = change.Value?.DeepClone(),
    };

    /// <summary>Alert policies live in Security &amp; Compliance: the same REST runner, its other endpoint.</summary>
    async Task<ApplyResult> SetAlertPolicyAsync(SetAlertPolicy change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        try
        {
            await exchange.InvokeAsync(CmdletEndpoint.Compliance, "Set-ProtectionAlert", AlertPolicyParameters(change), cancellationToken);
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Explanation);
        }
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var rows = await exchange.InvokeAsync(CmdletEndpoint.Compliance, "Get-ProtectionAlert", new Dictionary<string, object?> { ["Identity"] = change.Identity }, cancellationToken);
            if (rows.Count == 1 && rows[0].TryGetProperty(change.Parameter, out var v) && SameValue(v, change.Value))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Security & Compliance took it but still shows the old setting; check again in a minute.");
    }

    internal static System.Text.Json.Nodes.JsonObject AlertUpdateBody(UpdateSecurityAlert change)
    {
        var body = new System.Text.Json.Nodes.JsonObject { ["status"] = change.Status };
        if (change.Classification is { } c)
            body["classification"] = c;
        if (change.Determination is { } d)
            body["determination"] = d;
        return body;
    }

    async Task<ApplyResult> UpdateSecurityAlertAsync(UpdateSecurityAlert change, CancellationToken cancellationToken)
    {
        var path = $"security/alerts_v2/{change.Alert.Id}";
        await graph.PatchAsync(path, AlertUpdateBody(change), cancellationToken);
        if (change.Comment is { Length: > 0 } comment)
            await graph.PostAsync($"{path}/comments", new System.Text.Json.Nodes.JsonObject { ["comment"] = comment }, cancellationToken);
        var after = await graph.GetAsync(path, cancellationToken);
        return string.Equals(GraphUserRecords.StrOf(after, "status"), change.Status, StringComparison.OrdinalIgnoreCase)
            ? new ApplyResult(ChangeOutcome.Applied)
            : new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Microsoft took it but still shows the old status.");
    }

    // ---- Where reports go, and answering a report (12d) ---------------------------------------

    internal const string ReportPolicy = "DefaultReportSubmissionPolicy";
    internal const string ReportRule = "DefaultReportSubmissionRule";

    /// <summary>
    /// The policy's settings for a mode, as Microsoft's portal writes them: Microsoft on or off, the
    /// three report kinds to the mailbox or not (with its address), and the pop-ups. "Mailbox
    /// only" needs reporters notified; "off" needs both pop-ups on (Microsoft's rules).
    /// </summary>
    internal static Dictionary<string, object?> RoutingParameters(ReportRouting r)
    {
        var toMailbox = r.UsesMailbox;
        var addresses = toMailbox && r.Mailbox is { } m ? new[] { m.Trim() } : null;
        return new Dictionary<string, object?>
        {
            ["Identity"] = ReportPolicy,
            ["EnableReportToMicrosoft"] = r.Mode is ReportMode.MicrosoftAndMailbox or ReportMode.MicrosoftOnly,
            ["ReportJunkToCustomizedAddress"] = toMailbox, ["ReportNotJunkToCustomizedAddress"] = toMailbox, ["ReportPhishToCustomizedAddress"] = toMailbox,
            ["ReportJunkAddresses"] = addresses, ["ReportNotJunkAddresses"] = addresses, ["ReportPhishAddresses"] = addresses,
            ["EnableThirdPartyAddress"] = false,
            ["EnableUserEmailNotification"] = r.NotifyReporters || r.Mode == ReportMode.MailboxOnly,
            ["PreSubmitMessageEnabled"] = r.AskFirst || r.Mode == ReportMode.Off,
            ["PostSubmitMessageEnabled"] = r.ThankAfter || r.Mode == ReportMode.Off,
        };
    }

    /// <summary>Every call the new settings take, in order: the policy (made first if missing), then the rule that names the mailbox.</summary>
    internal static IReadOnlyList<(string Cmdlet, Dictionary<string, object?> Parameters)> RoutingCalls(SetReportRouting change, bool policyExists, bool ruleExists)
    {
        var calls = new List<(string, Dictionary<string, object?>)>();
        if (!policyExists)
            calls.Add(("New-ReportSubmissionPolicy", new()));
        calls.Add(("Set-ReportSubmissionPolicy", RoutingParameters(change.New)));
        if (change.New.UsesMailbox && change.New.Mailbox is { } mailbox)
            calls.Add(ruleExists
                ? ("Set-ReportSubmissionRule", new() { ["Identity"] = ReportRule, ["SentTo"] = new[] { mailbox.Trim() } })
                : ("New-ReportSubmissionRule", new() { ["Name"] = ReportRule, ["ReportSubmissionPolicy"] = ReportPolicy, ["SentTo"] = new[] { mailbox.Trim() } }));
        else if (ruleExists)
            calls.Add(("Remove-ReportSubmissionRule", new() { ["Identity"] = ReportRule, ["Confirm"] = false }));
        return calls;
    }

    async Task<ApplyResult> SetReportRoutingAsync(SetReportRouting change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        if (change.New.Problem is { } problem)
            return new ApplyResult(ChangeOutcome.Failed, problem);
        // Read what's there now: the policy and rule may not exist yet, or may have changed since.
        var policyExists = (await Read("Get-ReportSubmissionPolicy", new(), cancellationToken)).Count > 0;
        var ruleExists = (await Read("Get-ReportSubmissionRule", new(), cancellationToken)).Count > 0;
        foreach (var (cmdlet, parameters) in RoutingCalls(change, policyExists, ruleExists))
        {
            try
            {
                await exchange.InvokeAsync(CmdletEndpoint.Exchange, cmdlet, parameters, cancellationToken);
            }
            catch (CmdletException e)
            {
                return new ApplyResult(ChangeOutcome.Failed, $"{cmdlet} failed: {e.Explanation}");
            }
        }
        var after = GraphUserRecords.ToRouting((await Read("Get-ReportSubmissionPolicy", new(), cancellationToken)).FirstOrDefault(),
            (await Read("Get-ReportSubmissionRule", new(), cancellationToken)).FirstOrDefault());
        return after.Mode == change.New.Mode && (!change.New.UsesMailbox || string.Equals(after.Mailbox, change.New.Mailbox?.Trim(), StringComparison.OrdinalIgnoreCase))
            ? new ApplyResult(ChangeOutcome.Applied)
            : new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Exchange took the settings but still shows the old ones; check again in a minute.");
    }

    async Task<ApplyResult> ReviewSubmissionAsync(ReviewSubmission change, CancellationToken cancellationToken)
    {
        await graph.PostAsync($"{GraphUserRecords.Submissions}emailThreats/{change.SubmissionId}/review", new JsonObject { ["category"] = change.Verdict }, cancellationToken);
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var row = await graph.GetAsync($"{GraphUserRecords.Submissions}emailThreats/{change.SubmissionId}", cancellationToken);
            if (row.TryGetProperty("adminReview", out var review) && review.ValueKind == JsonValueKind.Object)
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Microsoft took the review but doesn't show it yet.");
    }

    /// <summary>Deny is only in the bulk form, which needs a placeholder Identity (Exchange ignores its value).</summary>
    internal static Dictionary<string, object?> DenyParameters(DenyQuarantineRelease change) => new()
    {
        ["Identities"] = new[] { change.Message.Id }, ["Identity"] = "000", ["ActionType"] = "Deny", ["Confirm"] = false,
    };

    Task<ApplyResult> DenyQuarantineReleaseAsync(DenyQuarantineRelease change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Release-QuarantineMessage", DenyParameters(change),
            ct => ReadQuarantined(change.Message.Id, ct),
            rows => rows.Count == 1 && rows[0].TryGetProperty("ReleaseStatus", out var s) && s.ValueKind == JsonValueKind.String
                    && s.GetString()!.Equals("DENIED", StringComparison.OrdinalIgnoreCase),
            cancellationToken);

    Task<ApplyResult> DeleteQuarantinedAsync(DeleteQuarantined change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Delete-QuarantineMessage", new() { ["Identity"] = change.Message.Id, ["Confirm"] = false },
            ct => ReadQuarantined(change.Message.Id, ct), rows => rows.Count == 0, cancellationToken);

    static void AddExpiry(Dictionary<string, object?> parameters, ListExpiry expiry)
    {
        if (expiry.On is { } on)
            parameters["ExpirationDate"] = on.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        else if (expiry.AfterLastUseDays is { } days)
            parameters["RemoveAfter"] = days;
        else
            parameters["NoExpiration"] = true;
    }

    Task<IReadOnlyList<JsonElement>> ReadEntry(ListKind kind, string value, CancellationToken ct) => kind == ListKind.Spoof
        ? Read("Get-TenantAllowBlockListSpoofItems", new(), ct)
        : Read("Get-TenantAllowBlockListItems", new() { ["ListType"] = AllowBlock.ListType(kind), ["Entry"] = value }, ct);

    static bool Matches(JsonElement row, ListKind kind, string value) =>
        kind != ListKind.Spoof
        || row.TryGetProperty("SpoofedUser", out var user) && string.Equals(user.GetString(), value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The cmdlet and parameters that add a block or allow entry (spoofed senders have their own cmdlet).</summary>
    static (string Cmdlet, Dictionary<string, object?> Parameters) AddListEntryCall(AddListEntry change)
    {
        var value = change.Entry.Name;
        if (change.Kind == ListKind.Spoof)
            return ("New-TenantAllowBlockListSpoofItems", new()
            {
                ["Identity"] = "Default", ["SpoofedUser"] = value, ["SendingInfrastructure"] = change.Infrastructure,
                ["SpoofType"] = change.SpoofType ?? "External", ["Action"] = change.Block ? "Block" : "Allow",
            });
        var parameters = new Dictionary<string, object?> { ["ListType"] = AllowBlock.ListType(change.Kind), ["Entries"] = new[] { value } };
        parameters[change.Block ? "Block" : "Allow"] = true;
        AddExpiry(parameters, change.Expiry);
        if (change.Notes is { Length: > 0 } notes)
            parameters["Notes"] = notes;
        return ("New-TenantAllowBlockListItems", parameters);
    }

    Task<ApplyResult> AddListEntryAsync(AddListEntry change, CancellationToken cancellationToken)
    {
        var value = change.Entry.Name;
        var (cmdlet, parameters) = AddListEntryCall(change);
        return RunCheckedAsync(cmdlet, parameters, ct => ReadEntry(change.Kind, value, ct),
            rows => rows.Any(r => Matches(r, change.Kind, value) && r.TryGetProperty("Action", out var a)
                                  && string.Equals(a.GetString(), change.Block ? "Block" : "Allow", StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }

    static (string Cmdlet, Dictionary<string, object?> Parameters) RemoveListEntryCall(RemoveListEntry change) => change.Kind == ListKind.Spoof
        ? ("Remove-TenantAllowBlockListSpoofItems", new() { ["Identity"] = "Default", ["Ids"] = new[] { change.Identity } })
        : ("Remove-TenantAllowBlockListItems", new() { ["ListType"] = AllowBlock.ListType(change.Kind), ["Ids"] = new[] { change.Identity } });

    Task<ApplyResult> RemoveListEntryAsync(RemoveListEntry change, CancellationToken cancellationToken)
    {
        var (cmdlet, parameters) = RemoveListEntryCall(change);
        return RunCheckedAsync(cmdlet, parameters, ct => ReadEntry(change.Kind, change.Entry.Name, ct),
            rows => change.Kind == ListKind.Spoof ? !rows.Any(r => Matches(r, change.Kind, change.Entry.Name)) : rows.Count == 0, cancellationToken);
    }

    /// <summary>Set-TenantAllowBlockListItems with only what changed (expiry, notes).</summary>
    static Dictionary<string, object?> UpdateListEntryParameters(UpdateListEntry change)
    {
        var parameters = new Dictionary<string, object?> { ["ListType"] = AllowBlock.ListType(change.Kind), ["Ids"] = new[] { change.Identity } };
        if (change.NewExpiry != change.OldExpiry)
            AddExpiry(parameters, change.NewExpiry);
        if ((change.NewNotes ?? "") != (change.OldNotes ?? ""))
            parameters["Notes"] = change.NewNotes ?? "";
        return parameters;
    }

    Task<ApplyResult> UpdateListEntryAsync(UpdateListEntry change, CancellationToken cancellationToken)
    {
        var parameters = UpdateListEntryParameters(change);
        return RunCheckedAsync("Set-TenantAllowBlockListItems", parameters, ct => ReadEntry(change.Kind, change.Entry.Name, ct),
            rows => rows.Any(r => (r.TryGetProperty("Notes", out var n) ? n.GetString() ?? "" : "") == (change.NewNotes ?? "")),
            cancellationToken);
    }

    Task<IReadOnlyList<JsonElement>> ReadRule(string identity, CancellationToken ct) =>
        Read("Get-TransportRule", new() { ["Identity"] = identity }, ct);

    static bool RuleHas(IReadOnlyList<JsonElement> rows, Func<MailFlowRule, bool> done) =>
        rows.Count == 1 && done(GraphUserRecords.ToMailFlowRule(rows[0]));

    Task<ApplyResult> SetRuleEnabledAsync(SetRuleEnabled change, CancellationToken cancellationToken) =>
        RunCheckedAsync(change.New ? "Enable-TransportRule" : "Disable-TransportRule", new() { ["Identity"] = change.Rule.Id, ["Confirm"] = false },
            ct => ReadRule(change.Rule.Id, ct), rows => RuleHas(rows, r => r.Enabled == change.New), cancellationToken);

    Task<ApplyResult> SetRuleModeAsync(SetRuleMode change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Set-TransportRule", new() { ["Identity"] = change.Rule.Id, ["Mode"] = MailFlowRule.ExchangeMode(change.New) },
            ct => ReadRule(change.Rule.Id, ct), rows => RuleHas(rows, r => r.Mode == change.New), cancellationToken);

    Task<ApplyResult> SetRulePriorityAsync(SetRulePriority change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Set-TransportRule", new() { ["Identity"] = change.Rule.Id, ["Priority"] = change.New },
            ct => ReadRule(change.Rule.Id, ct), rows => RuleHas(rows, r => r.Priority == change.New), cancellationToken);

    /// <summary>
    /// A whole new rule order: the moves it needs, sent one after another (Exchange moves one rule per
    /// call), then the order read back. A move that fails stops the rest and says how far it got.
    /// </summary>
    async Task<ApplyResult> ReorderRulesAsync(ReorderRules change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        var moves = change.Moves();
        for (var i = 0; i < moves.Count; i++)
        {
            // Exchange takes about half a minute for each move (seen live), so say which one is going.
            ApplyProgress.Report(moves.Count == 1
                ? $"Moving \"{Name(moves[i].Identity)}\" (Exchange takes about half a minute)"
                : $"Moving rule {i + 1} of {moves.Count}: \"{Name(moves[i].Identity)}\" (about half a minute each)");
            try
            {
                await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Set-TransportRule",
                    new Dictionary<string, object?> { ["Identity"] = moves[i].Identity, ["Priority"] = moves[i].Priority }, cancellationToken);
            }
            catch (CmdletException e)
            {
                return new ApplyResult(i == 0 ? ChangeOutcome.Failed : ChangeOutcome.AppliedNotYetVisible,
                    $"{(i == 0 ? "Nothing moved" : $"{i} of {moves.Count} moves went through")}; moving \"{Name(moves[i].Identity)}\" failed: {e.Explanation} Refresh the rules to see where they stand.");
            }
        }
        string Name(string identity) => change.New.FirstOrDefault(r => r.Identity == identity)?.Name ?? identity;
        ApplyProgress.Report("Checking the new order");
        var wanted = change.New.Select(r => r.Identity).ToArray();
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            // The rules' identities are their Guids (as GetRulesAsync reads them), so read the same field.
            var rows = await Read("Get-TransportRule", new() { ["ResultSize"] = "Unlimited" }, cancellationToken, "Guid", "Identity", "Priority");
            var order = rows.Select(GraphUserRecords.ToMailFlowRule).OrderBy(r => r.Priority).Select(r => r.Identity).ToArray();
            if (order.SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase))
                return new ApplyResult(ChangeOutcome.Applied, moves.Count == 1 ? "1 move." : $"{moves.Count} moves.");
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Exchange took every move but still shows another order; refresh the rules in a minute.");
    }

    internal static PsScript ReorderScript(ReorderRules change) => change.Moves() is { Count: > 0 } moves
        ? PsScript.Exchange(Ps.Lines([.. moves.Select(m => Ps.Cmdlet("Set-TransportRule",
                new Dictionary<string, object?> { ["Identity"] = m.Identity, ["Priority"] = m.Priority }))]),
            "Exchange moves one rule at a time and renumbers the others each time, so these run in this order.")
        : PsScript.Exchange("# Nothing to move: the rules are already in this order.");

    Task<ApplyResult> RemoveRuleAsync(RemoveRule change, CancellationToken cancellationToken) =>
        RunCheckedAsync("Remove-TransportRule", new() { ["Identity"] = change.Rule.Id, ["Confirm"] = false },
            ct => ReadRule(change.Rule.Id, ct), rows => rows.Count == 0, cancellationToken);

    static Dictionary<string, object?> RuleParameters(string json) =>
        JsonNode.Parse(json) is JsonObject o ? o.ToDictionary(p => p.Key, p => (object?)p.Value?.DeepClone(), StringComparer.OrdinalIgnoreCase) : [];

    static ApplyResult? NotAsAsked(IReadOnlyList<JsonElement> rows, IEnumerable<KeyValuePair<string, JsonNode?>> expected) =>
        rows.Count == 1 && RuleCodec.Mismatches(rows[0], expected) is { Count: > 0 } wrong
            ? new ApplyResult(ChangeOutcome.Failed, $"Exchange saved the rule, but these settings didn't read back as asked: {string.Join(", ", wrong)}.")
            : null;

    static Dictionary<string, object?> CreateRuleParameters(CreateRule change)
    {
        var parameters = RuleParameters(change.Parameters);
        parameters["Name"] = change.Rule.Name;
        return parameters;
    }

    static Dictionary<string, object?> UpdateRuleParameters(UpdateRule change)
    {
        var parameters = RuleParameters(change.Changes);
        parameters["Identity"] = change.Rule.Id;
        return parameters;
    }

    async Task<ApplyResult> CreateRuleAsync(CreateRule change, CancellationToken cancellationToken)
    {
        var parameters = CreateRuleParameters(change);
        var result = await RunCheckedAsync("New-TransportRule", parameters, ct => ReadRule(change.Rule.Name, ct), rows => rows.Count == 1, cancellationToken);
        if (result.Outcome != ChangeOutcome.Applied)
            return result;
        var expected = (JsonObject)JsonNode.Parse(change.Parameters)!;
        expected.Remove("Mode");
        expected.Remove("Enabled");
        return NotAsAsked(await ReadRule(change.Rule.Name, cancellationToken), expected) ?? result;
    }

    async Task<ApplyResult> UpdateRuleAsync(UpdateRule change, CancellationToken cancellationToken)
    {
        var parameters = UpdateRuleParameters(change);
        var result = await RunCheckedAsync("Set-TransportRule", parameters, ct => ReadRule(change.Rule.Id, ct), rows => rows.Count == 1, cancellationToken);
        if (result.Outcome != ChangeOutcome.Applied)
            return result;
        var expected = (JsonObject)JsonNode.Parse(change.Changes)!;
        expected.Remove("Mode");
        return NotAsAsked(await ReadRule(change.Rule.Id, cancellationToken), expected) ?? result;
    }

    static Dictionary<string, object?> SenderListedParameters(SetSenderListed change)
    {
        var parameters = new Dictionary<string, object?> { ["ListType"] = "Sender", ["Entries"] = new[] { change.Sender.Address ?? change.Sender.Id } };
        parameters[change.Block ? "Block" : "Allow"] = true;
        if (change.Block)
            parameters["NoExpiration"] = true;
        else
            parameters["RemoveAfter"] = change.RemoveAfterDays ?? AllowedSenderDays;
        if (change.Notes is { Length: > 0 } notes)
            parameters["Notes"] = notes;
        return parameters;
    }

    Task<ApplyResult> SetSenderListedAsync(SetSenderListed change, CancellationToken cancellationToken)
    {
        var entry = change.Sender.Address ?? change.Sender.Id;
        return RunCheckedAsync("New-TenantAllowBlockListItems", SenderListedParameters(change),
            ct => Read("Get-TenantAllowBlockListItems", new() { ["ListType"] = "Sender", ["Entry"] = entry }, ct),
            rows => rows.Any(r => r.TryGetProperty("Action", out var a) && a.ValueKind == JsonValueKind.String
                                  && a.GetString()!.Equals(change.Block ? "Block" : "Allow", StringComparison.OrdinalIgnoreCase)),
            cancellationToken);
    }
}
