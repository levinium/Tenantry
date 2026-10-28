using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>
/// Applies changes through Graph, then reads the object back to confirm the new value and
/// writes what it read into the local copy, so lists show the change at once. Microsoft 365 can
/// take a few seconds to show a write on reading it back, so the read is retried briefly; a
/// change still not visible after that is reported as applied-but-not-yet-visible, not failed.
/// </summary>
/// <remarks>
/// Distribution lists, mail-enabled security groups and mailbox permissions change through
/// Exchange (<paramref name="exchange"/>), and OneDrive access through the SharePoint admin site
/// (<paramref name="sharePoint"/>); without them, those changes fail with a reason. Passwords it
/// makes follow the tenant's <paramref name="passwordStyle"/> (read at each reset).
/// </remarks>
public sealed partial class GraphChangeExecutor(GraphClient graph, IEntityStore store, Func<TimeSpan, CancellationToken, Task>? delay = null,
    ICmdletRunner? exchange = null, SharePointAdmin? sharePoint = null, Func<PasswordStyle>? passwordStyle = null) : IChangeExecutor
{
    const string DirectoryObjects = "https://graph.microsoft.com/v1.0/directoryObjects/";

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
        CreateUser c => await CreateUserAsync(c, cancellationToken),
        CreateGroup g => await CreateGroupAsync(g, cancellationToken),
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
        SetGroupMembership m => await SetMembershipAsync(m, cancellationToken),
        SetMailboxAccess a => await SetMailboxAccessAsync(a, cancellationToken),
        SetManager m => await SetManagerAsync(m, cancellationToken),
        SetMailAlias a => await SetMailboxAsync(a.User, new() { ["EmailAddresses"] = AddOrRemove(a.New, "smtp:" + a.Address) },
            row => Addresses(row).Contains(a.Address, StringComparer.OrdinalIgnoreCase) == a.New, cancellationToken),
        SetPrimaryAddress p => await SetMailboxAsync(p.User, new() { ["WindowsEmailAddress"] = p.New },
            row => string.Equals(Text(row, "PrimarySmtpAddress"), p.New, StringComparison.OrdinalIgnoreCase), cancellationToken),
        // Forwarding is one address at a time: an SMTP address replaces any directory recipient.
        SetForwarding f => await SetMailboxAsync(f.User, new()
            {
                ["ForwardingSmtpAddress"] = f.NewTo is null ? null : "smtp:" + f.NewTo,
                ["ForwardingAddress"] = null,
                ["DeliverToMailboxAndForward"] = f.NewTo is not null && f.NewKeepCopy,
            },
            row => string.Equals(StripSmtp(Text(row, "ForwardingSmtpAddress")) ?? "", f.NewTo ?? "", StringComparison.OrdinalIgnoreCase)
                   && string.IsNullOrEmpty(Text(row, "ForwardingAddress")), cancellationToken),
        SetHiddenFromAddressList h => await SetMailboxAsync(h.User, new() { ["HiddenFromAddressListsEnabled"] = h.New },
            row => row.TryGetPropertyValue("HiddenFromAddressListsEnabled", out var v) && v?.GetValueKind() == JsonValueKind.True == h.New, cancellationToken),
        SetAutoReply r => await SetAutoReplyAsync(r, cancellationToken),
        SetMailboxValue v => await SetMailboxValueAsync(v, cancellationToken),
        SetEmailApp a => await SetEmailAppAsync(a, cancellationToken),
        SetArchive a => await SetArchiveAsync(a, cancellationToken),
        EnableAutoExpandingArchive a => await EnableAutoExpandingAsync(a, cancellationToken),
        SetLitigationHold h => await SetLitigationHoldAsync(h, cancellationToken),
        SetAllowedSender s => await SetAllowedSenderAsync(s, cancellationToken),
        SetInboxRuleEnabled r => await SetInboxRuleEnabledAsync(r, cancellationToken),
        RemoveInboxRule r => await RemoveInboxRuleAsync(r, cancellationToken),
        SetFolderPermission f => await SetFolderPermissionAsync(f, cancellationToken),
        SetMobileDevice d => await SetMobileDeviceAsync(d, cancellationToken),
        RestoreDeletedItem r => await RestoreDeletedItemAsync(r, cancellationToken),
        ConvertMailbox c => await ConvertMailboxAsync(c, cancellationToken),
        RemoveAuthMethod m => await RemoveAuthMethodAsync(m, cancellationToken),
        AddPhoneMethod p => await AddPhoneMethodAsync(p, cancellationToken),
        CreateTemporaryAccessPass t => await CreateTemporaryAccessPassAsync(t, cancellationToken),
        RemoveRoleAssignment r => await RemoveRoleAssignmentAsync(r, cancellationToken),
        RevokeAppConsent c => await RevokeAppConsentAsync(c, cancellationToken),
        SetUserRisk r => await SetUserRiskAsync(r, cancellationToken),
        SetDeviceEnabled d => await SetDeviceEnabledAsync(d, cancellationToken),
        RunDeviceAction a => await RunDeviceActionAsync(a, cancellationToken),
        DeleteDevice d => await DeleteDeviceAsync(d, cancellationToken),
        RenameDevice r => await RenameDeviceAsync(r, cancellationToken),
        SetPrimaryUser p => await SetPrimaryUserAsync(p, cancellationToken),
        SetDeviceOwnership o => await SetDeviceOwnershipAsync(o, cancellationToken),
        SetLostMode l => await SetLostModeAsync(l, cancellationToken),
        SetAutopilotGroupTag t => await SetAutopilotGroupTagAsync(t, cancellationToken),
        DeleteAutopilotDevice a => await DeleteAutopilotDeviceAsync(a, cancellationToken),
        SetOneDriveAccess a => await SetOneDriveAccessAsync(a, cancellationToken),
        ReleaseQuarantined r => await ReleaseQuarantinedAsync(r, cancellationToken),
        DeleteQuarantined d => await DeleteQuarantinedAsync(d, cancellationToken),
        SetSenderListed s => await SetSenderListedAsync(s, cancellationToken),
        AddListEntry a => await AddListEntryAsync(a, cancellationToken),
        RemoveListEntry r => await RemoveListEntryAsync(r, cancellationToken),
        UpdateListEntry u => await UpdateListEntryAsync(u, cancellationToken),
        SetRuleEnabled e => await SetRuleEnabledAsync(e, cancellationToken),
        SetRuleMode m => await SetRuleModeAsync(m, cancellationToken),
        SetRulePriority p => await SetRulePriorityAsync(p, cancellationToken),
        RemoveRule r => await RemoveRuleAsync(r, cancellationToken),
        CreateRule c => await CreateRuleAsync(c, cancellationToken),
        UpdateRule u => await UpdateRuleAsync(u, cancellationToken),
        SetSiteAdmin a => await SetSiteAdminAsync(a, cancellationToken),
        SetSiteGroupMember m => await SetSiteGroupMemberAsync(m, cancellationToken),
        SetSiteSetting s => await SetSiteSettingAsync(s, cancellationToken),
        SetSiteArchived a => await SetSiteArchivedAsync(a, cancellationToken),
        DeleteSite d => await DeleteSiteAsync(d, cancellationToken),
        RestoreSite r => await RestoreSiteAsync(r, cancellationToken),
        CreateSite c => await CreateSiteAsync(c, cancellationToken),
        AddRoleAssignment a => await AddRoleAssignmentAsync(a, cancellationToken),
        CreateCustomRole c => await CreateCustomRoleAsync(c, cancellationToken),
        UpdateCustomRole u => await UpdateCustomRoleAsync(u, cancellationToken),
        DeleteCustomRole d => await DeleteCustomRoleAsync(d, cancellationToken),
        SetRolePolicyRule r => await SetRolePolicyRuleAsync(r, cancellationToken),
        ExtendRoleAssignment e => await ExtendRoleAssignmentAsync(e, cancellationToken),
        ActivateRole a => await ActivateRoleAsync(a, cancellationToken),
        ReviewRoleRequest r => await ReviewRoleRequestAsync(r, cancellationToken),
        SetCaPolicyState c => await SetCaPolicyStateAsync(c, cancellationToken),
        DeleteCaPolicy d => await DeleteCaPolicyAsync(d, cancellationToken),
        _ => new ApplyResult(ChangeOutcome.Failed, $"Tenantry can't apply \"{change.Label}\" changes yet."),
    };

    /// <summary>
    /// Sets a generated temporary password and hands it back once (<see cref="ApplyResult.Reveal"/>)
    /// for the admin to pass on. It is never stored. There is nothing to read back: Graph never
    /// returns passwords.
    /// </summary>
    async Task<ApplyResult> ResetPasswordAsync(ResetPassword reset, CancellationToken cancellationToken)
    {
        var password = reset.Chosen ?? (passwordStyle?.Invoke() ?? PasswordStyle.Default).Create();
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
            // A password the admin chose isn't shown back: they know it.
            return reset.Chosen is null ? new ApplyResult(ChangeOutcome.Applied, Reveal: password) : new ApplyResult(ChangeOutcome.Applied, "Password set.");
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

    // ---- Groups ---------------------------------------------------------------------------------

    /// <summary>
    /// Adds or removes a member or owner, then reads the group's list back from Graph (which
    /// shows distribution lists too) into the local copy.
    /// </summary>
    async Task<ApplyResult> SetMembershipAsync(SetGroupMembership change, CancellationToken cancellationToken)
    {
        var relation = change.Role == GroupRole.Owner ? "owners" : "members";
        try
        {
            if (change.ExchangeManaged)
            {
                if (exchange is null)
                    return NoExchange;
                await exchange.InvokeAsync(CmdletEndpoint.Exchange, ExchangeMembershipCmdlet(change), ExchangeMembershipParameters(change), cancellationToken);
            }
            else if (change.New)
                await graph.PostAsync($"groups/{change.Group.Id}/{relation}/$ref",
                    new JsonObject { ["@odata.id"] = DirectoryObjects + change.Person.Id }, cancellationToken);
            else
                await graph.DeleteAsync($"groups/{change.Group.Id}/{relation}/{change.Person.Id}/$ref", cancellationToken);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e, change.Person));
        }

        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var list = new JsonObject();
            await foreach (var item in graph.GetAllAsync($"groups/{change.Group.Id}/{relation}?$select=id&$top=999", cancellationToken))
                list[item.GetProperty("id").GetString()!] = TypeName(item);
            var present = list.ContainsKey(change.Person.Id);
            store.Apply(DirectorySync.Groups, [new EntityChange(change.Group.Id, new JsonObject { ["id"] = change.Group.Id, [relation] = list }, OnlyIfExists: true)]);
            if (present == change.New)
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Microsoft 365 accepted the change but the group still shows the old list; it usually appears within a minute.");
    }

    static string ExchangeMembershipCmdlet(SetGroupMembership change) => change switch
    {
        { Role: GroupRole.Owner } => "Set-DistributionGroup",
        { New: true } => "Add-DistributionGroupMember",
        _ => "Remove-DistributionGroupMember",
    };

    /// <summary>
    /// Exchange names people by address. The manager check is bypassed because the admin is
    /// changing the group from outside it, as the admin center does.
    /// </summary>
    static Dictionary<string, object?> ExchangeMembershipParameters(SetGroupMembership change)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["Identity"] = change.Group.Address ?? change.Group.Id,
            ["BypassSecurityGroupManagerCheck"] = true,
        };
        var person = change.Person.Address ?? change.Person.Id;
        if (change.Role == GroupRole.Owner)
            parameters["ManagedBy"] = AddOrRemove(change.New, person);
        else
        {
            parameters["Member"] = person;
            if (!change.New)
                parameters["Confirm"] = false;
        }
        return parameters;
    }

    /// <summary>Exchange's form for adding to or removing from a multi-valued property.</summary>
    static Dictionary<string, object?> AddOrRemove(bool add, string value) => new()
    {
        ["@odata.type"] = "#Exchange.GenericHashTable",
        [add ? "Add" : "Remove"] = new[] { value },
    };

    // ---- Mailbox access -------------------------------------------------------------------------

    /// <summary>
    /// Full access, Send as or Send on behalf, through Exchange; then the mailbox's access (or,
    /// for Send on behalf, the mailbox itself) is read back into the local copy.
    /// </summary>
    async Task<ApplyResult> SetMailboxAccessAsync(SetMailboxAccess change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        var mailbox = change.Mailbox.Address ?? change.Mailbox.Id;
        var person = change.Person.Address ?? change.Person.Id;
        var (cmdlet, parameters) = change.Right switch
        {
            MailboxRight.FullAccess => (change.New ? "Add-MailboxPermission" : "Remove-MailboxPermission", new Dictionary<string, object?>
            {
                ["Identity"] = mailbox, ["User"] = person, ["AccessRights"] = new[] { "FullAccess" }, ["InheritanceType"] = "All",
            }),
            MailboxRight.SendAs => (change.New ? "Add-RecipientPermission" : "Remove-RecipientPermission", new Dictionary<string, object?>
            {
                ["Identity"] = mailbox, ["Trustee"] = person, ["AccessRights"] = new[] { "SendAs" }, ["Confirm"] = false,
            }),
            _ => ("Set-Mailbox", new Dictionary<string, object?>
            {
                ["Identity"] = mailbox, ["GrantSendOnBehalfTo"] = AddOrRemove(change.New, person),
            }),
        };
        if (cmdlet == "Remove-MailboxPermission")
            parameters["Confirm"] = false;
        try
        {
            await exchange.InvokeAsync(CmdletEndpoint.Exchange, cmdlet, parameters, cancellationToken);
        }
        catch (CmdletException e) when (e.IsNotFound && change.Right == MailboxRight.SendOnBehalf)
        {
            return new ApplyResult(ChangeOutcome.Failed,
                $"Exchange couldn't find {change.Person.Name} as a mail recipient. Send on behalf can only be given to someone with a mailbox or email address.");
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e, change.Person));
        }

        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            bool present;
            try
            {
                present = await ReadAccessAsync(change, mailbox, cancellationToken);
            }
            catch (CmdletException)
            {
                continue;
            }
            if (present == change.New)
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Exchange accepted the change but still shows the old access; it usually appears within a minute.");
    }

    async Task<bool> ReadAccessAsync(SetMailboxAccess change, string mailbox, CancellationToken cancellationToken)
    {
        if (change.Right == MailboxRight.SendOnBehalf)
        {
            var rows = await exchange!.InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox",
                new Dictionary<string, object?> { ["Identity"] = mailbox }, cancellationToken, ["ExternalDirectoryObjectId", "GrantSendOnBehalfTo"]);
            if (rows.Count == 0)
                return false;
            var row = JsonNode.Parse(rows[0].GetRawText())!.AsObject();
            store.Apply(DirectorySync.Mailboxes, [new EntityChange(change.Mailbox.Id, row, OnlyIfExists: true)]);
            return row["GrantSendOnBehalfTo"] is JsonArray delegates && delegates.Any(d => Names(d?.GetValue<string>(), change.Person));
        }
        var access = await DirectorySync.ReadMailboxAccessAsync(exchange!, change.Mailbox.Id, mailbox, cancellationToken);
        store.Apply(DirectorySync.MailboxAccess, [new EntityChange(change.Mailbox.Id, access)]);
        var list = access[change.Right == MailboxRight.FullAccess ? "fullAccess" : "sendAs"]!.AsArray();
        return list.Any(t => Names(t?.GetValue<string>(), change.Person));
    }

    /// <summary>Exchange names a trustee by address, alias or display name, whichever it has.</summary>
    static bool Names(string? trustee, ChangeTarget person)
    {
        if (string.IsNullOrEmpty(trustee))
            return false;
        var alias = person.Address?.Split('@')[0];
        return new[] { person.Address, person.Name, person.Id, alias }
            .Any(n => n is not null && string.Equals(n, trustee, StringComparison.OrdinalIgnoreCase));
    }

    static readonly ApplyResult NoExchange = new(ChangeOutcome.Failed,
        "This change goes through Exchange Online, which isn't connected for this tenant.");

    // ---- Mail -----------------------------------------------------------------------------------

    /// <summary>
    /// Set-Mailbox with the given parameters, then reads the mailbox back (the list's properties)
    /// into the local copy until <paramref name="done"/> holds.
    /// </summary>
    async Task<ApplyResult> SetMailboxAsync(ChangeTarget user, Dictionary<string, object?> parameters, Func<JsonObject, bool> done, CancellationToken cancellationToken,
        IReadOnlyList<string>? alsoRead = null, IReadOnlyList<TimeSpan>? waits = null) =>
        await RunAndReadBackAsync(user, "Set-Mailbox", parameters, "Get-Mailbox", [.. DirectorySync.MailboxSelect, .. alsoRead ?? []],
            DirectorySync.Mailboxes, done, cancellationToken, waits);

    /// <summary>
    /// Runs an Exchange cmdlet against a mailbox, then reads the mailbox back with
    /// <paramref name="readCmdlet"/> (storing the row under <paramref name="storeKind"/>, when
    /// given, so lists follow) until <paramref name="done"/> holds; Exchange can take a few
    /// seconds to show a change.
    /// </summary>
    async Task<ApplyResult> RunAndReadBackAsync(ChangeTarget user, string cmdlet, Dictionary<string, object?> parameters, string readCmdlet,
        IReadOnlyList<string> select, string? storeKind, Func<JsonObject, bool> done, CancellationToken cancellationToken, IReadOnlyList<TimeSpan>? waits = null)
    {
        if (exchange is null)
            return NoExchange;
        var identity = user.Address ?? user.Id;
        parameters["Identity"] = identity;
        try
        {
            await exchange.InvokeAsync(CmdletEndpoint.Exchange, cmdlet, parameters, cancellationToken);
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Explanation);
        }

        foreach (var wait in waits ?? ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var rows = await exchange.InvokeAsync(CmdletEndpoint.Exchange, readCmdlet,
                new Dictionary<string, object?> { ["Identity"] = identity }, cancellationToken, select);
            if (rows.Count == 0)
                continue;
            var row = JsonNode.Parse(rows[0].GetRawText())!.AsObject();
            if (storeKind is not null)
                store.Apply(storeKind, [new EntityChange(user.Id, row, OnlyIfExists: true)]);
            if (done(row))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Exchange accepted the change but still shows the old setting; it usually appears within a minute.");
    }

    // ---- Mailbox settings (4b-2) ----------------------------------------------------------------

    /// <summary>
    /// Whether what Exchange shows is what was sent: booleans as booleans; anything else as text,
    /// where Exchange may add detail after it ("35 MB" comes back as "35 MB (36,700,160 bytes)").
    /// </summary>
    static bool Shows(JsonNode? actual, JsonNode? wanted)
    {
        if (wanted is null)
            return actual is null || actual.GetValueKind() == JsonValueKind.Null || (actual.GetValueKind() == JsonValueKind.String && actual.GetValue<string>().Length == 0);
        if (wanted.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            return actual is not null && actual.GetValueKind() == wanted.GetValueKind();
        var want = wanted.GetValueKind() == JsonValueKind.String ? wanted.GetValue<string>() : wanted.ToJsonString();
        var have = actual is null ? "" : actual.GetValueKind() == JsonValueKind.String ? actual.GetValue<string>() : actual.ToJsonString();
        return have.StartsWith(want, StringComparison.OrdinalIgnoreCase);
    }

    Task<ApplyResult> SetMailboxValueAsync(SetMailboxValue change, CancellationToken cancellationToken) =>
        SetMailboxAsync(change.User, new() { [change.Parameter] = change.Value?.DeepClone() }, row => Shows(row[change.Parameter], change.Value),
            cancellationToken, [change.Parameter]);

    /// <summary>Set-CASMailbox; SMTP sending's parameter says "disabled", so its value is inverted.</summary>
    Task<ApplyResult> SetEmailAppAsync(SetEmailApp change, CancellationToken cancellationToken)
    {
        var value = change.Parameter == "SmtpClientAuthenticationDisabled" ? !change.New : change.New;
        return RunAndReadBackAsync(change.User, "Set-CASMailbox", new() { [change.Parameter] = value }, "Get-CASMailbox",
            ["ExternalDirectoryObjectId", change.Parameter], DirectorySync.CasMailboxes,
            row => row[change.Parameter]?.GetValueKind() == (value ? JsonValueKind.True : JsonValueKind.False), cancellationToken);
    }

    Task<ApplyResult> SetArchiveAsync(SetArchive change, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> parameters = change.New ? new() { ["Archive"] = true } : new() { ["Archive"] = true, ["Confirm"] = false };
        return RunAndReadBackAsync(change.User, change.New ? "Enable-Mailbox" : "Disable-Mailbox", parameters, "Get-Mailbox",
            [.. DirectorySync.MailboxSelect], DirectorySync.Mailboxes,
            row => string.Equals(Text(row, "ArchiveStatus"), "Active", StringComparison.OrdinalIgnoreCase) == change.New, cancellationToken);
    }

    Task<ApplyResult> EnableAutoExpandingAsync(EnableAutoExpandingArchive change, CancellationToken cancellationToken) =>
        RunAndReadBackAsync(change.User, "Enable-Mailbox", new() { ["AutoExpandingArchive"] = true }, "Get-Mailbox",
            ["AutoExpandingArchiveEnabled"], null, row => row["AutoExpandingArchiveEnabled"]?.GetValueKind() == JsonValueKind.True, cancellationToken);

    Task<ApplyResult> SetLitigationHoldAsync(SetLitigationHold change, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?> { ["LitigationHoldEnabled"] = change.NewOn };
        if (change.NewOn)
        {
            parameters["LitigationHoldDuration"] = change.NewDays is { } days ? days.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Unlimited";
            parameters["RetentionComment"] = change.NewNote ?? "";
        }
        return SetMailboxAsync(change.User, parameters,
            row => row["LitigationHoldEnabled"]?.GetValueKind() == (change.NewOn ? JsonValueKind.True : JsonValueKind.False), cancellationToken,
            ["LitigationHoldDuration", "RetentionComment"]);
    }

    Task<ApplyResult> SetAllowedSenderAsync(SetAllowedSender change, CancellationToken cancellationToken)
    {
        var sender = change.Sender.Address ?? change.Sender.Name;
        return SetMailboxAsync(change.User, new() { ["AcceptMessagesOnlyFromSendersOrMembers"] = AddOrRemove(change.New, sender) },
            row => (row["AcceptMessagesOnlyFromSendersOrMembers"] is JsonArray list && list.Any(s => Names(s?.GetValue<string>(), change.Sender))) == change.New,
            cancellationToken, ["AcceptMessagesOnlyFromSendersOrMembers"]);
    }

    /// <summary>
    /// Set-MailboxAutoReplyConfiguration. Messages go as HTML (line breaks kept); scheduled times
    /// as UTC. Read back by state and, when on, by the internal message's text.
    /// </summary>
    async Task<ApplyResult> SetAutoReplyAsync(SetAutoReply change, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        var identity = change.User.Address ?? change.User.Id;
        var reply = change.New;
        var parameters = new Dictionary<string, object?>
        {
            ["Identity"] = identity,
            ["AutoReplyState"] = reply.State switch { AutoReplyState.On => "Enabled", AutoReplyState.Scheduled => "Scheduled", _ => "Disabled" },
            ["InternalMessage"] = Html(reply.Internal),
            ["ExternalMessage"] = Html(reply.External),
            ["ExternalAudience"] = reply.Audience.ToString(),
        };
        if (reply.State == AutoReplyState.Scheduled)
        {
            parameters["StartTime"] = reply.Start?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            parameters["EndTime"] = reply.End?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        }
        try
        {
            await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Set-MailboxAutoReplyConfiguration", parameters, cancellationToken);
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Explanation);
        }

        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var rows = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Get-MailboxAutoReplyConfiguration",
                new Dictionary<string, object?> { ["Identity"] = identity }, cancellationToken,
                ["AutoReplyState", "StartTime", "EndTime", "InternalMessage", "ExternalMessage", "ExternalAudience"]);
            if (rows.Count == 0)
                continue;
            var actual = GraphUserRecords.ToAutoReply(rows[0]);
            if (actual.State == reply.State && (reply.State == AutoReplyState.Off || Same(actual.Internal, reply.Internal)))
            {
                // The list's "Automatic replies" column follows at once, not at the next hourly read.
                store.Apply(DirectorySync.MailboxDetails, [new EntityChange(change.User.Id,
                    new JsonObject { ["id"] = change.User.Id, ["autoReply"] = actual.State.ToString() })]);
                return new ApplyResult(ChangeOutcome.Applied);
            }
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Exchange accepted the change but still shows the old automatic replies; it usually appears within a minute.");
    }

    /// <summary>Typed text as the HTML Exchange stores: escaped, with line breaks kept.</summary>
    static string Html(string text) => System.Net.WebUtility.HtmlEncode(text.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>");

    static bool Same(string a, string b) =>
        string.Equals(string.Join(' ', a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            string.Join(' ', b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.Ordinal);

    static string? Text(JsonObject row, string name) => row[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string? StripSmtp(string? address) =>
        string.IsNullOrEmpty(address) ? null : address.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) ? address[5..] : address;

    /// <summary>Every SMTP address on the mailbox, primary included, without the "smtp:" prefix.</summary>
    static IEnumerable<string> Addresses(JsonObject row) =>
        row["EmailAddresses"] is JsonArray list
            ? list.Select(a => a?.GetValue<string>() ?? "").Where(a => a.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase)).Select(a => a[5..])
            : [];

    // ---- Manager --------------------------------------------------------------------------------

    async Task<ApplyResult> SetManagerAsync(SetManager change, CancellationToken cancellationToken)
    {
        var path = $"users/{change.User.Id}/manager";
        try
        {
            if (change.NewId is { } id)
                await graph.PutAsync(path + "/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + id }, cancellationToken);
            else
                await graph.DeleteAsync(path + "/$ref", cancellationToken);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }

        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            string? actual;
            try
            {
                actual = (await graph.GetAsync(path + "?$select=id", cancellationToken)).GetProperty("id").GetString();
            }
            catch (GraphException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
            {
                actual = null;
            }
            if (actual == change.NewId)
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Microsoft 365 accepted the change but still shows the old manager; it usually appears within a minute.");
    }

    /// <summary>"#microsoft.graph.user" → "user".</summary>
    static string TypeName(JsonElement item) =>
        item.TryGetProperty("@odata.type", out var type) && type.GetString() is { } t ? t[(t.LastIndexOf('.') + 1)..] : "unknown";

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

    /// <summary>Exchange's words without its internal prefix; a missing recipient named plainly.</summary>
    static string Describe(CmdletException e, ChangeTarget person) => e.IsNotFound
        ? $"Exchange couldn't find {person.Name}. They may have no mailbox or email address yet. ({e.Explanation})"
        : e.Explanation;

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
