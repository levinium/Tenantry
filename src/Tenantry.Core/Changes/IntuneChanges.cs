using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Core.Changes;

/// <summary>
/// An Intune policy's change. The policy is the target (kind "intunepolicy", Id = its Graph path,
/// Name = its name), so Test mode allows only zz-tenantry-test- policies.
/// </summary>
public abstract record IntuneChange(ChangeTarget Policy) : ChangeOp
{
    public static ChangeTarget TargetFor(string path, string name) => new("intunepolicy", path, name, null);

    /// <summary>The organization's own Intune settings (Test mode refuses them).</summary>
    public static ChangeTarget Organization(string what) => new("intunepolicy", "deviceManagement", what, null);

    /// <summary>The policy's Graph path ("deviceManagement/deviceCompliancePolicies/{id}").</summary>
    public string Path => Policy.Id;

    public override ChangeTarget Target => Policy;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// One setting of an Intune policy (PATCH with the policy's type). A dotted property
/// ("settings.secureByDefault") is a field inside an object, sent back whole with its other fields.
/// Renaming is guarded by the new name too.
/// </summary>
public sealed record SetIntuneSetting(ChangeTarget Policy, string ODataType, string Property, string SettingLabel, JsonNode? Value, string OldText, string NewText,
    bool Caution = false) : IntuneChange(Policy)
{
    public override string Key => $"{ODataType}:{Property}";
    public override string Label => SettingLabel;
    public override string Before => OldText;
    public override string After => NewText;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => OldText == NewText;
    public override IReadOnlyList<ChangeTarget> Guarded => Property is "displayName" or "name" && Value?.GetValue<string>() is { } name ? [Policy, Policy with { Name = name }] : [Policy];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetIntuneSetting e ? this with { OldText = e.OldText } : this;
}

/// <summary>
/// Who a policy (or app) reaches: its whole list of assignments, sent at once (Intune's assign
/// replaces them). In Test mode every group added or taken away must be a test group, and All users
/// or All devices is never allowed. Reaching everyone, or reaching fewer, is a caution.
/// </summary>
/// <param name="ListProperty">The assign body's list: "assignments" for policies, "mobileAppAssignments" for apps.</param>
/// <param name="ItemTemplate">Fields every assignment carries besides its target (a remediation's schedule).</param>
public sealed record SetIntuneAssignments(ChangeTarget Policy, string KindTitle, IReadOnlyList<IntuneAssignment> Old, IReadOnlyList<IntuneAssignment> New,
    string ListProperty = "assignments", JsonObject? ItemTemplate = null) : IntuneChange(Policy)
{
    public override string Key => "assignments";
    public override string Label => $"{KindTitle}: Assignments";
    public override string Before => Words(Old);
    public override string After => Words(New);

    static string Words(IReadOnlyList<IntuneAssignment> list) => list.Count == 0 ? "Nobody" : string.Join("; ", list.Select(a => a.Text));

    IEnumerable<IntuneAssignment> Added => New.Where(n => Old.All(o => o.Identity != n.Identity));
    IEnumerable<IntuneAssignment> Removed => Old.Where(o => New.All(n => n.Identity != o.Identity));

    public override bool IsCaution => Removed.Any() || Added.Any(a => a.Kind is AssignmentKind.AllUsers or AssignmentKind.AllDevices);
    public override bool IsNoChange => !Added.Any() && !Removed.Any();

    public override IReadOnlyList<ChangeTarget> Guarded =>
    [
        Policy,
        .. Added.Concat(Removed).Select(a => a.Kind switch
        {
            AssignmentKind.AllUsers => new ChangeTarget("group", "allusers", "All Users", null),
            AssignmentKind.AllDevices => new ChangeTarget("group", "alldevices", "All Devices", null),
            _ => new ChangeTarget("group", a.GroupId ?? "", a.GroupName ?? a.GroupId ?? "a group", null),
        }),
    ];

    public override ChangeOp Since(ChangeOp earlier) => earlier is SetIntuneAssignments e ? this with { Old = e.Old } : this;
}

/// <summary>A new compliance policy: Microsoft's defaults (nothing required yet), marked noncompliant after the grace days, and its first assignments.</summary>
public sealed record CreateCompliancePolicy(ChangeTarget Policy, string ODataType, string? Description, int GraceDays, IReadOnlyList<IntuneAssignment> Assignments) : IntuneChange(Policy)
{
    public override string Key => "create";
    public override string Label => $"New Compliance Policy \"{Policy.Name}\"";
    public override string Before => "None";
    public override string After => $"{CompliancePlatforms.Title(ODataType)}; noncompliant after {GraceDays} {(GraceDays == 1 ? "day" : "days")}; "
        + (Assignments.Count == 0 ? "not assigned yet" : string.Join("; ", Assignments.Select(a => a.Text)));
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded =>
        [Policy, .. new SetIntuneAssignments(Policy, "", [], Assignments).Guarded.Skip(1)];
}

/// <summary>What happens to a device that doesn't comply, and when (the whole list is sent at once). Taking a step away is a caution.</summary>
public sealed record SetComplianceActions(ChangeTarget Policy, string ODataType, IReadOnlyList<ComplianceAction> Old, IReadOnlyList<ComplianceAction> New) : IntuneChange(Policy)
{
    public override string Key => "actions";
    public override string Label => "Actions for Noncompliance";
    public override string Before => string.Join("; ", Old.Select(a => a.Text));
    public override string After => string.Join("; ", New.Select(a => a.Text));
    public override bool IsCaution => New.Any(a => a.Action is "retire" or "remoteLock" && !Old.Contains(a));
    public override bool IsNoChange => Old.SequenceEqual(New);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetComplianceActions e ? this with { Old = e.Old } : this;
}

/// <summary>Deletes an Intune policy. Devices it reached stop getting it. The admin types its name.</summary>
public sealed record DeleteIntunePolicy(ChangeTarget Policy, string KindTitle, string Detail) : IntuneChange(Policy)
{
    public override string Key => "delete";
    public override string Label => $"{KindTitle} \"{Policy.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Policy.Name;
    public override bool IsNoChange => false;
}

/// <summary>
/// A settings catalog profile's settings: the whole list, sent with the profile in one PUT (as
/// Intune's portal does). Before and After name the settings that changed.
/// </summary>
/// <param name="Header">The profile without its settings (name, description, platforms, technologies, template, scope tags).</param>
public sealed record SetCatalogSettings(ChangeTarget Policy, JsonObject Header, JsonArray Old, JsonArray New, string BeforeText, string AfterText) : IntuneChange(Policy)
{
    public override string Key => "settings";
    public override string Label => "Configuration Profile: Settings";
    public override string Before => BeforeText;
    public override string After => AfterText;
    public override bool IsNoChange => JsonNode.DeepEquals(Old, New);
    /// <summary>Taking a setting out lets devices fall back to their own: a caution.</summary>
    public override bool IsCaution => Old.Select(o => (string?)o?["settingDefinitionId"]).Except(New.Select(n => (string?)n?["settingDefinitionId"])).Any();
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetCatalogSettings e ? this with { Old = e.Old } : this;
}

/// <summary>A new settings catalog profile with its first setting (Intune refuses one with none); more are added on its card.</summary>
/// <param name="Settings">Its first settings (settingInstances).</param>
/// <param name="SettingsText">Those settings in words.</param>
public sealed record CreateConfigurationProfile(ChangeTarget Policy, string Platforms, string Technologies, string? Description, JsonArray Settings, string SettingsText) : IntuneChange(Policy)
{
    public override string Key => "create";
    public override string Label => $"New Configuration Profile \"{Policy.Name}\"";
    public override string Before => "None";
    public override string After => $"{SettingsCatalog.PlatformText(Platforms)}: {SettingsText}; not assigned yet";
    public override bool IsNoChange => false;
}

/// <summary>
/// A new Intune object of any kind (an app, a script, an update ring…): its body as the New form
/// filled it in, posted to the kind's collection; then, for some, a second post under it (a
/// notification template's message).
/// </summary>
public sealed record CreateIntuneObject(ChangeTarget Policy, string Collection, string KindTitle, JsonObject Body, string BodyText, string? ThenPath = null, JsonObject? ThenBody = null)
    : IntuneChange(Policy)
{
    public override string Key => "create";
    public override string Label => $"New {KindTitle} \"{Policy.Name}\"";
    public override string Before => "None";
    public override string After => BodyText;
    public override bool IsNoChange => false;
}

/// <summary>The apps an app protection policy protects, by bundle ID (iOS) or package ID (Android); the whole list is sent at once.</summary>
/// <param name="Platform">"ios" or "android".</param>
public sealed record SetProtectedApps(ChangeTarget Policy, string Platform, IReadOnlyList<string> Old, IReadOnlyList<string> New) : IntuneChange(Policy)
{
    public override string Key => "apps";
    public override string Label => "App Protection: Apps";
    public override string Before => Old.Count == 0 ? "None" : string.Join(", ", Old);
    public override string After => New.Count == 0 ? "None" : string.Join(", ", New);
    public override bool IsCaution => Old.Except(New, StringComparer.OrdinalIgnoreCase).Any();
    public override bool IsNoChange => Old.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(New.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetProtectedApps e ? this with { Old = e.Old } : this;
}
