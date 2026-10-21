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
        if (change is SetUserText text)
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
            _ => null,
        };
        return result ?? new ApplyResult(ChangeOutcome.Failed, $"The demo can't apply \"{change.Label}\" changes.");
    }

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
