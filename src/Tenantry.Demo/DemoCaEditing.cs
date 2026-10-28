using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's Conditional Access editing: a few Microsoft-style templates, one terms of use, a small app catalog, and create/edit in memory.</summary>
public sealed partial class DemoUserRecords : ICaEditingRecords
{
    static string Details(JsonObject conditions, JsonObject? grant, JsonObject? session = null) =>
        new JsonObject { ["conditions"] = conditions, ["grantControls"] = grant, ["sessionControls"] = session }.ToJsonString();

    static JsonObject TemplateApps(string app) => new() { ["includeApplications"] = new JsonArray(app) };

    public Task<IReadOnlyList<CaTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CaTemplate>>(
        [
            new("t-mfa-admins", "Require multifactor authentication for admins", "Require MFA for privileged administrative accounts.", ["secureFoundation", "zeroTrust"],
                Details(new JsonObject { ["users"] = new JsonObject { ["includeRoles"] = new JsonArray(Catalog[0].Id, Catalog[1].Id, Catalog[7].Id) },
                    ["applications"] = TemplateApps("All"), ["clientAppTypes"] = new JsonArray("all") }, new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("mfa") })),
            new("t-mfa-all", "Require multifactor authentication for all users", "Require MFA for everyone.", ["secureFoundation", "zeroTrust", "remoteWork"],
                Details(new JsonObject { ["users"] = new JsonObject { ["includeUsers"] = new JsonArray("All") }, ["applications"] = TemplateApps("All"), ["clientAppTypes"] = new JsonArray("all") },
                    new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("mfa") })),
            new("t-legacy", "Block legacy authentication", "Block sign-ins that can't do MFA.", ["secureFoundation", "zeroTrust"],
                Details(new JsonObject { ["users"] = new JsonObject { ["includeUsers"] = new JsonArray("All") }, ["applications"] = TemplateApps("All"),
                    ["clientAppTypes"] = new JsonArray("exchangeActiveSync", "other") }, new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("block") })),
            new("t-register", "Securing security info registration", "Only let people register MFA from a trusted place.", ["secureFoundation"],
                Details(new JsonObject { ["users"] = new JsonObject { ["includeUsers"] = new JsonArray("All") },
                    ["applications"] = new JsonObject { ["includeUserActions"] = new JsonArray("urn:user:registersecurityinfo") },
                    ["locations"] = new JsonObject { ["includeLocations"] = new JsonArray("All"), ["excludeLocations"] = new JsonArray("AllTrusted") },
                    ["clientAppTypes"] = new JsonArray("all") }, new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("mfa") })),
            new("t-session", "Use application enforced restrictions for unmanaged devices", "Limit what unmanaged devices can do in SharePoint and Exchange.", ["remoteWork"],
                Details(new JsonObject { ["users"] = new JsonObject { ["includeUsers"] = new JsonArray("All") }, ["applications"] = TemplateApps("Office365"),
                    ["clientAppTypes"] = new JsonArray("browser") }, null, new JsonObject { ["applicationEnforcedRestrictions"] = new JsonObject { ["isEnabled"] = true } })),
        ]);

    public Task<Available<IReadOnlyList<TermsOfUse>>> GetTermsOfUseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new Available<IReadOnlyList<TermsOfUse>>([new TermsOfUse("tou-1", "Acceptable Use Terms")]));

    static readonly (string AppId, string Name)[] AppCatalog =
    [
        ("00000002-0000-0ff1-ce00-000000000000", "Office 365 Exchange Online"),
        ("00000003-0000-0ff1-ce00-000000000000", "Office 365 SharePoint Online"),
        ("cc15fd57-2c6c-4117-a88c-83b1d56b4bbe", "Microsoft Teams Services"),
        ("c44b4083-3bb0-49c1-b47d-974e53cbdf3c", "Azure Portal"),
        ("33333333-4444-5555-6666-777777777777", "Northgate Payroll"),
    ];

    public Task<IReadOnlyList<(string AppId, string Name)>> SearchAppsAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<(string, string)>>([.. AppCatalog.Where(a => text.Trim().Length > 0 && a.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))]);

    /// <summary>Applies a policy made or changed in the editor; false when it isn't one.</summary>
    bool ApplyCaEdit(ChangeOp change)
    {
        var policies = CaPolicies();
        switch (change)
        {
            case CreateCaPolicy create:
                var made = JsonNode.Parse(create.Json)!.AsObject();
                var id = $"ca-{Guid.NewGuid():N}";
                made["id"] = id;
                policies.Add(new CaPolicy(id, create.Policy.Name, made["state"]?.GetValue<string>() ?? "disabled", DateTimeOffset.UtcNow, null, made.ToJsonString()));
                return true;
            case UpdateCaPolicy update:
                var index = policies.FindIndex(p => p.Id == update.Policy.Id);
                if (index >= 0)
                {
                    var node = JsonNode.Parse(update.Json)!.AsObject();
                    policies[index] = policies[index] with
                    {
                        Name = node["displayName"]?.GetValue<string>() ?? policies[index].Name,
                        State = node["state"]?.GetValue<string>() ?? policies[index].State,
                        Modified = DateTimeOffset.UtcNow,
                        Json = node.ToJsonString(),
                    };
                }
                return true;
            default:
                return false;
        }
    }
}
