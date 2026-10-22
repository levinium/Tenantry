using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>Applies changes to the demo tenant's generated people, in memory. Nothing is sent anywhere.</summary>
public sealed class DemoChangeExecutor(DemoTenantDirectory directory, DemoUserRecords? records = null) : IChangeExecutor
{
    public async Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default)
    {
        // A person's own mailbox isn't a row in Groups: its delegates live with the records.
        if (change is SetMailboxAccess { Mailbox.Kind: "user" } own && records is not null)
        {
            await records.RememberDelegateAsync(own.Mailbox.Id, own.Person.Address ?? own.Person.Name, own.Right, own.New);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        // What's inside a mailbox (rules, sharing, devices, deleted items) lives with the records.
        if (records is not null && change.Target.Kind == "user" && change is SetInboxRuleEnabled or RemoveInboxRule or SetFolderPermission or SetMobileDevice
                or RestoreDeletedItem)
        {
            var owner = (await directory.GetUsersAsync(cancellationToken)).FirstOrDefault(u => u.Id == change.Target.Id);
            var address = owner?.Mailbox?.PrimaryAddress ?? owner?.UserPrincipalName ?? "";
            records.ApplyContents(change, address);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        if (change is CreateTemporaryAccessPass)
            return new ApplyResult(ChangeOutcome.Applied, "Pass them this code (demo).", Reveal: "Tap" + Random.Shared.Next(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture) + "!x");
        if (records is not null && records.ApplyDevice(change))
            return new ApplyResult(ChangeOutcome.Applied);
        if (records is not null && records.ApplySecurity(change))
            return new ApplyResult(ChangeOutcome.Applied);        if (change is ConvertMailbox convert)
            return Mailbox(convert.User.Id, m => m with { Type = convert.NewShared ? "Shared" : "User" })
                   ?? new ApplyResult(ChangeOutcome.Failed, "That person has no mailbox.");        if (change is SetUserText text)
            records?.Remember(text.User.Id, text.Property, Empty(text.New));
        var result = change switch
        {
            SetUserText { Property: "displayName" } t => Done(directory.Update(t.User.Id, u => u with { DisplayName = t.New ?? u.DisplayName })),
            SetUserText { Property: "department" } t => Done(directory.Update(t.User.Id, u => u with { Department = Empty(t.New) })),
            SetUserText { Property: "jobTitle" } t => Done(directory.Update(t.User.Id, u => u with { JobTitle = Empty(t.New) })),
            SetUserText { Property: "userPrincipalName" } t => Done(directory.Update(t.User.Id, u => u with { UserPrincipalName = t.New ?? u.UserPrincipalName })),
            // Everything else on the Account tab lives only in the record details.
            SetUserText => Done(records is not null),
            SetSignInAllowed s => Done(directory.Update(s.User.Id, u => u with { AccountEnabled = s.New })),
            SetLicenseAssigned l when DemoUserRecords.SkuName(l.SkuId) is { } name =>
                Done(directory.Update(l.User.Id, u => u with
                {
                    Licenses = l.New ? u.Licenses.Append(name).Distinct().ToArray() : u.Licenses.Where(x => x != name).ToArray(),
                })),
            SetLicenseApps a => Remember(() => records?.RememberApps(a.User.Id, a.SkuId, a.NewDisabled)),
            RemoveAppAssignment r => Remember(() => records?.RememberRemovedApp(r.AssignmentId)),
            ResetPassword => new ApplyResult(ChangeOutcome.Applied, Reveal: TemporaryPassword.Create()),
            RevokeSessions => new ApplyResult(ChangeOutcome.Applied, "Signed out of every session (demo)."),
            SetManager m => Remember(() => records?.RememberManager(m.User.Id, m.NewId)),
            SetGroupMembership m => Done(directory.UpdateGroup(m.Group.Id, g => m.Role == GroupRole.Owner
                ? g with { Owners = Toggle(g.OwnerList, m.Person.Id, m.PersonType, null, m.New) }
                : g with { Members = Toggle(g.Members, m.Person.Id, m.PersonType, null, m.New) })),
            SetMailboxAccess a => Done(directory.UpdateGroup(a.Mailbox.Id, g => g with { Members = Grant(g.Members, a) })),
            SetMailAlias a => Mailbox(a.User.Id, m => m with
            {
                Aliases = a.New ? m.Aliases.Append(a.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    : m.Aliases.Where(x => !string.Equals(x, a.Address, StringComparison.OrdinalIgnoreCase)).ToArray(),
            }),
            SetPrimaryAddress p => Mailbox(p.User.Id, m => m with
            {
                PrimaryAddress = p.New,
                Aliases = m.Aliases.Where(x => !string.Equals(x, p.New, StringComparison.OrdinalIgnoreCase)).Append(p.Old).ToArray(),
            }),
            SetForwarding f => Mailbox(f.User.Id, m => m with
            {
                ForwardingTo = f.NewTo,
                KeepsCopyWhenForwarding = f.NewTo is not null && f.NewKeepCopy,
                Forwarding = f.NewTo is null ? ForwardingKind.None : f.NewIsExternal ? ForwardingKind.External : ForwardingKind.Internal,
            }),
            SetHiddenFromAddressList h => Mailbox(h.User.Id, m => m with { HiddenFromAddressList = h.New }),
            SetAutoReply r => Remember(() =>
            {
                records?.RememberAutoReply(r.User.Id, r.New);
                directory.Update(r.User.Id, u => u.Mailbox is { } m ? u with { Mailbox = m with { AutoReply = r.New.State } } : u);
            }),
            SetMailboxValue v => await SettingsAsync(v.User.Id, s => v.Parameter switch
            {
                "RecipientLimits" => s with { RecipientLimit = v.New },
                "MaxSendSize" => s with { MaxSendBytes = MailDetails.ParseBytes(v.New) },
                "MaxReceiveSize" => s with { MaxReceiveBytes = MailDetails.ParseBytes(v.New) },
                "RequireSenderAuthenticationEnabled" => s with { OnlyInternalSenders = v.Value?.GetValue<bool>() == true },
                "RetentionPolicy" => s with { RetentionPolicy = v.New },
                _ => s,
            }, cancellationToken),
            SetEmailApp a => await SettingsAsync(a.User.Id, s => s with { Apps = Switch(s.Apps, a.Parameter, a.New) }, cancellationToken,
                m => m with { Apps = Switch(m.Apps ?? new EmailApps(true, true, true, false, false, true, null, false), a.Parameter, a.New) }),
            SetArchive a => await SettingsAsync(a.User.Id, s => s with { ArchiveOn = a.New, AutoExpandingArchive = a.New && s.AutoExpandingArchive, ArchiveUsedBytes = a.New ? 0 : null },
                cancellationToken, m => m with { ArchiveEnabled = a.New }),
            EnableAutoExpandingArchive a => await SettingsAsync(a.User.Id, s => s with { AutoExpandingArchive = true }, cancellationToken),
            SetLitigationHold h => await SettingsAsync(h.User.Id, s => h.NewOn
                    ? s with { HoldOn = true, HoldDays = h.NewDays, HoldNote = h.NewNote, HoldSince = s.HoldOn ? s.HoldSince : DateTimeOffset.UtcNow, HoldBy = s.HoldBy ?? "you" }
                    : s with { HoldOn = false, HoldSince = null, HoldBy = null },
                cancellationToken, m => m with { LitigationHold = h.NewOn }),
            SetAllowedSender a => await SettingsAsync(a.User.Id, s => s with
            {
                AllowedSenders = a.New ? s.AllowedSenders.Append(a.Sender.Name).Distinct().ToArray()
                    : s.AllowedSenders.Where(x => !string.Equals(x, a.Sender.Name, StringComparison.OrdinalIgnoreCase)).ToArray(),
            }, cancellationToken),
            _ => null,
        };
        return result ?? new ApplyResult(ChangeOutcome.Failed, $"The demo can't apply \"{change.Label}\" changes.");
    }

    /// <summary>Changes the Mail tab's settings and, when given, the list's copy of the mailbox.</summary>
    async Task<ApplyResult?> SettingsAsync(string userId, Func<MailboxSettings, MailboxSettings> change, CancellationToken cancellationToken,
        Func<MailboxInfo, MailboxInfo>? listed = null)
    {
        var user = (await directory.GetUsersAsync(cancellationToken)).FirstOrDefault(u => u.Id == userId);
        if (records is null || user?.Mailbox is not { } mailbox)
            return null;
        records.RememberSettings(userId, mailbox, change);
        return listed is null ? new ApplyResult(ChangeOutcome.Applied) : Mailbox(userId, listed);
    }

    static EmailApps Switch(EmailApps apps, string parameter, bool on) => parameter switch
    {
        "OWAEnabled" => apps with { Owa = on },
        "MAPIEnabled" => apps with { Outlook = on },
        "ActiveSyncEnabled" => apps with { Mobile = on },
        "EwsEnabled" => apps with { Ews = on },
        "PopEnabled" => apps with { Pop = on },
        "ImapEnabled" => apps with { Imap = on },
        _ => apps with { SmtpSend = on },
    };

    ApplyResult? Mailbox(string userId, Func<MailboxInfo, MailboxInfo> change) =>
        Done(directory.Update(userId, u => u.Mailbox is { } m ? u with { Mailbox = change(m) } : u));

    static GroupMember[] Toggle(IReadOnlyList<GroupMember> list, string id, string type, string? access, bool present) =>
        present
            ? list.Any(m => m.Id == id) ? list.ToArray() : [.. list, new GroupMember(id, type, access)]
            : list.Where(m => m.Id != id).ToArray();

    /// <summary>A mailbox's people carry their rights as text ("Full access, Send as"): add or drop one.</summary>
    static GroupMember[] Grant(IReadOnlyList<GroupMember> people, SetMailboxAccess change)
    {
        var right = SetMailboxAccess.RightName(change.Right);
        var current = people.FirstOrDefault(p => p.Id == change.Person.Id);
        var rights = (current?.Access ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
        rights.Remove(right);
        if (change.New)
            rights.Add(right);
        var ordered = new[] { "Full access", "Send as", "Send on behalf" }.Where(rights.Contains).ToArray();
        var rest = people.Where(p => p.Id != change.Person.Id);
        return ordered.Length == 0
            ? rest.ToArray()
            : [.. rest, new GroupMember(change.Person.Id, "user", string.Join(", ", ordered))];
    }

    static ApplyResult? Done(bool applied) => applied ? new ApplyResult(ChangeOutcome.Applied) : null;

    ApplyResult? Remember(Action remember)
    {
        if (records is null)
            return null;
        remember();
        return new ApplyResult(ChangeOutcome.Applied);
    }

    static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
