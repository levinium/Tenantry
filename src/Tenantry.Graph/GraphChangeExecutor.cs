using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>
/// Applies changes through Graph, then reads the object back to confirm the new value and
/// writes what it read into the local copy, so lists show the change at once. Microsoft 365 can
/// take a few seconds to show a write on reading it back, so the read is retried briefly; a
/// change still not visible after that is reported as applied-but-not-yet-visible, not failed.
/// </summary>
public sealed class GraphChangeExecutor(GraphClient graph, IEntityStore store, Func<TimeSpan, CancellationToken, Task>? delay = null) : IChangeExecutor
{
    static readonly TimeSpan[] ReadBackWaits = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => change switch
    {
        // Phones are a list in Graph: the edited number becomes the only entry (or none).
        SetUserText { Property: "businessPhones" } t => await PatchUserAsync(t.User.Id, t.Property,
            string.IsNullOrWhiteSpace(t.New) ? new JsonArray() : new JsonArray(t.New.Trim()), cancellationToken),
        // An emptied text property is cleared (null), not set to "".
        SetUserText t => await PatchUserAsync(t.User.Id, t.Property, string.IsNullOrWhiteSpace(t.New) ? null : JsonValue.Create(t.New.Trim()), cancellationToken),
        SetSignInAllowed s => await PatchUserAsync(s.User.Id, "accountEnabled", JsonValue.Create(s.New), cancellationToken),
        ResetPassword r => await ResetPasswordAsync(r, cancellationToken),
        RevokeSessions r => await RevokeSessionsAsync(r, cancellationToken),
        SetLicenseAssigned l => await AssignLicenseAsync(l.User.Id,
            l.New ? new JsonArray(new JsonObject { ["skuId"] = l.SkuId, ["disabledPlans"] = new JsonArray() }) : [],
            l.New ? [] : new JsonArray(l.SkuId),
            licenses => licenses.Any(x => x.SkuId == l.SkuId) == l.New, cancellationToken),
        SetLicenseApps a => await AssignLicenseAsync(a.User.Id,
            new JsonArray(new JsonObject { ["skuId"] = a.SkuId, ["disabledPlans"] = new JsonArray(a.NewDisabled.Select(p => (JsonNode)p).ToArray()) }),
            [],
            licenses => licenses.FirstOrDefault(x => x.SkuId == a.SkuId) is { } x && x.Disabled.ToHashSet().SetEquals(a.NewDisabled), cancellationToken),
        RemoveAppAssignment r => await RemoveAppAssignmentAsync(r, cancellationToken),
        _ => new ApplyResult(ChangeOutcome.Failed, $"Tenantry can't apply \"{change.Label}\" changes yet."),
    };

    /// <summary>
    /// Sets a generated temporary password and hands it back once (<see cref="ApplyResult.Reveal"/>)
    /// for the admin to pass on. It is never stored. There is nothing to read back: Graph never
    /// returns passwords.
    /// </summary>
    async Task<ApplyResult> ResetPasswordAsync(ResetPassword reset, CancellationToken cancellationToken)
    {
        var password = TemporaryPassword.Create();
        try
        {
            await graph.PatchAsync($"users/{reset.User.Id}", new JsonObject
            {
                ["passwordProfile"] = new JsonObject
                {
                    ["password"] = password,
                    ["forceChangePasswordNextSignIn"] = reset.MustChangeAtNextSignIn,
                },
            }, cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied, Reveal: password);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    /// <summary>
    /// Adds or removes licenses (or changes a license's apps, by re-adding it with a new list of
    /// disabled plans), reads the user's licenses back into the local copy, and refreshes the
    /// tenant's license counts so "available" stays right.
    /// </summary>
    async Task<ApplyResult> AssignLicenseAsync(string userId, JsonArray add, JsonArray remove,
        Func<IReadOnlyList<(string SkuId, IReadOnlyList<string> Disabled)>, bool> done, CancellationToken cancellationToken)
    {
        try
        {
            await graph.PostAsync($"users/{userId}/assignLicense", new JsonObject { ["addLicenses"] = add, ["removeLicenses"] = remove }, cancellationToken);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }

        await RefreshSkusAsync(cancellationToken);
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var user = await graph.GetAsync($"users/{userId}?$select=id,assignedLicenses", cancellationToken);
            store.Apply(DirectorySync.Users, [new EntityChange(userId, JsonNode.Parse(user.GetRawText())!.AsObject(), OnlyIfExists: true)]);
            var licenses = user.GetProperty("assignedLicenses").EnumerateArray()
                .Select(l => (l.GetProperty("skuId").GetString()!,
                    (IReadOnlyList<string>)(l.TryGetProperty("disabledPlans", out var d) ? d.EnumerateArray().Select(p => p.GetString()!).ToArray() : [])))
                .ToArray();
            if (done(licenses))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Microsoft 365 accepted the change but still shows the old licenses; it usually appears within a minute.");
    }

    async Task RefreshSkusAsync(CancellationToken cancellationToken)
    {
        var changes = new List<EntityChange>();
        await foreach (var sku in graph.GetAllAsync($"subscribedSkus?$select={DirectorySync.SkuSelect}", cancellationToken))
            changes.Add(new EntityChange(sku.GetProperty("skuId").GetString()!, JsonNode.Parse(sku.GetRawText())!.AsObject()));
        store.Apply(DirectorySync.Skus, changes);
    }

    async Task<ApplyResult> RemoveAppAssignmentAsync(RemoveAppAssignment remove, CancellationToken cancellationToken)
    {
        try
        {
            await graph.DeleteAsync($"users/{remove.User.Id}/appRoleAssignments/{remove.AssignmentId}", cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    async Task<ApplyResult> RevokeSessionsAsync(RevokeSessions revoke, CancellationToken cancellationToken)
    {
        try
        {
            await graph.PostAsync($"users/{revoke.User.Id}/revokeSignInSessions", new JsonObject(), cancellationToken);
            return new ApplyResult(ChangeOutcome.Applied, "Signed out of every session; apps ask them to sign in again within about an hour.");
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    async Task<ApplyResult> PatchUserAsync(string id, string property, JsonNode? value, CancellationToken cancellationToken)
    {
        try
        {
            await graph.PatchAsync($"users/{id}", new JsonObject { [property] = value?.DeepClone() }, cancellationToken);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }

        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var user = await graph.GetAsync($"users/{id}?$select=id,{property}", cancellationToken);
            store.Apply(DirectorySync.Users, [new EntityChange(id, JsonNode.Parse(user.GetRawText())!.AsObject(), OnlyIfExists: true)]);
            // Compared as values, not text: "+1 555" and "+1 555" are the same number.
            var actual = user.TryGetProperty(property, out var v) ? JsonNode.Parse(v.GetRawText()) : null;
            if (JsonNode.DeepEquals(actual, value))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Microsoft 365 accepted the change but still shows the old value; it usually appears within a minute.");
    }

    /// <summary>Graph's own words, minus the status-code prefix an admin doesn't need.</summary>
    static string Describe(GraphException e) =>
        e.Status switch
        {
            System.Net.HttpStatusCode.Forbidden => $"Not allowed: {Message(e)} Your role may not cover this change, or the account is protected (for example an administrator).",
            _ => Message(e),
        };

    static string Message(GraphException e)
    {
        var text = e.Message;
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 ? text[(colon + 2)..] : text;
    }
}
