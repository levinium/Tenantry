namespace Tenantry.Core.Directory;

/// <summary>An app inside a license (a service plan): Exchange, Teams, Yammer, …</summary>
public sealed record LicensePlan(string Id, string Name);

/// <summary>A license the tenant owns, with how many are used and which apps it includes.</summary>
public sealed record LicenseSku(string SkuId, string Name, int Total, int Used, IReadOnlyList<LicensePlan> Plans)
{
    public int Available => Math.Max(0, Total - Used);
}

/// <summary>
/// One license on a user. <see cref="AssignedByGroupId"/> is set when it comes from a group
/// (group-based licensing): it can only be removed there.
/// </summary>
/// <param name="Error">Why Microsoft couldn't apply it ("CountViolation", …), or null.</param>
public sealed record LicenseAssignment(
    string SkuId,
    string Name,
    string? AssignedByGroupId,
    string? AssignedByGroupName,
    string? Error,
    IReadOnlyList<string> DisabledPlanIds)
{
    public bool IsDirect => AssignedByGroupId is null;
}

/// <summary>An enterprise application the user is assigned to (an app role assignment).</summary>
public sealed record AppAssignment(string Id, string AppName, string AppId, DateTimeOffset? Since);

/// <summary>Everything the Licenses &amp; apps tab shows for one user.</summary>
public sealed record UserLicensing(
    string? UsageLocation,
    IReadOnlyList<LicenseAssignment> Assignments,
    IReadOnlyList<LicenseSku> Catalog,
    IReadOnlyList<AppAssignment> Apps)
{
    /// <summary>Microsoft explains each assignment error code; these say what to do about it.</summary>
    public static string Explain(string error) => error switch
    {
        "CountViolation" => "No licenses of this kind are left. Buy more or free one up.",
        "MutuallyExclusiveViolation" => "This license can't be combined with another license they have.",
        "DependencyViolation" => "An app in this license needs another license they don't have.",
        "ProhibitedInUsageLocationViolation" => "This license isn't available in their usage location.",
        "UniquenessViolation" => "This license can only be assigned once per person.",
        _ => error,
    };
}
