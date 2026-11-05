using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>
/// Changes to a group itself: its name, description, alias and privacy, how it gets its members,
/// and deleting or restoring it. Graph writes Microsoft 365 and security groups; distribution lists
/// and mail-enabled security groups are read-only in Graph, so they go through Exchange.
/// </summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>The Set-DistributionGroup parameter for each setting.</summary>
    static string ListParameter(string setting) => setting switch
    {
        SetGroupSetting.DisplayName => "DisplayName",
        SetGroupSetting.Description => "Description",
        SetGroupSetting.Alias => "Alias",
        // Anything else is passed as named (lists have no privacy; the editor doesn't offer it for them).
        var other => other,
    };

    /// <summary>Set-DistributionGroup for a list's setting. Its description is a list of lines in Exchange.</summary>
    internal static Dictionary<string, object?> ListSettingParameters(SetGroupSetting change)
    {
        var value = string.IsNullOrWhiteSpace(change.New) ? null : change.New.Trim();
        return new Dictionary<string, object?>
        {
            ["Identity"] = change.Group.Address ?? change.Group.Id,
            [ListParameter(change.Setting)] = change.Setting == SetGroupSetting.Description ? value is null ? null : new[] { value } : value,
            ["BypassSecurityGroupManagerCheck"] = true,
        };
    }

    static JsonObject GraphSettingBody(SetGroupSetting change) =>
        new() { [change.Setting] = string.IsNullOrWhiteSpace(change.New) ? null : change.New.Trim() };

    async Task<ApplyResult> SetGroupSettingAsync(SetGroupSetting change, CancellationToken cancellationToken)
    {
        var value = string.IsNullOrWhiteSpace(change.New) ? null : change.New.Trim();
        ApplyResult result;
        if (change.ThroughExchange)
        {
            var parameter = ListParameter(change.Setting);
            result = await RunCheckedAsync("Set-DistributionGroup", ListSettingParameters(change),
                ct => Read("Get-DistributionGroup", new() { ["Identity"] = change.Group.Address ?? change.Group.Id }, ct, parameter),
                rows => rows.Count == 1 && string.Equals(ListValue(rows[0], parameter), value ?? "", StringComparison.Ordinal),
                cancellationToken);
        }
        else
            result = await GraphCheckedAsync(
                ct => graph.PatchAsync($"groups/{change.Group.Id}", GraphSettingBody(change), ct),
                ct => graph.GetAsync($"groups/{change.Group.Id}?$select={change.Setting}", ct),
                answer => answer is { } g && string.Equals(g.TryGetProperty(change.Setting, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null,
                    value, StringComparison.Ordinal),
                cancellationToken);
        // The lists show it at once (Entra shows a list's change a little later).
        if (result.Succeeded)
            store.Apply(DirectorySync.Groups, [new EntityChange(change.Group.Id, new JsonObject { ["id"] = change.Group.Id, [change.Setting] = value }, OnlyIfExists: true)]);
        return result;
    }

    /// <summary>A list's value as text; a description comes back as a list of lines.</summary>
    static string ListValue(JsonElement row, string parameter) =>
        !row.TryGetProperty(parameter, out var v) ? ""
        : v.ValueKind == JsonValueKind.Array ? string.Join("\n", v.EnumerateArray().Select(x => x.GetString()))
        : v.ValueKind == JsonValueKind.String ? v.GetString() ?? ""
        : "";

    /// <summary>The PATCH that sets how a group gets its members (Entra's own conversion keeps a stopped rule as Paused).</summary>
    internal static JsonObject MembershipBody(SetMembershipRule change)
    {
        var types = new JsonArray();
        if (change.Kind == GroupKind.Microsoft365)
            types.Add("Unified");
        if (change.NewDynamic)
            types.Add("DynamicMembership");
        var body = new JsonObject { ["groupTypes"] = types };
        if (change.NewDynamic)
        {
            body["membershipRule"] = change.NewRule?.Trim();
            body["membershipRuleProcessingState"] = change.NewPaused ? "Paused" : "On";
        }
        else
            body["membershipRuleProcessingState"] = "Paused";
        return body;
    }

    async Task<ApplyResult> SetMembershipRuleAsync(SetMembershipRule change, CancellationToken cancellationToken)
    {
        var result = await GraphCheckedAsync(
            ct => graph.PatchAsync($"groups/{change.Group.Id}", MembershipBody(change), ct),
            ct => graph.GetAsync($"groups/{change.Group.Id}?$select=groupTypes,membershipRule,membershipRuleProcessingState", ct),
            answer => answer is { } g
                      && g.TryGetProperty("groupTypes", out var t) && t.EnumerateArray().Any(x => x.GetString() == "DynamicMembership") == change.NewDynamic
                      && (!change.NewDynamic || (g.TryGetProperty("membershipRule", out var r) && r.GetString()?.Trim() == change.NewRule?.Trim())),
            cancellationToken);
        if (result.Succeeded)
            await IntoLocalCopyAsync(sync => sync.RefreshGroupAsync(change.Group.Id, cancellationToken));
        return result;
    }

    async Task<ApplyResult> DeleteGroupAsync(DeleteGroup change, CancellationToken cancellationToken)
    {
        try
        {
            if (change.ThroughExchange)
            {
                if (exchange is null)
                    return NoExchange;
                await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Remove-DistributionGroup", ListRemoveParameters(change), cancellationToken);
            }
            else
                await DeleteIfThereAsync($"groups/{change.Group.Id}", cancellationToken);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
        catch (CmdletException e) when (!e.IsNotFound)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Explanation);
        }
        store.Apply(DirectorySync.Groups, [EntityChange.Removed(change.Group.Id)]);
        return new ApplyResult(ChangeOutcome.Applied);
    }

    internal static Dictionary<string, object?> ListRemoveParameters(DeleteGroup change) =>
        new() { ["Identity"] = change.Group.Address ?? change.Group.Id, ["BypassSecurityGroupManagerCheck"] = true, ["Confirm"] = false };

    async Task<ApplyResult> RestoreGroupAsync(RestoreGroup change, CancellationToken cancellationToken)
    {
        var result = await GraphCheckedAsync(
            ct => graph.PostAsync($"directory/deletedItems/{change.Group.Id}/restore", new JsonObject(), ct),
            ct => graph.GetAsync($"groups/{change.Group.Id}?$select=id", ct),
            answer => answer is not null,
            cancellationToken);
        if (result.Succeeded)
            await IntoLocalCopyAsync(sync => sync.RefreshGroupAsync(change.Group.Id, cancellationToken));
        return result;
    }

    /// <summary>The PowerShell for group setting changes (the same calls as above).</summary>
    static PsScript? GroupScript(ChangeOp change) => change switch
    {
        SetGroupSetting { ThroughExchange: true } s => PsScript.Exchange(Ps.Cmdlet("Set-DistributionGroup", ListSettingParameters(s))),
        SetGroupSetting s => PsScript.Graph(Ps.Graph("PATCH", $"groups/{s.Group.Id}", GraphSettingBody(s))),
        SetMembershipRule m => PsScript.Graph(Ps.Graph("PATCH", $"groups/{m.Group.Id}", MembershipBody(m))),
        DeleteGroup { ThroughExchange: true } d => PsScript.Exchange(Ps.Cmdlet("Remove-DistributionGroup", ListRemoveParameters(d))),
        DeleteGroup d => PsScript.Graph(Ps.Graph("DELETE", $"groups/{d.Group.Id}")),
        RestoreGroup r => PsScript.Graph(Ps.Graph("POST", $"directory/deletedItems/{r.Group.Id}/restore", new JsonObject())),
        _ => null,
    };
}
