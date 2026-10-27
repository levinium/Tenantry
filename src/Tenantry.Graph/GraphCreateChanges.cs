using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.Exchange;

namespace Tenantry.Graph;

/// <summary>New users (and their first licenses, manager and groups).</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>
    /// Creates the account, then the parts that need it to exist. If the account is made but a later
    /// part fails, the change still counts as applied (the person exists) and says what to finish by hand.
    /// The password is shown once (generated) or not at all (typed); it's never stored.
    /// </summary>
    async Task<ApplyResult> CreateUserAsync(CreateUser create, CancellationToken cancellationToken)
    {
        var password = create.Chosen ?? (passwordStyle?.Invoke() ?? PasswordStyle.Default).Create();
        var body = new JsonObject
        {
            ["accountEnabled"] = create.SignInAllowed,
            ["displayName"] = create.User.Name,
            ["mailNickname"] = create.MailNickname,
            ["userPrincipalName"] = create.User.Address,
            ["usageLocation"] = create.UsageLocation,
            ["passwordProfile"] = new JsonObject { ["password"] = password, ["forceChangePasswordNextSignIn"] = create.MustChangePassword },
        };
        if (create.GivenName.Length > 0)
            body["givenName"] = create.GivenName;
        if (create.Surname.Length > 0)
            body["surname"] = create.Surname;
        foreach (var (property, value) in create.Details)
            if (!string.IsNullOrWhiteSpace(value))
                body[property] = property == "businessPhones" ? new JsonArray(value.Trim()) : value.Trim();

        string id;
        try
        {
            id = (await graph.PostAsync("users", body, cancellationToken)).GetProperty("id").GetString()!;
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }

        var unfinished = new List<string>();
        if (create.Licenses.Count > 0)
        {
            try
            {
                await graph.PostAsync($"users/{id}/assignLicense", new JsonObject
                {
                    ["addLicenses"] = new JsonArray(create.Licenses.Select(l => (JsonNode)new JsonObject { ["skuId"] = l.SkuId, ["disabledPlans"] = new JsonArray() }).ToArray()),
                    ["removeLicenses"] = new JsonArray(),
                }, cancellationToken);
                await RefreshSkusAsync(cancellationToken);
            }
            catch (GraphException e)
            {
                unfinished.Add($"licenses ({Describe(e)})");
            }
        }
        if (create.ManagerId is { } manager)
        {
            try
            {
                await graph.PutAsync($"users/{id}/manager/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + manager }, cancellationToken);
            }
            catch (GraphException e)
            {
                unfinished.Add($"manager ({Describe(e)})");
            }
        }
        foreach (var group in create.Groups)
        {
            try
            {
                if (group.ExchangeManaged)
                {
                    // Exchange only knows the person once their account reaches it (and a mailbox, if licensed): usually within minutes.
                    if (exchange is null)
                        throw new CmdletException("Exchange Online isn't connected", 0, "");
                    await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Add-DistributionGroupMember",
                        new Dictionary<string, object?> { ["Identity"] = group.Group.Id, ["Member"] = create.User.Address, ["BypassSecurityGroupManagerCheck"] = true },
                        cancellationToken);
                }
                else
                    await graph.PostAsync($"groups/{group.Group.Id}/members/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + id }, cancellationToken);
            }
            catch (GraphException e)
            {
                unfinished.Add($"{group.Group.Name} ({Describe(e)})");
            }
            catch (CmdletException e)
            {
                unfinished.Add($"{group.Group.Name} ({(e.Explanation.Contains("couldn't be found", StringComparison.OrdinalIgnoreCase)
                    ? "Exchange doesn't know them yet; add them from the group in a few minutes"
                    : e.Explanation)})");
            }
        }

        await IntoLocalCopyAsync(sync => sync.RefreshUserAsync(id, cancellationToken));

        var detail = unfinished.Count == 0 ? null : "The account was made, but these didn't go through: " + string.Join("; ", unfinished) + ".";
        if (create.Chosen is not null)
            return new ApplyResult(ChangeOutcome.Applied, detail ?? "Created with the password you chose.");
        return new ApplyResult(ChangeOutcome.Applied, detail, Reveal: password);
    }

    /// <summary>Reads something just made into the local copy the way a sync stores it, so the lists show it now; the next sync catches anything missed.</summary>
    async Task IntoLocalCopyAsync(Func<DirectorySync, Task> refresh)
    {
        try
        {
            await refresh(new DirectorySync(graph, store, exchange: exchange));
        }
        catch (Exception e) when (e is GraphException or CmdletException)
        {
        }
    }

    const string GraphUsers = "https://graph.microsoft.com/v1.0/users/";

    /// <summary>Graph accepts at most this many owners or members in the request that makes a group; the rest are added after.</summary>
    const int BindLimit = 20;

    /// <summary>
    /// Makes the group and adds its people. Microsoft 365 and security groups through Graph (people
    /// bound in the same request where Graph allows); distribution lists and shared mailboxes
    /// through Exchange, which can take a few seconds to know a brand-new mailbox before it takes
    /// permissions on it.
    /// </summary>
    async Task<ApplyResult> CreateGroupAsync(CreateGroup create, CancellationToken cancellationToken) => create.Kind switch
    {
        Core.Directory.GroupKind.Microsoft365 or Core.Directory.GroupKind.Security => await CreateGraphGroupAsync(create, cancellationToken),
        Core.Directory.GroupKind.Distribution or Core.Directory.GroupKind.MailEnabledSecurity => await CreateDistributionListAsync(create, cancellationToken),
        Core.Directory.GroupKind.SharedMailbox => await CreateSharedMailboxAsync(create, cancellationToken),
        _ => new ApplyResult(ChangeOutcome.Failed, "Tenantry can't create that kind of group."),
    };

    async Task<ApplyResult> CreateGraphGroupAsync(CreateGroup create, CancellationToken cancellationToken)
    {
        var unified = create.Kind == Core.Directory.GroupKind.Microsoft365;
        var dynamic = create.MembershipRule is not null;
        var body = new JsonObject
        {
            ["displayName"] = create.Group.Name,
            ["mailNickname"] = create.MailNickname,
            ["mailEnabled"] = unified,
            ["securityEnabled"] = !unified,
            ["groupTypes"] = new JsonArray(new[] { unified ? "Unified" : null, dynamic ? "DynamicMembership" : null }.OfType<string>().Select(t => (JsonNode)t).ToArray()),
        };
        if (create.Description is { Length: > 0 } description)
            body["description"] = description;
        if (unified)
            body["visibility"] = create.IsPrivate ? "Private" : "Public";
        if (dynamic)
        {
            body["membershipRule"] = create.MembershipRule;
            body["membershipRuleProcessingState"] = "On";
        }
        if (create.Owners.Count > 0)
            body["owners@odata.bind"] = new JsonArray(create.Owners.Take(BindLimit).Select(o => (JsonNode)(GraphUsers + o.Id)).ToArray());
        if (!dynamic && create.Members.Count > 0)
            body["members@odata.bind"] = new JsonArray(create.Members.Take(BindLimit).Select(m => (JsonNode)(GraphUsers + m.Id)).ToArray());

        string id;
        try
        {
            id = (await graph.PostAsync("groups", body, cancellationToken)).GetProperty("id").GetString()!;
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
        var unfinished = new List<string>();
        foreach (var (relation, people) in new[] { ("owners", create.Owners.Skip(BindLimit)), ("members", dynamic ? [] : create.Members.Skip(BindLimit)) })
            foreach (var person in people)
            {
                try
                {
                    await graph.PostAsync($"groups/{id}/{relation}/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + person.Id }, cancellationToken);
                }
                catch (GraphException e)
                {
                    unfinished.Add($"{person.Name} ({Describe(e)})");
                }
            }
        await IntoLocalCopyAsync(sync => sync.RefreshGroupAsync(id, cancellationToken));
        return new ApplyResult(ChangeOutcome.Applied, unfinished.Count == 0 ? null : "The group was made, but these people weren't added: " + string.Join("; ", unfinished) + ".");
    }

    static object[] Addresses(IEnumerable<ChangeTarget> people) => people.Select(p => (object)(p.Address ?? p.Name)).ToArray();

    async Task<ApplyResult> CreateDistributionListAsync(CreateGroup create, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        var parameters = new Dictionary<string, object?>
        {
            ["Name"] = create.Group.Name,
            ["DisplayName"] = create.Group.Name,
            ["Alias"] = create.MailNickname,
            ["PrimarySmtpAddress"] = create.Group.Address,
            ["Type"] = create.Kind == Core.Directory.GroupKind.MailEnabledSecurity ? "Security" : "Distribution",
            ["RequireSenderAuthenticationEnabled"] = !create.OutsideSenders,
        };
        if (create.Members.Count > 0)
            parameters["Members"] = Addresses(create.Members);
        if (create.Owners.Count > 0)
            parameters["ManagedBy"] = Addresses(create.Owners);
        if (create.Description is { Length: > 0 } description)
            parameters["Notes"] = description;
        IReadOnlyList<System.Text.Json.JsonElement> made;
        try
        {
            made = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "New-DistributionGroup", parameters, cancellationToken);
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Explanation);
        }
        if (made.FirstOrDefault() is { ValueKind: System.Text.Json.JsonValueKind.Object } row && row.TryGetProperty("ExternalDirectoryObjectId", out var id)
            && id.GetString() is { Length: > 0 } groupId)
            await IntoLocalCopyAsync(async sync =>
            {
                // Exchange makes it first; Entra (where the lists read groups from) has it a few seconds later.
                foreach (var wait in ReadBackWaits)
                {
                    if (wait > TimeSpan.Zero)
                        await _delay(wait, cancellationToken);
                    try
                    {
                        await sync.RefreshGroupAsync(groupId, cancellationToken);
                        if (store.Get(DirectorySync.Groups, groupId) is not null)
                            return;
                    }
                    catch (GraphException e) when (e.Status == System.Net.HttpStatusCode.NotFound)
                    {
                    }
                }
            });
        return new ApplyResult(ChangeOutcome.Applied);
    }

    async Task<ApplyResult> CreateSharedMailboxAsync(CreateGroup create, CancellationToken cancellationToken)
    {
        if (exchange is null)
            return NoExchange;
        IReadOnlyList<System.Text.Json.JsonElement> made;
        try
        {
            made = await exchange.InvokeAsync(CmdletEndpoint.Exchange, "New-Mailbox", new Dictionary<string, object?>
            {
                ["Shared"] = true,
                ["Name"] = create.Group.Name,
                ["DisplayName"] = create.Group.Name,
                ["Alias"] = create.MailNickname,
                ["PrimarySmtpAddress"] = create.Group.Address,
            }, cancellationToken);
        }
        catch (CmdletException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, e.Explanation);
        }

        // A brand-new mailbox can take a few seconds to take permissions: each grant is retried briefly.
        var unfinished = new List<string>();
        foreach (var person in create.Members)
        {
            var who = person.Address ?? person.Name;
            var granted = false;
            string? problem = null;
            foreach (var wait in ReadBackWaits.Append(TimeSpan.FromSeconds(8)))
            {
                if (wait > TimeSpan.Zero)
                    await _delay(wait, cancellationToken);
                try
                {
                    await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Add-MailboxPermission", new Dictionary<string, object?>
                    {
                        ["Identity"] = create.Group.Address, ["User"] = who, ["AccessRights"] = new[] { "FullAccess" }, ["InheritanceType"] = "All", ["AutoMapping"] = true,
                    }, cancellationToken);
                    await exchange.InvokeAsync(CmdletEndpoint.Exchange, "Add-RecipientPermission", new Dictionary<string, object?>
                    {
                        ["Identity"] = create.Group.Address, ["Trustee"] = who, ["AccessRights"] = new[] { "SendAs" }, ["Confirm"] = false,
                    }, cancellationToken);
                    granted = true;
                    break;
                }
                catch (CmdletException e) when (e.IsNotFound)
                {
                    problem = e.Explanation;
                }
                catch (CmdletException e)
                {
                    problem = e.Explanation;
                    break;
                }
            }
            if (!granted)
                unfinished.Add($"{person.Name} ({problem})");
        }

        if (made.FirstOrDefault() is { ValueKind: System.Text.Json.JsonValueKind.Object } row && row.TryGetProperty("ExternalDirectoryObjectId", out var id)
            && id.GetString() is { Length: > 0 } accountId)
            await IntoLocalCopyAsync(sync => sync.RefreshUserAsync(accountId, cancellationToken));
        return new ApplyResult(ChangeOutcome.Applied,
            unfinished.Count == 0 ? null : "The shared mailbox was made, but these people weren't given access: " + string.Join("; ", unfinished) + ". Add them from the mailbox.");
    }
}
