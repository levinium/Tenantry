using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>A role in the preview pane: what it is, who holds it (the first few), and Open Role.</summary>
public sealed partial class RolePreviewViewModel(RoleSummary role, Action<RoleSummary>? openRole)
{
    const int Shown = 8;

    public RoleSummary Role => role;
    public string Name => role.Role.Name;
    public string Description => role.Role.Description;
    public bool IsPrivileged => role.Role.IsPrivileged;
    public string TypeText => role.Role.IsBuiltIn ? RoleFields.BuiltIn : RoleFields.Custom;

    public IReadOnlyList<SummaryLine> Facts =>
    [
        new("Active", $"{role.Active:N0}"),
        .. role.Eligible > 0 ? [new SummaryLine("Eligible", $"{role.Eligible:N0}")] : Array.Empty<SummaryLine>(),
        .. role.PermanentPeople > 0 ? [new SummaryLine("Permanent People", $"{role.PermanentPeople:N0}")] : Array.Empty<SummaryLine>(),
        new("Permissions", $"{role.Role.AllowedActions.Count:N0}"),
    ];

    /// <summary>The first few holders: name, and what kind of holder and where.</summary>
    public IReadOnlyList<SummaryLine> Holders =>
    [
        .. role.Grants.Take(Shown).Select(g => new SummaryLine(g.PrincipalName,
            string.Join(" · ", new[] { RoleGrant.TypeName(g.PrincipalType), g.Kind == RoleGrantKind.Eligible ? "Eligible" : null, g.ScopeId == "/" ? null : g.ScopeName }.OfType<string>()))),
    ];

    public bool HasHolders => role.Grants.Count > 0;

    public string? MoreText => role.Grants.Count > Shown ? $"and {role.Grants.Count - Shown:N0} more" : null;
    public bool HasMore => MoreText is not null;

    public bool CanOpen => openRole is not null;

    [RelayCommand]
    void Open() => openRole?.Invoke(role);
}
