using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Deleted groups (directory/deletedItems), and testing a dynamic rule against a person.</summary>
public sealed partial class GraphUserRecords : IGroupRecords
{
    public async Task<IReadOnlyList<DeletedGroup>> GetDeletedGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = new List<DeletedGroup>();
        await foreach (var g in graph.GetAllAsync(
                           "directory/deletedItems/microsoft.graph.group?$select=id,displayName,mail,groupTypes,mailEnabled,securityEnabled,deletedDateTime&$top=999",
                           cancellationToken))
        {
            var summary = CachedTenantDirectory.ToGroup(g, new HashSet<string>());
            DateTimeOffset? deleted = g.TryGetProperty("deletedDateTime", out var d) && d.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(d.GetString()!, CultureInfo.InvariantCulture)
                : null;
            groups.Add(new DeletedGroup(summary.Id, summary.DisplayName, summary.Mail, summary.Kind, deleted));
        }
        return groups;
    }

    /// <summary>
    /// Entra's own evaluation of an unsaved rule for one person (beta; there's no syntax-only check).
    /// A mistake in the rule comes back as Entra's message.
    /// </summary>
    public async Task<RuleTest> TestRuleAsync(string rule, string personId, string personName, CancellationToken cancellationToken = default)
    {
        try
        {
            var answer = await graph.PostAsync("https://graph.microsoft.com/beta/groups/evaluateDynamicMembership",
                new JsonObject { ["memberId"] = personId, ["membershipRule"] = rule }, cancellationToken);
            return new RuleTest(personId, personName,
                answer.TryGetProperty("membershipRuleEvaluationResult", out var r) && r.ValueKind == JsonValueKind.True, null);
        }
        catch (GraphException e) when (e.Status == System.Net.HttpStatusCode.Forbidden)
        {
            // Seen live: Entra refuses rule tests to an admin whose role comes through a group (Microsoft
            // documents that indirect role assignment isn't supported here).
            return new RuleTest(personId, personName, null,
                "Entra won't test rules for this sign-in. Testing needs an admin role (Groups Administrator or higher) assigned to you directly, not through a group. The rule can still be saved.");
        }
        catch (GraphException e)
        {
            return new RuleTest(personId, personName, null, e.Message);
        }
    }
}
