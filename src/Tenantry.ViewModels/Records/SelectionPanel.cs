using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>A checked row in the selection panel, with × to uncheck it.</summary>
public sealed partial class SelectedChip(string name, string detail, Action remove) : ObservableObject
{
    public string Name => name;
    public string Detail => detail;

    [RelayCommand]
    void Remove() => remove();
}

/// <summary>One line of "what they have in common" ("Sign-in · 2 allowed, 1 blocked").</summary>
public sealed record SummaryLine(string Label, string Value);

/// <summary>
/// What the preview pane shows when several rows are checked: who's selected (each removable),
/// what they have in common, editable details that read "Mixed" where they differ (users), and
/// the bulk actions, each opening in place. Every change is staged for review.
/// </summary>
public sealed partial class SelectionPanelViewModel : ObservableObject, ISelectionPanel, IDisposable
{
    const int ChipsShown = 8;

    readonly string _singular;
    readonly string _plural;
    readonly Func<object, (string Name, string Detail)> _describe;
    readonly Func<IReadOnlyList<object>, IReadOnlyList<SummaryLine>> _summarize;
    readonly Action<object> _uncheck;
    IReadOnlyList<object> _rows;

    public SelectionPanelViewModel(IReadOnlyList<object> rows, Action<object> uncheck, string singular, string plural,
        Func<object, (string Name, string Detail)> describe, Func<IReadOnlyList<object>, IReadOnlyList<SummaryLine>> summarize,
        Func<Func<IReadOnlyList<object>>, IReadOnlyList<BulkAction>> actions, ChangePipeline? pipeline, Availability canEdit,
        Func<Func<IReadOnlyList<object>>, BulkDetailsViewModel?>? details = null)
    {
        _rows = rows;
        _uncheck = uncheck;
        _singular = singular;
        _plural = plural;
        _describe = describe;
        _summarize = summarize;
        CanEdit = pipeline is not null && canEdit.IsAvailable;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit.Reason ?? "";
        if (CanEdit)
        {
            Bulk = new BulkEditorViewModel(Heading, actions(() => _rows), pipeline!);
            Details = details?.Invoke(() => _rows);
        }
        Rebuild();
    }

    public string Heading => $"{_rows.Count:N0} {(_rows.Count == 1 ? _singular : _plural)} selected";

    public bool CanEdit { get; }

    public string EditReason { get; }

    public ObservableCollection<SelectedChip> Chips { get; } = [];

    [ObservableProperty]
    public partial bool ShowAll { get; set; }

    partial void OnShowAllChanged(bool value) => Rebuild();

    public string? MoreText => !ShowAll && _rows.Count > ChipsShown ? $"Show all {_rows.Count:N0}" : null;

    public bool HasMore => MoreText is not null;

    [RelayCommand]
    void ShowEveryone() => ShowAll = true;

    [ObservableProperty]
    public partial IReadOnlyList<SummaryLine> Summary { get; private set; } = [];

    /// <summary>Editable details that read "Mixed" where people differ (users only).</summary>
    public BulkDetailsViewModel? Details { get; }

    public bool HasDetails => Details is not null;

    /// <summary>The bulk actions; null when the admin can't edit (the reason shows instead).</summary>
    public BulkEditorViewModel? Bulk { get; }

    public void Update(IReadOnlyList<object> rows)
    {
        _rows = rows;
        Rebuild();
        Details?.Refresh();
    }

