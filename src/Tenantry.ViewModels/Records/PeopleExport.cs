using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// One person (or group, contact, device, app) attached to something, for an export.
/// <paramref name="Container"/> names what they're attached to when one file covers several
/// (a selection of groups); <paramref name="Detail"/> is a fact about the attachment itself
/// (a mailbox's rights, a role's scope). <paramref name="ContainerId"/> is the exported group's
/// own id, so a nested group that loops back to it doesn't list it as its own member.
/// </summary>
/// <param name="Address">For someone the local copy doesn't hold: the address to show.</param>
/// <param name="TypeText">For someone the local copy doesn't hold: what they are ("Person", "Everyone", "App").</param>
public sealed record ExportEntry(string Role, GroupMember Member, string? Container = null, string? Detail = null, string? ContainerId = null,
    string? Address = null, string? TypeText = null)
{
    /// <summary>
    /// Someone named by another service (SharePoint, a role, an app): matched to the local copy
    /// by object id, then by sign-in address, so they get full details; otherwise kept as given.
    /// </summary>
    public static ExportEntry Principal(string role, string? id, string name, string? address, string typeText, IDirectoryIndex index,
        string? detail = null, string? container = null)
    {
        if (id is { Length: > 0 } && index.User(id) is { } byId)
            return new(role, new GroupMember(byId.Id, "user"), container, detail);
        if (id is { Length: > 0 } && index.Group(id) is { } group)
            return new(role, new GroupMember(group.Id, "group"), container, detail);
        if (address is { Length: > 0 } && address.Contains('@', StringComparison.Ordinal) && index.UserByUpn(address) is { } byUpn)
            return new(role, new GroupMember(byUpn.Id, "user"), container, detail);
        return new(role, new GroupMember(id ?? "", "unresolved", Name: name), container, detail, Address: address, TypeText: typeText);
    }
}

/// <summary>
/// Writes people lists as CSV: who they are (from the local copy), how they're attached, and
/// optionally everyone inside nested groups, each marked with the group they came through.
/// </summary>
public static class PeopleExport
{
    /// <param name="containerHeading">Heading of the first column when entries name a container ("Group"); null leaves it out.</param>
    /// <param name="detailHeading">Heading for <see cref="ExportEntry.Detail"/> ("Rights"); null leaves it out.</param>
    public static string ToCsv(IEnumerable<ExportEntry> entries, IDirectoryIndex index, bool expandNested,
        string? containerHeading = null, string? detailHeading = null)
    {
        var list = entries as IReadOnlyCollection<ExportEntry> ?? entries.ToArray();
        entries = list;
        // Only where something has one (a selection of groups only has no mailbox rights).
        if (!list.Any(e => e.Detail is not null))
            detailHeading = null;
        var header = new List<string>();
        if (containerHeading is not null)
            header.Add(containerHeading);
        header.Add("Role");
        header.AddRange(["Name", "Address", "Type", "Job Title", "Department", "Sign-In", "Guest", "Licensed", "Object ID"]);
        if (detailHeading is not null)
            header.Add(detailHeading);
        if (expandNested)
            header.Add("Through Group");
        var rows = new List<IEnumerable<string>> { header };
        foreach (var row in Rows(entries, index, expandNested))
        {
            var cells = new List<string>();
            if (containerHeading is not null)
                cells.Add(row.Entry.Container ?? "");
            cells.Add(row.Entry.Role);
            cells.AddRange(Describe(row.Member, index, row.Through is null ? row.Entry : null));
            if (detailHeading is not null)
                cells.Add(row.Entry.Detail ?? "");
            if (expandNested)
                cells.Add(row.Through ?? "");
            rows.Add(cells);
        }
        return Csv.WriteInert(rows);
    }

    /// <summary>How many rows <see cref="ToCsv"/> writes (for the "Saved N rows" note).</summary>
    public static int Count(IEnumerable<ExportEntry> entries, IDirectoryIndex index, bool expandNested) =>
        Rows(entries, index, expandNested).Count();

    /// <summary>Whether any entry is a group, so expanding nested groups would add anyone.</summary>
    public static bool HasGroups(IEnumerable<ExportEntry> entries) => entries.Any(e => e.Member.Type == "group");

