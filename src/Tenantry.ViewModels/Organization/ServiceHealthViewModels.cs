using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Organization;

/// <summary>Service health and Message Center as lists (15a).</summary>
public static class ServiceHealthLists
{
    public static class I
    {
        public const string Title = "title";
        public const string Service = "service";
        public const string Status = "status";
        public const string Kind = "kind";
        public const string State = "state";
        public const string Feature = "feature";
        public const string Started = "started";
        public const string Updated = "updated";
        public const string Ended = "ended";

        public const string Active = "Active";
        public const string Resolved = "Resolved";
    }

    static string StateOf(ServiceIssue i) => i.IsResolved ? I.Resolved : I.Active;

    public static IReadOnlyList<Field<ServiceIssue>> IssueFields { get; } =
    [
        new() { Key = I.Title, Label = "Issue", Kind = FieldKind.Text, Searchable = true, Text = i => i.Title },
        new() { Key = I.Service, Label = "Service", Kind = FieldKind.Choice, Aliases = ["svc"], Text = i => i.Service },
        new() { Key = I.Status, Label = "Status", Kind = FieldKind.Choice, Text = i => i.StatusText },
        new() { Key = I.Kind, Label = "Kind", Kind = FieldKind.Choice, ChoiceOrder = ["Incident", "Advisory"], Text = i => i.KindText },
        new() { Key = I.State, Label = "State", Kind = FieldKind.Choice, ChoiceOrder = [I.Active, I.Resolved], Text = StateOf },
        new() { Key = I.Feature, Label = "Feature", Kind = FieldKind.Text, Searchable = true, Text = i => i.Feature },
        new() { Key = "id", Label = "ID", Kind = FieldKind.Text, Searchable = true, Text = i => i.Id },
        new() { Key = I.Started, Label = "Started", Kind = FieldKind.Date, Date = i => i.Start },
        new() { Key = I.Updated, Label = "Updated", Kind = FieldKind.Date, Aliases = ["modified"], Date = i => i.Modified },
        new() { Key = I.Ended, Label = "Ended", Kind = FieldKind.Date, Date = i => i.End },
    ];

    static readonly ListQuery IssuesNewest = ListQuery.Empty with { Sort = [new SortLevel(I.Updated, Descending: true)] };

    static SavedView IssueView(string id, string name, bool pinned, params Condition[] where) =>
        new(id, name, IssuesNewest with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: pinned);

    public static IReadOnlyList<SavedView> IssueViews { get; } =
    [
        IssueView("active", "Active", true, new ChoiceCondition(I.State, [I.Active], [])),
        IssueView("incidents", "Active Incidents", true, new ChoiceCondition(I.State, [I.Active], []), new ChoiceCondition(I.Kind, ["Incident"], [])),
        IssueView("resolved", "Resolved", true, new ChoiceCondition(I.State, [I.Resolved], [])),
        IssueView("all", "All", false),
    ];

    static (string, BadgeKind) StatusBadge(ServiceIssue i) => (i.StatusText, i.IsResolved || ServiceHealth.IsHealthy(i.Status) ? BadgeKind.Success
        : ServiceHealth.Severity(i.Status) <= 1 ? BadgeKind.Danger : BadgeKind.Warning);