    void Rebuild()
    {
        Chips.Clear();
        foreach (var row in ShowAll ? _rows : _rows.Take(ChipsShown))
        {
            var (name, detail) = _describe(row);
            Chips.Add(new SelectedChip(name, detail, () => _uncheck(row)));
        }
        Summary = _summarize(_rows);
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(HasMore));
    }

    public void Dispose() => Details?.Dispose();

    // ---- The two lists' panels --------------------------------------------------------------

    public static SelectionPanelViewModel ForUsers(IReadOnlyList<object> rows, Action<object> uncheck, IDirectoryIndex index, IUserRecords? records,
        ChangePipeline? pipeline, Availability canEdit) =>
        new(rows, uncheck, "user", "users",
            r => (((UserSummary)r).DisplayName, ((UserSummary)r).UserPrincipalName),
            SummarizeUsers,
            current => BulkActions.ForUsers(() => current().Cast<UserSummary>().ToArray(), index, records),
            pipeline, canEdit,
            current => new BulkDetailsViewModel(() => current().Cast<UserSummary>().ToArray(), records, pipeline!));

    public static SelectionPanelViewModel ForGroups(IReadOnlyList<object> rows, Action<object> uncheck, IDirectoryIndex index,
        ChangePipeline? pipeline, Availability canEdit) =>
        new(rows, uncheck, "group or mailbox", "groups and mailboxes",
            r => (((GroupSummary)r).DisplayName, ((GroupSummary)r).Mail ?? GroupSummary.KindName(((GroupSummary)r).Kind)),
            SummarizeGroups,
            current => BulkActions.ForGroups(() => current().Cast<GroupSummary>().ToArray(), index),
            pipeline, canEdit);

    static IReadOnlyList<SummaryLine> SummarizeUsers(IReadOnlyList<object> rows)
    {
        var users = rows.Cast<UserSummary>().ToArray();
        var lines = new List<SummaryLine>
        {
            new("Accounts", Counts(users.Select(UserFields.TypeOf), nouns: true)),
            new("Sign-in", Counts(users.Select(u => u.AccountEnabled ? "allowed" : "blocked"), nouns: false)),
        };
        // Licenses everyone has, then how many have none.
        var common = users.Length == 0 ? [] : users.Skip(1).Aggregate(users[0].Licenses.ToHashSet(StringComparer.Ordinal),
            (set, u) => { set.IntersectWith(u.Licenses); return set; });
        var unlicensed = users.Count(u => u.Licenses.Count == 0);
        lines.Add(new("Licenses", common.Count > 0 ? $"All have {string.Join(", ", common.Order(StringComparer.CurrentCulture))}"
            : unlicensed == users.Length ? "None of them is licensed"
            : unlicensed > 0 ? $"No license in common; {unlicensed:N0} unlicensed"
            : "No license in common"));
        return lines;
    }

    static IReadOnlyList<SummaryLine> SummarizeGroups(IReadOnlyList<object> rows)
    {
        var groups = rows.Cast<GroupSummary>().ToArray();
        var kinds = groups.GroupBy(g => g.Kind).OrderByDescending(k => k.Count()).Select(k => $"{k.Count():N0} {KindWord(k.Key, k.Count())}");
        var lines = new List<SummaryLine> { new("Kinds", string.Join(" · ", kinds)) };
        var withPeople = groups.Sum(g => g.Members.Count);
        lines.Add(new("People", $"{withPeople:N0} members or people with access in all"));
        var ownerless = groups.Count(g => !g.IsMailbox && g.OwnerList.Count == 0);
        if (ownerless > 0)
            lines.Add(new("Owners", $"{ownerless:N0} {(ownerless == 1 ? "has" : "have")} no owner"));
        var dynamic = groups.Count(g => g.IsDynamic);
        if (dynamic > 0)
            lines.Add(new("Dynamic", $"{dynamic:N0} follow a rule: their members can't be changed here"));
        return lines;
    }

    /// <summary>
    /// "2 members · 1 guest" or "All members" for nouns; "2 allowed · 1 blocked" or "All allowed"
    /// for states; kinds keep their own names ("2 Microsoft 365 · 1 Shared mailbox").
    /// </summary>
    static string Counts(IEnumerable<string> values, bool nouns)
    {
        var counts = values.GroupBy(v => v).OrderByDescending(g => g.Count()).Select(g => (g.Key, Count: g.Count())).ToArray();
        string Word(string key, int count) => nouns ? Plural(key.ToLowerInvariant(), count) : key;
        return counts.Length == 1 ? $"All {Word(counts[0].Key, 2)}"
            : string.Join(" · ", counts.Select(c => $"{c.Count:N0} {Word(c.Key, c.Count)}"));
    }

    /// <summary>"1 security group", "2 shared mailboxes", "3 Microsoft 365 groups".</summary>
    static string KindWord(GroupKind kind, int count)
    {
        var word = kind switch
        {
            GroupKind.Microsoft365 => "Microsoft 365 group",
            GroupKind.Security => "security group",
            GroupKind.MailEnabledSecurity => "mail-enabled security group",
            GroupKind.Distribution => "distribution list",
            GroupKind.SharedMailbox => "shared mailbox",
            GroupKind.RoomMailbox => "room",
            _ => "equipment mailbox",
        };
        return Plural(word, count);
    }

    static string Plural(string word, int count) => count == 1 ? word : word.EndsWith('x') ? word + "es" : word + "s";
}

