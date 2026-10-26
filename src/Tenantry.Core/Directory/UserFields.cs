using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>The filterable, sortable and displayable fields of a user.</summary>
public static class UserFields
{
    public const string Name = "name";
    public const string SignInName = "upn";
    public const string Email = "mail";
    public const string Department = "department";
    public const string Title = "title";
    public const string Status = "status";
    public const string Type = "type";
    public const string Licenses = "license";
    public const string LicenseCount = "licensecount";
    public const string LastSignIn = "signin";
    public const string Created = "created";
    public const string Mailbox = "mailbox";
    public const string Addresses = "addresses";
    public const string Forwarding = "forwarding";
    public const string ForwardingTo = "forwardto";
    public const string LitigationHold = "hold";
    public const string Archive = "archive";
    public const string AddressList = "addresslist";
    public const string AutoReply = "autoreply";
    public const string StorageUsed = "storage";
    public const string Storage = "storagestate";
    public const string EmailAppsOn = "emailapps";
    public const string Mfa = "mfa";
    public const string AdminRole = "admin";

    public const string Active = "Active";
    public const string Blocked = "Blocked";
    public const string Member = "Member";
    public const string Guest = "Guest";
    public const string Unlicensed = "Unlicensed";
    public const string NoMailbox = "No Mailbox";
    public const string SharedMailbox = "Shared";
    public const string NotForwarding = "Not Forwarding";
    public const string ForwardsInternally = "Inside the Organization";
    public const string ForwardsExternally = "Outside the Organization";
    public const string On = "On";
    public const string Off = "Off";
    public const string SharedMailboxType = "Shared Mailbox";
    public const string RoomType = "Room";
    public const string EquipmentType = "Equipment";
    public const string Shown = "Shown";
    public const string Hidden = "Hidden";
    public const string Scheduled = "Scheduled";
    public const string NotReadYet = "Not Read Yet";
    public const string NearlyFull = "Nearly Full";
    public const string NotFull = "Under 90%";
    public const string MfaRegistered = "Registered";
    public const string MfaNotRegistered = "Not Registered";
    public const string IsAdmin = "Admin";
    public const string NotAdmin = "Not Admin";

    /// <summary>
    /// What kind of account this is. Entra only says Member or Guest; Exchange knows a shared,
    /// room or equipment mailbox's account isn't a person, and that is what an admin needs to see.
    /// </summary>
    public static string TypeOf(UserSummary u) => u.Mailbox?.Type switch
    {
        "Shared" => SharedMailboxType,
        "Room" => RoomType,
        "Equipment" => EquipmentType,
        _ => u.Kind == UserKind.Guest ? Guest : Member,
    };

    static string? OnOff(UserSummary u, Func<MailboxInfo, bool> flag) => u.Mailbox is { } m ? (flag(m) ? On : Off) : null;

