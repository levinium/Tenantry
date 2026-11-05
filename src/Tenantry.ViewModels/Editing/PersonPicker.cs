using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Editing;

/// <summary>Something a picker offers: a person, a group or a mailbox.</summary>
/// <param name="Type">Graph's type ("user", "group").</param>
/// <param name="SearchText">Lower-case text the query is matched against.</param>
public sealed record PickOption(string Id, string Name, string Detail, string Type, string? Address, string SearchText)
{
    public ChangeTarget Target(string kind) => new(kind, Id, Name, Address);

    public static PickOption Of(UserSummary u)
    {
        var address = u.Kind == UserKind.Guest && u.Mail is { Length: > 0 } m ? m : u.UserPrincipalName;
        return new(u.Id, u.DisplayName, u.Department is { Length: > 0 } d ? $"{address} · {d}" : address, "user", u.UserPrincipalName,
            string.Join(' ', u.DisplayName, u.UserPrincipalName, u.Mail, u.Department).ToLowerInvariant());
    }

    public static PickOption Of(GroupSummary g) =>
        new(g.Id, g.DisplayName, g.Mail is { } mail ? $"{mail} · {GroupSummary.KindName(g.Kind)}" : GroupSummary.KindName(g.Kind),
            "group", g.Mail, string.Join(' ', g.DisplayName, g.Mail, GroupSummary.KindName(g.Kind)).ToLowerInvariant());
}

/// <summary>
/// A search box that offers matching people (or groups) as you type; choosing one hands it to
/// the owner and clears the box. Every word typed must appear somewhere in the option.
/// </summary>
public sealed partial class PersonPicker : ObservableObject
{
    const int MaxResults = 8;

    /// <summary>In an Add popup: this many shown, in a scrolling list, before and while typing.</summary>
    const int MaxBrowsed = 100;

    readonly Func<IEnumerable<PickOption>> _options;
    readonly Action<PickOption> _picked;

    public PersonPicker(string placeholder, Func<IEnumerable<PickOption>> options, Action<PickOption> picked)
    {
        Placeholder = placeholder;
        _options = options;
        _picked = picked;
    }

    public string Placeholder { get; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults), nameof(ShowNoMatch))]
    public partial IReadOnlyList<PickOption> Results { get; private set; } = [];

    /// <summary>The results as rows the view shows, each with its tick (several can be added at once).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PickRow> Rows { get; private set; } = [];

    /// <summary>
    /// Several can be chosen: a click ticks a row (Shift ticks a range), ticks stay while typing
    /// narrows the list, and Add hands them all over. Otherwise a click hands over that one.
    /// </summary>
    [ObservableProperty]
    public partial bool Multiple { get; set; }

    /// <summary>What's ticked to add (kept while typing narrows the list).</summary>
    public PickTicks Ticks { get; } = new();

    public bool HasTicked => Ticks.Any;

    public string AddTickedText => Ticks.AddText;

    public string TickedText => Ticks.Text;

    public bool HasResults => Results.Count > 0;

    public bool ShowNoMatch => !HasResults && (!string.IsNullOrWhiteSpace(Query) || Browsing);

    /// <summary>What the empty popup says: nothing typed matched, or there's nothing left to add.</summary>
    public string NoMatchText => string.IsNullOrWhiteSpace(Query) ? "Nothing left to add." : "No matches. Try part of a name or address.";

    /// <summary>"Showing the first 100: type to narrow it" when the list was cut short.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    public partial string? MoreText { get; private set; }

    public bool HasMore => MoreText is not null;

    /// <summary>
    /// An Add popup lists what can be added before anything is typed (A to Z, scrolling), and
    /// typing narrows it. A plain search box only lists matches once something is typed.
    /// </summary>
    public bool Browsing { get; private set; }

    /// <summary>Starts browsing: the list shows at once, from the current options.</summary>
    public void Browse()
    {
        Browsing = true;
        Refresh();
    }

    void Refresh()
    {
        Results = Search(Query);
        Rows = Ticks.RowsFor(Results);
        OnPropertyChanged(nameof(ShowNoMatch));
        OnPropertyChanged(nameof(NoMatchText));
    }

    partial void OnQueryChanged(string value) => Refresh();

    IReadOnlyList<PickOption> Search(string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        MoreText = null;
        if (words.Length == 0 && !Browsing)
            return [];
        var matches = _options()
            .Where(o => words.All(w => o.SearchText.Contains(w, StringComparison.Ordinal)))
            // Names that start with what was typed come first.
            .OrderByDescending(o => words.Length > 0 && o.Name.StartsWith(words[0], StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase);
        var limit = Browsing ? MaxBrowsed : MaxResults;
        var shown = matches.Take(limit + 1).ToArray();
        if (shown.Length <= limit)
            return shown;
        if (Browsing)
            MoreText = $"Showing the first {limit:N0}. Type to narrow it down.";
        return shown[..limit];
    }

    /// <summary>Raised after a choice is handed over (an Add button's popup closes on it).</summary>
    public event EventHandler? Picked;

    [RelayCommand]
    void Pick(PickOption option)
    {
        Query = "";
        _picked(option);
        Picked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A row was clicked: with <see cref="Multiple"/> it's ticked or unticked (with Shift, every
    /// shown row from the last one clicked takes the same state); otherwise it's chosen.
    /// </summary>
    public void Choose(PickRow row, bool shift = false)
    {
        if (!Multiple)
        {
            Pick(row.Option);
            return;
        }
        Ticks.Click(Rows, row, shift);
        TicksChanged();
    }

    /// <summary>Hands over every ticked one, in the order ticked, then clears the ticks.</summary>
    [RelayCommand(CanExecute = nameof(HasTicked))]
    void AddTicked()
    {
        var chosen = Ticks.Take();
        Query = "";
        foreach (var option in chosen)
            _picked(option);
        TicksChanged();
        Refresh();
        Picked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the ticks (the popup closed without adding).</summary>
    public void ClearTicks()
    {
        Ticks.Clear(Rows);
        TicksChanged();
    }

    void TicksChanged()
    {
        OnPropertyChanged(nameof(HasTicked));
        OnPropertyChanged(nameof(AddTickedText));
        OnPropertyChanged(nameof(TickedText));
        AddTickedCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>
/// One checkbox on a row ("Member", "Owner", "Full access"): shows the state once pending changes
/// apply, and stages a change when ticked or cleared.
/// </summary>
public sealed partial class RightToggle(string label, bool isOn, bool isPending, bool canEdit, string tip, Action<bool> set) : ObservableObject
{
    public string Label => label;

    public bool CanEdit => canEdit;

    public string Tip => tip;

    public bool IsPending => isPending;

    public bool IsOn
    {
        get => isOn;
        set
        {
            if (value == isOn || !canEdit)
                return;
            isOn = value;
            set(value);
        }
    }
}
