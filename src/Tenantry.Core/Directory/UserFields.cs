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

    public const string Active = "Active";
    public const string Blocked = "Blocked";
    public const string Member = "Member";
    public const string Guest = "Guest";
    public const string Unlicensed = "Unlicensed";

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
    ];
}
