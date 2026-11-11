using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>Intune compliance policies (13a), their assignments and what they need: group names, filters, email templates.</summary>
public sealed partial class GraphUserRecords : IIntunePolicyRecords
{
    const string IntuneBeta = "https://graph.microsoft.com/beta/";

    public async Task<IReadOnlyList<CompliancePolicy>> GetCompliancePoliciesAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<JsonElement>();
        await foreach (var p in graph.GetAllAsync("deviceManagement/deviceCompliancePolicies?$expand=assignments,scheduledActionsForRule($expand=scheduledActionConfigurations)", cancellationToken))
            rows.Add(p);
        var counts = await Task.WhenAll(rows.Select(async p =>
        {
            try
            {
                return await graph.GetAsync($"deviceManagement/deviceCompliancePolicies/{Text(p, "id")}/deviceStatusOverview", cancellationToken);
            }
            catch (GraphException)
            {
                return (JsonElement?)null;
            }
        }).Select(async t => (JsonElement?)await t));
        var policies = rows.Select((p, i) => ToCompliancePolicy(p, counts[i])).ToList();
        return await NameAssignmentsAsync(policies, p => p.Assignments, (p, a) => p with { Assignments = a }, cancellationToken);
    }

    internal static CompliancePolicy ToCompliancePolicy(JsonElement p, JsonElement? overview)
    {
        var assignments = p.TryGetProperty("assignments", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(IntuneAssignment.From).OfType<IntuneAssignment>().ToArray() : [];
        var actions = new List<ComplianceAction>();
        if (p.TryGetProperty("scheduledActionsForRule", out var rules) && rules.ValueKind == JsonValueKind.Array)
            foreach (var rule in rules.EnumerateArray())
                if (rule.TryGetProperty("scheduledActionConfigurations", out var configs) && configs.ValueKind == JsonValueKind.Array)
                    foreach (var c in configs.EnumerateArray())
                        actions.Add(new ComplianceAction(Text(c, "actionType") ?? "", c.TryGetProperty("gracePeriodHours", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : 0,
                            Text(c, "notificationTemplateId") is { Length: > 0 } t && t != "00000000-0000-0000-0000-000000000000" ? t : null,
                            c.TryGetProperty("notificationMessageCCList", out var cc) && cc.ValueKind == JsonValueKind.Array ? [.. cc.EnumerateArray().Select(x => x.GetString()).OfType<string>()] : null));
        ComplianceCounts? counts = overview is { } o
            ? new ComplianceCounts(Count(o, "successCount"), Count(o, "failedCount"), Count(o, "errorCount"), Count(o, "conflictCount"), Count(o, "notApplicableCount"), Count(o, "pendingCount"))
            : null;
        return new CompliancePolicy(Text(p, "id") ?? "", Text(p, "displayName") ?? "A policy", Text(p, "description") is { Length: > 0 } d ? d : null, Text(p, "@odata.type") ?? "",
            When(p, "lastModifiedDateTime"), p, assignments, [.. actions.OrderBy(x => x.GraceHours)], counts);
    }

    /// <summary>Fills in group and filter names for every policy's assignments, in one read of each.</summary>
    async Task<IReadOnlyList<T>> NameAssignmentsAsync<T>(IReadOnlyList<T> items, Func<T, IReadOnlyList<IntuneAssignment>> get, Func<T, IReadOnlyList<IntuneAssignment>, T> set,
        CancellationToken cancellationToken)
    {
        var groupIds = items.SelectMany(get).Select(a => a.GroupId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in groupIds.Chunk(1000))
        {
            var answer = await graph.PostAsync("directoryObjects/getByIds", new JsonObject { ["ids"] = new JsonArray([.. chunk.Select(i => (JsonNode)i)]), ["types"] = new JsonArray("group") },
                cancellationToken);
            foreach (var g in answer.GetProperty("value").EnumerateArray())
                names[Text(g, "id") ?? ""] = Text(g, "displayName") ?? "A group";
        }
        var filters = items.SelectMany(get).Any(a => a.FilterId is not null)
            ? (await GetAssignmentFiltersAsync(cancellationToken)).ToDictionary(f => f.Id, f => f.Name, StringComparer.OrdinalIgnoreCase)
            : [];
        return [.. items.Select(i => set(i, [.. get(i).Select(a => a with
        {
            GroupName = a.GroupId is { } id ? names.GetValueOrDefault(id, "A deleted group") : null,
            FilterName = a.FilterId is { } f ? filters.GetValueOrDefault(f, "a filter") : null,
        })]))];
    }

    public async Task<IReadOnlyList<DeviceComplianceStatus>> GetComplianceStatusesAsync(string policyId, CancellationToken cancellationToken = default)
    {
        var statuses = new List<DeviceComplianceStatus>();
        await foreach (var s in graph.GetAllAsync($"deviceManagement/deviceCompliancePolicies/{policyId}/deviceStatuses", cancellationToken))
            statuses.Add(new DeviceComplianceStatus(Text(s, "deviceDisplayName") ?? "A device", Text(s, "userName") is { Length: > 0 } u ? u : null, Text(s, "status") ?? "unknown",
                When(s, "lastReportedDateTime")));
        return [.. statuses.OrderBy(s => s.Status == "compliant").ThenBy(s => s.DeviceName, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<JsonElement> GetComplianceSettingsAsync(CancellationToken cancellationToken = default)
    {
        var dm = await graph.GetAsync("deviceManagement?$select=settings", cancellationToken);
        // A tenant that never changed them gets null back (seen live): Microsoft's defaults apply.
        return dm.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object ? s.Clone()
            : JsonDocument.Parse(GraphChangeExecutor.DefaultComplianceSettings().ToJsonString()).RootElement.Clone();
    }

    public async Task<IReadOnlyList<AssignmentFilter>> GetAssignmentFiltersAsync(CancellationToken cancellationToken = default)
    {
        var filters = new List<AssignmentFilter>();
        try
        {
            await foreach (var f in graph.GetAllAsync($"{IntuneBeta}deviceManagement/assignmentFilters?$select=id,displayName,platform,rule", cancellationToken))
                filters.Add(new AssignmentFilter(Text(f, "id") ?? "", Text(f, "displayName") ?? "A filter", Text(f, "platform") ?? "", Text(f, "rule") ?? ""));
        }
        catch (GraphException)
        {
        }
        return filters;
    }

    public async Task<IReadOnlyList<NotificationTemplate>> GetNotificationTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var templates = new List<NotificationTemplate>();
        await foreach (var t in graph.GetAllAsync("deviceManagement/notificationMessageTemplates?$select=id,displayName", cancellationToken))
            templates.Add(new NotificationTemplate(Text(t, "id") ?? "", Text(t, "displayName") ?? "A template"));
        return templates;
    }
}

/// <summary>Intune policy changes: settings, assignments, actions for noncompliance, new policies and deletes.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>The assign body: every assignment (an app's with its intent), replacing what's there.</summary>
    internal static JsonObject AssignBody(SetIntuneAssignments change) => new()
    {
        [change.ListProperty] = new JsonArray([.. change.New.Select(a => (JsonNode)(change.ListProperty == "mobileAppAssignments"
            ? new JsonObject { ["@odata.type"] = "#microsoft.graph.mobileAppAssignment", ["intent"] = a.Intent ?? "required", ["target"] = a.Target() }
            : new JsonObject { ["target"] = a.Target() }))]),
    };

    static JsonObject ActionsJson(IEnumerable<ComplianceAction> actions) => new()
    {
        ["ruleName"] = "PasswordRequired",
        ["scheduledActionConfigurations"] = new JsonArray([.. actions.Select(a => (JsonNode)new JsonObject
        {
            ["actionType"] = a.Action,
            ["gracePeriodHours"] = a.GraceHours,
            ["notificationTemplateId"] = a.TemplateId ?? "",
            ["notificationMessageCCList"] = new JsonArray([.. (a.CcGroupIds ?? []).Select(g => (JsonNode)g)]),
        })]),
    };

    internal static JsonObject ComplianceActionsBody(SetComplianceActions change) =>
        new() { ["deviceComplianceScheduledActionForRules"] = new JsonArray(ActionsJson(change.New)) };

    internal static JsonObject CompliancePolicyBody(CreateCompliancePolicy change) => new()
    {
        ["@odata.type"] = change.ODataType,
        ["displayName"] = change.Policy.Name,
        ["description"] = change.Description,
        ["scheduledActionsForRule"] = new JsonArray(ActionsJson([new ComplianceAction("block", change.GraceDays * 24)])),
    };

    /// <summary>Intune's compliance settings when a tenant has never changed them (Graph returns null).</summary>
    internal static JsonObject DefaultComplianceSettings() => new()
    {
        ["deviceComplianceCheckinThresholdDays"] = 30, ["isScheduledActionEnabled"] = true, ["secureByDefault"] = false, ["enhancedJailBreak"] = false,
    };

    /// <summary>A plain setting is one PATCH field; a dotted one is a field in an object sent back whole (Intune's defaults when it has none yet).</summary>
    internal static JsonObject SettingBody(SetIntuneSetting change, JsonElement? current)
    {
        if (change.Property.Split('.', 2) is [var outer, var inner])
        {
            var obj = current is { } c && c.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object ? JsonNode.Parse(o.GetRawText())!.AsObject()
                : outer == "settings" && change.Path == "deviceManagement" ? DefaultComplianceSettings() : new JsonObject();
            obj.Remove("@odata.type");
            obj[inner] = change.Value?.DeepClone();
            return new JsonObject { [outer] = obj };
        }
        var body = new JsonObject { [change.Property] = change.Value?.DeepClone() };
        if (change.ODataType.StartsWith('#'))
            body["@odata.type"] = change.ODataType;
        return body;
    }

    async Task<ApplyResult> ApplyIntuneAsync(IntuneChange change, CancellationToken cancellationToken)
    {
        if (change is SetCatalogSettings or CreateConfigurationProfile)
            return await ApplyCatalogAsync(change, cancellationToken);
        try
        {
            switch (change)
            {
                case SetIntuneSetting s:
                    JsonElement? current = s.Property.Contains('.') ? await graph.GetAsync($"{s.Path}?$select={s.Property.Split('.')[0]}", cancellationToken) : null;
                    await graph.PatchAsync(s.Path, SettingBody(s, current), cancellationToken);
                    break;
                case SetIntuneAssignments a:
                    await graph.PostAsync($"{a.Path}/assign", AssignBody(a), cancellationToken);
                    break;
                case SetComplianceActions c:
                    await graph.PostAsync($"{c.Path}/scheduleActionsForRules", ComplianceActionsBody(c), cancellationToken);
                    break;
                case CreateCompliancePolicy n:
                    var made = await graph.PostAsync("deviceManagement/deviceCompliancePolicies", CompliancePolicyBody(n), cancellationToken);
                    if (n.Assignments.Count > 0)
                        await graph.PostAsync($"deviceManagement/deviceCompliancePolicies/{made.GetProperty("id").GetString()}/assign",
                            AssignBody(new SetIntuneAssignments(n.Policy, "", [], n.Assignments)), cancellationToken);
                    break;
                case DeleteIntunePolicy d:
                    await DeleteIfThereAsync(d.Path, cancellationToken);
                    break;
            }
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    internal static PsScript? IntuneScript(ChangeOp change) => change switch
    {
        SetIntuneSetting { Property: var p } s when p.Contains('.') => PsScript.Graph(Ps.Lines(
                Ps.Graph("GET", $"{s.Path}?$select={p.Split('.')[0]}", assignTo: "current"),
                $"$current.{p.Split('.')[0]}.{p.Split('.')[1]} = {(s.Value is null ? "$null" : s.Value.ToJsonString())}",
                $"Invoke-MgGraphRequest -Method PATCH -Uri {Ps.Quote(Ps.GraphRoot + s.Path)} -Body @{{ {p.Split('.')[0]} = $current.{p.Split('.')[0]} }}"),
            "The object's other settings are sent back as they were."),
        SetIntuneSetting s => PsScript.Graph(Ps.Graph("PATCH", s.Path, SettingBody(s, null))),
        SetIntuneAssignments a => PsScript.Graph(Ps.Graph("POST", $"{a.Path}/assign", AssignBody(a)), "Intune replaces every assignment with this list."),
        SetComplianceActions c => PsScript.Graph(Ps.Graph("POST", $"{c.Path}/scheduleActionsForRules", ComplianceActionsBody(c)), "The whole list of actions is sent at once."),
        CreateCompliancePolicy n => PsScript.Graph(Ps.Lines(
            Ps.Graph("POST", "deviceManagement/deviceCompliancePolicies", CompliancePolicyBody(n), assignTo: "policy"),
            n.Assignments.Count == 0 ? null
                : $"Invoke-MgGraphRequest -Method POST -Uri ({Ps.Quote(Ps.GraphRoot + "deviceManagement/deviceCompliancePolicies/")} + $policy.id + '/assign') -Body '{AssignBody(new SetIntuneAssignments(n.Policy, "", [], n.Assignments)).ToJsonString().Replace("'", "''", StringComparison.Ordinal)}'")),
        DeleteIntunePolicy d => PsScript.Graph(Ps.Graph("DELETE", d.Path)),
        _ => null,
    };
}
