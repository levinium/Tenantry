using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>Security &amp; sign-in changes (4c-1): sign-in methods, Temporary Access Passes, roles, app consents, risk.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>
    /// Sends one Graph write, then reads back with <paramref name="read"/> until
    /// <paramref name="done"/> holds; a read answered "not found" counts as an empty answer.
    /// </summary>
    async Task<ApplyResult> GraphCheckedAsync(Func<CancellationToken, Task> write, Func<CancellationToken, Task<JsonElement>>? read,
        Func<JsonElement?, bool> done, CancellationToken cancellationToken)
    {
        try
        {
            await write(cancellationToken);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
        if (read is null)
            return new ApplyResult(ChangeOutcome.Applied);
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            JsonElement? answer;
            try
            {
                answer = await read(cancellationToken);
            }
            catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
            {
                answer = null;
            }
            if (done(answer))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Microsoft 365 accepted the change but still shows the old state; it usually appears within a minute.");
    }

    static IEnumerable<JsonElement> Values(JsonElement? page) =>
        page is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    Task<ApplyResult> RemoveAuthMethodAsync(RemoveAuthMethod change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.DeleteAsync($"users/{change.User.Id}/authentication/{change.Segment}/{change.MethodId}", ct),
            ct => graph.GetAsync($"users/{change.User.Id}/authentication/methods", ct),
            page => !Values(page).Any(m => m.TryGetProperty("id", out var id) && id.GetString() == change.MethodId),
            cancellationToken);

    Task<ApplyResult> AddPhoneMethodAsync(AddPhoneMethod change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PostAsync($"users/{change.User.Id}/authentication/phoneMethods",
                new JsonObject { ["phoneNumber"] = change.Number, ["phoneType"] = change.PhoneType }, ct),
            ct => graph.GetAsync($"users/{change.User.Id}/authentication/phoneMethods", ct),
            page => Values(page).Any(m => m.TryGetProperty("phoneType", out var t) && t.GetString() == change.PhoneType),
            cancellationToken);

    /// <summary>The pass comes back once, in the answer, and is handed to the admin (never stored).</summary>
    async Task<ApplyResult> CreateTemporaryAccessPassAsync(CreateTemporaryAccessPass change, CancellationToken cancellationToken)
    {
        try
        {
            var created = await graph.PostAsync($"users/{change.User.Id}/authentication/temporaryAccessPassMethods",
                new JsonObject { ["lifetimeInMinutes"] = change.Minutes, ["isUsableOnce"] = change.OneTime }, cancellationToken);
            var pass = created.TryGetProperty("temporaryAccessPass", out var p) ? p.GetString() : null;
            return pass is null
                ? new ApplyResult(ChangeOutcome.Failed, "Microsoft 365 created the pass but didn't return it.")
                : new ApplyResult(ChangeOutcome.Applied, "Pass them this code; they sign in with it and set up MFA.", Reveal: pass);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    Task<ApplyResult> RemoveRoleAssignmentAsync(RemoveRoleAssignment change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.DeleteAsync($"roleManagement/directory/roleAssignments/{change.AssignmentId}", ct),
            ct => graph.GetAsync($"roleManagement/directory/roleAssignments/{change.AssignmentId}", ct),
            answer => answer is null,
            cancellationToken);

    Task<ApplyResult> RevokeAppConsentAsync(RevokeAppConsent change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.DeleteAsync($"oauth2PermissionGrants/{change.GrantId}", ct),
            ct => graph.GetAsync($"oauth2PermissionGrants/{change.GrantId}", ct),
            answer => answer is null,
            cancellationToken);

    Task<ApplyResult> SetUserRiskAsync(SetUserRisk change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PostAsync($"identityProtection/riskyUsers/{(change.Compromised ? "confirmCompromised" : "dismiss")}",
                new JsonObject { ["userIds"] = new JsonArray(change.User.Id) }, ct),
            null, _ => true, cancellationToken);
}
