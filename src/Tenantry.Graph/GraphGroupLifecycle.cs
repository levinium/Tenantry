using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Core.Licensing;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>A group's lifecycle (G3): expiry and renewal, sensitivity label, group licenses, upgrading a list.</summary>
public sealed partial class GraphUserRecords : IGroupLifecycleRecords
{
    public async Task<GroupLifecycle> GetGroupLifecycleAsync(GroupSummary group, CancellationToken cancellationToken = default)
    {
        var catalog = Catalog();
        var names = catalog.ToDictionary(s => s.SkuId, s => s.Name, StringComparer.OrdinalIgnoreCase);
        var g = await graph.GetAsync($"groups/{group.Id}?$select=expirationDateTime,renewedDateTime,assignedLicenses,licenseProcessingState", cancellationToken);
        DateTimeOffset? Date(string name) => g.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture) : null;
        var licenses = g.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
            ? al.EnumerateArray().Select(l =>
            {
                var sku = l.GetProperty("skuId").GetString()!;
                var disabled = l.TryGetProperty("disabledPlans", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
                return new GroupLicense(sku, names.GetValueOrDefault(sku, LicenseNames.For(sku, null)), disabled);
            }).ToArray()
            : [];
        var state = g.TryGetProperty("licenseProcessingState", out var ls) && ls.ValueKind == JsonValueKind.Object && ls.TryGetProperty("state", out var st) ? st.GetString() : null;

        // The label: its own read, as a tenant without labels can refuse it.
        string? label = null;
        if (group.Kind == GroupKind.Microsoft365)
            try
            {
                var labels = await graph.GetAsync($"groups/{group.Id}?$select=assignedLabels", cancellationToken);
                label = labels.TryGetProperty("assignedLabels", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
                    ? a[0].TryGetProperty("displayName", out var n) ? n.GetString() : null
                    : null;
            }
            catch (GraphException)
            {
            }

        int? policyDays = null;
        if (group.Kind == GroupKind.Microsoft365)
            try
            {
                var policy = await graph.GetFirstAsync("groupLifecyclePolicies", cancellationToken);
                if (policy is { } p && p.TryGetProperty("managedGroupTypes", out var types) && types.GetString() is { } managed && managed != "None")
                {
                    var covered = managed == "All" || await graph.GetFirstAsync($"groups/{group.Id}/groupLifecyclePolicies", cancellationToken) is not null;
                    if (covered && p.TryGetProperty("groupLifetimeInDays", out var days) && days.ValueKind == JsonValueKind.Number)
                        policyDays = days.GetInt32();
                }
            }
            catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                // No Entra ID P1 (expiration needs it): it simply doesn't expire as far as Tenantry knows.
            }

        int? errors = null;
        if (licenses.Length > 0)
            try
            {
                errors = 0;
                await foreach (var _ in graph.GetAllAsync($"groups/{group.Id}/membersWithLicenseErrors?$select=id&$top=999", cancellationToken))
                    errors++;
            }
            catch (GraphException)
            {
                errors = null;
            }

        var (canUpgrade, blocked) = group.Kind == GroupKind.Distribution ? await UpgradeEligibilityAsync(group, cancellationToken) : (false, null);
        return new GroupLifecycle(policyDays is null ? null : Date("expirationDateTime"), Date("renewedDateTime"), policyDays, label, licenses, catalog, state, errors,
            canUpgrade, blocked);
    }

    /// <summary>Exchange lists the distribution lists it can upgrade; one it doesn't list can't be (Microsoft names the usual reasons).</summary>
    async Task<(bool, string?)> UpgradeEligibilityAsync(GroupSummary group, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return (false, "Upgrading goes through Exchange Online, which isn't connected for this tenant.");
        if (group.SyncedFromOnPremises)
            return (false, "It's synced from on-premises AD: lists managed there can't be upgraded here.");
        try
        {
            var rows = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-EligibleDistributionGroupForMigration",
                new Dictionary<string, object?> { ["Identity"] = group.Mail ?? group.Id }, cancellationToken, ["PrimarySmtpAddress"]);
            return rows.Count > 0 ? (true, null) : (false, NotEligible);
        }
        catch (CmdletException e) when (e.IsNotFound || e.Explanation.Contains("eligible", StringComparison.OrdinalIgnoreCase))
        {
            return (false, NotEligible);
        }
        catch (CmdletException e)
        {
            return (false, $"Exchange couldn't say whether it can be upgraded: {e.Explanation}");
        }
    }

    const string NotEligible = "Microsoft won't upgrade this list. Usual reasons: it has no owner or no members, it's inside another list or has lists inside it, "
                               + "a member isn't a mailbox, or another list's sender rules name it.";
}

public sealed partial class GraphChangeExecutor
{
    async Task<ApplyResult> RenewGroupAsync(RenewGroup change, CancellationToken cancellationToken) =>
        await GraphCheckedAsync(ct => graph.PostAsync($"groups/{change.Group.Id}/renew", new JsonObject(), ct), null, _ => true, cancellationToken);

    internal static JsonObject GroupLicenseBody(SetGroupLicense change) => new()
    {
        ["addLicenses"] = change.New ? new JsonArray(new JsonObject { ["skuId"] = change.SkuId, ["disabledPlans"] = new JsonArray() }) : new JsonArray(),
        ["removeLicenses"] = change.New ? new JsonArray() : new JsonArray(change.SkuId),
    };

    async Task<ApplyResult> SetGroupLicenseAsync(SetGroupLicense change, CancellationToken cancellationToken)
    {
        var result = await GraphCheckedAsync(
            ct => graph.PostAsync($"groups/{change.Group.Id}/assignLicense", GroupLicenseBody(change), ct),
            ct => graph.GetAsync($"groups/{change.Group.Id}?$select=assignedLicenses", ct),
            answer => answer is { } g && g.TryGetProperty("assignedLicenses", out var a)
                      && a.EnumerateArray().Any(l => string.Equals(l.GetProperty("skuId").GetString(), change.SkuId, StringComparison.OrdinalIgnoreCase)) == change.New,
            cancellationToken);
        return result.Succeeded && result.Outcome == ChangeOutcome.Applied
            ? new ApplyResult(ChangeOutcome.Applied, "Members get (or lose) it as Microsoft processes the group, usually within minutes.")
            : result;
    }

    internal static Dictionary<string, object?> UpgradeParameters(UpgradeList change) =>
        new() { ["DlIdentities"] = new[] { change.Group.Address ?? change.Group.Id } };

    async Task<ApplyResult> UpgradeListAsync(UpgradeList change, CancellationToken cancellationToken)
    {
        var result = await RunCheckedAsync("Upgrade-DistributionGroup", UpgradeParameters(change), null, _ => true, cancellationToken);
        return result.Succeeded
            ? new ApplyResult(ChangeOutcome.Applied, "Upgrade asked for. Microsoft does it in the background; in testing (twice, on an eligible list) it hadn't happened hours later, so check back. If it still hasn't by tomorrow, upgrade it in the Exchange admin center.")
            : result;
    }

    static PsScript? GroupLifecycleScript(ChangeOp change) => change switch
    {
        RenewGroup r => PsScript.Graph(Ps.Graph("POST", $"groups/{r.Group.Id}/renew", new JsonObject())),
        SetGroupLicense l => PsScript.Graph(Ps.Graph("POST", $"groups/{l.Group.Id}/assignLicense", GroupLicenseBody(l))),
        UpgradeList u => PsScript.Exchange(Ps.Cmdlet("Upgrade-DistributionGroup", UpgradeParameters(u))),
        _ => null,
    };
}
