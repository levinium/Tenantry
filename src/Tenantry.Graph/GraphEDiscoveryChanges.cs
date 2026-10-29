using System.Text.Json;
using Tenantry.Core.Changes;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>
/// eDiscovery changes (8f) through Purview's cmdlets: cases, their members, searches (run again
/// after each change, to count what they find) and holds (a hold policy plus its rule).
/// </summary>
public sealed partial class GraphChangeExecutor
{
    Task<ApplyResult> ApplyEDiscoveryAsync(EdChange change, CancellationToken cancellationToken) => change switch
    {
        CreateCase c => Purview("New-ComplianceCase", With(new() { ["Name"] = c.Case.Name }, "Description", c.Description),
            ct => ReadPurview("Get-ComplianceCase", new() { ["Identity"] = c.Case.Name }, ct), rows => rows.Count == 1, cancellationToken),
        SetCaseClosed s => Purview("Set-ComplianceCase", new() { ["Identity"] = s.Case.Name, [s.Close ? "Close" : "Reopen"] = true },
            ct => ReadPurview("Get-ComplianceCase", new() { ["Identity"] = s.Case.Name }, ct),
            rows => rows.Count == 1 && string.Equals(rows[0].TryGetProperty("Status", out var st) ? st.GetString() : null, s.Close ? "Closed" : "Active",
                StringComparison.OrdinalIgnoreCase), cancellationToken),
        SetCaseDescription d => Purview("Set-ComplianceCase", new() { ["Identity"] = d.Case.Name, ["Description"] = d.New ?? "" },
            ct => ReadPurview("Get-ComplianceCase", new() { ["Identity"] = d.Case.Name }, ct),
            rows => rows.Count == 1 && (rows[0].TryGetProperty("Description", out var v) ? v.GetString() ?? "" : "") == (d.New ?? ""), cancellationToken),
        DeleteCase d => DeleteCaseAsync(d, cancellationToken),
        SetCaseMember m => Purview(m.Add ? "Add-ComplianceCaseMember" : "Remove-ComplianceCaseMember",
            new() { ["Case"] = m.Case.Name, ["Member"] = EdChange.Place(m.Member), ["Confirm"] = false },
            ct => ReadPurview("Get-ComplianceCaseMember", new() { ["Case"] = m.Case.Name }, ct),
            rows => rows.Any(r => IsPerson(r, m.Member)) == m.Add, cancellationToken),
        CreateSearch s => CreateSearchAsync(s, cancellationToken),
        UpdateSearch u => UpdateSearchAsync(u, cancellationToken),
        DeleteSearch d => Purview("Remove-ComplianceSearch", new() { ["Identity"] = d.Search.Name, ["Confirm"] = false },
            ct => ReadPurview("Get-ComplianceSearch", new() { ["Identity"] = d.Search.Name }, ct), rows => rows.Count == 0, cancellationToken),
        CreateHold h => CreateHoldAsync(h, cancellationToken),
        UpdateHold u => UpdateHoldAsync(u, cancellationToken),
        SetHoldEnabled e => Purview("Set-CaseHoldPolicy", new() { ["Identity"] = e.Hold.Name, ["Enabled"] = e.New },
            ct => ReadPurview("Get-CaseHoldPolicy", new() { ["Identity"] = e.Hold.Name }, ct),
            rows => rows.Count == 1 && rows[0].TryGetProperty("Enabled", out var on) && on.ValueKind == (e.New ? JsonValueKind.True : JsonValueKind.False), cancellationToken),
        // Purview keeps a deleted hold for a while as "PendingDeletion" while it lifts it everywhere.
        DeleteHold d => Purview("Remove-CaseHoldPolicy", new() { ["Identity"] = d.Hold.Name, ["Confirm"] = false },
            ct => ReadPurview("Get-CaseHoldPolicy", new() { ["Identity"] = d.Hold.Name }, ct),
            rows => rows.Count == 0 || rows[0].TryGetProperty("Mode", out var mode) && string.Equals(mode.GetString(), "PendingDeletion", StringComparison.OrdinalIgnoreCase),
            cancellationToken),
        _ => Task.FromResult(new ApplyResult(ChangeOutcome.Failed, $"Tenantry can't apply \"{change.Label}\" changes yet.")),
    };

