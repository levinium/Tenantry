using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>
/// One device in the Devices list: its Entra record and its Intune record joined on the Entra device
/// ID (either can be missing). Kept small, like <see cref="UserSummary"/>: the device's page reads the rest.
/// </summary>
/// <param name="Key">Stable list key: the Entra object ID, or "intune:" and the Intune ID for a device only Intune knows.</param>
/// <param name="ObjectId">Entra's object ID (enable, disable, delete, groups).</param>
/// <param name="DeviceId">Entra's device ID: what Intune, BitLocker and LAPS file it under.</param>
/// <param name="ManagedId">Intune's managed device ID (remote actions).</param>
/// <param name="JoinType">"Entra joined", "Hybrid joined", "Registered" or "Intune only".</param>
/// <param name="LastCheckIn">When it last checked in with Intune.</param>
/// <param name="LastSignIn">When it was last used to sign in (Entra's approximate time).</param>
/// <param name="Encrypted">Intune's word, or true when a BitLocker key is backed up for it (<paramref name="KeyBackedUp"/>).</param>
public sealed record DeviceSummary(
    string Key,
    string? ObjectId,
    string? DeviceId,
    string? ManagedId,
    string Name,
    string? Os,
    string? OsVersion,
    string? Manufacturer,
    string? Model,
    string? Serial,
    string JoinType,
    bool? Enabled,
    bool? Compliant,
    bool? Encrypted,
    DateTimeOffset? LastCheckIn,
    DateTimeOffset? LastSignIn,
    DateTimeOffset? Registered,
    string? Ownership,
    string? UserId,
    string? UserName,
    string? UserPrincipalName,
    long? StorageTotal = null,
    long? StorageFree = null,
    bool KeyBackedUp = false)
{
    public bool IsManaged => ManagedId is not null;

    /// <summary>The later of its last Intune check-in and its last sign-in.</summary>
    public DateTimeOffset? LastSeen => LastCheckIn is { } a && LastSignIn is { } b ? (a > b ? a : b) : LastCheckIn ?? LastSignIn;

    /// <summary>"Windows", "macOS", "iOS/iPadOS", "Android", "Linux" or "Other": the platform family.</summary>
    public string Platform => (Os ?? "").ToLowerInvariant() switch
    {
        var os when os.StartsWith("windows", StringComparison.Ordinal) => "Windows",
        var os when os.StartsWith("mac", StringComparison.Ordinal) => "macOS",
        "ios" or "ipados" or "iphone" or "ipad" => "iOS/iPadOS",
        var os when os.StartsWith("android", StringComparison.Ordinal) => "Android",
        "linux" => "Linux",
        _ => "Other",
    };
}

/// <summary>The filterable, sortable and displayable fields of a device.</summary>
public static class DeviceFields
{
    public const string Name = "name";
    public const string Platform = "platform";
    public const string OsVersion = "osversion";
    public const string Join = "join";
    public const string Managed = "managed";
    public const string Compliance = "compliance";
    public const string Encryption = "encryption";
    public const string Status = "status";
    public const string Ownership = "ownership";
    public const string User = "user";
    public const string LastSeen = "lastseen";
    public const string LastCheckIn = "checkin";
    public const string Registered = "registered";
    public const string Model = "model";
    public const string Serial = "serial";
    public const string StorageFree = "storagefree";

    public const string ManagedYes = "Managed by Intune";
    public const string ManagedNo = "Not Managed";
    public const string Compliant = "Compliant";
    public const string NotCompliant = "Not Compliant";
    public const string Unknown = "Unknown";
    public const string Encrypted = "Encrypted";
    public const string NotEncrypted = "Not Encrypted";
    public const string Enabled = "Enabled";
    public const string Disabled = "Disabled";
    public const string Company = "Company-Owned";
    public const string Personal = "Personal";

    public static IReadOnlyList<Field<DeviceSummary>> All { get; } =
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Aliases = ["device", "devicename"], Text = d => d.Name },
        new()
        {
            Key = Platform, Label = "Platform", Kind = FieldKind.Choice, Aliases = ["os", "platform"],
            ChoiceOrder = ["Windows", "macOS", "iOS/iPadOS", "Android", "Linux", "Other"], Text = d => d.Platform,
        },
        new() { Key = OsVersion, Label = "OS Version", Kind = FieldKind.Text, Searchable = true, Aliases = ["version", "build"], Text = d => d.OsVersion },
        new()
        {
            Key = Join, Label = "Join Type", Kind = FieldKind.Choice, Aliases = ["jointype", "trust"],
            ChoiceOrder = ["Entra joined", "Hybrid joined", "Registered", "Intune only"], Text = d => d.JoinType,
        },
        new()
        {
            Key = Managed, Label = "Intune", Kind = FieldKind.Choice, Aliases = ["intune", "mdm"],
            ChoiceOrder = [ManagedYes, ManagedNo], Text = d => d.IsManaged ? ManagedYes : ManagedNo,
        },
        new()
        {
            Key = Compliance, Label = "Compliance", Kind = FieldKind.Choice, Aliases = ["compliant"],
            ChoiceOrder = [Compliant, NotCompliant, Unknown], Text = d => d.Compliant switch { true => Compliant, false => NotCompliant, _ => Unknown },
        },
        new()
        {
            Key = Encryption, Label = "Encryption", Kind = FieldKind.Choice, Aliases = ["encrypted", "bitlocker", "filevault"],
            ChoiceOrder = [Encrypted, NotEncrypted, Unknown], Text = d => d.Encrypted switch { true => Encrypted, false => NotEncrypted, _ => Unknown },
        },
        new()
        {
            Key = Status, Label = "Status", Kind = FieldKind.Choice, Aliases = ["enabled", "disabled"],
            ChoiceOrder = [Enabled, Disabled, Unknown], Text = d => d.Enabled switch { true => Enabled, false => Disabled, _ => Unknown },
        },
        new()
        {
            Key = Ownership, Label = "Ownership", Kind = FieldKind.Choice, Aliases = ["owner", "byod"],
            EmptyLabel = Unknown, ChoiceOrder = [Company, Personal, Unknown], Text = d => d.Ownership,
        },
        new() { Key = User, Label = "Primary User", Kind = FieldKind.Text, Searchable = true, Aliases = ["user", "person"], EmptyLabel = "No User", Text = d => d.UserName },
        new() { Key = LastSeen, Label = "Last Seen", Kind = FieldKind.Date, Aliases = ["lastseen", "active"], EmptyLabel = "Never", Date = d => d.LastSeen },
        new() { Key = LastCheckIn, Label = "Last Intune Check-In", Kind = FieldKind.Date, Aliases = ["checkin", "sync"], EmptyLabel = "Never", Date = d => d.LastCheckIn },
        new() { Key = Registered, Label = "Registered", Kind = FieldKind.Date, Aliases = ["enrolled", "registereddate"], Date = d => d.Registered },
        new()
        {
            Key = Model, Label = "Model", Kind = FieldKind.Text, Searchable = true, Aliases = ["manufacturer", "make"],
            Text = d => string.Join(' ', new[] { d.Manufacturer, d.Model }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
        },
        new() { Key = Serial, Label = "Serial Number", Kind = FieldKind.Text, Searchable = true, Aliases = ["serialnumber", "sn"], Text = d => d.Serial },
        new()
        {
            Key = StorageFree, Label = "Storage Free (GB)", Kind = FieldKind.Number, Aliases = ["free", "disk"],
            Number = d => d.StorageFree is { } free ? Math.Round(free / (double)(1L << 30), 1) : null,
        },
    ];
}
