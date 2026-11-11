using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>The generic Intune objects (13c-13f): apps, app protection, updates, scripts, enrollment, tenant administration.</summary>
public sealed partial class GraphUserRecords : IIntuneObjectRecords
{
    public async Task<IReadOnlyList<IntuneObject>> GetObjectsAsync(IntuneKind kind, CancellationToken cancellationToken = default)
    {
        var items = new List<IntuneObject>();
        await foreach (var o in graph.GetAllAsync(kind.ListUrl, cancellationToken))
            if (kind.Belongs(Text(o, "@odata.type") ?? ""))
                items.Add(ToObject(kind, o));
        if (!kind.ExpandAssignments && kind.Assign != AssignMode.None)
        {
            // A kind whose list can't expand assignments (terms and conditions): each read on its own.
            var reads = await Task.WhenAll(items.Select(async i =>
            {
                var list = new List<IntuneAssignment>();
                try
                {
                    await foreach (var a in graph.GetAllAsync($"{kind.ItemUrl(i.Id)}/assignments", cancellationToken))
                        if (IntuneAssignment.From(a) is { } read)
                            list.Add(read);
                }
                catch (GraphException)
                {
                }
                return i with { Assignments = list };
            }));
            items = [.. reads];
        }
        return await NameAssignmentsAsync(items, i => i.Assignments, (i, a) => i with { Assignments = a }, cancellationToken);
    }

    internal static IntuneObject ToObject(IntuneKind kind, JsonElement o) => new(Text(o, "id") ?? "", Text(o, kind.NameProperty) ?? Text(o, "displayName") ?? "An item",
        Text(o, "description") is { Length: > 0 } d ? d : null, Text(o, "@odata.type") ?? "", When(o, "lastModifiedDateTime") ?? When(o, "lastModifiedTime"),
        o.TryGetProperty("assignments", out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Select(IntuneAssignment.From).OfType<IntuneAssignment>()] : [],
        o.Clone());

    public async Task<JsonElement> GetObjectAsync(IntuneKind kind, string id, CancellationToken cancellationToken = default)
    {
        var expand = kind.ListUrl.Split('?') is [_, var query] ? string.Join('&', query.Split('&').Where(q => q.StartsWith("$expand=", StringComparison.Ordinal))) : "";
        return await graph.GetAsync(expand.Length == 0 ? kind.ItemUrl(id) : $"{kind.ItemUrl(id)}?{expand}", cancellationToken);
    }

    public async Task<AppInstallSummary?> GetInstallSummaryAsync(string appId, CancellationToken cancellationToken = default)
    {
        try
        {
            var s = await graph.GetAsync($"{IntuneBeta}deviceAppManagement/mobileApps/{appId}/installSummary", cancellationToken);
            return new AppInstallSummary(Count(s, "installedDeviceCount"), Count(s, "failedDeviceCount"), Count(s, "pendingInstallDeviceCount"), Count(s, "notInstalledDeviceCount"),
                Count(s, "notApplicableDeviceCount"));
        }
        catch (GraphException)
        {
            return null;
        }
    }
}

/// <summary>Making an Intune object, and the apps an app protection policy protects.</summary>
public sealed partial class GraphChangeExecutor
{
    internal static JsonObject TargetAppsBody(SetProtectedApps change) => new()
    {
        ["apps"] = new JsonArray([.. change.New.Select(id => (JsonNode)new JsonObject
        {
            ["mobileAppIdentifier"] = change.Platform == "ios"
                ? new JsonObject { ["@odata.type"] = "#microsoft.graph.iosMobileAppIdentifier", ["bundleId"] = id }
                : new JsonObject { ["@odata.type"] = "#microsoft.graph.androidMobileAppIdentifier", ["packageId"] = id },
        })]),
    };

    async Task<ApplyResult> ApplyObjectAsync(IntuneChange change, CancellationToken cancellationToken)
    {
        try
        {
            switch (change)
            {
                case CreateIntuneObject c:
                    var made = await graph.PostAsync(c.Collection, c.Body, cancellationToken);
                    if (c.ThenPath is { } then && c.ThenBody is { } thenBody && made.TryGetProperty("id", out var id))
                        await graph.PostAsync($"{c.Collection}/{id.GetString()}/{then}", thenBody, cancellationToken);
                    break;
                case SetProtectedApps p:
                    await graph.PostAsync($"{p.Path}/targetApps", TargetAppsBody(p), cancellationToken);
                    break;
            }
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    internal static PsScript? ObjectScript(ChangeOp change) => change switch
    {
        CreateIntuneObject c => PsScript.Graph(Ps.Lines(
            Ps.Graph("POST", c.Collection, c.Body, assignTo: c.ThenPath is null ? null : "made"),
            c.ThenPath is { } then && c.ThenBody is { } body
                ? $"Invoke-MgGraphRequest -Method POST -Uri ({Ps.Quote(Ps.GraphRoot + c.Collection + "/")} + $made.id + {Ps.Quote("/" + then)}) -Body '{body.ToJsonString().Replace("'", "''", StringComparison.Ordinal)}'"
                : null)),
        SetProtectedApps p => PsScript.Graph(Ps.Graph("POST", $"{p.Path}/targetApps", TargetAppsBody(p)), "The whole list of apps is sent at once."),
        _ => null,
    };
}