    /// <summary>
    /// Purview refuses while the case has holds, including ones deleted a minute or two ago that it
    /// is still lifting (seen live: about 90 seconds), so the refusal says which.
    /// </summary>
    async Task<ApplyResult> DeleteCaseAsync(DeleteCase change, CancellationToken cancellationToken)
    {
        var result = await Purview("Remove-ComplianceCase", new() { ["Identity"] = change.Case.Name, ["Confirm"] = false },
            ct => ReadPurview("Get-ComplianceCase", new() { ["Identity"] = change.Case.Name }, ct), rows => rows.Count == 0, cancellationToken);
        return result.Outcome == ChangeOutcome.Failed && (result.Detail ?? "").Contains("contains hold objects", StringComparison.OrdinalIgnoreCase)
            ? result with { Detail = "The case still has holds. Delete them first; if you just did, Purview is still lifting them, which can take up to about 20 minutes, so try again later." }
            : result;
    }

    Task<ApplyResult> Purview(string cmdlet, Dictionary<string, object?> parameters, Func<CancellationToken, Task<IReadOnlyList<JsonElement>>>? read,
        Func<IReadOnlyList<JsonElement>, bool> done, CancellationToken cancellationToken) =>
        RunCheckedAsync(cmdlet, parameters, read, done, cancellationToken, CmdletEndpoint.Compliance);

    Task<IReadOnlyList<JsonElement>> ReadPurview(string cmdlet, Dictionary<string, object?> parameters, CancellationToken cancellationToken) =>
        exchange!.InvokeAsync(CmdletEndpoint.Compliance, cmdlet, parameters, cancellationToken);

    static Dictionary<string, object?> With(Dictionary<string, object?> parameters, string name, object? value)
    {
        if (value is not null and not "")
            parameters[name] = value;
        return parameters;
    }

    static bool IsPerson(JsonElement row, ChangeTarget person) =>
        new[] { "PrimarySmtpAddress", "WindowsLiveID", "Name", "DisplayName" }.Any(p =>
            row.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
            && (string.Equals(v.GetString(), person.Address, StringComparison.OrdinalIgnoreCase) || string.Equals(v.GetString(), person.Name, StringComparison.OrdinalIgnoreCase)));

    static string[] Places(IEnumerable<ChangeTarget> places) => [.. places.Select(EdChange.Place)];

    /// <summary>Where a search or hold looks, as New-ComplianceSearch and New-CaseHoldPolicy take it (OneDrives are SharePoint locations).</summary>
    static void AddPlaces(Dictionary<string, object?> parameters, IReadOnlyList<ChangeTarget> mailboxes, IReadOnlyList<ChangeTarget> sites, bool publicFolders)
    {
        if (mailboxes.Count > 0)
            parameters["ExchangeLocation"] = Places(mailboxes);
        if (sites.Count > 0)
            parameters["SharePointLocation"] = Places(sites);
        if (publicFolders)
            parameters["PublicFolderLocation"] = new[] { "All" };
    }

    /// <summary>Add… and Remove… parameters for the places that changed (an "All" swaps like any other place).</summary>
    static void ChangePlaces(Dictionary<string, object?> parameters, string kind, IReadOnlyList<ChangeTarget> old, IReadOnlyList<ChangeTarget> now)
    {
        var before = Places(old).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = Places(now).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (after.Except(before, StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } added)
            parameters[$"Add{kind}Location"] = added;
        if (before.Except(after, StringComparer.OrdinalIgnoreCase).ToArray() is { Length: > 0 } removed)
            parameters[$"Remove{kind}Location"] = removed;
    }

    /// <summary>Makes the search, then runs it so its counts are there when it's opened.</summary>
    async Task<ApplyResult> CreateSearchAsync(CreateSearch change, CancellationToken cancellationToken)
    {
        var parameters = With(With(new() { ["Name"] = change.Search.Name, ["ContentMatchQuery"] = change.Query.Trim() }, "Case", change.CaseName),
            "Description", change.Description);
        AddPlaces(parameters, change.Mailboxes, change.Sites, change.PublicFolders);
        var made = await Purview("New-ComplianceSearch", parameters, ct => ReadPurview("Get-ComplianceSearch", new() { ["Identity"] = change.Search.Name }, ct),
            rows => rows.Count == 1, cancellationToken);
        return made.Outcome == ChangeOutcome.Applied ? await StartAfterAsync(change.Search.Name, made, cancellationToken) : made;
    }

