using System.Collections.ObjectModel;
using System.Net.Mail;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// New Contact, in one popup: an outside address with a name, optionally its company, whether it
/// shows in the address list, and the lists it joins. Adding puts one change in Pending Changes.
/// Opened from the Contacts list, or from a list's Add Member with the address already typed.
/// </summary>
public sealed partial class NewContactViewModel : ObservableObject
{
    readonly ChangePipeline _pipeline;
    readonly IDirectoryIndex _index;
    readonly Action _close;
    bool _nameEdited;
    bool _filling;

    /// <param name="email">An address already typed (Add Member's "new contact").</param>
    /// <param name="list">A list to put it on straight away (the list it was added from).</param>
    public NewContactViewModel(ChangePipeline pipeline, IDirectoryIndex index, Action close, string? email = null, GroupSummary? list = null)
    {
        _pipeline = pipeline;
        _index = index;
        _close = close;
        ListPicker = new PersonPicker("List or group name", ListOptions, o =>
        {
            if (_index.Group(o.Id) is { } g && Lists.All(l => l.Id != g.Id))
            {
                Lists.Add(g);
                Changed();
            }
        });
        if (list is not null)
            Lists.Add(list);
        Email = email?.Trim() ?? "";
        if (email is not null)
            Guess(email);
        Changed();
    }

    /// <summary>A name for the address ("avery.quinn@…" → Avery Quinn), as a start the admin can change.</summary>
    void Guess(string email)
    {
        var local = email.Split('@')[0];
        var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.All(char.IsLetter))
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant())
            .ToArray();
        if (parts.Length != 2)
            return;
        _filling = true;
        FirstName = parts[0];
        LastName = parts[1];
        _filling = false;
        Fill();
    }

    [ObservableProperty]
    public partial string FirstName { get; set; } = "";

    [ObservableProperty]
    public partial string LastName { get; set; } = "";

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "";

    [ObservableProperty]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    public partial string Company { get; set; } = "";

    /// <summary>Kept out of Outlook's address book (it can still be mailed and put on lists).</summary>
    [ObservableProperty]
    public partial bool Hidden { get; set; }

    partial void OnFirstNameChanged(string value) => Fill();

    partial void OnLastNameChanged(string value) => Fill();

    partial void OnDisplayNameChanged(string value)
    {
        if (!_filling)
            _nameEdited = value.Trim().Length > 0;
        Changed();
    }

    partial void OnEmailChanged(string value) => Changed();

    /// <summary>The display name follows the first and last names until it's typed over.</summary>
    void Fill()
    {
        if (_filling || _nameEdited)
        {
            Changed();
            return;
        }
        _filling = true;
        DisplayName = string.Join(' ', new[] { FirstName.Trim(), LastName.Trim() }.Where(s => s.Length > 0));
        _filling = false;
        Changed();
    }

    // ---- Lists -----------------------------------------------------------------------------

    public PersonPicker ListPicker { get; }

    public ObservableCollection<GroupSummary> Lists { get; } = [];

    IEnumerable<PickOption> ListOptions() => _index.AllGroups
        .Where(g => GroupAccess.TakesContacts(g) && GroupAccess.MembersEditable(g) && Lists.All(l => l.Id != g.Id))
        .OrderBy(g => g.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .Select(PickOption.Of);

    [RelayCommand]
    void RemoveList(GroupSummary list)
    {
        Lists.Remove(list);
        Changed();
    }

    // ---- Checks and adding -----------------------------------------------------------------

    /// <summary>What's wrong with an outside address, or null when it's a usable one.</summary>
    public static string? AddressProblem(string address)
    {
        var a = address.Trim();
        if (a.Length == 0)
            return "Type the address mail to the contact goes to.";
        var at = a.IndexOf('@');
        if (at <= 0 || at != a.LastIndexOf('@') || at == a.Length - 1 || a.Contains(' ') || !a[(at + 1)..].Contains('.')
            || !MailAddress.TryCreate(a, out var parsed) || parsed.Address != a)
            return "An email address looks like name@example.com, with no spaces.";
        return null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>Problems wait until something's been typed, so an empty form doesn't open with warnings.</summary>
    public bool ShowProblems => HasProblems && (DisplayName.Length > 0 || Email.Length > 0);

    void Changed()
    {
        var problems = new List<string>();
        var name = DisplayName.Trim();
        var email = Email.Trim();
        if (name.Length == 0)
            problems.Add("Give the contact a name.");
        else if (_index.AllContacts.Any(c => string.Equals(c.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                 || _index.AllGroups.Any(g => string.Equals(g.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"Something in the address list is already called \"{name}\". Exchange needs a name of its own: add the company, say.");
        if (AddressProblem(email) is { } bad)
            problems.Add(bad);
        else if (_index.AllContacts.FirstOrDefault(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase)) is { } existing)
            problems.Add($"{existing.DisplayName} is already a contact with {email}: add them to the list from its page instead.");
        else if (_index.AllUsers.Any(u => string.Equals(u.Mail, email, StringComparison.OrdinalIgnoreCase) || string.Equals(u.UserPrincipalName, email, StringComparison.OrdinalIgnoreCase))
                 || _index.AllGroups.Any(g => string.Equals(g.Mail, email, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{email} belongs to someone or something in this organization, so it can't be a contact.");
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
        var email = Email.Trim();
        _pipeline.Stage(new CreateContact(
            new ChangeTarget(ContactChange.Kind, email, DisplayName.Trim(), email),
            Empty(FirstName), Empty(LastName), Empty(Company), Hidden,
            Lists.Select(g => new NewUserGroup(GroupAccess.Target(g), g.IsExchangeManaged)).ToArray()));
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();

    static string? Empty(string value) => value.Trim() is { Length: > 0 } v ? v : null;
}