/// <summary>
/// A detail for the whole selection: the value they share, or "Mixed" (the tooltip lists the
/// values). Editing it stages the same value for everyone; "Pending for 3" shows what's staged.
/// </summary>
/// <summary>What a <see cref="MixedField"/> needs from the panel that owns it.</summary>
public interface IMixedFieldHost
{
    /// <summary>One change per person who differs; <paramref name="close"/> ends the edit when it worked.</summary>
    Task StageAsync(MixedField field, bool close);

    /// <summary>Remembers each person's pending change for this detail as the edit starts.</summary>
    void Begin(MixedField field);

    /// <summary>Puts everyone's pending change for this detail back as it was when the edit started.</summary>
    void Restore(MixedField field);
}

public sealed partial class MixedField : ObservableObject
{
    readonly IMixedFieldHost _host;

    public MixedField(string property, string label, string? hint, IMixedFieldHost host)
    {
        Property = property;
        Label = label;
        Hint = hint;
        _host = host;
    }

    public string Property { get; }
    public string Label { get; }
    public string? Hint { get; }

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    [ObservableProperty]
    public partial bool IsMixed { get; set; }

    [ObservableProperty]
    public partial string? ValuesTip { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    public partial string? PendingText { get; set; }

    public bool HasPending => PendingText is not null;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    [RelayCommand]
    void Edit()
    {
        Draft = IsMixed ? "" : Value == BulkDetailsViewModel.NotSet ? "" : Value;
        _startDraft = Draft;
        Message = null;
        _host.Begin(this);
        IsEditing = true;
    }

    string _startDraft = "";

    /// <summary>Esc: everyone goes back to how they were before editing, even if a pause staged changes.</summary>
    [RelayCommand]
    void Cancel()
    {
        if (IsEditing)
            _host.Restore(this);
        IsEditing = false;
        Message = null;
    }

    /// <summary>
    /// Ends the edit (Enter or leaving the box): an untouched value just closes, so opening a
    /// mixed field and clicking away never clears it for everyone.
    /// </summary>
    [RelayCommand]
    Task Stage()
    {
        if (!IsEditing)
            return Task.CompletedTask;
        if (Draft.Trim() == _startDraft.Trim())
        {
            Cancel();
            return Task.CompletedTask;
        }
        return _host.StageAsync(this, close: true);
    }

    /// <summary>A pause in typing: one change per person goes to Pending changes; the box stays open.</summary>
    [RelayCommand]
    Task StageDraft()
    {
        if (!IsEditing)
            return Task.CompletedTask;
        if (Draft.Trim() == _startDraft.Trim())
        {
            _host.Restore(this);
            return Task.CompletedTask;
        }
        return _host.StageAsync(this, close: false);
    }

    /// <summary>Someone selected has a value, and no edit is open.</summary>
    public bool CanClear => !IsEditing && (IsMixed || (Value.Length > 0 && Value != BulkDetailsViewModel.NotSet));

    partial void OnIsEditingChanged(bool value) => OnPropertyChanged(nameof(CanClear));
    partial void OnIsMixedChanged(bool value) => OnPropertyChanged(nameof(CanClear));
    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(CanClear));

    /// <summary>Clears the detail for everyone selected (an empty box alone is left as it was).</summary>
    [RelayCommand]
    Task ClearForEveryone()
    {
        Draft = "";
        _host.Begin(this);
        _startDraft = "\u0000";
        IsEditing = true;
        return _host.StageAsync(this, close: true);
    }
}

/// <summary>The selection panel's details: one <see cref="MixedField"/> per detail a bulk edit can set.</summary>
public sealed partial class BulkDetailsViewModel : ObservableObject, IMixedFieldHost, IDisposable
{
    public const string NotSet = "Not set";

    /// <summary>Above this many people, details outside the list aren't read up front (still editable).</summary>
    const int ReadLimit = 100;

    const int ReadsAtOnce = 4;

    readonly Func<IReadOnlyList<UserSummary>> _selected;
    readonly IUserRecords? _records;
    readonly ChangePipeline _pipeline;
    readonly Dictionary<string, UserDetails> _known = new(StringComparer.Ordinal);
    bool _reading;

