using System.Net;
using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>
/// Mail contacts: Entra's through <c>contacts/delta</c> (changes only, like users and groups), and
/// Exchange's Get-MailContact with the mailbox read (the address list setting, which Graph doesn't
/// have). Groups list contacts as members by their Entra ID, so this is also how they're named there.
/// </summary>
public sealed partial class DirectorySync
{
    /// <summary>Entra's contacts (orgContact), keyed by object ID.</summary>
    public const string Contacts = "contact";

    /// <summary>Exchange's mail contacts, keyed by ExternalDirectoryObjectId (the Entra ID).</summary>
    public const string MailContacts = "mailcontact";

    /// <summary>The contact properties the local copy keeps (also what a new contact is read back with).</summary>
    public const string ContactSelect = "id,displayName,givenName,surname,mail,mailNickname,companyName,department,jobTitle,phones,onPremisesSyncEnabled";

    /// <summary>The Get-MailContact properties kept.</summary>
    public static readonly string[] MailContactSelect =
        ["ExternalDirectoryObjectId", "HiddenFromAddressListsEnabled", "ExternalEmailAddress", "PrimarySmtpAddress", "Alias"];

    /// <summary>Entra's contacts, changes only. No permission to read them is a note, not a failed sync.</summary>
    async Task<int> SyncContactsAsync(List<string> notes, SyncScope scope, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report("Checking contacts for changes…");
            return await SyncDeltaAsync(Contacts, Contacts, $"contacts/delta?$select={ContactSelect}", "contacts", scope,
                _ => item => new EntityChange(Id(item), ToObject(item)), progress, cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            notes.Add("Contacts weren't read: reading them needs a role that can read the directory.");
            return 0;
        }
    }

    /// <summary>Every mail contact in one call, stored whole (contacts removed since drop out).</summary>
    async Task<int> SyncMailContactsAsync(List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailContact",
                new Dictionary<string, object?> { ["ResultSize"] = "Unlimited" }, cancellationToken, MailContactSelect);
            var changes = new List<EntityChange>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!row.TryGetProperty("ExternalDirectoryObjectId", out var owner) || owner.GetString() is not { Length: > 0 } id)
                    continue;
                seen.Add(id);
                changes.Add(new EntityChange(id, ToObject(row)));
            }
            return store.Apply(MailContacts, changes) + store.RemoveExcept(MailContacts, seen);
        }
        catch (CmdletException e)
        {
            notes.Add($"Contacts' address list settings weren't read: {e.Explanation}");
            return 0;
        }
    }

    /// <summary>
    /// One contact re-read (after a change, or its page opening): Entra's details and, when
    /// Exchange is connected, its mail settings. Gone from Entra means deleted: it leaves the local copy.
    /// </summary>
    public async Task RefreshContactAsync(string contactId, CancellationToken cancellationToken = default)
    {
        try
        {
            var contact = await graph.GetAsync($"contacts/{contactId}?$select={ContactSelect}", cancellationToken);
            store.Apply(Contacts, [new EntityChange(contactId, ToObject(contact))]);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
            store.Apply(Contacts, [EntityChange.Removed(contactId)]);
            store.Apply(MailContacts, [EntityChange.Removed(contactId)]);
            return;
        }
        if (exchange is null)
            return;
        try
        {
            var rows = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailContact",
                new Dictionary<string, object?> { ["Identity"] = contactId }, cancellationToken, MailContactSelect);
            if (rows.Count > 0)
                store.Apply(MailContacts, [new EntityChange(contactId, ToObject(rows[0]))]);
        }
        catch (CmdletException)
        {
            // Its mail settings stay as last read.
        }
    }

    /// <summary>"SMTP:avery@fabrikam.example" → "avery@fabrikam.example".</summary>
    public static string? SmtpAddress(JsonElement row, string property) =>
        row.TryGetProperty(property, out var v) && v.GetString() is { Length: > 0 } s
            ? s.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? s[5..] : s
            : null;
}