    public static IReadOnlyList<Field<UserSummary>> All { get; } =
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Text = u => u.DisplayName },
        new() { Key = SignInName, Label = "Sign-In Name", Kind = FieldKind.Text, Searchable = true, Aliases = ["signinname", "userprincipalname"], Text = u => u.UserPrincipalName },
        new() { Key = Email, Label = "Email", Kind = FieldKind.Text, Searchable = true, Aliases = ["email"], Text = u => u.Mail },
        new() { Key = Department, Label = "Department", Kind = FieldKind.Choice, Searchable = true, Aliases = ["dept"], EmptyLabel = "No Department", Text = u => u.Department },
        new() { Key = Title, Label = "Job Title", Kind = FieldKind.Text, Searchable = true, Aliases = ["jobtitle"], Text = u => u.JobTitle },
        new()
        {
            Key = Status, Label = "Status", Kind = FieldKind.Choice, Aliases = ["signinstatus"],
            ChoiceOrder = [Active, Blocked], Text = u => u.AccountEnabled ? Active : Blocked,
        },
        new()
        {
            Key = Type, Label = "Type", Kind = FieldKind.Choice, Aliases = ["usertype", "kind"],
            ChoiceOrder = [Member, Guest, SharedMailboxType, RoomType, EquipmentType], Text = TypeOf,
        },
        new()
        {
            Key = Licenses, Label = "Licenses", Kind = FieldKind.MultiChoice, Searchable = true, Aliases = ["licenses", "lic", "sku"],
            EmptyLabel = Unlicensed, Values = u => u.Licenses,
        },
        new() { Key = LicenseCount, Label = "License Count", Kind = FieldKind.Number, Aliases = ["licensecount", "lc"], Number = u => u.Licenses.Count },
        new() { Key = LastSignIn, Label = "Last Sign-In", Kind = FieldKind.Date, Aliases = ["lastsignin", "login"], EmptyLabel = "Never", Date = u => u.LastSignIn },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Aliases = ["createddate"], Date = u => u.CreatedAt },

        // From Exchange (Get-Mailbox). Empty for everyone when the admin has no Exchange role.
        new()
        {
            Key = Mailbox, Label = "Mailbox", Kind = FieldKind.Choice, Aliases = ["mailboxtype", "mbx"],
            EmptyLabel = NoMailbox, ChoiceOrder = ["User", SharedMailbox, "Room", "Equipment", NoMailbox], Text = u => u.Mailbox?.Type,
        },
        new()
        {
            Key = Addresses, Label = "Email Addresses", Kind = FieldKind.Text, Searchable = true, Aliases = ["alias", "aliases", "proxy"],
            Text = u => u.Mailbox is { } m ? string.Join(" ", m.Aliases.Prepend(m.PrimaryAddress ?? "")).Trim() : null,
        },
        new()
        {
            Key = Forwarding, Label = "Forwarding", Kind = FieldKind.Choice, Aliases = ["forward", "fwd"],
            EmptyLabel = NotForwarding, ChoiceOrder = [ForwardsExternally, ForwardsInternally, NotForwarding],
            Text = u => u.Mailbox?.Forwarding switch
            {
                ForwardingKind.External => ForwardsExternally,
                ForwardingKind.Internal => ForwardsInternally,
                _ => null,
            },
        },
        new() { Key = ForwardingTo, Label = "Forwards To", Kind = FieldKind.Text, Searchable = true, Aliases = ["forwardsto"], Text = u => u.Mailbox?.ForwardingTo },
        new()
        {
            Key = LitigationHold, Label = "Litigation Hold", Kind = FieldKind.Choice, Aliases = ["litigationhold", "lithold"],
            EmptyLabel = NoMailbox, ChoiceOrder = [On, Off, NoMailbox], Text = u => OnOff(u, m => m.LitigationHold),
        },
        new()
        {
            Key = Archive, Label = "Archive", Kind = FieldKind.Choice, Aliases = ["archivemailbox"],
            EmptyLabel = NoMailbox, ChoiceOrder = [On, Off, NoMailbox], Text = u => OnOff(u, m => m.ArchiveEnabled),
        },
        new()
        {
            Key = AddressList, Label = "Address List", Kind = FieldKind.Choice, Aliases = ["gal", "hidden", "addressbook"],
            EmptyLabel = NoMailbox, ChoiceOrder = [Shown, Hidden, NoMailbox],
            Text = u => u.Mailbox is { } m ? (m.HiddenFromAddressList ? Hidden : Shown) : null,
        },

        // From the hourly mailbox-details read (size and automatic replies, one mailbox at a time).
        new()
        {
            Key = AutoReply, Label = "Automatic Replies", Kind = FieldKind.Choice, Aliases = ["autoreplies", "oof", "outofoffice"],
            EmptyLabel = NoMailbox, ChoiceOrder = [On, Scheduled, Off, NotReadYet, NoMailbox],
            Text = u => u.Mailbox is not { } m ? null
                : m.AutoReply switch
                {
                    AutoReplyState.On => On,
                    AutoReplyState.Scheduled => Scheduled,
                    AutoReplyState.Off => Off,
                    _ => NotReadYet,
                },
        },
        new()
        {
            Key = Storage, Label = "Mailbox Storage", Kind = FieldKind.Choice, Aliases = ["full", "quota"],
            EmptyLabel = NoMailbox, ChoiceOrder = [NearlyFull, NotFull, NotReadYet, NoMailbox],
            Text = u => u.Mailbox is not { } m ? null : m.UsedPercent is null ? NotReadYet : m.IsNearlyFull ? NearlyFull : NotFull,
        },
        new()
        {
            Key = StorageUsed, Label = "Storage Used (%)", Kind = FieldKind.Number, Aliases = ["used", "storageused"],
            Number = u => u.Mailbox?.UsedPercent is { } p ? Math.Round(p) : null,
        },
        new()
        {
            Key = EmailAppsOn, Label = "Email Apps On", Kind = FieldKind.MultiChoice, Aliases = ["apps", "protocols", "pop", "imap"],
            EmptyLabel = "None", ChoiceOrder = EmailApps.All.Select(a => a.Name).ToArray(),
            Values = u => u.Mailbox?.Apps is { } apps ? EmailApps.All.Where(a => apps.IsOn(a.Parameter)).Select(a => a.Name).ToArray() : [],
        },

        // From Entra's MFA registration report (needs Entra ID P1; "Not read" without it).
        new()
        {
            Key = Mfa, Label = "MFA", Kind = FieldKind.Choice, Aliases = ["multifactor", "2fa"],
            EmptyLabel = NotReadYet, ChoiceOrder = [MfaRegistered, MfaNotRegistered, NotReadYet],
            Text = u => u.Security is { } s ? (s.MfaRegistered ? MfaRegistered : MfaNotRegistered) : null,
        },
        new()
        {
            Key = AdminRole, Label = "Admin Role", Kind = FieldKind.Choice, Aliases = ["admin", "isadmin"],
            EmptyLabel = NotReadYet, ChoiceOrder = [IsAdmin, NotAdmin, NotReadYet],
            Text = u => u.Security is { } s ? (s.IsAdmin ? IsAdmin : NotAdmin) : null,
        },
    ];
}
