using Tenantry.Core.Changes;

namespace Tenantry.Demo;

/// <summary>Applies changes to the demo tenant's generated people, in memory. Nothing is sent anywhere.</summary>
public sealed class DemoChangeExecutor(DemoTenantDirectory directory, DemoUserRecords? records = null) : IChangeExecutor
{
    public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default)
    {
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
            _ => null,
        };
        return Task.FromResult(result ?? new ApplyResult(ChangeOutcome.Failed, $"The demo can't apply \"{change.Label}\" changes."));
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
