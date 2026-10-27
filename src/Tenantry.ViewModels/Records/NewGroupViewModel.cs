using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>A kind of group New Group can make, with what it's for.</summary>
public sealed record NewGroupKind(GroupKind Kind, string Title, string Description, string Icon);

/// <summary>Someone added to the new group (an owner, member, or person with access), shown as a chip.</summary>
public sealed record ChosenPerson(UserSummary Person, bool IsOwner)
{
    public string Name => Person.DisplayName;
}

/// <summary>
/// New Group, in one popup: what kind (Microsoft 365 group, security group, distribution list,
/// shared mailbox), its name and address, then its people. Security groups can be dynamic (members
/// by rule). Adding puts one change in Pending Changes.
/// </summary>
public sealed partial class NewGroupViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly IUserRecords? _records;
    readonly IDirectoryIndex _index;
    readonly Action _close;
    bool _aliasEdited;
    bool _filling;

    public static IReadOnlyList<NewGroupKind> Kinds { get; } =
    [
        new(GroupKind.Microsoft365, "Microsoft 365 Group", "A team space: a shared inbox, calendar and files.", "PeopleTeam"),
        new(GroupKind.Security, "Security Group", "For giving access to apps, files and devices, and for licensing. No email.", "ShieldPerson"),
        new(GroupKind.Distribution, "Distribution List", "An email address that sends to everyone on the list.", "MailMultiple"),
        new(GroupKind.SharedMailbox, "Shared Mailbox", "One inbox several people read and send from, like info@ or support@.", "MailInbox"),
    ];

    public NewGroupViewModel(ChangePipeline pipeline, IUserRecords? records, IDirectoryIndex index, Action close)
    {
        _pipeline = pipeline;
        _records = records;
        _index = index;
        _close = close;
        _kind = Kinds[0];
        OwnerPicker = new PersonPicker("Add an owner…", () => Candidates(), p => AddPerson(p.Id, owner: true));
        MemberPicker = new PersonPicker("Add someone…", () => Candidates(), p => AddPerson(p.Id, owner: false));
        _ = LoadAsync();
    }

    IEnumerable<PickOption> Candidates() =>
        _index.AllUsers.Where(u => u.Kind == UserKind.Member && u.Mailbox?.Type is not ("Shared" or "Room" or "Equipment"))
            .Where(u => !IsDistribution || Mailable(u))
            .Where(u => People.All(p => p.Person.Id != u.Id)).Select(PickOption.Of);

    /// <summary>Exchange only takes people it can mail as a distribution list's members and owners.</summary>
    static bool Mailable(UserSummary u) => !string.IsNullOrEmpty(u.Mail) || u.Mailbox is not null;

    async Task LoadAsync()
    {
        if (_records is null)
            return;
        try
        {
            Domains = await _records.GetDomainsAsync();
            Domain ??= Domains.FirstOrDefault();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the tenant's domains: {Friendly.Message(e)}";
        }
        Changed();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; private set; }

    public bool HasLoadError => LoadError is not null;

    // ---- Kind -----------------------------------------------------------------------------------

    NewGroupKind _kind;

    public NewGroupKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value))
                return;
            foreach (var name in new[] { nameof(IsMicrosoft365), nameof(IsSecurity), nameof(IsDistribution), nameof(IsShared), nameof(HasOwners), nameof(HasDomainChoice),
                         nameof(HasEmail), nameof(MembersTitle), nameof(ShowMembers), nameof(AddressNote) })
                OnPropertyChanged(name);
            Changed();
        }
    }

    public bool IsMicrosoft365 => Kind.Kind == GroupKind.Microsoft365;
    public bool IsSecurity => Kind.Kind == GroupKind.Security;
    public bool IsDistribution => Kind.Kind == GroupKind.Distribution;
    public bool IsShared => Kind.Kind == GroupKind.SharedMailbox;

    /// <summary>Shared mailboxes have people with access, not owners.</summary>
    public bool HasOwners => !IsShared;

    /// <summary>Exchange takes the address it's given; a Microsoft 365 group gets the tenant's default domain.</summary>
    public bool HasDomainChoice => IsDistribution || IsShared;

    public bool HasEmail => !IsSecurity;

    public string AddressNote => IsMicrosoft365 ? $"Its email address will be {Alias}@ your default domain." : "";

    // ---- Name and address -------------------------------------------------------------------------

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Alias { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> Domains { get; private set; } = [];

    [ObservableProperty]
    public partial string? Domain { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    partial void OnNameChanged(string value)
    {
        if (!_aliasEdited)
        {
            _filling = true;
            Alias = AliasFor(value);
            _filling = false;
        }
        Changed();
    }

    partial void OnAliasChanged(string value)
    {
        if (!_filling)
            _aliasEdited = value.Length > 0;
        OnPropertyChanged(nameof(AddressNote));
        Changed();
    }

    partial void OnDomainChanged(string? value) => Changed();

    /// <summary>"Finance & Accounts" → "finance-accounts".</summary>
    public static string AliasFor(string name)
    {
        var alias = new StringBuilder();
        foreach (var c in name.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
                alias.Append(c);
            else if (alias.Length > 0 && alias[^1] != '-' && (char.IsWhiteSpace(c) || c is '-' or '_' or '&' or '/' or '.'))
                alias.Append('-');
        }
        return alias.ToString().Trim('-');
    }

    public string? Address => HasDomainChoice && Domain is not null ? $"{Alias.Trim()}@{Domain}" : null;

    // ---- Settings by kind -------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsPrivate { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMembers))]
    public partial bool IsDynamic { get; set; }

    [ObservableProperty]
    public partial string MembershipRule { get; set; } = "";

    /// <summary>Also usable to grant access (a mail-enabled security group).</summary>
    [ObservableProperty]
    public partial bool AlsoSecurity { get; set; }

    [ObservableProperty]
    public partial bool OutsideSenders { get; set; }

    partial void OnIsDynamicChanged(bool value) => Changed();
    partial void OnMembershipRuleChanged(string value) => Changed();

    // ---- People -----------------------------------------------------------------------------------

    public PersonPicker OwnerPicker { get; }

    public PersonPicker MemberPicker { get; }

    public ObservableCollection<ChosenPerson> People { get; } = [];

    public IEnumerable<ChosenPerson> Owners => People.Where(p => p.IsOwner);

    public IEnumerable<ChosenPerson> Members => People.Where(p => !p.IsOwner);

    public string MembersTitle => IsShared ? "PEOPLE WHO CAN READ AND SEND FROM IT" : "MEMBERS";

    public bool ShowMembers => !(IsSecurity && IsDynamic);

    void AddPerson(string id, bool owner)
    {
        if (_index.User(id) is not { } person)
            return;
        People.Add(new ChosenPerson(person, owner));
        PeopleChanged();
    }

    [RelayCommand]
    void RemovePerson(ChosenPerson person)
    {
        People.Remove(person);
        PeopleChanged();
    }

    void PeopleChanged()
    {
        OnPropertyChanged(nameof(Owners));
        OnPropertyChanged(nameof(Members));
        Changed();
    }

    // ---- Checks and adding --------------------------------------------------------------------------

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
        else if (_index.AllGroups.Any(g => string.Equals(g.DisplayName, Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            problems.Add($"There's already a group called \"{Name.Trim()}\".");
        var alias = Alias.Trim();
        if (alias.Length == 0)
            problems.Add("Give it an alias (the part of the address before the @).");
        else if (alias.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) || alias.StartsWith('.') || alias.EndsWith('.'))
            problems.Add("An alias can use letters, digits, dots, dashes and underscores, and can't start or end with a dot.");
        else if (Address is { } address && (_index.AllGroups.Any(g => string.Equals(g.Mail, address, StringComparison.OrdinalIgnoreCase))
                                            || _index.AllUsers.Any(u => string.Equals(u.Mail, address, StringComparison.OrdinalIgnoreCase)
                                                                        || string.Equals(u.UserPrincipalName, address, StringComparison.OrdinalIgnoreCase))))
            problems.Add($"{address} is already in use.");
        else if (HasDomainChoice && Domain is null)
            problems.Add("Choose a domain for its address.");
        if (IsDistribution)
            foreach (var person in People.Where(p => !Mailable(p.Person)))
                problems.Add($"{person.Name} has no email address, so Exchange can't add them to a distribution list.");
        if (IsSecurity && IsDynamic && MembershipRule.Trim().Length == 0)
            problems.Add("Write the rule for who belongs, like (user.department -eq \"Finance\").");
        Problems = problems;
        OnPropertyChanged(nameof(ShowProblems));
        OnPropertyChanged(nameof(Address));
    }

    bool CanAdd => Problems.Count == 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    void Add()
    {
        Changed();
        if (Problems.Count > 0)
            return;
        var kind = IsDistribution && AlsoSecurity ? GroupKind.MailEnabledSecurity : Kind.Kind;
        static ChangeTarget Person(ChosenPerson p) => new("user", p.Person.Id, p.Person.DisplayName, p.Person.UserPrincipalName);
        _pipeline.Stage(new CreateGroup(
            new ChangeTarget("group", Alias.Trim(), Name.Trim(), Address),
            kind, Alias.Trim(), Description.Trim() is { Length: > 0 } d ? d : null,
            IsSecurity && IsDynamic ? MembershipRule.Trim() : null,
            IsPrivate, OutsideSenders,
            HasOwners ? Owners.Select(Person).ToArray() : [],
            ShowMembers ? Members.Select(Person).ToArray() : []));
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}