    /// <summary>
    /// The entries as given, then (when expanding) everyone inside each nested group, through any
    /// depth, once per container and role: someone already listed directly isn't repeated, and a
    /// group that contains itself (directly or round a loop) is read once.
    /// </summary>
    static IEnumerable<(ExportEntry Entry, GroupMember Member, string? Through)> Rows(IEnumerable<ExportEntry> entries, IDirectoryIndex index, bool expandNested)
    {
        var list = entries as IReadOnlyCollection<ExportEntry> ?? entries.ToArray();
        foreach (var entry in list)
            yield return (entry, entry.Member, null);
        if (!expandNested)
            yield break;
        foreach (var set in list.GroupBy(e => (e.Container, e.Role)))
        {
            var seen = set.Select(e => e.Member.Id).ToHashSet(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var self in set.Select(e => e.ContainerId).OfType<string>())
            {
                seen.Add(self);
                visited.Add(self);
            }
            var queue = new Queue<(ExportEntry Entry, string GroupId)>(set.Where(e => e.Member.Type == "group").Select(e => (e, e.Member.Id)));
            while (queue.Count > 0)
            {
                var (entry, groupId) = queue.Dequeue();
                if (!visited.Add(groupId) || index.Group(groupId) is not { } group)
                    continue;
                foreach (var member in group.Members)
                {
                    if (member.Type == "group")
                        queue.Enqueue((entry, member.Id));
                    if (seen.Add(member.Id))
                        yield return (entry, member, group.DisplayName);
                }
            }
        }
    }

    /// <summary>Name, address, type, job title, department, sign-in, guest, licensed, object ID.</summary>
    /// <param name="given">The entry itself (not a nested person), whose address and type are used when the local copy doesn't know them.</param>
    static string[] Describe(GroupMember member, IDirectoryIndex index, ExportEntry? given)
    {
        if (member.Type == "unresolved" && given is not null)
            // An address Exchange couldn't match is its own name; something named by type ("All Users") has none.
            return [member.Name ?? "", given.Address ?? (given.TypeText is null ? member.Name ?? "" : ""), given.TypeText ?? "Other", "", "", "", "", "",
                given.Address is null && given.TypeText is null ? "" : member.Id];
        var row = GroupAccess.Describe(member, index);
        if (row.User is { } u)
            return [u.DisplayName, u.Mail ?? u.UserPrincipalName, "User", u.JobTitle ?? "", u.Department ?? "",
                u.AccountEnabled ? "Allowed" : "Blocked", u.Kind == UserKind.Guest ? "Yes" : "No", u.Licenses.Count > 0 ? "Yes" : "No", u.Id];
        if (row.Group is { } g)
            return [g.DisplayName, g.Mail ?? "", "Group", "", "", "", "", "", g.Id];
        if (row.Contact is { } c)
            return [c.DisplayName, c.Email ?? "", "Contact", "", c.Company ?? "", "", "", "", c.Id];
        // Unresolved addresses carry the address as their name; others keep their Graph id.
        return [row.Name, member.Type == "unresolved" ? member.Name ?? "" : "", row.TypeText, "", "", "", "", "",
            member.Type == "unresolved" ? "" : member.Id];
    }

