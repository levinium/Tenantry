using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>A license offered to a new person, ticked or not, with how many are left.</summary>
public sealed partial class NewLicenseChoice(LicenseSku sku) : ObservableObject
{
    public LicenseSku Sku { get; } = sku;
    public string Name => Sku.Name;
    public string LeftText => Sku.Available == 1 ? "1 left" : $"{Sku.Available} left";
    public bool IsAvailable => Sku.Available > 0;

    [ObservableProperty]
    public partial bool IsChosen { get; set; }
}

/// <summary>A group a new person will join, shown as a chip with × (the form's RemoveGroup command).</summary>
public sealed record ChosenGroup(GroupSummary Group)
{
    public string Name => Group.DisplayName;
    public string Kind => GroupSummary.KindName(Group.Kind);
}

/// <summary>
/// New User, in one popup: name and sign-in, password, licenses, then (folded away until wanted)
/// groups and manager, and job and contact details. "Copy from someone" fills the department,
/// title, office, manager, groups and licenses from a colleague. Adding it puts one change in
/// Pending Changes; nothing is created until the changes are applied.
/// </summary>
public sealed partial class NewUserViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly IUserRecords? _records;
    readonly IDirectoryIndex _index;
    readonly Action _close;
    bool _displayNameEdited;
    bool _aliasEdited;

    public NewUserViewModel(ChangePipeline pipeline, IUserRecords? records, IDirectoryIndex index, Action close)
    {
        _pipeline = pipeline;
        _records = records;
        _index = index;
        _close = close;
        CopyFrom = new PersonPicker("Copy from a colleague…", () => _index.AllUsers.Where(u => u.Kind == UserKind.Member).Select(PickOption.Of), p => _ = CopyAsync(p.Id));
        ManagerPicker = new PersonPicker("Search for their manager…", () => _index.AllUsers.Where(u => u.Kind == UserKind.Member).Select(PickOption.Of), p =>
        {
            ManagerId = p.Id;
            ManagerName = p.Name;
        });
        GroupPicker = new PersonPicker("Add a group…", () => _index.AllGroups.Where(CanJoin).Where(g => Groups.All(c => c.Group.Id != g.Id)).Select(PickOption.Of), p =>
        {
            if (_index.Group(p.Id) is { } group)
                AddGroup(group);
        });
        Countries = Core.Directory.Countries.All.Select(c => new Choice<string>(c.Code, c.Name)).ToArray();
        var here = RegionInfo.CurrentRegion.TwoLetterISORegionName;
        _usageLocation = Countries.FirstOrDefault(c => c.Value == here) ?? Countries[0];
        _ = LoadAsync();
    }

    /// <summary>Dynamic groups follow their rule; mailboxes give access rather than membership.</summary>
    static bool CanJoin(GroupSummary g) => !g.IsDynamic && !g.IsMailbox;

    async Task LoadAsync()
    {
        if (_records is null)
            return;
        try
        {
            Domains = await _records.GetDomainsAsync();
            Domain ??= Domains.FirstOrDefault();
            foreach (var sku in (await _records.GetLicenseCatalogAsync()).OrderBy(s => s.Name, StringComparer.CurrentCulture))
            {
                var choice = new NewLicenseChoice(sku);
                choice.PropertyChanged += (_, _) => Changed();
                Licenses.Add(choice);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the tenant's domains and licenses: {Friendly.Message(e)}";
        }
        Changed();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; private set; }

    public bool HasLoadError => LoadError is not null;

    // ---- Name and sign-in -------------------------------------------------------------------

    [ObservableProperty]
    public partial string FirstName { get; set; } = "";

    [ObservableProperty]
    public partial string LastName { get; set; } = "";

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string Alias { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> Domains { get; private set; } = [];

    [ObservableProperty]
    public partial string? Domain { get; set; }

    bool _filling;

    partial void OnFirstNameChanged(string value) => NameTyped();
    partial void OnLastNameChanged(string value) => NameTyped();

    /// <summary>Display name and sign-in name follow first and last name until edited by hand.</summary>
    void NameTyped()
    {
        _filling = true;
        if (!_displayNameEdited)
            DisplayName = $"{FirstName.Trim()} {LastName.Trim()}".Trim();
        if (!_aliasEdited)
            Alias = AliasFor(FirstName, LastName);
        _filling = false;
        Changed();
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (!_filling)
            _displayNameEdited = value.Length > 0;
        Changed();
    }

    partial void OnAliasChanged(string value)
    {
        if (!_filling)
            _aliasEdited = value.Length > 0;
        Changed();
    }

    partial void OnDomainChanged(string? value) => Changed();

    /// <summary>"Zoë O'Brien" → "zoe.obrien": letters and digits only, accents taken off.</summary>
    public static string AliasFor(string first, string last)
    {
        static string Clean(string s)
        {
            var plain = new StringBuilder();
            foreach (var c in s.Normalize(NormalizationForm.FormD))
                if (char.IsAsciiLetterOrDigit(c))
                    plain.Append(char.ToLowerInvariant(c));
            return plain.ToString();
        }
        return string.Join('.', new[] { Clean(first), Clean(last) }.Where(p => p.Length > 0));
    }

    public string SignInName => Domain is null ? Alias.Trim() : $"{Alias.Trim()}@{Domain}";

    // ---- Password -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Generate))]
    public partial bool TypeOwn { get; set; }

    public bool Generate
    {
        get => !TypeOwn;
        set => TypeOwn = !value;
    }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowPassword { get; set; }

    [ObservableProperty]
    public partial bool MustChange { get; set; } = true;

    [ObservableProperty]
    public partial bool SignInAllowed { get; set; } = true;

    partial void OnTypeOwnChanged(bool value) => Changed();
    partial void OnPasswordChanged(string value) => Changed();

    // ---- Licenses -----------------------------------------------------------------------------

    public ObservableCollection<NewLicenseChoice> Licenses { get; } = [];

    public IReadOnlyList<Choice<string>> Countries { get; }

    Choice<string> _usageLocation;

    /// <summary>Microsoft needs a country before it will license someone.</summary>
    public Choice<string> UsageLocation
    {
        get => _usageLocation;
        set => SetProperty(ref _usageLocation, value);
    }

    // ---- Groups and manager -------------------------------------------------------------------

    public PersonPicker CopyFrom { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopiedFrom), nameof(CopiedText))]
    public partial string? CopiedFrom { get; private set; }

    public bool HasCopiedFrom => CopiedFrom is not null;

    public string CopiedText => $"Copied from {CopiedFrom}: department, job title, office, manager, groups and licenses. Change anything below.";

    public PersonPicker ManagerPicker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManager))]
    public partial string? ManagerId { get; private set; }

    [ObservableProperty]
    public partial string? ManagerName { get; private set; }

    public bool HasManager => ManagerId is not null;

    [RelayCommand]
    void ClearManager()
    {
        ManagerId = null;
        ManagerName = null;
    }

    public PersonPicker GroupPicker { get; }

    public ObservableCollection<ChosenGroup> Groups { get; } = [];

    public bool HasGroups => Groups.Count > 0;

    void AddGroup(GroupSummary group)
    {
        if (Groups.Any(g => g.Group.Id == group.Id))
            return;
        Groups.Add(new ChosenGroup(group));
        OnPropertyChanged(nameof(HasGroups));
    }

    [RelayCommand]
    void RemoveGroup(ChosenGroup group)
    {
        Groups.Remove(group);
        OnPropertyChanged(nameof(HasGroups));
    }

    [ObservableProperty]
    public partial bool ShowGroups { get; set; }

    [ObservableProperty]
    public partial bool ShowJob { get; set; }

    [RelayCommand]
    void ToggleGroups() => ShowGroups = !ShowGroups;

    [RelayCommand]
    void ToggleJob() => ShowJob = !ShowJob;

    // ---- Job and contact ------------------------------------------------------------------------

    [ObservableProperty]
    public partial string Department { get; set; } = "";

    [ObservableProperty]
    public partial string JobTitle { get; set; } = "";

    [ObservableProperty]
    public partial string Office { get; set; } = "";

    [ObservableProperty]
    public partial string MobilePhone { get; set; } = "";

    [ObservableProperty]
    public partial string EmployeeId { get; set; } = "";

    /// <summary>
    /// Fills what a colleague has: department, title, office (their record), manager, the groups
    /// they're a direct member of (not dynamic ones), and their licenses where any are left.
    /// </summary>
    async Task CopyAsync(string userId)
    {
        if (_index.User(userId) is not { } person)
            return;
        CopiedFrom = person.DisplayName;
        Department = person.Department ?? "";
        JobTitle = person.JobTitle ?? "";
        foreach (var group in _index.AllGroups.Where(CanJoin).Where(g => g.Members.Any(m => m.Id == userId)).OrderBy(g => g.DisplayName, StringComparer.CurrentCulture))
            AddGroup(group);
        foreach (var license in Licenses)
            license.IsChosen = license.IsAvailable && person.Licenses.Contains(license.Name);
        ShowGroups = true;
        if (_records is null)
            return;
        try
        {
            var details = await _records.GetDetailsAsync(userId);
            Office = details["officeLocation"] ?? Office;
            if (details.ManagerId is { } managerId)
            {
                ManagerId = managerId;
                ManagerName = details.ManagerName ?? _index.User(managerId)?.DisplayName;
            }
            if (details["usageLocation"] is { } country && Countries.FirstOrDefault(c => c.Value == country) is { } match)
                UsageLocation = match;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The rest was copied; their manager and office just aren't.
        }
    }

    // ---- Checks and adding ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>Only shown once they've started: an empty form isn't a list of mistakes.</summary>
    public bool ShowProblems => HasProblems && (FirstName.Length > 0 || LastName.Length > 0 || DisplayName.Length > 0);

    void Changed()
    {
        var problems = new List<string>();
        if (DisplayName.Trim().Length == 0)
            problems.Add("Give them a name.");
        if (Alias.Trim().Length == 0)
            problems.Add("Give them a sign-in name.");
        else if (Alias.Trim().Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || Alias.StartsWith('.') || Alias.EndsWith('.'))
            problems.Add("A sign-in name can use letters, digits, dots, dashes and underscores, and can't start or end with a dot.");
        else if (Domain is null)
            problems.Add("Choose a domain for the sign-in name.");
        else if (_index.AllUsers.Any(u => string.Equals(u.UserPrincipalName, SignInName, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{SignInName} is already someone's sign-in name.");
        if (TypeOwn && TemporaryPassword.Problem(Password) is { } weak)
            problems.Add(Password.Length == 0 ? "Type a password, or let Tenantry make one." : weak);
        foreach (var full in Licenses.Where(l => l.IsChosen && !l.IsAvailable))
            problems.Add($"No {full.Name} licenses are left.");
        Problems = problems;
        OnPropertyChanged(nameof(ShowProblems));
        OnPropertyChanged(nameof(SignInName));
    }

    bool CanAdd => Problems.Count == 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    void Add()
    {
        Changed();
        if (Problems.Count > 0)
            return;
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["department"] = Department,
            ["jobTitle"] = JobTitle,
            ["officeLocation"] = Office,
            ["mobilePhone"] = MobilePhone,
            ["employeeId"] = EmployeeId,
        }.Where(d => !string.IsNullOrWhiteSpace(d.Value)).ToDictionary(d => d.Key, d => d.Value.Trim(), StringComparer.Ordinal);
        _pipeline.Stage(new CreateUser(
            new ChangeTarget("user", SignInName, DisplayName.Trim(), SignInName),
            FirstName.Trim(), LastName.Trim(), Alias.Trim(), UsageLocation.Value, SignInAllowed, MustChange,
            Licenses.Where(l => l.IsChosen).Select(l => new NewUserLicense(l.Sku.SkuId, l.Name)).ToArray(),
            Groups.Select(g => new NewUserGroup(new ChangeTarget("group", g.Group.Id, g.Group.DisplayName, g.Group.Mail), g.Group.IsExchangeManaged)).ToArray(),
            ManagerId, ManagerName, details, TypeOwn ? Password : null));
        Password = "";
        _close();
    }

    [RelayCommand]
    void Cancel()
    {
        Password = "";
        _close();
    }
}
