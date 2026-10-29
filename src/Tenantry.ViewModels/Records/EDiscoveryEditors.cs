using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>New Case (8f): a name and what it's about. Add puts it in Pending changes and closes the popup.</summary>
public sealed partial class NewCaseViewModel(ChangePipeline pipeline, IEnumerable<string> taken, Action close) : ObservableObject
{
    readonly string[] _taken = [.. taken];

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [RelayCommand]
    void Add()
    {
        Problems = EDiscovery.NameProblem(Name, _taken) is { } problem ? [problem] : [];
        if (Problems.Count > 0)
            return;
        pipeline.Stage(new CreateCase(EdPlaces.CaseTarget(Name.Trim()), Description.Trim() is { Length: > 0 } d ? d : null));
        close();
    }

    [RelayCommand]
    void Cancel() => close();
}

/// <summary>A chosen mailbox or site, shown as a chip with ×.</summary>
public sealed partial class PlaceChip(ChangeTarget place, Action<PlaceChip> remove)
{
    public ChangeTarget Place => place;
    public string Name => place.Name;
    public string Detail => place.Kind == "site" ? place.Id : place.Address ?? "";

    [RelayCommand]
    void Remove() => remove(this);
}

/// <summary>Where a search or hold looks for one kind of place: nowhere, everywhere (searches only) or chosen ones.</summary>
public sealed partial class PlacesEditor : ObservableObject
{
    public const string None = "none";
    public const string All = "all";
    public const string Chosen = "chosen";

    readonly string _kind;

    public PlacesEditor(string kind, string title, bool allowAll, Func<IEnumerable<PickOption>> options, IReadOnlyList<ChangeTarget> current)
    {
        _kind = kind;
        Title = title;
        var plural = kind == "site" ? "sites and OneDrives" : "mailboxes";
        Modes =
        [
            new(None, "None", $"Don't look in any {plural}."),
            .. allowAll ? [new EditorChoice(All, "All", $"Every {(kind == "site" ? "site and OneDrive" : "mailbox")} in the organization.")] : Array.Empty<EditorChoice>(),
            new(Chosen, "Chosen", $"Only the {plural} you pick."),
        ];
        Picker = new PersonPicker(kind == "site" ? "Add a site or OneDrive: type its name or address" : "Add a mailbox: type a name or address", () =>
        {
            var have = Chips.Select(c => EdChange.Place(c.Place)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return options().Where(o => !have.Contains(kind == "site" ? o.Id : o.Address ?? o.Name));
        }, o => Add(o.Target(kind == "site" ? "site" : o.Type == "group" ? "group" : "mailbox")));
        if (EdChange.IsEverywhere(current) && allowAll)
            Mode = Modes.First(m => m.Value == All);
        else
        {
            foreach (var place in current.Where(p => p.Id != EDiscovery.All))
                Chips.Add(new PlaceChip(place, c => Chips.Remove(c)));
            Mode = Modes.First(m => m.Value == (Chips.Count > 0 ? Chosen : None));
        }
    }

    public string Title { get; }

    public IReadOnlyList<EditorChoice> Modes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChosen))]
    public partial EditorChoice Mode { get; set; }

    public bool IsChosen => Mode.Value == Chosen;

    public PersonPicker Picker { get; }

    public ObservableCollection<PlaceChip> Chips { get; } = [];

    public void Add(ChangeTarget place)
    {
        if (Chips.Any(c => string.Equals(EdChange.Place(c.Place), EdChange.Place(place), StringComparison.OrdinalIgnoreCase)))
            return;
        Chips.Add(new PlaceChip(place, c => Chips.Remove(c)));
    }

    /// <summary>The places as a change carries them.</summary>
    public IReadOnlyList<ChangeTarget> Value => Mode.Value switch
    {
        All => [EdChange.Everywhere(_kind)],
        Chosen => [.. Chips.Select(c => c.Place)],
        _ => [],
    };
}