    public static ListDefinition<ServiceIssue> Issues(Func<ServiceIssue, object> preview) => new()
    {
        Key = "serviceissues",
        Title = "Service Issues",
        Noun = "issues",
        Fields = IssueFields,
        Id = i => i.Id,
        Views = IssueViews,
        SearchHint = "Search issues, features, IDs… or try service:\"exchange online\" kind:incident",
        Preview = preview,
        Columns =
        [
            new() { Key = I.Title, Header = "Issue", Cell = CellKind.Primary, MinWidth = 280, DefaultWidth = 420,
                Text = i => i.Title, Subtitle = i => $"{i.Service}{(i.Feature is { Length: > 0 } f ? " · " + f : "")} · {i.Id}", Initials = i => Display.Initials(i.Service) },
            new() { Key = I.Status, Header = "Status", Cell = CellKind.Badge, MinWidth = 130, DefaultWidth = 160, Badge = StatusBadge },
            new() { Key = I.Kind, Header = "Kind", Cell = CellKind.Text, MinWidth = 90, DefaultWidth = 100, Text = i => i.KindText },
            new() { Key = I.Updated, Header = "Updated", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 140, Date = i => i.Modified },
            new() { Key = I.Started, Header = "Started", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 140, Date = i => i.Start },
            new() { Key = I.Ended, Header = "Ended", Cell = CellKind.Date, MinWidth = 120, DefaultWidth = 140, DefaultVisible = false, Date = i => i.End },
        ],
    };

    public static class M
    {
        public const string Title = "title";
        public const string Services = "services";
        public const string Category = "category";
        public const string Severity = "severity";
        public const string Tags = "tags";
        public const string Major = "major";
        public const string ActBy = "actby";
        public const string Published = "published";
        public const string Updated = "updated";
        public const string Read = "read";
        public const string Archived = "archived";
        public const string Favorite = "favorite";

        public const string Unread = "Unread";
        public const string IsRead = "Read";
        public const string InArchive = "Archived";
        public const string NotArchived = "Not Archived";
        public const string Starred = "Favorite";
        public const string NotStarred = "Not Favorite";
        public const string MajorChange = "Major Change";
        public const string NotMajor = "Not Major";
    }

    public static IReadOnlyList<Field<MessageCenterPost>> PostFields { get; } =
    [
        new() { Key = M.Title, Label = "Post", Kind = FieldKind.Text, Searchable = true, Text = m => m.Title },
        new() { Key = M.Services, Label = "Service", Kind = FieldKind.MultiChoice, Aliases = ["service", "svc"], Values = m => m.Services },
        new() { Key = M.Category, Label = "Category", Kind = FieldKind.Choice, ChoiceOrder = ["Plan for Change", "Prevent or Fix Issues", "Stay Informed"], Text = m => m.CategoryText },
        new() { Key = M.Severity, Label = "Severity", Kind = FieldKind.Choice, ChoiceOrder = ["Critical", "High", "Normal"], Text = m => m.SeverityText },
        new() { Key = M.Tags, Label = "Tags", Kind = FieldKind.MultiChoice, Aliases = ["tag"], Values = m => m.Tags },
        new() { Key = M.Major, Label = "Major Change", Kind = FieldKind.Choice, ChoiceOrder = [M.MajorChange, M.NotMajor], Text = m => m.IsMajorChange ? M.MajorChange : M.NotMajor },
        new() { Key = M.Read, Label = "Read", Kind = FieldKind.Choice, ChoiceOrder = [M.Unread, M.IsRead], Text = m => m.IsRead ? M.IsRead : M.Unread },
        new() { Key = M.Archived, Label = "Archived", Kind = FieldKind.Choice, ChoiceOrder = [M.NotArchived, M.InArchive], Text = m => m.IsArchived ? M.InArchive : M.NotArchived },
        new() { Key = M.Favorite, Label = "Favorite", Kind = FieldKind.Choice, ChoiceOrder = [M.Starred, M.NotStarred], Text = m => m.IsFavorited ? M.Starred : M.NotStarred },
        new() { Key = "id", Label = "ID", Kind = FieldKind.Text, Searchable = true, Text = m => m.Id },
        new() { Key = "body", Label = "Text", Kind = FieldKind.Text, Searchable = true, Text = m => m.Body },
        new() { Key = M.ActBy, Label = "Act By", Kind = FieldKind.Date, Aliases = ["due"], Date = m => m.ActBy },
        new() { Key = M.Published, Label = "Published", Kind = FieldKind.Date, Aliases = ["posted"], Date = m => m.Start },
        new() { Key = M.Updated, Label = "Updated", Kind = FieldKind.Date, Date = m => m.Modified },
    ];

