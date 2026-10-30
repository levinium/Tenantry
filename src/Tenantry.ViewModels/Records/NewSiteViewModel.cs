using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>A kind of site New Site can make, with what it's for.</summary>
public sealed record NewSiteKind(SiteKind Kind, string Title, string Description, string Icon);

/// <summary>
/// New Site, in one popup: a team site (made with a Microsoft 365 group, for a group of people
/// working together) or a communication site (for publishing to many), its name and address, and
/// its owners. Adding puts one change in Pending Changes.
/// </summary>
public sealed partial class NewSiteViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly IDirectoryIndex _index;
    readonly IReadOnlyList<SiteSummary> _sites;
    readonly IReadOnlyList<string> _deletedUrls;
    readonly Action _close;
    bool _aliasEdited;
    bool _filling;

    public static IReadOnlyList<NewSiteKind> Kinds { get; } =
    [
        new(SiteKind.TeamSite, "Team Site", "For people working together: shared files, plus a Microsoft 365 group's inbox and calendar.", "PeopleTeam"),
        new(SiteKind.CommunicationSite, "Communication Site", "For sharing news and information with many people, who mostly read.", "News"),
    ];

    /// <param name="rootUrl">The tenant's SharePoint address (https://contoso.sharepoint.com).</param>
    /// <param name="sites">Sites in the local copy, to catch an address already taken.</param>
    /// <param name="me">The signed-in admin, the first owner unless they're taken off.</param>
    /// <param name="deletedUrls">Deleted sites still in SharePoint's recycle bin, when they've been read: their addresses are still taken.</param>
    public NewSiteViewModel(ChangePipeline pipeline, IDirectoryIndex index, string rootUrl, IReadOnlyList<SiteSummary> sites, string? me, Action close,
        IReadOnlyList<string>? deletedUrls = null)
    {
        _pipeline = pipeline;
        _index = index;
        _sites = sites;
        _deletedUrls = deletedUrls ?? [];
        _close = close;
        RootUrl = rootUrl.TrimEnd('/');
        _kind = Kinds[0];
        OwnerPicker = new PersonPicker("Add an owner…", Candidates, p => AddOwner(p.Id));
        _me = me is null ? null : index.UserByUpn(me);
        if (_me is not null)
            Owners.Add(new ChosenPerson(_me, true));
        Changed();
    }

    public string RootUrl { get; }

    readonly UserSummary? _me;

    /// <summary>What happens to owners for this kind of site.</summary>
    public string OwnersNote => IsTeam
        ? "Owners manage the site and its Microsoft 365 group."
        : "You become its owner, since you create it. Anyone added here joins its Owners group too.";

    // A communication site's other owners join its SharePoint Owners group, which takes licensed
    // people only; a team site's join its Microsoft 365 group, which takes anyone.
    IEnumerable<PickOption> Candidates() =>
        _index.AllUsers.Where(u => u.Kind == UserKind.Member && u.Mailbox?.Type is not ("Shared" or "Room" or "Equipment"))
            .Where(u => Owners.All(p => p.Person.Id != u.Id))
            .Select(u =>
            {
                var o = PickOption.Of(u);
                return !IsTeam && u.Licenses.Count == 0 ? o with { Detail = $"{o.Detail} · {GroupAccess.NoSharePointLicense}" } : o;
            });

    NewSiteKind _kind;

    public NewSiteKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value))
                return;
            OnPropertyChanged(nameof(IsTeam));
            OnPropertyChanged(nameof(OwnersNote));
            Changed();
        }
    }

    public bool IsTeam => Kind.Kind == SiteKind.TeamSite;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Alias { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial bool IsPrivate { get; set; } = true;

    public string SiteUrl => $"{RootUrl}/sites/{Alias.Trim()}";

    partial void OnNameChanged(string value)
    {
        if (!_aliasEdited)
        {
            _filling = true;
            Alias = NewGroupViewModel.AliasFor(value);
            _filling = false;
        }
        Changed();
    }

    partial void OnAliasChanged(string value)
    {
        if (!_filling)
            _aliasEdited = value.Length > 0;
        OnPropertyChanged(nameof(SiteUrl));
        Changed();
    }

    public PersonPicker OwnerPicker { get; }

    public ObservableCollection<ChosenPerson> Owners { get; } = [];

    void AddOwner(string id)
    {
        if (_index.User(id) is not { } person)
            return;
        Owners.Add(new ChosenPerson(person, true));
        Changed();
    }

    [RelayCommand]
    void RemoveOwner(ChosenPerson person)
    {
        Owners.Remove(person);
        Changed();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    public bool ShowProblems => HasProblems && Name.Length > 0;

    void Changed()
    {
        var problems = new List<string>();
        if (Name.Trim().Length == 0)
            problems.Add("Give it a name.");
        var alias = Alias.Trim();
        if (alias.Length == 0)
            problems.Add("Give it an address (the part after /sites/).");
        else if (alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            problems.Add("An address can use letters, digits, dashes and underscores.");
        else if (_sites.Any(s => string.Equals(s.Url.TrimEnd('/'), SiteUrl, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{SiteUrl} is already a site.");
        else if (_deletedUrls.Any(u => string.Equals(u.TrimEnd('/'), SiteUrl, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{SiteUrl} belongs to a deleted site still in SharePoint's recycle bin. Restore it from Deleted Sites, or choose another address.");
        else if (IsTeam && _index.AllGroups.Any(g => g.Mail is { } mail && mail.StartsWith(alias + "@", StringComparison.OrdinalIgnoreCase)))
            problems.Add($"A group already uses the alias {alias}.");
        if (IsTeam && Owners.Count == 0)
            problems.Add("Add at least one owner.");
        Problems = problems;
        OnPropertyChanged(nameof(ShowProblems));
    }

    bool CanAdd => Problems.Count == 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    void Add()
    {
        Changed();
        if (Problems.Count > 0)
            return;
        var alias = Alias.Trim();
        // A communication site's creator is its owner already.
        var owners = Owners.Where(o => IsTeam || o.Person.Id != _me?.Id).Select(o => GroupAccess.Target(o.Person)).ToArray();
        _pipeline.Stage(new CreateSite(new ChangeTarget("site", alias, Name.Trim(), alias), SiteUrl, Kind.Kind,
            Description.Trim() is { Length: > 0 } d ? d : null, IsPrivate, owners));
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}