    public BulkDetailsViewModel(Func<IReadOnlyList<UserSummary>> selected, IUserRecords? records, ChangePipeline pipeline)
    {
        _selected = selected;
        _records = records;
        _pipeline = pipeline;
        Fields = BulkActions.DetailFields.Select(d =>
        {
            var parts = d.Label.Split(" (");
            return new MixedField(d.Property, parts[0], parts.Length > 1 ? parts[1].TrimEnd(')') : null, this);
        }).ToArray();
        _pipeline.Changed += OnPendingChanged;
        Refresh();
    }

    public IReadOnlyList<MixedField> Fields { get; }

    [ObservableProperty]
    public partial bool IsReading { get; private set; }

    /// <summary>Recomputes every field for the current selection, reading details not yet known.</summary>
    public void Refresh()
    {
        Show();
        _ = ReadMissingAsync();
    }

    async Task ReadMissingAsync()
    {
        var missing = _selected().Where(u => !_known.ContainsKey(u.Id)).ToArray();
        if (_reading || _records is null || missing.Length == 0 || _selected().Count > ReadLimit)
            return;
        _reading = true;
        IsReading = true;
        try
        {
            var read = new System.Collections.Concurrent.ConcurrentDictionary<string, UserDetails>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = ReadsAtOnce },
                async (u, ct) => read[u.Id] = await _records.GetDetailsAsync(u.Id, ct));
            foreach (var (id, details) in read)
                _known[id] = details;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unread details show as "Not read"; staging still reads what it needs.
        }
        finally
        {
            _reading = false;
            IsReading = false;
        }
        Show();
    }

    void Show()
    {
        var users = _selected();
        foreach (var field in Fields)
        {
            var listed = BulkActions.IsListDetail(field.Property);
            var values = new List<string?>();
            var unread = 0;
            var pending = 0;
            foreach (var u in users)
            {
                if (_pipeline.PendingFor(u.Id, field.Property) is SetUserText p)
                {
                    values.Add(p.New);
                    pending++;
                }
                else if (listed)
                    values.Add(BulkActions.ListValue(u, field.Property));
                else if (_known.TryGetValue(u.Id, out var d))
                    values.Add(d[field.Property]);
                else
                    unread++;
            }
            var distinct = values.Select(v => string.IsNullOrEmpty(v) ? NotSet : v).GroupBy(v => v).OrderByDescending(g => g.Count()).ToArray();
            field.IsMixed = distinct.Length > 1;
            field.Value = unread > 0 && distinct.Length == 0 ? (IsReading ? "Reading…" : "Not read")
                : distinct.Length == 1 ? distinct[0].Key
                : "Mixed";
            field.ValuesTip = distinct.Length > 1
                ? string.Join("\n", distinct.Take(6).Select(g => $"{g.Key} ({g.Count():N0})")) + (distinct.Length > 6 ? $"\nand {distinct.Length - 6:N0} more" : "")
                : null;
            field.PendingText = pending > 0 ? $"Pending for {pending:N0}" : null;
        }
    }

    void OnPendingChanged(object? sender, EventArgs e) => Show();

    /// <summary>Each selected person's pending change for a detail when its edit started, by detail.</summary>
    readonly Dictionary<string, (string Id, ChangeOp? Pending)[]> _atStart = new(StringComparer.Ordinal);

    void IMixedFieldHost.Begin(MixedField field) =>
        _atStart[field.Property] = _selected().Select(u => (u.Id, _pipeline.PendingFor(u.Id, field.Property))).ToArray();

    void IMixedFieldHost.Restore(MixedField field)
    {
        if (_atStart.TryGetValue(field.Property, out var start))
            _pipeline.Restore(start.Select(s => (s.Id, field.Property, s.Pending)));
    }

    async Task IMixedFieldHost.StageAsync(MixedField field, bool close)
    {
        var plan = await BulkActions.SetDetailAsync(_selected(), _records, field.Property, field.Draft.Trim(), CancellationToken.None, _known);
        if (plan.Error is { } error)
        {
            field.Message = error;
            return;
        }
        // Typing on after a pause: start again from how things were, so a shorter value replaces the longer one cleanly.
        ((IMixedFieldHost)this).Restore(field);
        _pipeline.StageAll(plan.Changes);
        field.Message = plan.Changes.Count == 0 ? "Everyone already has that." : null;
        if (close)
        {
            field.IsEditing = false;
            _atStart.Remove(field.Property);
        }
    }

    public void Dispose() => _pipeline.Changed -= OnPendingChanged;
}
