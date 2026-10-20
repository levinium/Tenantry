using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels;

public enum UserQuickFilter
{
    All,
    Members,
    Guests,
    Blocked,
    NeverSignedIn,
}

/// <summary>A quick-filter chip: its label and how many users it would show.</summary>
public sealed partial class QuickFilterChip(UserQuickFilter filter, string label) : ObservableObject
{
    public UserQuickFilter Filter { get; } = filter;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed partial class UserListViewModel : TabViewModel
{
    readonly ITenantDirectory _directory;
    readonly Action<UserSummary> _openUser;
    readonly Func<DateTimeOffset> _now;

    // Each user's searchable text, lowercased once at load so a keystroke costs one pass of
    // substring checks rather than re-joining and re-casing every field every time.
    (UserRow Row, string Haystack)[] _index = [];

    public UserListViewModel(ITenantDirectory directory, Action<UserSummary> openUser, Availability? createUsers = null, Func<DateTimeOffset>? now = null)
    {
        _directory = directory;
        _openUser = openUser;
        _now = now ?? (() => DateTimeOffset.Now);
        CreateUsers = createUsers ?? Availability.Yes;
        FilterText = "";
        VisibleUsers = [];
        Chips =
        [
            new(UserQuickFilter.All, "All") { IsSelected = true },
            new(UserQuickFilter.Members, "Members"),
            new(UserQuickFilter.Guests, "Guests"),
            new(UserQuickFilter.Blocked, "Sign-in blocked"),
            new(UserQuickFilter.NeverSignedIn, "Never signed in"),
        ];
    }

    public override string Title => "Users";
    public override bool CanClose => false;

    /// <summary>Whether this admin may create users; the button shows the reason when not.</summary>
    public Availability CreateUsers { get; }

    public string CreateUsersTip => CreateUsers.IsAvailable ? "Creating users arrives with the change pipeline (Phase 6)" : CreateUsers.Reason!;

    public IReadOnlyList<QuickFilterChip> Chips { get; }

    [ObservableProperty]
    public partial UserQuickFilter QuickFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string FilterText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmpty))]
    public partial IReadOnlyList<UserRow> VisibleUsers { get; private set; }

    [ObservableProperty]
    public partial UserRow? SelectedUser { get; set; }

    public int TotalCount => _index.Length;

    public bool IsEmpty => !IsLoading && VisibleUsers.Count == 0;

    public string CountText => VisibleUsers.Count == TotalCount
        ? $"{TotalCount:N0} users"
        : $"{VisibleUsers.Count:N0} of {TotalCount:N0}";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            var users = await _directory.GetUsersAsync(cancellationToken);
            var now = _now();
            _index = users.Select(u => (new UserRow(u, now), BuildHaystack(u))).ToArray();
            foreach (var chip in Chips)
                chip.Count = _index.Count(e => Matches(e.Row.User, chip.Filter));
            OnPropertyChanged(nameof(TotalCount));
        }
        finally
        {
            IsLoading = false;
        }
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnQuickFilterChanged(UserQuickFilter value)
    {
        foreach (var chip in Chips)
            chip.IsSelected = chip.Filter == value;
        ApplyFilter();
    }

    [RelayCommand]
    void SelectChip(QuickFilterChip chip) => QuickFilter = chip.Filter;

    [RelayCommand]
    void OpenSelected()
    {
        if (SelectedUser is { } row)
            _openUser(row.User);
    }

    static bool Matches(UserSummary u, UserQuickFilter filter) => filter switch
    {
        UserQuickFilter.Members => u.Kind == UserKind.Member,
        UserQuickFilter.Guests => u.Kind == UserKind.Guest,
        UserQuickFilter.Blocked => !u.AccountEnabled,
        UserQuickFilter.NeverSignedIn => u.LastSignIn is null,
        _ => true,
    };

    void ApplyFilter()
    {
        // Every whitespace-separated word must appear somewhere in the row, in any order, so
        // "finance guest" or "smith disabled" narrow the way people expect a search to.
        var tokens = FilterText.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        VisibleUsers = _index
            .Where(e => Matches(e.Row.User, QuickFilter))
            .Where(e => tokens.All(t => e.Haystack.Contains(t, StringComparison.Ordinal)))
            .Select(e => e.Row)
            .ToArray();
    }

    static string BuildHaystack(UserSummary u) => string.Join('\n',
        u.DisplayName,
        u.UserPrincipalName,
        u.Mail ?? "",
        u.Department ?? "",
        u.JobTitle ?? "",
        u.Kind == UserKind.Guest ? "guest" : "member",
        u.AccountEnabled ? "enabled active" : "disabled blocked",
        string.Join('\n', u.Licenses)).ToLowerInvariant();
}