    /// <summary>A file name from what's exported ("Sales Team owners 2026-11-25.csv"), without characters files can't have.</summary>
    public static string FileName(string what, string? part = null)
    {
        var name = string.Join(' ', new[] { what, part, DateTime.Now.ToString("yyyy-MM-dd") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        foreach (var c in Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']))
            name = name.Replace(c, '-');
        return name.Trim() + ".csv";
    }
}

/// <summary>One button in the Export pop-up: what it saves (all roles, or only some).</summary>
public sealed record ExportChoice(string Label, string Tip, IReadOnlySet<string>? Roles, string? FilePart);

/// <summary>
/// The Export button for a list of people: a pop-up with a button per choice (owners and
/// members, owners only, members only) and, where the list holds groups, "Include people in
/// nested groups". Each choice saves a CSV file through the usual save dialog.
/// </summary>
public sealed partial class PeopleExporter : ObservableObject
{
    readonly Func<IReadOnlyList<ExportEntry>> _entries;
    readonly IDirectoryIndex _index;
    readonly Func<string> _what;
    readonly string? _containerHeading;
    readonly string? _detailHeading;
    readonly bool _nested;

    /// <param name="what">What's exported, for the file name ("Sales Team").</param>
    /// <param name="entries">Read when the pop-up opens and when a choice is clicked, so it's always current.</param>
    /// <param name="nested">Offer "Include people in nested groups" when the list holds groups (off for a list of someone's groups).</param>
    public PeopleExporter(Func<string> what, Func<IReadOnlyList<ExportEntry>> entries, IDirectoryIndex index,
        IReadOnlyList<ExportChoice> choices, string? containerHeading = null, string? detailHeading = null, bool nested = true)
    {
        _nested = nested;
        _what = what;
        _entries = entries;
        _index = index;
        _containerHeading = containerHeading;
        _detailHeading = detailHeading;
        Choices = choices;
    }

    /// <summary>A single choice saving everything (a list with one kind of person).</summary>
    public static IReadOnlyList<ExportChoice> Everyone(string label = "Save as a CSV File") => [new(label, "Save everyone listed as a CSV file", null, null)];

    /// <summary>Owners and members, owners only, members only.</summary>
    public static IReadOnlyList<ExportChoice> OwnersAndMembers { get; } =
    [
        new("Owners and Members", "One file with everyone, each row marked Owner or Member", null, "owners and members"),
        new("Owners Only", "Only the owners", new HashSet<string>(StringComparer.Ordinal) { "Owner" }, "owners"),
        // A shared mailbox's people count as its members here (a selection can mix both).
        new("Members Only", "Only the members", new HashSet<string>(StringComparer.Ordinal) { "Member", "Has Access" }, "members"),
    ];

    public IReadOnlyList<ExportChoice> Choices { get; }

    /// <summary>Also list everyone inside groups that are members (through any depth).</summary>
    [ObservableProperty]
    public partial bool IncludeNested { get; set; }

    /// <summary>Shown only where the list holds a group.</summary>
    [ObservableProperty]
    public partial bool HasNested { get; private set; }

    [ObservableProperty]
    public partial bool HasAny { get; private set; }

    /// <summary>"Saved 42 rows to Sales Team members 2026-11-25.csv", or why not.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    /// <summary>Reads the list again (the pop-up calls it on opening).</summary>
    public void Refresh()
    {
        var entries = _entries();
        HasAny = entries.Count > 0;
        HasNested = _nested && PeopleExport.HasGroups(entries);
        Status = null;
    }

    /// <summary>Checking for changes before saving: the choices wait.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool IsChecking { get; private set; }

    bool CanExport => !IsChecking;

    [RelayCommand(CanExecute = nameof(CanExport))]
    async Task Export(ExportChoice choice)
    {
        // The file should match Microsoft 365 now, not the copy as it was at the last check.
        IsChecking = true;
        Status = "Checking Microsoft 365 for changes first…";
        string? note;
        try
        {
            note = await Freshness.BeforeExportAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            note = $"Couldn't check for changes ({Friendly.Message(e)}), so this file uses the copy as it was.";
        }
        finally
        {
            IsChecking = false;
        }
        Status = null;
        var entries = _entries().Where(e => choice.Roles is null || choice.Roles.Contains(e.Role)).ToArray();
        var nested = _nested && IncludeNested && PeopleExport.HasGroups(entries);
        var csv = PeopleExport.ToCsv(entries, _index, nested, _containerHeading, _detailHeading);
        var name = PeopleExport.FileName(_what(), choice.FilePart);
        var count = PeopleExport.Count(entries, _index, nested);
        try
        {
            Status = await Platform.SaveCsv(name, csv) ? $"Saved {count:N0} {(count == 1 ? "row" : "rows")}." + (note is null ? "" : " " + note) : note;
        }
        catch (Exception e)
        {
            Status = "Couldn't save: " + Friendly.Message(e);
        }
    }
}
