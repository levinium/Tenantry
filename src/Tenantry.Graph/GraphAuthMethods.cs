using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>
/// The authentication methods policy (8d-1), read and changed through beta, which lists every method
/// the admin center shows (v1.0 leaves out hardware tokens, passwords and federated credentials).
/// Group IDs in its targets are named in one batched read.
/// </summary>
public sealed partial class GraphUserRecords : IAuthMethodRecords
{
    const string MethodsPolicy = "https://graph.microsoft.com/beta/policies/authenticationMethodsPolicy";

    public async Task<AuthMethodsPolicy> GetAuthMethodsAsync(CancellationToken cancellationToken = default)
    {
        var policyRead = graph.GetAsync(MethodsPolicy, cancellationToken);
        var defaultsRead = graph.GetAsync("policies/identitySecurityDefaultsEnforcementPolicy", cancellationToken);
        var policy = await policyRead;
        var defaults = await defaultsRead;

        // Every group ID the policy names, wherever it sits (targets, feature settings).
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("targetType", out _) && e.TryGetProperty("id", out var id) && id.GetString() is { } value && Guid.TryParse(value, out _))
                        ids.Add(value);
                    foreach (var p in e.EnumerateObject())
                        Collect(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var x in e.EnumerateArray())
                        Collect(x);
                    break;
            }
        }
        Collect(policy);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in ids.Chunk(1000))
        {
            var answer = await graph.PostAsync("directoryObjects/getByIds",
                new JsonObject { ["ids"] = new JsonArray([.. chunk.Select(i => (JsonNode)i)]), ["types"] = new JsonArray("group") }, cancellationToken);
            foreach (var g in answer.GetProperty("value").EnumerateArray())
                names[Text(g, "id") ?? ""] = Text(g, "displayName") ?? "A group";
        }
        return new AuthMethodsPolicy(policy.GetRawText(), defaults.TryGetProperty("isEnabled", out var on) && on.ValueKind == JsonValueKind.True, names);
    }
}

public sealed partial class GraphChangeExecutor
{
    const string AuthMethodsPolicyPath = "https://graph.microsoft.com/beta/policies/authenticationMethodsPolicy";

    async Task<ApplyResult> SetAuthMethodConfigAsync(SetAuthMethodConfig change, CancellationToken cancellationToken)
    {
        var path = change.Method.Kind == "authpolicy" ? AuthMethodsPolicyPath : $"{AuthMethodsPolicyPath}/authenticationMethodConfigurations/{change.Method.Id}";
        try
        {
            await graph.PatchAsync(path, JsonNode.Parse(change.Json)!.AsObject(), cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>Entra refuses security defaults while Conditional Access policies are on; its words say so.</summary>
    async Task<ApplyResult> SetSecurityDefaultsAsync(SetSecurityDefaults change, CancellationToken cancellationToken)
    {
        try
        {
            await graph.PatchAsync("policies/identitySecurityDefaultsEnforcementPolicy", new JsonObject { ["isEnabled"] = change.New }, cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }
}
