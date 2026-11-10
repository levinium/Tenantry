using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's group mail settings: started from each group's address, then changed in memory.</summary>
public sealed partial class DemoTenantDirectory
{
    readonly Dictionary<string, GroupMail> _groupMail = new(StringComparer.Ordinal);

    public async Task<GroupMail> GroupMailAsync(GroupSummary group, CancellationToken cancellationToken = default)
    {
        if (_groupMail.TryGetValue(group.Id, out var known))
            return known;
        var unified = group.Kind == GroupKind.Microsoft365;
        // One person in the demo may send as each list, so the Send As card has someone in it.
        var people = (await GetUsersAsync(cancellationToken)).Where(u => u.Kind == UserKind.Member).ToArray();
        var someone = people.Length == 0 ? null : people[Math.Abs(group.Id.GetHashCode(StringComparison.Ordinal)) % people.Length];
        return _groupMail[group.Id] = new GroupMail(
            group.Mail,
            group.Mail is null ? [] : [$"SMTP:{group.Mail}"],
            Hidden: false,
            OutsideSenders: group.Id == PartnersListId,
            AcceptFrom: [],
            RejectFrom: [],
            Moderated: false,
            Moderators: [],
            BypassModeration: [],
            ModerationNotices: "Always",
            MailTip: group.Id == PartnersListId ? "This list reaches people outside the organization." : null,
            SendAs: !unified && someone is not null ? [someone.UserPrincipalName] : [],
            SendOnBehalf: [],
            HiddenFromOutlook: unified ? false : null,
            AutoSubscribe: unified ? false : null,
            CalendarEvents: unified ? true : null,
            WelcomeMessage: unified ? true : null,
            JoinRestriction: group.Kind == GroupKind.Distribution ? "Closed" : null,
            DepartRestriction: group.Kind == GroupKind.Distribution ? "Open" : null);
    }

    /// <summary>Applies a group mail change; false when it isn't one.</summary>
    bool ApplyGroupMail(ChangeOp change)
    {
        if (change is not GroupChange g || !_groupMail.TryGetValue(g.Group.Id, out var mail))
            return false;
        static string[] Ids(JsonNode? value) => value is JsonArray list ? [.. list.Select(x => x!.GetValue<string>())] : [];
        static bool On(JsonNode? value) => value?.GetValue<bool>() == true;
        _groupMail[g.Group.Id] = change switch
        {
            SetGroupMailValue v => v.Parameter switch
            {
                "HiddenFromAddressListsEnabled" => mail with { Hidden = On(v.Value) },
                "RequireSenderAuthenticationEnabled" => mail with { OutsideSenders = !On(v.Value) },
                "AcceptMessagesOnlyFromSendersOrMembers" => mail with { AcceptFrom = Ids(v.Value) },
                "RejectMessagesFromSendersOrMembers" => mail with { RejectFrom = Ids(v.Value) },
                "ModerationEnabled" => mail with { Moderated = On(v.Value) },
                "ModeratedBy" => mail with { Moderators = Ids(v.Value) },
                "BypassModerationFromSendersOrMembers" => mail with { BypassModeration = Ids(v.Value) },
                "SendModerationNotifications" => mail with { ModerationNotices = v.Value?.GetValue<string>() ?? "Always" },
                "MailTip" => mail with { MailTip = GroupMail.PlainTip(v.Value?.GetValue<string>()) },
                "GrantSendOnBehalfTo" => mail with { SendOnBehalf = Ids(v.Value) },
                "HiddenFromExchangeClientsEnabled" => mail with { HiddenFromOutlook = On(v.Value), Hidden = On(v.Value) || mail.Hidden },
                "AutoSubscribeNewMembers" => mail with { AutoSubscribe = On(v.Value) },
                "AlwaysSubscribeMembersToCalendarEvents" => mail with { CalendarEvents = On(v.Value) },
                "UnifiedGroupWelcomeMessageEnabled" => mail with { WelcomeMessage = On(v.Value) },
                "MemberJoinRestriction" => mail with { JoinRestriction = v.Value?.GetValue<string>() },
                "MemberDepartRestriction" => mail with { DepartRestriction = v.Value?.GetValue<string>() },
                _ => mail,
            },
            SetGroupAddress a => mail with
            {
                Addresses = a.New
                    ? [.. mail.Addresses, "smtp:" + a.Address]
                    : [.. mail.Addresses.Where(x => !string.Equals(x[(x.IndexOf(':') + 1)..], a.Address, StringComparison.OrdinalIgnoreCase))],
            },
            SetGroupPrimaryAddress p => mail with
            {
                PrimaryAddress = p.New,
                Addresses = [.. mail.Addresses.Select(x => x[(x.IndexOf(':') + 1)..]) is var all
                    ? all.Where(x => !string.Equals(x, p.New, StringComparison.OrdinalIgnoreCase)).Select(x => "smtp:" + x).Prepend("SMTP:" + p.New)
                    : []],
            },
            SetGroupSendAs s => mail with
            {
                SendAs = s.New
                    ? [.. mail.SendAs, s.Person.Address ?? s.Person.Id]
                    : [.. mail.SendAs.Where(t => !string.Equals(t, s.Person.Address, StringComparison.OrdinalIgnoreCase) && t != s.Person.Id)],
            },
            _ => mail,
        };
        if (change is SetGroupPrimaryAddress primary)
            UpdateGroup(g.Group.Id, x => x with { Mail = primary.New });
        return change is SetGroupMailValue or SetGroupAddress or SetGroupPrimaryAddress or SetGroupSendAs;
    }
}

public sealed partial class DemoUserRecords
{
    public Task<GroupMail> GetGroupMailAsync(GroupSummary group, CancellationToken cancellationToken = default) => directory.GroupMailAsync(group, cancellationToken);
}