    static readonly ListQuery PostsNewest = ListQuery.Empty with { Sort = [new SortLevel(M.Published, Descending: true)] };

    static SavedView PostView(string id, string name, bool pinned, params Condition[] where) =>
        new(id, name, PostsNewest with { Where = ConditionGroup.All(where) }, BuiltIn: true, Pinned: pinned);

    static readonly Condition Inbox = new ChoiceCondition(M.Archived, [M.NotArchived], []);

    public static IReadOnlyList<SavedView> PostViews { get; } =
    [
        PostView("inbox", "Inbox", true, Inbox),
        PostView("unread", "Unread", true, Inbox, new ChoiceCondition(M.Read, [M.Unread], [])),
        PostView("actby", "Action Needed", true, Inbox, new ChoiceCondition(M.Category, ["Plan for Change", "Prevent or Fix Issues"], [])) with
        {
            Query = PostsNewest with { Where = ConditionGroup.All([Inbox, new ChoiceCondition(M.Category, ["Plan for Change", "Prevent or Fix Issues"], [])]), Sort = [new SortLevel(M.ActBy, Descending: false)] },
        },
        PostView("major", "Major Changes", true, Inbox, new ChoiceCondition(M.Major, [M.MajorChange], [])),
        PostView("favorites", "Favorites", true, new ChoiceCondition(M.Favorite, [M.Starred], [])),
        PostView("archived", "Archived", false, new ChoiceCondition(M.Archived, [M.InArchive], [])),
        PostView("all", "All", false),
    ];

    static (string, BadgeKind) SeverityBadge(MessageCenterPost m) => m.Severity switch
    {
        "critical" => ("Critical", BadgeKind.Danger),
        "high" => ("High", BadgeKind.Warning),
        _ => ("Normal", BadgeKind.Neutral),
    };

    public static ListDefinition<MessageCenterPost> Posts(Func<MessageCenterPost, object> preview) => new()
    {
        Key = "messagecenter",
        Title = "Message Center",
        Noun = "posts",
        Fields = PostFields,
        Id = m => m.Id,
        Views = PostViews,
        SearchHint = "Search titles, text, IDs… or try service:teams tag:retirement read:unread",
        Preview = preview,
        Columns =
        [
            new() { Key = M.Title, Header = "Post", Cell = CellKind.Primary, MinWidth = 300, DefaultWidth = 460,
                Text = m => m.IsRead ? m.Title : "● " + m.Title, Subtitle = m => $"{string.Join(", ", m.Services)} · {m.Id}",
                Initials = m => Display.Initials(m.Services.FirstOrDefault() ?? m.Title) },
            new() { Key = M.Category, Header = "Category", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 170, Text = m => m.CategoryText },
            new() { Key = M.Severity, Header = "Severity", Cell = CellKind.Badge, MinWidth = 100, DefaultWidth = 110, Badge = SeverityBadge },
            new() { Key = M.ActBy, Header = "Act By", Cell = CellKind.Date, MinWidth = 110, DefaultWidth = 130, Date = m => m.ActBy },
            new() { Key = M.Published, Header = "Published", Cell = CellKind.Date, MinWidth = 110, DefaultWidth = 130, Date = m => m.Start },
            new() { Key = M.Updated, Header = "Updated", Cell = CellKind.Date, MinWidth = 110, DefaultWidth = 130, DefaultVisible = false, Date = m => m.Modified },
            new() { Key = M.Tags, Header = "Tags", Cell = CellKind.Text, MinWidth = 140, DefaultWidth = 200, DefaultVisible = false, Text = m => string.Join(", ", m.Tags) },
        ],
    };
}

