using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Exporting the people of a group, mailbox or selection as CSV.</summary>
public sealed class PeopleExportTests
{
    sealed class Index(IEnumerable<UserSummary> users, IEnumerable<GroupSummary> groups) : IDirectoryIndex
    {
        readonly Dictionary<string, UserSummary> _users = users.ToDictionary(u => u.Id);
        readonly Dictionary<string, GroupSummary> _groups = groups.ToDictionary(g => g.Id);
        public UserSummary? User(string id) => _users.GetValueOrDefault(id);
        public GroupSummary? Group(string id) => _groups.GetValueOrDefault(id);
        public IEnumerable<UserSummary> AllUsers => _users.Values;
        public IEnumerable<GroupSummary> AllGroups => _groups.Values;
    }

    static UserSummary Person(string id, string name, bool enabled = true, UserKind kind = UserKind.Member, params string[] licenses) =>
        new(id, name, $"{id}@contoso.example", null, "Sales", "Planner", enabled, kind, DateTimeOffset.UnixEpoch, null, licenses);

    static GroupSummary Group(string id, string name, GroupMember[] members, GroupMember[]? owners = null, GroupKind kind = GroupKind.Security) =>
        new(id, name, null, null, kind, false, false, null, null, members, owners);

    static GroupMember U(string id) => new(id, "user");
    static GroupMember G(string id) => new(id, "group");

    static IReadOnlyList<IReadOnlyList<string>> Read(string csv) => Csv.Read(csv);

    [Fact]
    public void A_group_exports_owners_and_members_with_details()
    {
        var ada = Person("ada", "Ada", licenses: "sku1");
        var bo = Person("bo", "Bo", enabled: false, kind: UserKind.Guest);
        var group = Group("g1", "Planners", [U("bo")], owners: [U("ada")]);
        var rows = Read(PeopleExport.ToCsv(GroupPeopleEditor.ExportEntries(group), new Index([ada, bo], [group]), expandNested: false));

        Assert.Equal(["Role", "Name", "Address", "Type", "Job Title", "Department", "Sign-In", "Guest", "Licensed", "Object ID"], rows[0]);
        Assert.Equal(["Owner", "Ada", "ada@contoso.example", "User", "Planner", "Sales", "Allowed", "No", "Yes", "ada"], rows[1]);
        Assert.Equal(["Member", "Bo", "bo@contoso.example", "User", "Planner", "Sales", "Blocked", "Yes", "No", "bo"], rows[2]);
    }

    [Fact]
    public void Nested_groups_add_their_people_once_marked_with_the_group_they_came_through()
    {
        var people = new[] { Person("a", "A"), Person("b", "B"), Person("c", "C") };
        var inner = Group("inner", "Inner", [U("b"), U("a"), G("outer")]);  // a loop back to the top
        var deeper = Group("deeper", "Deeper", [U("c")]);
        var outer = Group("outer", "Outer", [U("a"), G("inner"), G("deeper")]);
        var index = new Index(people, [inner, deeper, outer]);

        var flat = Read(PeopleExport.ToCsv(GroupPeopleEditor.ExportEntries(outer), index, expandNested: false));
        Assert.Equal(4, flat.Count);  // header + a + two groups
        Assert.DoesNotContain("Through Group", flat[0]);

        var rows = Read(PeopleExport.ToCsv(GroupPeopleEditor.ExportEntries(outer), index, expandNested: true));
        Assert.Equal("Through Group", rows[0][^1]);
        var names = rows.Skip(1).Select(r => (r[1], r[^1])).ToArray();
        // Direct rows first (no "through"), then b via Inner and c via Deeper; a isn't repeated and the loop ends.
        Assert.Equal([("A", ""), ("Inner", ""), ("Deeper", ""), ("B", "Inner"), ("C", "Deeper")], names);
        Assert.Equal(5, PeopleExport.Count(GroupPeopleEditor.ExportEntries(outer), index, expandNested: true));
    }

    [Fact]
    public void A_selection_adds_a_group_column_and_rights_only_when_a_mailbox_is_in_it()
    {
        var ada = Person("ada", "Ada");
        var g = Group("g1", "Planners", [U("ada")]);
        var box = Group("m1", "Front Desk", [new GroupMember("ada", "user", "Full access, Send as")], kind: GroupKind.SharedMailbox);
        var index = new Index([ada], [g, box]);

        var groupsOnly = Read(PeopleExport.ToCsv(GroupPeopleEditor.ExportEntries(g, g.DisplayName), index, false, "Group", "Rights"));
        Assert.Equal("Group", groupsOnly[0][0]);
        Assert.DoesNotContain("Rights", groupsOnly[0]);

        var both = Read(PeopleExport.ToCsv(GroupPeopleEditor.ExportEntries(g, g.DisplayName).Concat(GroupPeopleEditor.ExportEntries(box, box.DisplayName)),
            index, false, "Group", "Rights"));
        Assert.Equal("Rights", both[0][^1]);
        Assert.Equal(["Front Desk", "Has Access"], both[2].Take(2));
        Assert.Equal("Full access, Send as", both[2][^1]);
    }

