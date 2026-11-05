using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>
/// Mail contacts: made, changed and deleted through Exchange (Graph only reads them), then read
/// back into the local copy so the Contacts list and group pages follow.
/// </summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>How long a new contact can take to get its Entra ID (Exchange writes it through).</summary>
    static readonly TimeSpan[] NewContactWaits = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    /// <summary>
    /// How long to wait for Entra to list a contact Exchange has just made (measured live: about 3 s)
    /// before adding it to security groups, which go through Graph.
    /// </summary>
    static readonly TimeSpan[] EntraCopyWaits = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8)];

    /// <summary>The contact as Entra has it, once Entra lists it; null if it still doesn't.</summary>
    async Task<JsonElement?> WaitForEntraContactAsync(string id, CancellationToken cancellationToken)
    {
        foreach (var wait in EntraCopyWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            try
            {
                return await graph.GetAsync($"contacts/{id}?$select={DirectorySync.ContactSelect}", cancellationToken);
            }
            catch (GraphException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
            {
            }
        }
        return null;
    }

    /// <summary>
    /// New-MailContact, then its company and address list setting, then its lists. Once it exists
    /// the change counts as applied; a later part that fails is named so it can be finished by hand.
    /// </summary>
    async Task<ApplyResult> CreateContactAsync(CreateContact create, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        var address = create.Contact.Address!;
        try
        {
            await exchange.InvokeAsync(CmdletEndpoint.Exchange, "New-MailContact", NewContactParameters(create), cancellationToken);
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Explanation);
        }

        // Its Entra ID, which groups and the local copy know it by.
        string? id = null;
        foreach (var wait in NewContactWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            try
            {
                var rows = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailContact",
                    new Dictionary<string, object?> { ["Identity"] = address }, cancellationToken, DirectorySync.MailContactSelect);
                if (rows.Count > 0 && rows[0].TryGetProperty("ExternalDirectoryObjectId", out var e) && e.GetString() is { Length: > 0 } found)
                {
                    id = found;
                    break;
                }
            }
            catch (CmdletException e) when (e.IsNotFound)
            {
            }
        }

        var unfinished = new List<string>();
        var identity = id ?? address;
        if (create.Company is { Length: > 0 } company)
            await TryAsync("company", () => exchange.InvokeAsync(CmdletEndpoint.Exchange, "Set-Contact",
                new Dictionary<string, object?> { ["Identity"] = identity, ["Company"] = company }, cancellationToken));
        if (create.Hidden)
            await TryAsync("hiding it from the address list", () => exchange.InvokeAsync(CmdletEndpoint.Exchange, "Set-MailContact",
                new Dictionary<string, object?> { ["Identity"] = identity, ["HiddenFromAddressListsEnabled"] = true }, cancellationToken));
        foreach (var list in create.Lists.Where(l => l.ExchangeManaged))
            await TryAsync(list.Group.Name, () => exchange.InvokeAsync(CmdletEndpoint.Exchange, "Add-DistributionGroupMember",
                new Dictionary<string, object?> { ["Identity"] = list.Group.Id, ["Member"] = address, ["BypassSecurityGroupManagerCheck"] = true }, cancellationToken));

        // Entra lists the contact a few seconds after Exchange makes it: security groups (Graph) and
        // the local copy wait for that.
        var entra = id is null ? null : await WaitForEntraContactAsync(id, cancellationToken);
        foreach (var list in create.Lists.Where(l => !l.ExchangeManaged))
        {
            if (entra is null)
                unfinished.Add($"{list.Group.Name} (Entra doesn't list the contact yet; add it from the group in a few minutes)");
            else
                await TryAsync(list.Group.Name, () => graph.PostAsync($"groups/{list.Group.Id}/members/$ref",
                    new JsonObject { ["@odata.id"] = DirectoryObjects + id }, cancellationToken));
        }

        if (id is not null)
        {
            // What Entra listed, with what Tenantry has just set on top (Entra can still be catching up),
            // or, if Entra doesn't list it yet, what was sent: the Contacts list shows it either way.
            var row = entra is { } e ? JsonNode.Parse(e.GetRawText())!.AsObject() : new JsonObject { ["id"] = id };
            row["displayName"] = create.Contact.Name;
            row["mail"] = address;
            if (create.FirstName is { Length: > 0 } f)
                row["givenName"] = f;
            if (create.LastName is { Length: > 0 } l)
                row["surname"] = l;
            if (create.Company is { Length: > 0 } c)
                row["companyName"] = c;
            store.Apply(DirectorySync.Contacts, [new EntityChange(id, row)]);
            store.Apply(DirectorySync.MailContacts, [new EntityChange(id, new JsonObject
            {
                ["ExternalDirectoryObjectId"] = id, ["HiddenFromAddressListsEnabled"] = create.Hidden, ["ExternalEmailAddress"] = "SMTP:" + address,
            })]);
        }
        foreach (var list in create.Lists)
            await IntoLocalCopyAsync(sync => sync.RefreshGroupAsync(list.Group.Id, cancellationToken));

        return new ApplyResult(ChangeOutcome.Applied,
            unfinished.Count == 0 ? null : "The contact was made, but these didn't go through: " + string.Join("; ", unfinished) + ".");

        async Task TryAsync(string part, Func<Task> call)
        {
            try
            {
                await call();
            }
            catch (CmdletException e)
            {
                unfinished.Add($"{part} ({e.Explanation})");
            }
            catch (GraphException e)
            {
                unfinished.Add($"{part} ({Describe(e)})");
            }
        }
    }

    /// <summary>The New-MailContact call (its name must be unique among recipients; the display name is the same).</summary>
    internal static Dictionary<string, object?> NewContactParameters(CreateContact create)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Name"] = create.Contact.Name,
            ["DisplayName"] = create.Contact.Name,
            ["ExternalEmailAddress"] = create.Contact.Address,
        };
        if (create.FirstName is { Length: > 0 } first)
            parameters["FirstName"] = first;
        if (create.LastName is { Length: > 0 } last)
            parameters["LastName"] = last;
        return parameters;
    }

    /// <summary>The PowerShell for a new contact: the same calls, in the same order, by its address.</summary>
    static Tenantry.Core.PowerShell.PsScript NewContactScript(CreateContact create)
    {
        var address = create.Contact.Address;
        var steps = new List<string> { Tenantry.Core.PowerShell.Ps.Cmdlet("New-MailContact", NewContactParameters(create)) };
        if (create.Company is { Length: > 0 } company)
            steps.Add(Tenantry.Core.PowerShell.Ps.Cmdlet("Set-Contact", new Dictionary<string, object?> { ["Identity"] = address, ["Company"] = company }));
        if (create.Hidden)
            steps.Add(Tenantry.Core.PowerShell.Ps.Cmdlet("Set-MailContact", new Dictionary<string, object?> { ["Identity"] = address, ["HiddenFromAddressListsEnabled"] = true }));
        var graph = false;
        foreach (var list in create.Lists)
        {
            if (list.ExchangeManaged)
                steps.Add(Tenantry.Core.PowerShell.Ps.Cmdlet("Add-DistributionGroupMember",
                    new Dictionary<string, object?> { ["Identity"] = list.Group.Id, ["Member"] = address, ["BypassSecurityGroupManagerCheck"] = true }));
            else
            {
                graph = true;
                steps.Add(Tenantry.Core.PowerShell.Ps.Graph("POST", $"groups/{list.Group.Id}/members/$ref",
                    new JsonObject { ["@odata.id"] = DirectoryObjects + "<the contact's ExternalDirectoryObjectId>" }));
            }
        }
        return new Tenantry.Core.PowerShell.PsScript(Tenantry.Core.PowerShell.Ps.Lines(steps),
            graph ? Tenantry.Core.PowerShell.PsService.Graph | Tenantry.Core.PowerShell.PsService.Exchange : Tenantry.Core.PowerShell.PsService.Exchange,
            graph ? "Security groups add the contact by its Entra ID: read it with Get-MailContact (ExternalDirectoryObjectId) once it's made." : null);
    }

    /// <summary>Set-MailContact or Set-Contact for one detail; an emptied one is cleared.</summary>
    internal static (string Cmdlet, Dictionary<string, object?> Parameters) ContactTextCall(SetContactText change)
    {
        var mail = SetContactText.MailContactParameters.Contains(change.Parameter);
        var value = string.IsNullOrWhiteSpace(change.New) ? null : change.New.Trim();
        return (mail ? "Set-MailContact" : "Set-Contact", new Dictionary<string, object?> { ["Identity"] = change.Contact.Id, [change.Parameter] = value });
    }

    async Task<ApplyResult> SetContactTextAsync(SetContactText change, CancellationToken cancellationToken)
    {
        var (cmdlet, parameters) = ContactTextCall(change);
        var read = cmdlet == "Set-MailContact" ? "Get-MailContact" : "Get-Contact";
        var expected = string.IsNullOrWhiteSpace(change.New) ? "" : change.New.Trim();
        var result = await RunCheckedAsync(cmdlet, parameters,
            ct => Read(read, new() { ["Identity"] = change.Contact.Id }, ct, change.Parameter),
            rows => rows.Count == 1 && string.Equals(ContactValue(rows[0], change.Parameter), expected, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
        if (result.Succeeded)
            ContactIntoLocalCopy(change);
        return result;
    }

    /// <summary>The Entra property each Exchange contact parameter is copied to.</summary>
    static string? EntraProperty(string parameter) => parameter switch
    {
        "DisplayName" => "displayName",
        "FirstName" => "givenName",
        "LastName" => "surname",
        "Company" => "companyName",
        "Department" => "department",
        "Title" => "jobTitle",
        SetContactText.Address => "mail",
        _ => null,
    };

    /// <summary>
    /// Writes a changed detail straight into the local copy, as Entra will show it: Exchange has the
    /// change at once, Entra a few seconds later (measured live), so reading Entra now would undo it.
    /// </summary>
    void ContactIntoLocalCopy(SetContactText change)
    {
        var value = string.IsNullOrWhiteSpace(change.New) ? null : change.New.Trim();
        var id = change.Contact.Id;
        if (change.Parameter is "Phone" or "MobilePhone")
        {
            var type = change.Parameter == "Phone" ? "business" : "mobile";
            var phones = new JsonArray();
            if (store.Get(DirectorySync.Contacts, id) is { } json && JsonNode.Parse(json)?["phones"] is JsonArray stored)
                foreach (var p in stored)
                    if (p?["type"]?.GetValue<string>() != type)
                        phones.Add(p!.DeepClone());
            if (value is not null)
                phones.Add(new JsonObject { ["type"] = type, ["number"] = value });
            store.Apply(DirectorySync.Contacts, [new EntityChange(id, new JsonObject { ["id"] = id, ["phones"] = phones }, OnlyIfExists: true)]);
            return;
        }
        if (EntraProperty(change.Parameter) is { } property)
            store.Apply(DirectorySync.Contacts, [new EntityChange(id, new JsonObject { ["id"] = id, [property] = value }, OnlyIfExists: true)]);
        if (change.Parameter == SetContactText.Address)
            store.Apply(DirectorySync.MailContacts, [new EntityChange(id, new JsonObject { ["ExternalEmailAddress"] = value is null ? null : "SMTP:" + value }, OnlyIfExists: true)]);
    }

    /// <summary>A detail as Exchange shows it, as text ("SMTP:" dropped from an address).</summary>
    static string ContactValue(JsonElement row, string parameter) =>
        parameter == SetContactText.Address ? DirectorySync.SmtpAddress(row, parameter) ?? ""
        : row.TryGetProperty(parameter, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    async Task<ApplyResult> SetContactHiddenAsync(SetHiddenFromAddressList change, CancellationToken cancellationToken)
    {
        var result = await RunCheckedAsync("Set-MailContact",
            new() { ["Identity"] = change.User.Id, ["HiddenFromAddressListsEnabled"] = change.New },
            ct => Read("Get-MailContact", new() { ["Identity"] = change.User.Id }, ct, "HiddenFromAddressListsEnabled"),
            rows => rows.Count == 1 && rows[0].TryGetProperty("HiddenFromAddressListsEnabled", out var h) && h.ValueKind == (change.New ? JsonValueKind.True : JsonValueKind.False),
            cancellationToken);
        // Exchange keeps this setting (Entra doesn't have it): what it read back is current.
        if (result.Succeeded)
            store.Apply(DirectorySync.MailContacts, [new EntityChange(change.User.Id, new JsonObject
            {
                ["ExternalDirectoryObjectId"] = change.User.Id, ["HiddenFromAddressListsEnabled"] = change.New,
            }, OnlyIfExists: true)]);
        return result;
    }

    /// <summary>Remove-MailContact; the local copy drops it, and it leaves the lists it was on there too.</summary>
    async Task<ApplyResult> DeleteContactAsync(DeleteContact change, CancellationToken cancellationToken)
    {
        var result = await RunCheckedAsync("Remove-MailContact", new() { ["Identity"] = change.Contact.Id, ["Confirm"] = false },
            ct => Read("Get-MailContact", new() { ["Identity"] = change.Contact.Id }, ct, "ExternalDirectoryObjectId"),
            rows => rows.Count == 0, cancellationToken);
        if (!result.Succeeded)
            return result;
        store.Apply(DirectorySync.Contacts, [EntityChange.Removed(change.Contact.Id)]);
        store.Apply(DirectorySync.MailContacts, [EntityChange.Removed(change.Contact.Id)]);
        var lists = new List<EntityChange>();
        foreach (var json in store.All(DirectorySync.Groups))
        {
            if (JsonNode.Parse(json) is not JsonObject group || group["members"] is not JsonObject members || !members.ContainsKey(change.Contact.Id))
                continue;
            var kept = members.DeepClone().AsObject();
            kept.Remove(change.Contact.Id);
            lists.Add(new EntityChange(group["id"]!.GetValue<string>(), new JsonObject { ["id"] = group["id"]!.GetValue<string>(), ["members"] = kept }, OnlyIfExists: true));
        }
        store.Apply(DirectorySync.Groups, lists);
        return result;
    }
}