/// <summary>A service's health, as a tile on Service Health.</summary>
public sealed record ServiceTile(ServiceStatus Status, int OpenIssues)
{
    public string Name => Status.Service;
    public string StatusText => Status.StatusText;
    public bool IsHealthy => Status.IsHealthy;
    public bool IsDown => Status.Status == "serviceInterruption";
    public bool IsDegraded => !IsHealthy && !IsDown;
    public BadgeKind Kind => IsHealthy ? BadgeKind.Success : IsDown ? BadgeKind.Danger : BadgeKind.Warning;
    public string IssuesText => OpenIssues switch { 0 => "No open issues", 1 => "1 open issue", var n => $"{n} open issues" };
}

/// <summary>An incident or advisory in the preview pane: what's affected, and Microsoft's updates newest first.</summary>
public sealed class ServiceIssuePreviewViewModel(ServiceIssue issue, Func<DateTimeOffset> now)
{
    public ServiceIssue Issue { get; } = issue;
    public string Title => Issue.Title;
    public string Subtitle => $"{Issue.KindText} · {Issue.Service}{(Issue.Feature is { Length: > 0 } f ? " · " + f : "")} · {Issue.Id}";
    public string StatusText => Issue.StatusText;
    public bool IsResolved => Issue.IsResolved;
    public bool IsOpen => !Issue.IsResolved;
    public string? Impact => Issue.Impact;
    public bool HasImpact => Issue.Impact is { Length: > 0 };
    public string When => Issue.End is { } end
        ? $"Started {Display.Relative(Issue.Start ?? end, now())}, ended {Display.Relative(end, now())}"
        : $"Started {(Issue.Start is { } s ? Display.Relative(s, now()) : "at an unknown time")}{(Issue.Modified is { } m ? $", updated {Display.Relative(m, now())}" : "")}";

    public IReadOnlyList<IssuePostRow> Posts => [.. Issue.Posts.OrderByDescending(p => p.When).Select(p => new IssuePostRow(
        p.When is { } w ? $"{w.ToLocalTime():ddd d MMM yyyy, h:mm tt}" : "", p.Text))];

    public bool HasPosts => Issue.Posts.Count > 0;
}

public sealed record IssuePostRow(string When, string Text);

/// <summary>One of a post's details (its roadmap ID, platforms…).</summary>
public sealed record PostDetail(string Name, string Value);

/// <summary>
/// Service Health (15a): a tile per service with its status, and the incidents and advisories as
/// a list. Read live from Graph when shown, and on Refresh.
/// </summary>
public sealed partial class ServiceHealthViewModel : ObservableObject
{
    readonly IServiceHealthRecords? _records;
    ServiceHealthRead? _read;

    public ServiceHealthViewModel(IServiceHealthRecords? records, IListStateStore? listState, Func<DateTimeOffset> now)
    {
        _records = records;
        Issues = DataListViewModel.Create(ServiceHealthLists.Issues(i => new ServiceIssuePreviewViewModel(i, now)),
            async ct => (_read ??= await ReadAsync(ct)).Issues, row => Issues!.PreviewRow(row), listState, now);
        Issues.HasRecordPages = false;
        Issues.PrimaryAction = new ListPrimaryAction("Refresh", "ArrowSync", true, "Read service health from Microsoft again", () => _ = Refresh());
        Issues.EmptyText = "No issues match. Microsoft keeps resolved issues here for about a month.";
    }

    public DataListViewModel Issues { get; }

