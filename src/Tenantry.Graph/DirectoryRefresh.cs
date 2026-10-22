using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>
/// One object re-read on demand (a record page opening), stored in exactly the shapes the sync
/// stores, so the lists and the page read it back as they would after a sync.
/// </summary>
public sealed partial class DirectorySync : IRecordRefresher
{
    public async Task RefreshUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        JsonElement user;
        try
        {
            user = await ReadUserAsync(userId, cancellationToken);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            // Deleted since the last sync: it leaves the local copy now rather than at the next one.
            store.Apply(Users, [EntityChange.Removed(userId)]);
            store.Apply(Mailboxes, [EntityChange.Removed(userId)]);
            return;
        }
        store.Apply(Users, [new EntityChange(userId, ToObject(user))]);

        if (exchange is null || user.TryGetProperty("userPrincipalName", out var upn) is false || upn.GetString() is not { Length: > 0 } identity)
            return;
        await RefreshMailboxAsync(userId, identity, cancellationToken);
    }

    /// <summary>The user with last sign-in when the tenant has it (Entra ID P1); without it otherwise.</summary>
    async Task<JsonElement> ReadUserAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            return await graph.GetAsync($"users/{userId}?$select={UserSelect},signInActivity", cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            return await graph.GetAsync($"users/{userId}?$select={UserSelect}", cancellationToken);
        }
    }

    /// <summary>Get-Mailbox and Get-CASMailbox for one mailbox (read together), plus who can use it when it's shared.</summary>
    async Task RefreshMailboxAsync(string userId, string identity, CancellationToken cancellationToken)
    {
        var who = new Dictionary<string, object?> { ["Identity"] = identity };
        var mailboxRead = exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox", who, cancellationToken, MailboxSelect);
        var casRead = exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-CASMailbox", who, cancellationToken, CasSelect);
        IReadOnlyList<JsonElement> mailbox;
        try
        {
            mailbox = await mailboxRead;
        }
        catch (CmdletException e) when (e.IsNotFound)
        {
            // No mailbox (any more): unlicensed, or a license without Exchange.
            store.Apply(Mailboxes, [EntityChange.Removed(userId)]);
            store.Apply(CasMailboxes, [EntityChange.Removed(userId)]);
            await Quietly(casRead);
            return;
        }
        if (mailbox.Count == 0)
            return;
        store.Apply(Mailboxes, [new EntityChange(userId, ToObject(mailbox[0]))]);
        try
        {
            if ((await casRead).FirstOrDefault() is { ValueKind: JsonValueKind.Object } cas)
                store.Apply(CasMailboxes, [new EntityChange(userId, ToObject(cas))]);
        }
        catch (CmdletException)
        {
            // Email apps stay as last synced.
        }
        if (mailbox[0].TryGetProperty("RecipientTypeDetails", out var type) && SharedTypes.Contains(type.GetString() ?? "")
            && mailbox[0].TryGetProperty("PrimarySmtpAddress", out var address) && address.GetString() is { Length: > 0 } primary)
            store.Apply(MailboxAccess, [new EntityChange(userId, await ReadMailboxAccessAsync(exchange, userId, primary, cancellationToken))]);
    }

    static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (CmdletException)
        {
        }
    }

    public async Task RefreshGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        // A shared mailbox's row is keyed by its account, which isn't in the groups store.
        if (store.Get(Groups, groupId) is null && store.Get(Mailboxes, groupId) is not null)
        {
            await RefreshUserAsync(groupId, cancellationToken);
            return;
        }
        JsonElement group;
        try
        {
            group = await graph.GetAsync($"groups/{groupId}?$select={GroupSelect.Replace(",members,owners", "", StringComparison.Ordinal)}", cancellationToken);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            store.Apply(Groups, [EntityChange.Removed(groupId)]);
            return;
        }
        var properties = ToObject(group);
        // Members and owners as the delta sync keeps them: a map of id → type.
        var members = ReadRelationAsync(groupId, "members", cancellationToken);
        var owners = ReadRelationAsync(groupId, "owners", cancellationToken);
        properties["members"] = await members;
        properties["owners"] = await owners;
        store.Apply(Groups, [new EntityChange(groupId, properties)]);
    }

    async Task<JsonObject> ReadRelationAsync(string groupId, string relation, CancellationToken cancellationToken)
    {
        var map = new JsonObject();
        await foreach (var entry in graph.GetAllAsync($"groups/{groupId}/{relation}?$select=id&$top=999", cancellationToken))
            map[Id(entry)] = TypeName(entry);
        return map;
    }
}
