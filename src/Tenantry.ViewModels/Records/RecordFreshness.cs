using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Records;

public enum FreshnessState
{
    /// <summary>Nothing to say: the tenant can't be checked (the demo), or no check has started.</summary>
    None,
    Checking,
    UpToDate,

    /// <summary>The check found values that changed in Microsoft 365; they now show the current ones.</summary>
    Updated,
    Failed,
}

/// <summary>One value that differs between the saved copy and Microsoft 365.</summary>
/// <param name="Quiet">Changes all the time (last sign-in): updated, but not called out.</param>
public sealed record FieldChange(string Key, string Label, bool Quiet = false);

/// <summary>
/// A record page's "is this current?" line. The page shows the saved copy at once; this says
/// when it's being checked against Microsoft 365, and names what changed if anything did. Kept
/// deliberately quiet: muted text, no warning colours, and the changed values get a brief tint.
/// </summary>
public sealed partial class RecordFreshness : ObservableObject
{
    /// <summary>How long "Updated from Microsoft 365: …" stays before settling to "Up to date".</summary>
    public static TimeSpan SettleAfter { get; set; } = TimeSpan.FromSeconds(10);

    int _generation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsChecking), nameof(IsUpdated), nameof(IsSettled), nameof(IsUpToDate), nameof(IsFailed), nameof(Text),
        nameof(Tip))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial FreshnessState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(Tip))]
    public partial DateTimeOffset? CheckedAt { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial IReadOnlyList<string> ChangedLabels { get; private set; } = [];

    string? _problem;

    public bool IsVisible => State != FreshnessState.None;
    public bool IsChecking => State == FreshnessState.Checking;
    public bool IsUpdated => State == FreshnessState.Updated;

    /// <summary>Up to date or couldn't check: the quiet resting state, with the refresh button.</summary>
    public bool IsSettled => State is FreshnessState.UpToDate or FreshnessState.Failed;

    public bool IsUpToDate => State == FreshnessState.UpToDate;

    public bool IsFailed => State == FreshnessState.Failed;

    string Time => CheckedAt is { } at ? at.ToLocalTime().ToString("h:mm tt") : "";

    public string Text => State switch
    {
        FreshnessState.Checking => "Checking Microsoft 365 for changes…",
        FreshnessState.UpToDate => $"Up to date · checked {Time}",
        FreshnessState.Updated => $"Updated from Microsoft 365: {Join(ChangedLabels)}",
        FreshnessState.Failed => "Showing the saved copy · couldn't check for changes",
        _ => "",
    };

    public string Tip => State switch
    {
        FreshnessState.Checking => "Reading this record from Microsoft 365. Keep working: anything that changed updates in place.",
        FreshnessState.UpToDate => $"Matches Microsoft 365 as of {Time}. Refresh to check again.",
        FreshnessState.Updated => "These changed in Microsoft 365 since the last sync (someone else, or another admin tool). The page now shows the current values, briefly tinted.",
        FreshnessState.Failed => $"The saved copy is shown. Checking failed: {_problem}",
        _ => "",
    };

    /// <summary>Raised with the changed fields' keys, so the values showing them can be tinted.</summary>
    public event EventHandler<IReadOnlySet<string>>? Highlight;

    /// <summary>Runs a check now (set by whoever owns the page; the refresh button calls it).</summary>
    public Func<Task>? CheckNow { get; set; }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    Task Refresh() => CheckNow?.Invoke() ?? Task.CompletedTask;

    bool CanRefresh => State != FreshnessState.Checking && CheckNow is not null;

    public void Start()
    {
        _generation++;
        State = FreshnessState.Checking;
    }

    public void UpToDate(DateTimeOffset at)
    {
        _generation++;
        CheckedAt = at;
        State = FreshnessState.UpToDate;
    }

    public void Failed(string problem)
    {
        _generation++;
        _problem = problem;
        State = FreshnessState.Failed;
    }

    /// <summary>Names what changed (quiet ones only tinted), then settles to "Up to date" after a few seconds.</summary>
    /// <remarks>
    /// async void (callers don't wait for the settle), so nothing may escape: an exception here
    /// would end the app. A failure only means the line doesn't settle.
    /// </remarks>
    public async void Updated(IReadOnlyList<FieldChange> changes, DateTimeOffset at)
    {
        try
        {
            var generation = ++_generation;
            CheckedAt = at;
            var shown = changes.Where(c => !c.Quiet).ToArray();
            Highlight?.Invoke(this, changes.Select(c => c.Key).ToHashSet(StringComparer.Ordinal));
            if (shown.Length == 0)
            {
                State = FreshnessState.UpToDate;
                return;
            }
            ChangedLabels = shown.Select(c => c.Label).ToArray();
            State = FreshnessState.Updated;
            await Task.Delay(SettleAfter);
            if (generation == _generation)
                State = FreshnessState.UpToDate;
        }
        catch (Exception)
        {
            // Nothing to show: the freshness line just keeps its last state.
        }
    }

    static string Join(IReadOnlyList<string> labels) => labels.Count switch
    {
        <= 3 => string.Join(", ", labels),
        _ => $"{string.Join(", ", labels.Take(2))} and {labels.Count - 2} more",
    };
}

/// <summary>What differs between two copies of a record, field by field, as the lists define the fields.</summary>
public static class RecordChanges
{
    /// <summary>Fields that change on their own all the time: kept current, never announced.</summary>
    static readonly HashSet<string> QuietFields = [UserFields.LastSignIn, UserFields.StorageUsed, UserFields.Storage];

    public static IReadOnlyList<FieldChange> Between(UserSummary old, UserSummary fresh) => Compare(UserFields.All, old, fresh);

    public static IReadOnlyList<FieldChange> Between(GroupSummary old, GroupSummary fresh)
    {
        var changes = Compare(GroupFields.All, old, fresh).Where(c => c.Key != GroupFields.Members).ToList();
        // Same count, different people still counts: compare who, and with what access.
        if (!People(old.Members).SetEquals(People(fresh.Members)))
            changes.Add(new FieldChange(GroupFields.Members, "Members"));
        if (!People(old.OwnerList).SetEquals(People(fresh.OwnerList)))
            changes.Add(new FieldChange("owners", "Owners"));
        return changes;
    }

    static HashSet<string> People(IReadOnlyList<GroupMember> people) => people.Select(p => $"{p.Id}|{p.Access}").ToHashSet(StringComparer.OrdinalIgnoreCase);

    static List<FieldChange> Compare<T>(IReadOnlyList<Field<T>> fields, T old, T fresh) =>
        fields.Where(f => Value(f, old) != Value(f, fresh))
            .Select(f => new FieldChange(f.Key, f.Label, QuietFields.Contains(f.Key)))
            .ToList();

    static string Value<T>(Field<T> field, T item) => string.Join("\u001f",
        field.Text?.Invoke(item) ?? "",
        field.Values is { } values ? string.Join("|", values(item).Order(StringComparer.Ordinal)) : "",
        field.Number?.Invoke(item)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
        field.Date?.Invoke(item)?.ToString("o") ?? "");
}