    [ObservableProperty]
    public partial IReadOnlyList<ServiceTile> Services { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    public partial string Summary { get; private set; } = "Reading…";

    public bool IsLoaded { get; private set; }

    async Task<ServiceHealthRead> ReadAsync(CancellationToken cancellationToken)
    {
        var read = _records is null ? new ServiceHealthRead([], [], "Service health isn't available for this tenant.") : await _records.GetServiceHealthAsync(cancellationToken);
        var open = read.Issues.Where(i => !i.IsResolved).GroupBy(i => i.Service, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        Services = [.. read.Services.Select(s => new ServiceTile(s, open.GetValueOrDefault(s.Service)))];
        Note = read.Note;
        var unhealthy = read.Services.Count(s => !s.IsHealthy);
        Summary = read.Note is not null ? "" : unhealthy == 0 ? $"All {read.Services.Count} services are healthy."
            : $"{unhealthy} of {read.Services.Count} services {(unhealthy == 1 ? "has" : "have")} a problem right now.";
        return read;
    }

    public async Task LoadAsync()
    {
        if (IsLoaded)
            return;
        IsLoaded = true;
        await Issues.LoadAsync();
    }

    [RelayCommand]
    public async Task Refresh()
    {
        _read = null;
        IsLoaded = true;
        await Issues.ReloadAsync(busyText: "Reading service health from Microsoft…");
    }
}

/// <summary>
/// A Message Center post in the preview pane. Opening it marks it read (as the admin center does);
/// Archive and Favorite change only the signed-in admin's view, so they happen at once rather than
/// going to Pending Changes.
/// </summary>
public sealed partial class MessagePostPreviewViewModel : ObservableObject
{
    readonly MessageCenterViewModel _owner;

    public MessagePostPreviewViewModel(MessageCenterPost post, MessageCenterViewModel owner, Func<DateTimeOffset> now)
    {
        Post = post;
        _owner = owner;
        var bits = new List<string> { post.CategoryText };
        if (post.Start is { } s)
            bits.Add($"published {Display.Relative(s, now())}");
        if (post.IsUpdated && post.Modified is { } m)
            bits.Add($"updated {Display.Relative(m, now())}");
        Subtitle = $"{string.Join(", ", post.Services)} · {post.Id}";
        When = string.Join(", ", bits);
        ActByText = post.ActBy is { } a ? (a < now() ? $"Was due {a.ToLocalTime():d MMM yyyy}" : $"Act by {a.ToLocalTime():d MMM yyyy}") : null;
        IsOverdue = post.ActBy is { } due && due < now();
    }

    public MessageCenterPost Post { get; }
    public string Title => Post.Title;
    public string Subtitle { get; }
    public string When { get; }
    public string Body => Post.Body.Length > 0 ? Post.Body : "Microsoft didn't include any text.";
    public string? ActByText { get; }
    public bool HasActBy => ActByText is not null;
    public bool IsOverdue { get; }
    public bool IsMajorChange => Post.IsMajorChange;
    public string SeverityText => Post.SeverityText;
    public bool IsUrgent => Post.Severity is "high" or "critical";
    public string TagsText => string.Join(" · ", Post.Tags);
    public bool HasTags => Post.Tags.Count > 0;
    public IReadOnlyList<PostDetail> Details => [.. Post.Details.Select(d => new PostDetail(d.Key, d.Value))];
    public bool HasDetails => Post.Details.Count > 0;

    public string ArchiveText => Post.IsArchived ? "Move to Inbox" : "Archive";
    public string FavoriteText => Post.IsFavorited ? "Remove Favorite" : "Favorite";
    public string ReadText => Post.IsRead ? "Mark Unread" : "Mark Read";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    Task ToggleArchive() => Set(Post.IsArchived ? MessageView.Unarchive : MessageView.Archive);

    [RelayCommand]
    Task ToggleFavorite() => Set(Post.IsFavorited ? MessageView.Unfavorite : MessageView.Favorite);

    [RelayCommand]
    Task ToggleRead() => Set(Post.IsRead ? MessageView.Unread : MessageView.Read);

    async Task Set(MessageView view)
    {
        Error = await _owner.SetViewAsync([Post.Id], view);
    }
}

/// <summary>Several posts ticked: mark them read or unread, archive or move them back, all at once.</summary>
public sealed partial class MessageSelectionViewModel(IReadOnlyList<object> rows, Action<object> uncheck, MessageCenterViewModel owner) : ObservableObject, ISelectionPanel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial IReadOnlyList<MessageCenterPost> Posts { get; private set; } = [.. rows.OfType<MessageCenterPost>()];

    public string CountText => $"{Posts.Count:N0} posts selected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public void Update(IReadOnlyList<object> rows) => Posts = [.. rows.OfType<MessageCenterPost>()];

    [RelayCommand]
    void Remove(MessageCenterPost post) => uncheck(post);

    [RelayCommand]
    async Task Set(string view)
    {
        if (Enum.TryParse<MessageView>(view, out var v))
            Error = await owner.SetViewAsync([.. Posts.Select(p => p.Id)], v);
    }
}

/// <summary>
/// Message Center (15a): Microsoft's posts about changes and things to do, with views for unread,
/// action needed and major changes. Read live from Graph when shown; read, archived and favorite
/// are the signed-in admin's own, kept by Microsoft.
/// </summary>
public sealed partial class MessageCenterViewModel : ObservableObject
{
    readonly IServiceHealthRecords? _records;
    List<MessageCenterPost>? _posts;