    [Fact]
    public void Unknown_members_are_kept_with_what_is_known()
    {
        var group = Group("g1", "List", [new GroupMember("x", "unresolved", Name: "someone@elsewhere.example"), new GroupMember("d1", "device")]);
        var rows = Read(PeopleExport.ToCsv(GroupPeopleEditor.ExportEntries(group), new Index([], [group]), false));
        Assert.Equal(["Member", "someone@elsewhere.example", "someone@elsewhere.example", "Other", "", "", "", "", "", ""], rows[1]);
        Assert.Equal("Device", rows[2][3]);
        Assert.Equal("d1", rows[2][^1]);
    }

    [Fact]
    public void Names_that_look_like_formulas_stay_text()
    {
        var sneaky = Person("x", "=HYPERLINK(\"http://example.invalid\",\"Click\")") with { JobTitle = "+1 555 0100" };
        var group = Group("g1", "G", [U("x")]);
        var rows = Read(PeopleExport.ToCsv(GroupPeopleEditor.ExportEntries(group), new Index([sneaky], [group]), false));
        Assert.StartsWith("'=HYPERLINK", rows[1][1], StringComparison.Ordinal);
        Assert.Equal("+1 555 0100", rows[1][4]);  // a phone number is left alone
    }

    [Fact]
    public void Someone_named_by_another_service_is_matched_to_the_directory_or_kept_as_given()
    {
        var ada = Person("ada", "Ada");
        var index = new Index([ada], []);
        var byUpn = ExportEntry.Principal("Site Owners", null, "Ada L.", "ada@contoso.example", "Person", index);
        Assert.Equal(("user", "ada"), (byUpn.Member.Type, byUpn.Member.Id));
        var outsider = ExportEntry.Principal("Guest", null, "Pat", "pat@partner.example", "Guest", index);
        var everyone = ExportEntry.Principal("Included", null, "All Users", null, "Everyone", index, detail: "Only devices matching Laptops");
        var rows = Read(PeopleExport.ToCsv([byUpn, outsider, everyone], index, false, detailHeading: "Filter"));
        Assert.Equal(["Site Owners", "Ada", "ada@contoso.example", "User"], rows[1].Take(4));
        Assert.Equal(["Guest", "Pat", "pat@partner.example", "Guest"], rows[2].Take(4));
        Assert.Equal(["Included", "All Users", "", "Everyone"], rows[3].Take(4));
        Assert.Equal("Only devices matching Laptops", rows[3][^1]);
    }

    [Fact]
    public void A_list_of_someones_groups_never_offers_nested_people()
    {
        var group = Group("g1", "Planners", [U("ada")]);
        var index = new Index([Person("ada", "Ada")], [group]);
        var exporter = new PeopleExporter(() => "Ada", () => [new ExportEntry("Member", G("g1"))], index, PeopleExporter.Everyone(), nested: false);
        exporter.Refresh();
        Assert.False(exporter.HasNested);
        var offered = new PeopleExporter(() => "x", () => [new ExportEntry("Member", G("g1"))], index, PeopleExporter.Everyone());
        offered.Refresh();
        Assert.True(offered.HasNested);
    }

    [Fact]
    public void File_names_drop_characters_files_cannot_have()
    {
        var name = PeopleExport.FileName("Sales/Ops: \"Team\"", "members");
        Assert.EndsWith(".csv", name, StringComparison.Ordinal);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('"', name);
        Assert.StartsWith("Sales-Ops- -Team- members ", name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_big_nested_group_exports_quickly()
    {
        var users = Enumerable.Range(0, 50_000).Select(i => Person($"u{i}", $"Person {i}")).ToArray();
        var children = Enumerable.Range(0, 50).Select(c => Group($"c{c}", $"Child {c}", users.Skip(c * 1000).Take(1000).Select(u => U(u.Id)).ToArray())).ToArray();
        var top = Group("top", "Top", children.Select(c => G(c.Id)).Concat(users.Take(1000).Select(u => U(u.Id))).ToArray());
        var index = new Index(users, [.. children, top]);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var csv = PeopleExport.ToCsv(GroupPeopleEditor.ExportEntries(top), index, expandNested: true);
        watch.Stop();
        // 50 groups + 1,000 direct people + the other 49,000 through the children (the first 1,000 are direct).
        Assert.Equal(50 + 50_000 + 1, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }
}
