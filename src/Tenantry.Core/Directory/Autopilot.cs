using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>
/// A device registered with Windows Autopilot: its hardware hash is uploaded, so it sets itself up
/// for the organization when it's first turned on (or after an Autopilot reset).
/// </summary>
/// <param name="ProfileStatus">Whether an Autopilot profile is assigned: "Assigned", "Not Assigned", "Pending", "Failed".</param>
/// <param name="EnrollmentState">"Enrolled", "Not Contacted", "Failed", …</param>
/// <param name="ManagedDeviceId">Its Intune device, once it has enrolled.</param>
public sealed record AutopilotDevice(
    string Id,
    string Serial,
    string? Manufacturer,
    string? Model,
    string? GroupTag,
    string? ProfileName,
    string ProfileStatus,
    string EnrollmentState,
    DateTimeOffset? LastContacted,
    string? ManagedDeviceId,
    string? EntraDeviceId,
    string? AssignedUser,
    string? PurchaseOrder);

/// <summary>Reads the tenant's Autopilot devices (live, not kept in the local copy).</summary>
public interface IAutopilotRecords
{
    Task<Available<IReadOnlyList<AutopilotDevice>>> GetAutopilotDevicesAsync(CancellationToken cancellationToken = default);
}

/// <summary>The Autopilot list's fields.</summary>
public static class AutopilotFields
{
    public const string Serial = "serial";
    public const string Model = "model";
    public const string GroupTag = "grouptag";
    public const string Profile = "profile";
    public const string ProfileStatus = "profilestatus";
    public const string Enrollment = "enrollment";
    public const string LastContacted = "contacted";
    public const string User = "user";
    public const string PurchaseOrder = "order";

    public const string NoTag = "No Group Tag";

    public static IReadOnlyList<Field<AutopilotDevice>> All { get; } =
    [
        new() { Key = Serial, Label = "Serial Number", Kind = FieldKind.Text, Searchable = true, Aliases = ["sn", "serialnumber"], Text = d => d.Serial },
        new()
        {
            Key = Model, Label = "Model", Kind = FieldKind.Text, Searchable = true, Aliases = ["manufacturer", "make"],
            Text = d => string.Join(' ', new[] { d.Manufacturer, d.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
        },
        new() { Key = GroupTag, Label = "Group Tag", Kind = FieldKind.Choice, Searchable = true, Aliases = ["tag"], EmptyLabel = NoTag, Text = d => d.GroupTag },
        new() { Key = Profile, Label = "Profile", Kind = FieldKind.Choice, Searchable = true, EmptyLabel = "No Profile", Text = d => d.ProfileName },
        new()
        {
            Key = ProfileStatus, Label = "Profile Status", Kind = FieldKind.Choice, Aliases = ["assigned"],
            ChoiceOrder = ["Assigned", "Pending", "Not Assigned", "Failed"], Text = d => d.ProfileStatus,
        },
        new() { Key = Enrollment, Label = "Enrollment", Kind = FieldKind.Choice, Aliases = ["enrolled", "state"], Text = d => d.EnrollmentState },
        new() { Key = LastContacted, Label = "Last Contacted", Kind = FieldKind.Date, Aliases = ["contacted", "lastseen"], EmptyLabel = "Never", Date = d => d.LastContacted },
        new() { Key = User, Label = "Assigned User", Kind = FieldKind.Text, Searchable = true, Aliases = ["upn"], EmptyLabel = "No User", Text = d => d.AssignedUser },
        new() { Key = PurchaseOrder, Label = "Purchase Order", Kind = FieldKind.Text, Searchable = true, Aliases = ["po"], Text = d => d.PurchaseOrder },
    ];
}
