using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>
/// A mail contact's record, as a page or in the preview pane: its details (edited in place), whether
/// it shows in the address list, the lists it's on (joined and left here), and Delete. Every change
/// goes to Pending Changes first. A contact synced from on-premises AD is changed there, except
/// for the cloud lists it's on.
/// </summary>
public sealed partial class ContactDetailViewModel : TabViewModel, IDisposable
{
    public const string SyncedReason = "Synced from on-premises Active Directory: change it there, and Entra Connect copies it up.";

    const int PreviewLists = 8;

    readonly IDirectoryIndex _index;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly Action<GroupSummary> _openGroup;
    readonly Action<ContactSummary>? _openPage;

    public ContactDetailViewModel(ContactSummary contact, IDirectoryIndex index, ChangePipeline? pipeline, Availability canEdit,
        Action<GroupSummary>? openGroup = null, Action<ContactSummary>? openPage = null)
    {
        Contact = contact;
        _index = index;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _openGroup = openGroup ?? (_ => { });
        _openPage = openPage;
        ListPicker = new PersonPicker("List or group name", ListOptions, o =>
        {
            if (CanEditLists && _index.Group(o.Id) is { } g)
                _pipeline!.Stage(Membership(g, false, true));
        });
        Build();
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public ContactSummary Contact { get; private set; }

    public override string Title => Contact.DisplayName;

    public string Initials => Display.Initials(Contact.DisplayName);

    public string Email => Contact.Email ?? "No email address";

    /// <summary>"Account Manager · Fabrikam", or what's known of it.</summary>
    public string? WorkText => string.Join(" · ", new[] { Contact.JobTitle, Contact.Company }.Where(s => !string.IsNullOrEmpty(s))) is { Length: > 0 } t ? t : null;

    public bool HasWorkText => WorkText is not null;

    public bool IsHidden => Contact.Hidden == true;

    public bool IsSynced => Contact.SyncedFromOnPremises;

    ChangeTarget Target => GroupAccess.Target(Contact);

    // ---- Editing ---------------------------------------------------------------------------

    /// <summary>The contact's own details: not for one synced from AD.</summary>
    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable && !IsSynced;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : IsSynced ? SyncedReason : _canEdit.Reason ?? "";

    /// <summary>Lists it's on are cloud groups, so even a synced contact can join or leave them.</summary>
    public bool CanEditLists => _pipeline is not null && _canEdit.IsAvailable;

    public string ListsReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    [ObservableProperty]
    public partial IReadOnlyList<FieldGroup> Details { get; private set; } = [];

    void Build()
    {
        var target = Target;
        var editable = CanEdit ? _canEdit : Availability.No(EditReason);
        EditableField Field(string label, string parameter, string? current, bool required = false, Func<string, string?>? validate = null, string? hint = null) =>
            new(label, parameter, current, target, _pipeline, editable, required, validate: validate, hint: hint,
                change: (old, next) => new SetContactText(target, parameter, label, old, next), key: "contact:" + parameter);
        Details =
        [
            new("Name", "Person",
            [
                Field("Display Name", "DisplayName", Contact.DisplayName, required: true, hint: "How the contact appears in Outlook and the address list."),
                Field("First Name", "FirstName", Contact.FirstName),
                Field("Last Name", "LastName", Contact.LastName),
            ]),
            new("Email", "Mail",
            [
                Field("Email Address", SetContactText.Address, Contact.Email, required: true, validate: ValidateAddress,
                    hint: "Where mail sent to this contact goes. Lists it's on send there too."),
            ]),
            new("Work", "Briefcase",
            [
                Field("Company", "Company", Contact.Company),
                Field("Department", "Department", Contact.Department),
                Field("Job Title", "Title", Contact.JobTitle),
            ]),
            new("Phone", "Call",
            [
                Field("Work Phone", "Phone", Contact.Phone),
                Field("Mobile Phone", "MobilePhone", Contact.Mobile),
            ]),
        ];
        RebuildLists();
    }

    /// <summary>An outside address that's well formed and not already someone else's here.</summary>
    string? ValidateAddress(string address) =>
        NewContactViewModel.AddressProblem(address)
        ?? (_index.AllContacts.Any(c => c.Id != Contact.Id && string.Equals(c.Email, address, StringComparison.OrdinalIgnoreCase))
            ? $"Another contact already has {address}."
            : null);

    // ---- Address list ----------------------------------------------------------------------

    SetHiddenFromAddressList? PendingHidden => _pipeline?.PendingFor(Contact.Id, "gal") as SetHiddenFromAddressList;

    /// <summary>Shown in the address list, as it will be once pending changes apply.</summary>
    public bool ShowsInAddressList
    {
        get => !(PendingHidden?.New ?? IsHidden);
        set
        {
            if (!CanEdit || value == ShowsInAddressList)
                return;
            if (PendingHidden is { } p)
                _pipeline!.Unstage(p);
            else
                _pipeline!.Stage(new SetHiddenFromAddressList(Target, IsHidden, !value));
        }
    }

    public bool HasPendingHidden => PendingHidden is not null;

    /// <summary>The switch's state in words, as it will be once pending changes apply.</summary>
    public string AddressListState => ShowsInAddressList ? "Shown" : "Hidden";

    public string AddressListText => Contact.Hidden is null && !HasPendingHidden ? "Not read yet: Exchange is read with the mailboxes."
        : ShowsInAddressList ? "People find the contact in Outlook's address book."
        : "People can still type the address, but won't find the contact in Outlook's address book.";

    public string AddressListTip => CanEdit ? "Show or hide the contact in Outlook's address book (added to Pending Changes)" : EditReason;

    // ---- Lists -----------------------------------------------------------------------------

    public PersonPicker ListPicker { get; }

    public ObservableCollection<PersonRow> Lists { get; } = [];

    public bool HasNoLists => Lists.Count == 0;

    public string ListsHeading => $"Lists and Groups ({Contact.Lists.Count:N0})";

    /// <summary>"On Partner Updates and 2 others" for the preview.</summary>
    public string ListsText
    {
        get
        {
            var names = Contact.Lists.Select(id => _index.Group(id)?.DisplayName).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            return names.Length switch
            {
                0 => "Not on any list",
                1 => $"On {names[0]}",
                2 => $"On {names[0]} and {names[1]}",
                _ => $"On {names[0]} and {names.Length - 1:N0} others",
            };
        }
    }

    public IReadOnlyList<MemberRow> FirstLists => ListRows().Take(PreviewLists).Select(r => r.Row).ToArray();

    public string MoreListsText => Contact.Lists.Count > PreviewLists ? $"and {Contact.Lists.Count - PreviewLists:N0} more" : "";

    SetGroupMembership Membership(GroupSummary g, bool old, bool @new) =>
        new(GroupAccess.Target(g), Target, "orgContact", GroupRole.Member, g.IsExchangeManaged, old, @new);

    static MemberRow RowOf(GroupSummary g) =>
        new(g.DisplayName, g.Mail ?? GroupSummary.KindName(g.Kind), GroupList.TypeBadge(g.Kind).Text, GroupList.TypeBadge(g.Kind).Kind, null, g);

    IEnumerable<(MemberRow Row, GroupSummary Group)> ListRows() => Contact.Lists
        .Select(id => _index.Group(id))
        .OfType<GroupSummary>()
        .Select(g => (RowOf(g), g))
        .OrderBy(r => r.Item1.Name, StringComparer.CurrentCultureIgnoreCase);

    void RebuildLists()
    {
        Lists.Clear();
        var on = Contact.Lists.ToHashSet(StringComparer.Ordinal);
        foreach (var (row, g) in ListRows())
        {
            var pending = _pipeline?.PendingFor(g.Id, $"{GroupRole.Member}:{Contact.Id}");
            var locked = pending is null ? GroupAccess.LockedReason(g, GroupRole.Member) : null;
            var tip = !CanEditLists ? ListsReason : locked ?? (pending is not null ? "Undo: keep it on this list" : "Take it off this list (added to Pending Changes)");
            Lists.Add(new PersonRow(row, pending is not null ? "Removing" : null, CanEditLists && locked is null, tip,
                () => Toggle(pending, () => Membership(g, true, false)), () => _openGroup(g), showRemove: pending is not null || locked is null));
        }
        foreach (var add in _pipeline?.Pending.OfType<SetGroupMembership>().Where(p => p.Person.Id == Contact.Id && p.New && !on.Contains(p.Group.Id)) ?? [])
            if (_index.Group(add.Group.Id) is { } g)
                Lists.Add(new PersonRow(RowOf(g), "Adding", true, "Undo: don't add it", () => _pipeline!.Unstage(add), () => _openGroup(g)));
        OnPropertyChanged(nameof(HasNoLists));
    }

    void Toggle(ChangeOp? pending, Func<ChangeOp> change)
    {
        if (!CanEditLists)
            return;
        if (pending is not null)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(change());
    }

    /// <summary>Lists and groups that take contacts, it isn't on (or being added to), whose members can be changed here.</summary>
    IEnumerable<PickOption> ListOptions()
    {
        var taken = Contact.Lists
            .Concat(_pipeline?.Pending.OfType<SetGroupMembership>().Where(p => p.Person.Id == Contact.Id && p.New).Select(p => p.Group.Id) ?? [])
            .ToHashSet(StringComparer.Ordinal);
        return _index.AllGroups
            .Where(g => GroupAccess.TakesContacts(g) && GroupAccess.MembersEditable(g) && !taken.Contains(g.Id))
            .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(PickOption.Of);
    }

    // ---- Delete ----------------------------------------------------------------------------

    DeleteContact? PendingDelete => _pipeline?.PendingFor(Contact.Id, "delete") as DeleteContact;

    public bool HasPendingDelete => PendingDelete is not null;

    public string DeleteTip => CanEdit ? "Delete the contact: it leaves every list it's on, and can't be restored (added to Pending Changes)" : EditReason;

    [RelayCommand]
    void Delete()
    {
        if (!CanEdit)
            return;
        if (PendingDelete is { } p)
            _pipeline!.Unstage(p);
        else
            _pipeline!.Stage(new DeleteContact(Target, string.Join(" · ", new[] { Contact.Email, ListsText }.Where(s => !string.IsNullOrEmpty(s)))));
    }

    // ---- Opening ---------------------------------------------------------------------------

    public bool CanOpenPage => _openPage is not null;

    [RelayCommand]
    void OpenPage() => _openPage?.Invoke(Contact);

    [RelayCommand]
    void OpenList(MemberRow row)
    {
        if (row.Group is { } g)
            _openGroup(g);
    }

    /// <summary>Shows the contact as it is now (after changes are applied and read back).</summary>
    public void Update(ContactSummary contact)
    {
        Contact = contact;
        Build();
        Refresh();
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var field in Details.SelectMany(g => g.Fields))
            field.Refresh();
        RebuildLists();
        Refresh();
    }

    void Refresh()
    {
        foreach (var name in new[]
                 {
                     nameof(Contact), nameof(Title), nameof(Initials), nameof(Email), nameof(WorkText), nameof(HasWorkText), nameof(IsHidden), nameof(IsSynced),
                     nameof(CanEdit), nameof(EditReason), nameof(ShowsInAddressList), nameof(HasPendingHidden), nameof(AddressListText),
                     nameof(AddressListState), nameof(AddressListTip),
                     nameof(ListsHeading), nameof(ListsText), nameof(FirstLists), nameof(MoreListsText), nameof(HasPendingDelete), nameof(DeleteTip),
                 })
            OnPropertyChanged(name);
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