    public MessageCenterViewModel(IServiceHealthRecords? records, IListStateStore? listState, Func<DateTimeOffset> now)
    {
        _records = records;
        Posts = DataListViewModel.Create(ServiceHealthLists.Posts(p => Opened(p, now)), LoadAsync, row => Posts!.PreviewRow(row), listState, now);
        Posts.HasRecordPages = false;
        Posts.PrimaryAction = new ListPrimaryAction("Refresh", "ArrowSync", true, "Read Message Center from Microsoft again", () => _ = Refresh());
        Posts.EmptyText = "No posts match these filters.";
        Posts.SelectionPanel = (rows, uncheck) => new MessageSelectionViewModel(rows, uncheck, this);
    }

    public DataListViewModel Posts { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    public bool IsLoaded { get; private set; }

    async Task<IReadOnlyList<MessageCenterPost>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_posts is null)
        {
            var read = _records is null ? new MessageCenterRead([], "Message Center isn't available for this tenant.") : await _records.GetMessageCenterAsync(cancellationToken);
            Note = read.Note;
            _posts = [.. read.Posts];
        }
        return [.. _posts];
    }

    /// <summary>The preview for a post; an unread one is marked read, as opening it in the admin center does.</summary>
    object Opened(MessageCenterPost post, Func<DateTimeOffset> now)
    {
        if (!post.IsRead && _records is not null)
        {
            _ = SetViewAsync([post.Id], MessageView.Read, reload: false);
            post = post with { IsRead = true };
        }
        return new MessagePostPreviewViewModel(post, this, now);
    }

    /// <summary>
    /// Changes how posts show for this admin; the list updates from what's already read. Returns why
    /// it failed, or null. Marking read on opening doesn't reload, so the open post stays in an Unread view.
    /// </summary>
    public async Task<string?> SetViewAsync(IReadOnlyList<string> ids, MessageView view, bool reload = true)
    {
        if (_records is null || ids.Count == 0)
            return null;
        try
        {
            await _records.SetMessageViewAsync(ids, view);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"Microsoft didn't take that: {Friendly.Message(e)}";
        }
        if (_posts is not null)
        {
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < _posts.Count; i++)
                if (wanted.Contains(_posts[i].Id))
                    _posts[i] = view switch
                    {
                        MessageView.Read => _posts[i] with { IsRead = true },
                        MessageView.Unread => _posts[i] with { IsRead = false },
                        MessageView.Archive => _posts[i] with { IsArchived = true },
                        MessageView.Unarchive => _posts[i] with { IsArchived = false },
                        MessageView.Favorite => _posts[i] with { IsFavorited = true },
                        _ => _posts[i] with { IsFavorited = false },
                    };
        }
        if (reload)
            await Posts.ReloadAsync();
        return null;
    }

    public async Task Load()
    {
        if (IsLoaded)
            return;
        IsLoaded = true;
        await Posts.LoadAsync();
    }

    [RelayCommand]
    public async Task Refresh()
    {
        _posts = null;
        IsLoaded = true;
        await Posts.ReloadAsync(busyText: "Reading Message Center from Microsoft…");
    }
}
