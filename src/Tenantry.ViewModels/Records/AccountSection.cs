using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>A titled group of fields on the Account tab ("Name", "Work", "Contact").</summary>
public sealed record FieldGroup(string Title, string Icon, IReadOnlyList<EditableField> Fields);

/// <summary>
/// The Account tab: who the person is, how to reach them, where they work, how they sign in.
/// Read live when first shown (the list's local copy holds only list fields); every value is
/// editable in place through the Pending changes tray.
/// </summary>
public sealed partial class AccountSection : RecordSection, IDisposable
{
    UserSummary _user;
    readonly IUserRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    bool _loaded;

    public AccountSection(UserSummary user, IUserRecords? records, ChangePipeline? pipeline, Availability canEdit)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        Summary = user.Department is { Length: > 0 } d ? $"{user.JobTitle ?? "No title"} · {d}" : user.JobTitle ?? "Name, contact and work details";
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public override string Key => "account";
    public override string Title => "Account";
    public override string Icon => "Person";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    public bool IsReady => !IsLoading && Groups.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial IReadOnlyList<FieldGroup> Groups { get; private set; } = [];

    [ObservableProperty]
    public partial string? ManagerText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuest))]
    public partial string? GuestStatus { get; private set; }

    public bool IsGuest => GuestStatus is not null;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded || IsLoading)
            return;
        if (_records is null)
        {
            Build(new UserDetails(_user.Id, new Dictionary<string, string?>
            {
                ["displayName"] = _user.DisplayName,
                ["userPrincipalName"] = _user.UserPrincipalName,
                ["jobTitle"] = _user.JobTitle,
                ["department"] = _user.Department,
            }, null, null, null, null), []);
            _loaded = true;
            return;
        }
        IsLoading = true;
        LoadError = null;
        try
        {
            var details = await _records.GetDetailsAsync(_user.Id, cancellationToken);
            var domains = await _records.GetDomainsAsync(cancellationToken);
            Build(details, domains);
            _loaded = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the account details: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    void Build(UserDetails d, IReadOnlyList<string> domains)
    {
        var target = new ChangeTarget("user", _user.Id, _user.DisplayName, _user.UserPrincipalName);
        EditableField Field(string label, string property, bool required = false, IReadOnlyList<FieldChoice>? choices = null,
            Func<string, string?>? validate = null, string? hint = null) =>
            new(label, property, d[property], target, _pipeline, _canEdit, required, choices: choices, validate: validate, hint: hint);

        var countries = Countries.All.Select(c => new FieldChoice(c.Code, $"{c.Name} ({c.Code})")).ToArray();
        Groups =
        [
            new("Name", "Person",
            [
                Field("First name", "givenName"),
                Field("Last name", "surname"),
                Field("Display name", "displayName", required: true, hint: "How the name appears in Outlook, Teams and the address list."),
            ]),
            new("Work", "Briefcase",
            [
                Field("Job title", "jobTitle"),
                Field("Department", "department"),
                Field("Company", "companyName"),
                Field("Employee ID", "employeeId"),
                Field("Office", "officeLocation"),
            ]),
            new("Contact", "Call",
            [
                Field("Work phone", "businessPhones"),
                Field("Mobile phone", "mobilePhone"),
            ]),
            new("Address", "Location",
            [
                Field("Street", "streetAddress"),
                Field("City", "city"),
                Field("State or province", "state"),
                Field("Postal code", "postalCode"),
                Field("Country or region", "country"),
            ]),
            new("Sign-in and licensing", "Key",
            [
                Field("Sign-in name", "userPrincipalName", required: true, validate: upn => ValidateSignInName(upn, domains),
                    hint: "Changing it changes how they sign in everywhere; their old name stops working."),
                Field("Usage location", "usageLocation", choices: countries,
                    hint: "The country they use Microsoft 365 in. Required before licenses can be assigned."),
            ]),
        ];
        ManagerText = d.ManagerName is { } m ? m : "No manager set";
        GuestStatus = d.GuestInvitationState switch
        {
            "PendingAcceptance" => $"Invitation not accepted yet{(d.GuestInvitationChanged is { } at ? $" (sent {Display.Relative(at, DateTimeOffset.Now)})" : "")}",
            "Accepted" => "Invitation accepted",
            null => null,
            var other => other,
        };
    }

    public override void RecordChanged(UserSummary user)
    {
        _user = user;
        if (!_loaded)
            return;
        _loaded = false;
        _ = LoadAsync();
    }

    /// <summary>A sign-in name must be name@domain, on one of the tenant's own domains.</summary>
    public static string? ValidateSignInName(string upn, IReadOnlyList<string> domains)
    {
        var at = upn.IndexOf('@');
        if (at <= 0 || at != upn.LastIndexOf('@') || at == upn.Length - 1 || upn.Contains(' '))
            return "A sign-in name looks like name@domain, with no spaces.";
        var domain = upn[(at + 1)..];
        if (domains.Count > 0 && !domains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            return $"\"{domain}\" isn't one of this tenant's domains ({string.Join(", ", domains)}).";
        return null;
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var field in Groups.SelectMany(g => g.Fields))
            field.Refresh();
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
