using Tenantry.Core.Changes;

namespace Tenantry.Demo;

/// <summary>Applies changes to the demo tenant's generated people, in memory. Nothing is sent anywhere.</summary>
public sealed class DemoChangeExecutor(DemoTenantDirectory directory) : IChangeExecutor
{
    public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default)
    {
        var applied = change switch
        {
            SetUserText { Property: "displayName" } t => directory.Update(t.User.Id, u => u with { DisplayName = t.New ?? u.DisplayName }),
            SetUserText { Property: "department" } t => directory.Update(t.User.Id, u => u with { Department = Empty(t.New) }),
            SetUserText { Property: "jobTitle" } t => directory.Update(t.User.Id, u => u with { JobTitle = Empty(t.New) }),
            SetSignInAllowed s => directory.Update(s.User.Id, u => u with { AccountEnabled = s.New }),
            _ => false,
        };
        return Task.FromResult(applied
            ? new ApplyResult(ChangeOutcome.Applied)
            : new ApplyResult(ChangeOutcome.Failed, $"The demo can't apply \"{change.Label}\" changes."));
    }

    static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
