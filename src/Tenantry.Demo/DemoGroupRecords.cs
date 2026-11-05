using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's group settings: deleted groups kept for restoring, and a stand-in for Entra's rule
/// evaluation that understands the simple "property -eq value" rules the demo uses.
/// </summary>
public sealed partial class DemoUserRecords : IGroupRecords
{
    public Task<IReadOnlyList<DeletedGroup>> GetDeletedGroupsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeletedGroup>>(directory.DeletedGroups
            .Where(g => GroupRules.Restorable(g.Group.Kind))
            .Select(g => new DeletedGroup(g.Group.Id, g.Group.DisplayName, g.Group.Mail, g.Group.Kind, g.At))
            .ToArray());

    public async Task<RuleTest> TestRuleAsync(string rule, string personId, string personName, CancellationToken cancellationToken = default)
    {
        if (GroupRules.RuleShapeProblem(rule) is { } problem)
            return new RuleTest(personId, personName, null, problem);
        var person = (await directory.GetUsersAsync(cancellationToken)).FirstOrDefault(u => u.Id == personId);
        if (person is null)
            return new RuleTest(personId, personName, null, "That person isn't in the directory.");
        // Each (user.x -eq "value") part; any one matching counts (the demo's rules are single checks).
        var parts = System.Text.RegularExpressions.Regex.Matches(rule, "user\\.(\\w+)\\s+-eq\\s+\"([^\"]*)\"");
        if (parts.Count == 0)
            return new RuleTest(personId, personName, null, "The demo only checks rules like (user.department -eq \"Finance\").");
        var matches = parts.Any(p => string.Equals(Value(person, p.Groups[1].Value), p.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
        return new RuleTest(personId, personName, matches, null);
    }

    static string? Value(UserSummary u, string property) => property switch
    {
        "department" => u.Department,
        "jobTitle" => u.JobTitle,
        "userType" => u.Kind == UserKind.Guest ? "Guest" : "Member",
        "displayName" => u.DisplayName,
        "userPrincipalName" => u.UserPrincipalName,
        _ => null,
    };
}

public sealed partial class DemoTenantDirectory
{
    readonly List<(GroupSummary Group, DateTimeOffset At)> _deletedGroups = [];

    /// <summary>Groups deleted in this session, newest last.</summary>
    public IReadOnlyList<(GroupSummary Group, DateTimeOffset At)> DeletedGroups => _deletedGroups;

    /// <summary>Applies a group setting change; null when it isn't one.</summary>
    public async Task<bool?> ApplyGroupAsync(ChangeOp change, CancellationToken cancellationToken = default)
    {
        if (change is SetGroupMailValue or SetGroupAddress or SetGroupPrimaryAddress or SetGroupSendAs)
        {
            if (change is GroupChange g && !_groupMail.ContainsKey(g.Group.Id) && (await GetGroupsAsync(cancellationToken)).FirstOrDefault(x => x.Id == g.Group.Id) is { } group)
                await GroupMailAsync(group, cancellationToken);
            return ApplyGroupMail(change);
        }
        switch (change)
        {
            case SetGroupSetting s:
                var value = string.IsNullOrWhiteSpace(s.New) ? null : s.New.Trim();
                return UpdateGroup(s.Group.Id, g => s.Setting switch
                {
                    SetGroupSetting.DisplayName => g with { DisplayName = value ?? g.DisplayName },
                    SetGroupSetting.Description => g with { Description = value },
                    SetGroupSetting.Alias => g with { MailNickname = value },
                    SetGroupSetting.Visibility => g with { Visibility = value },
                    _ => g,
                });
            case SetMembershipRule m:
                return UpdateGroup(m.Group.Id, g => g with
                {
                    IsDynamic = m.NewDynamic,
                    MembershipRule = m.NewDynamic ? m.NewRule?.Trim() : g.MembershipRule,
                    RulePaused = m.NewDynamic ? m.NewPaused : true,
                });
            case DeleteGroup d:
                var groups = (await GetGroupsAsync(cancellationToken)).ToList();
                var gone = groups.FirstOrDefault(g => g.Id == d.Group.Id);
                if (gone is null)
                    return false;
                _deletedGroups.Add((gone, DateTimeOffset.UtcNow));
                _groups = groups.Where(g => g.Id != d.Group.Id).ToArray();
                return true;
            case RestoreGroup r:
                var index = _deletedGroups.FindIndex(x => x.Group.Id == r.Group.Id);
                if (index < 0)
                    return false;
                await AddGroupAsync(_deletedGroups[index].Group);
                _deletedGroups.RemoveAt(index);
                return true;
            default:
                return null;
        }
    }
}