    async Task<ApplyResult> UpdateSearchAsync(UpdateSearch change, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.Search.Name, ["Confirm"] = false };
        if (change.OldQuery.Trim() != change.NewQuery.Trim())
            parameters["ContentMatchQuery"] = change.NewQuery.Trim();
        ChangePlaces(parameters, "Exchange", change.OldMailboxes, change.NewMailboxes);
        ChangePlaces(parameters, "SharePoint", change.OldSites, change.NewSites);
        if (change.OldPublicFolders != change.NewPublicFolders)
            parameters[change.NewPublicFolders ? "AddPublicFolderLocation" : "RemovePublicFolderLocation"] = new[] { "All" };
        var query = change.NewQuery.Trim();
        var set = await Purview("Set-ComplianceSearch", parameters, ct => ReadPurview("Get-ComplianceSearch", new() { ["Identity"] = change.Search.Name }, ct),
            rows => rows.Count == 1 && (rows[0].TryGetProperty("ContentMatchQuery", out var q) ? q.GetString() ?? "" : "") == query, cancellationToken);
        return set.Outcome == ChangeOutcome.Applied ? await StartAfterAsync(change.Search.Name, set, cancellationToken) : set;
    }

    /// <summary>Starts the search; the change itself stands even when the run can't start (it can be run from its page).</summary>
    async Task<ApplyResult> StartAfterAsync(string name, ApplyResult result, CancellationToken cancellationToken)
    {
        try
        {
            await exchange!.InvokeAsync(CmdletEndpoint.Compliance, "Start-ComplianceSearch", new Dictionary<string, object?> { ["Identity"] = name, ["Confirm"] = false }, cancellationToken);
            return result with { Detail = "Saved; it's counting what it finds now (open it to see)." };
        }
        catch (CmdletException e)
        {
            return result with { Detail = $"Saved, but it couldn't start running: {e.Explanation} Use Run Search on its page." };
        }
    }

    /// <summary>A hold is a policy (where) plus a rule (what). If the rule can't be made, the policy is taken away again.</summary>
    async Task<ApplyResult> CreateHoldAsync(CreateHold change, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["Name"] = change.Hold.Name, ["Case"] = change.CaseName };
        AddPlaces(parameters, change.Mailboxes, change.Sites, change.PublicFolders);
        var made = await Purview("New-CaseHoldPolicy", parameters, ct => ReadPurview("Get-CaseHoldPolicy", new() { ["Identity"] = change.Hold.Name }, ct),
            rows => rows.Count == 1, cancellationToken);
        if (made.Outcome != ChangeOutcome.Applied)
            return made;
        try
        {
            await exchange!.InvokeAsync(CmdletEndpoint.Compliance, "New-CaseHoldRule",
                With(new() { ["Name"] = change.Hold.Name, ["Policy"] = change.Hold.Name }, "ContentMatchQuery", change.Query.Trim()), cancellationToken);
        }
        catch (CmdletException e)
        {
            try
            {
                await exchange!.InvokeAsync(CmdletEndpoint.Compliance, "Remove-CaseHoldPolicy", new Dictionary<string, object?> { ["Identity"] = change.Hold.Name, ["Confirm"] = false }, cancellationToken);
            }
            catch (CmdletException)
            {
            }
            return new ApplyResult(ChangeOutcome.Failed, $"Purview couldn't set what the hold keeps, so it wasn't made: {e.Explanation}");
        }
        return made with { Detail = "Made; Purview takes a little while to put it in place everywhere." };
    }

    async Task<ApplyResult> UpdateHoldAsync(UpdateHold change, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.Hold.Name };
        ChangePlaces(parameters, "Exchange", change.OldMailboxes, change.NewMailboxes);
        ChangePlaces(parameters, "SharePoint", change.OldSites, change.NewSites);
        if (change.OldPublicFolders != change.NewPublicFolders)
            parameters[change.NewPublicFolders ? "AddPublicFolderLocation" : "RemovePublicFolderLocation"] = new[] { "All" };
        if (parameters.Count > 1)
        {
            var placed = await Purview("Set-CaseHoldPolicy", parameters, null, _ => true, cancellationToken);
            if (placed.Outcome != ChangeOutcome.Applied)
                return placed;
        }
        if (change.OldQuery.Trim() == change.NewQuery.Trim())
            return new ApplyResult(ChangeOutcome.Applied);
        var rules = await ReadPurview("Get-CaseHoldRule", new() { ["Policy"] = change.Hold.Name }, cancellationToken);
        var rule = rules.Select(r => r.TryGetProperty("Name", out var n) ? n.GetString() : null).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        var query = change.NewQuery.Trim();
        return rule is null
            ? await Purview("New-CaseHoldRule", With(new() { ["Name"] = change.Hold.Name, ["Policy"] = change.Hold.Name }, "ContentMatchQuery", query), null, _ => true, cancellationToken)
            : await Purview("Set-CaseHoldRule", new() { ["Identity"] = rule, ["ContentMatchQuery"] = query },
                ct => ReadPurview("Get-CaseHoldRule", new() { ["Identity"] = rule }, ct),
                rows => rows.Count == 1 && (rows[0].TryGetProperty("ContentMatchQuery", out var q) ? q.GetString() ?? "" : "") == query, cancellationToken);
    }
}
