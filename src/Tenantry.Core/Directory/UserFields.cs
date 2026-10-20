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

    public const string Active = "Active";
    public const string Blocked = "Blocked";
    public const string Member = "Member";
    public const string Guest = "Guest";
    public const string Unlicensed = "Unlicensed";
    public const string NoMailbox = "No mailbox";
    public const string SharedMailbox = "Shared";
    public const string NotForwarding = "Not forwarding";
    public const string ForwardsInternally = "Inside the organization";
    public const string ForwardsExternally = "Outside the organization";
    public const string On = "On";
    public const string Off = "Off";

    static string? OnOff(UserSummary u, Func<MailboxInfo, bool> flag) => u.Mailbox is { } m ? (flag(m) ? On : Off) : null;

    public static IReadOnlyList<Field<UserSummary>> All { get; } =
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Text = u => u.DisplayName },
        new() { Key = SignInName, Label = "Sign-in name", Kind = FieldKind.Text, Searchable = true, Aliases = ["signinname", "userprincipalname"], Text = u => u.UserPrincipalName },
        new() { Key = Email, Label = "Email", Kind = FieldKind.Text, Searchable = true, Aliases = ["email"], Text = u => u.Mail },
        new() { Key = Department, Label = "Department", Kind = FieldKind.Choice, Searchable = true, Aliases = ["dept"], EmptyLabel = "No department", Text = u => u.Department },
        new() { Key = Title, Label = "Job title", Kind = FieldKind.Text, Searchable = true, Aliases = ["jobtitle"], Text = u => u.JobTitle },
        new()
        {
            Key = Status, Label = "Status", Kind = FieldKind.Choice, Aliases = ["signinstatus"],
            ChoiceOrder = [Active, Blocked], Text = u => u.AccountEnabled ? Active : Blocked,
        },
        new()
        {
            Key = Type, Label = "Type", Kind = FieldKind.Choice, Aliases = ["usertype", "kind"],
            ChoiceOrder = [Member, Guest], Text = u => u.Kind == UserKind.Guest ? Guest : Member,
        },
        new()
        {
            Key = Licenses, Label = "Licenses", Kind = FieldKind.MultiChoice, Searchable = true, Aliases = ["licenses", "lic", "sku"],
            EmptyLabel = Unlicensed, Values = u => u.Licenses,
        },
        new() { Key = LicenseCount, Label = "License count", Kind = FieldKind.Number, Aliases = ["licensecount", "lc"], Number = u => u.Licenses.Count },
        new() { Key = LastSignIn, Label = "Last sign-in", Kind = FieldKind.Date, Aliases = ["lastsignin", "login"], EmptyLabel = "Never", Date = u => u.LastSignIn },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Aliases = ["createddate"], Date = u => u.CreatedAt },

        // From Exchange (Get-Mailbox). Empty for everyone when the admin has no Exchange role.
        new()
        {
            Key = Mailbox, Label = "Mailbox", Kind = FieldKind.Choice, Aliases = ["mailboxtype", "mbx"],
            EmptyLabel = NoMailbox, ChoiceOrder = ["User", SharedMailbox, "Room", "Equipment", NoMailbox], Text = u => u.Mailbox?.Type,
        },
        new()
        {
            Key = Addresses, Label = "Email addresses", Kind = FieldKind.Text, Searchable = true, Aliases = ["alias", "aliases", "proxy"],
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
        new() { Key = ForwardingTo, Label = "Forwards to", Kind = FieldKind.Text, Searchable = true, Aliases = ["forwardsto"], Text = u => u.Mailbox?.ForwardingTo },
        new()
        {
            Key = LitigationHold, Label = "Litigation hold", Kind = FieldKind.Choice, Aliases = ["litigationhold", "lithold"],
            EmptyLabel = NoMailbox, ChoiceOrder = [On, Off, NoMailbox], Text = u => OnOff(u, m => m.LitigationHold),
        },
        new()
        {
            Key = Archive, Label = "Archive", Kind = FieldKind.Choice, Aliases = ["archivemailbox"],
            EmptyLabel = NoMailbox, ChoiceOrder = [On, Off, NoMailbox], Text = u => OnOff(u, m => m.ArchiveEnabled),
        },
    ];
}
