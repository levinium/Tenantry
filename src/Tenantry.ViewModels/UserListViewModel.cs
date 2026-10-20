using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

public sealed partial class UserListViewModel : TabViewModel
{
    readonly ITenantDirectory _directory;
    readonly Action<UserSummary> _openUser;

    // Each user's searchable text, lowercased once at load so a keystroke costs one pass of
    // substring checks rather than re-joining and re-casing every field every time.
    (UserSummary User, string Haystack)[] _index = [];

    public UserListViewModel(ITenantDirectory directory, Action<UserSummary> openUser, Availability? createUsers = null)
    {
        _directory = directory;
        _openUser = openUser;
        CreateUsers = createUsers ?? Availability.Yes;
        FilterText = "";
        VisibleUsers = [];
    }

    public override string Title => "Users";
    public override bool CanClose => false;

    /// <summary>Whether this admin may create users; the button shows the reason when not.</summary>
    public Availability CreateUsers { get; }

    public string CreateUsersTip => CreateUsers.IsAvailable ? "Creating users arrives with the change pipeline (Phase 6)" : CreateUsers.Reason!;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string FilterText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial IReadOnlyList<UserSummary> VisibleUsers { get; private set; }

    [ObservableProperty]
    public partial UserSummary? SelectedUser { get; set; }

    public int TotalCount => _index.Length;

    public string CountText => VisibleUsers.Count == TotalCount
        ? $"{TotalCount:N0} users"
        : $"{VisibleUsers.Count:N0} of {TotalCount:N0} users";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            var users = await _directory.GetUsersAsync(cancellationToken);
            _index = users.Select(u => (u, BuildHaystack(u))).ToArray();
            OnPropertyChanged(nameof(TotalCount));
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    void OpenSelected()
    {
        if (SelectedUser is { } user)
            _openUser(user);
    }

    void ApplyFilter()
    {
        // Every whitespace-separated word must appear somewhere in the row, in any order, so
        // "finance guest" or "smith disabled" narrow the way people expect a search to.
        var tokens = FilterText.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        VisibleUsers = tokens.Length == 0
            ? _index.Select(e => e.User).ToArray()
            : _index.Where(e => tokens.All(t => e.Haystack.Contains(t, StringComparison.Ordinal)))
                    .Select(e => e.User)
                    .ToArray();
    }

    static string BuildHaystack(UserSummary u) => string.Join('\n',
        u.DisplayName,
        u.UserPrincipalName,
        u.Mail ?? "",
        u.Department ?? "",
        u.JobTitle ?? "",
        u.Kind == UserKind.Guest ? "guest" : "member",
        u.AccountEnabled ? "enabled" : "disabled",
        string.Join('\n', u.Licenses)).ToLowerInvariant();
}