/// <summary>
/// A new or changed search or hold (8f): name (new only), what to look for (a keyword query), where
/// (mailboxes, sites and OneDrives, public folders) and, for searches, a description. Saving puts the
/// change in Pending changes and closes the popup. A search runs again once the change is applied.
/// </summary>
public sealed partial class EdItemEditorViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly Action _close;
    readonly string[] _taken;
    readonly EdSearchDetail? _search;
    readonly EdHold? _hold;

    /// <param name="caseName">The case it's in (always, for holds), or null for a search outside any case.</param>
    /// <param name="search">The search being changed, or null.</param>
    /// <param name="hold">The hold being changed, or null.</param>
    public EdItemEditorViewModel(ChangePipeline pipeline, bool isHold, string? caseName, IEnumerable<string> taken, IDirectoryIndex index,
        Func<IReadOnlyList<SiteSummary>> sites, Action close, EdSearchDetail? search = null, EdHold? hold = null)
    {
        _pipeline = pipeline;
        _close = close;
        _taken = [.. taken];
        _search = search;
        _hold = hold;
        IsHold = isHold;
        CaseName = caseName;
        IsNew = search is null && hold is null;
        Name = search?.Search.Name ?? hold?.Name ?? "";
        Query = search?.Query ?? hold?.Query ?? "";
        PublicFolders = search?.PublicFolders ?? hold?.PublicFolders ?? false;
        Mailboxes = new PlacesEditor("mailbox", "MAILBOXES", !isHold, () => EdPlaces.MailboxOptions(index),
            EdPlaces.Mailboxes(search?.Mailboxes ?? hold?.Mailboxes ?? [], index));
        Sites = new PlacesEditor("site", "SITES AND ONEDRIVES", !isHold, () => sites().Select(EdPlaces.Option),
            EdPlaces.Sites(search?.Sites ?? hold?.Sites ?? [], sites()));
    }

    public bool IsHold { get; }
    public bool IsSearch => !IsHold;
    public bool IsNew { get; }
    public string? CaseName { get; }

    public string Intro => IsHold
        ? "While the hold is on, content in these places (all of it, or what matches the query) can't be deleted for good, whatever people or retention settings do."
        : "Purview counts what the search finds as soon as it's saved. Nothing found is changed or shown to anyone.";

    public string QueryHint => IsHold
        ? "Leave empty to keep everything. Or, for example: subject:\"contract\" AND sent>=2025-01-01"
        : "Leave empty to find everything. Or, for example: from:someone@example.com AND \"budget\"";

    public string SaveText => IsNew ? "Add to Pending Changes" : "Save to Pending Changes";

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial bool PublicFolders { get; set; }

    public PlacesEditor Mailboxes { get; }
    public PlacesEditor Sites { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>What Purview would refuse, or what would make it a different thing than intended.</summary>
    public IReadOnlyList<string> Check()
    {
        var problems = new List<string>();
        if (IsNew && EDiscovery.NameProblem(Name, _taken) is { } name)
            problems.Add(name);
        if (EDiscovery.QueryProblem(Query) is { } query)
            problems.Add(query);
        if (Mailboxes.Value.Count == 0 && Sites.Value.Count == 0 && !PublicFolders)
            problems.Add($"Choose at least one place for the {(IsHold ? "hold" : "search")}.");
        if (Mailboxes.IsChosen && Mailboxes.Chips.Count == 0)
            problems.Add("Pick the mailboxes, or choose None.");
        if (Sites.IsChosen && Sites.Chips.Count == 0)
            problems.Add("Pick the sites and OneDrives, or choose None.");
        return problems;
    }

    [RelayCommand]
    void Save()
    {
        Problems = Check();
        if (Problems.Count > 0)
            return;
        var mailboxes = Mailboxes.Value;
        var sites = Sites.Value;
        var query = Query.Trim();
        ChangeOp change = (IsHold, IsNew) switch
        {
            (false, true) => new CreateSearch(EdPlaces.SearchTarget(Name.Trim()), CaseName, query, mailboxes, sites, PublicFolders,
                Description.Trim() is { Length: > 0 } d ? d : null),
            (false, false) => new UpdateSearch(EdPlaces.SearchTarget(_search!.Search.Name), _search.Query, query, EdPlaces.Mailboxes(_search.Mailboxes, null), mailboxes,
                EdPlaces.Sites(_search.Sites, []), sites, _search.PublicFolders, PublicFolders),
            (true, true) => new CreateHold(EdPlaces.HoldTarget(Name.Trim()), CaseName!, query, mailboxes, sites, PublicFolders),
            (true, false) => new UpdateHold(EdPlaces.HoldTarget(_hold!.Name), _hold.Query, query, EdPlaces.Mailboxes(_hold.Mailboxes, null), mailboxes,
                EdPlaces.Sites(_hold.Sites, []), sites, _hold.PublicFolders, PublicFolders),
        };
        if (!change.IsNoChange)
            _pipeline.Stage(change);
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

/// <summary>eDiscovery targets and places as change targets.</summary>
public static class EdPlaces
{
    public static ChangeTarget CaseTarget(string name) => new(EdChange.CaseKind, name, name, null);
    public static ChangeTarget SearchTarget(string name) => new(EdChange.SearchKind, name, name, null);
    public static ChangeTarget HoldTarget(string name) => new(EdChange.HoldKind, name, name, null);

    /// <summary>People and shared mailboxes with a mailbox, and mail-enabled groups (Purview searches their members' mailboxes).</summary>
    public static IEnumerable<PickOption> MailboxOptions(IDirectoryIndex index) =>
        index.AllUsers.Where(u => u.Mailbox is not null).Select(u => PickOption.Of(u) with { Address = u.Mailbox!.PrimaryAddress ?? u.UserPrincipalName })
            .Concat(index.AllGroups.Where(g => g.Mail is not null && !g.IsMailbox).Select(PickOption.Of));

    public static PickOption Option(SiteSummary s) =>
        new(s.Url, s.Title, s.Url, "site", s.Alias, string.Join(' ', s.Title, s.Url).ToLowerInvariant());

    /// <summary>Addresses as targets, named from the local copy where it knows them (judged by address in Test mode).</summary>
    public static IReadOnlyList<ChangeTarget> Mailboxes(IReadOnlyList<string> addresses, IDirectoryIndex? index)
    {
        if (EDiscovery.IsAll(addresses))
            return [EdChange.Everywhere("mailbox")];
        var byAddress = new Dictionary<string, UserSummary>(StringComparer.OrdinalIgnoreCase);
        if (addresses.Count > 0 && index is not null)
            foreach (var u in index.AllUsers)
            {
                byAddress.TryAdd(u.UserPrincipalName, u);
                if (u.Mailbox?.PrimaryAddress is { } primary)
                    byAddress.TryAdd(primary, u);
            }
        return [.. addresses.Select(a => byAddress.TryGetValue(a, out var u)
            ? new ChangeTarget("mailbox", u.Id, u.DisplayName, a)
            : new ChangeTarget("mailbox", a, a, a))];
    }

    /// <summary>Site addresses as targets (judged by the end of the address in Test mode).</summary>
    public static IReadOnlyList<ChangeTarget> Sites(IReadOnlyList<string> urls, IReadOnlyList<SiteSummary> known)
    {
        if (EDiscovery.IsAll(urls))
            return [EdChange.Everywhere("site")];
        return [.. urls.Select(u => new ChangeTarget("site", u,
            known.FirstOrDefault(s => string.Equals(s.Url.TrimEnd('/'), u.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))?.Title ?? u, SiteSummary.AliasOf(u)))];
    }
}
