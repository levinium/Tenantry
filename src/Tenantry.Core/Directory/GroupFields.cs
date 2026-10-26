using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>The filterable, sortable and displayable fields of a group.</summary>
public static class GroupFields
{
    public const string Name = "name";
    public const string Email = "mail";
    public const string Type = "type";
    public const string Membership = "membership";
    public const string Team = "team";
    public const string Visibility = "visibility";
    public const string Members = "members";
    public const string Created = "created";
    public const string Description = "description";

    public const string Assigned = "Assigned";
    public const string Dynamic = "Dynamic";
    public const string Yes = "Yes";
    public const string No = "No";

    public static IReadOnlyList<Field<GroupSummary>> All { get; } =
    [
        new() { Key = Name, Label = "Name", Kind = FieldKind.Text, Searchable = true, Text = g => g.DisplayName },
        new() { Key = Email, Label = "Email", Kind = FieldKind.Text, Searchable = true, Aliases = ["email"], Text = g => g.Mail },
        new()
        {
            Key = Type, Label = "Type", Kind = FieldKind.Choice, Aliases = ["kind", "grouptype"],
            ChoiceOrder = Enum.GetValues<GroupKind>().Select(GroupSummary.KindName).ToArray(),
            Text = g => GroupSummary.KindName(g.Kind),
        },
        new()
        {
            Key = Membership, Label = "Membership", Kind = FieldKind.Choice, Aliases = ["membershiptype"],
            ChoiceOrder = [Assigned, Dynamic], Text = g => g.IsDynamic ? Dynamic : Assigned,
        },
        new() { Key = Team, Label = "Team", Kind = FieldKind.Choice, Aliases = ["teams"], ChoiceOrder = [Yes, No], Text = g => g.IsTeam ? Yes : No },
        new() { Key = Visibility, Label = "Visibility", Kind = FieldKind.Choice, EmptyLabel = "Not Set", Text = g => g.Visibility },
        new() { Key = Members, Label = "Members", Kind = FieldKind.Number, Aliases = ["membercount", "count"], Number = g => g.Members.Count },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Aliases = ["createddate"], Date = g => g.CreatedAt },
        new() { Key = Description, Label = "Description", Kind = FieldKind.Text, Searchable = true, Aliases = ["desc"], Text = g => g.Description },
    ];
}
