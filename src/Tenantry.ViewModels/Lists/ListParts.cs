using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Lists;

public enum SortState
{
    None,
    Ascending,
    Descending,
}

/// <summary>
/// A column as the grid shows it. Cell accessors take the row as <c>object</c> so the view can
/// build cells without knowing the row type.
/// </summary>
public sealed partial class ColumnViewModel : ObservableObject
{
    public required string Key { get; init; }
    public required string Header { get; init; }
    public required CellKind Cell { get; init; }
    public required string? Field { get; init; }
    public required double MinWidth { get; init; }
    public bool CanSort => Field is not null;
    public bool CanHide => Cell != CellKind.Primary;

    public Func<object, string?> Text { get; init; } = _ => null;
    public Func<object, string?> Subtitle { get; init; } = _ => null;
    public Func<object, string> Initials { get; init; } = _ => "";
    public Func<object, (string Text, BadgeKind Kind)>? Badge { get; init; }
    public Func<object, IReadOnlyList<string>> Badges { get; init; } = _ => [];

    /// <summary>For date cells: relative text, exact text for the tooltip, and an optional warning tone.</summary>
    public Func<object, (string Relative, string Exact, BadgeKind? Tone)>? DateCell { get; init; }

    public Func<object, string> Plain { get; init; } = _ => "";

    public string EmptyText { get; init; } = "";

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    [ObservableProperty]
    public partial double? Width { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSorted), nameof(SortGlyph), nameof(SortTip))]
    public partial SortState Sort { get; set; }

    /// <summary>1-based position among sort levels; shown when more than one column sorts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityText), nameof(SortTip))]
    public partial int SortPriority { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PriorityText))]
    public partial bool ShowPriority { get; set; }

    public bool IsSorted => Sort != SortState.None;
    public string SortGlyph => Sort == SortState.Descending ? "↓" : "↑";
    public string PriorityText => ShowPriority ? SortPriority.ToString() : "";

    public string SortTip => Sort switch
    {
        SortState.Ascending => $"Sorted A→Z{(ShowPriority ? $" (level {SortPriority})" : "")}. Click to sort Z→A.",
        SortState.Descending => $"Sorted Z→A{(ShowPriority ? $" (level {SortPriority})" : "")}. Click to sort A→Z.",
        _ => CanSort ? $"Click to sort by {Header}" : Header,
    };
}

/// <summary>One active filter, shown as a removable pill above the list.</summary>
public sealed class FilterPill
{
    public FilterPill(string text, Condition condition, bool isAdvanced, Action<FilterPill> edit, Action<FilterPill> remove)
    {
        Text = text;
        Condition = condition;
        IsAdvanced = isAdvanced;
        EditCommand = new RelayCommand(() => edit(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Text { get; }
    public Condition Condition { get; }
    public bool IsAdvanced { get; }
    public IRelayCommand EditCommand { get; }
    public IRelayCommand RemoveCommand { get; }
}

/// <summary>A field offered in the Add filter menu and the sort/group pickers.</summary>
public sealed record FieldOption(string Key, string Label, FieldKind Kind)
{
    public override string ToString() => Label;
}

/// <summary>One level in the Sort panel: "Then by Department, A→Z".</summary>
public sealed partial class SortLevelViewModel(IReadOnlyList<FieldOption> fields) : ObservableObject
{
    public IReadOnlyList<FieldOption> Fields { get; } = fields;

    [ObservableProperty]
    public partial FieldOption? Field { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionText))]
    public partial bool Descending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText))]
    public partial int Index { get; set; }

    public string LevelText => Index == 0 ? "Sort by" : "Then by";

    public string DirectionText => Field?.Kind switch
    {
        FieldKind.Date => Descending ? "Newest first" : "Oldest first",
        FieldKind.Number => Descending ? "Largest first" : "Smallest first",
        _ => Descending ? "Z → A" : "A → Z",
    };

    [RelayCommand]
    void ToggleDirection() => Descending = !Descending;
}

/// <summary>A view in the Views menu and the view chips.</summary>
public sealed partial class SavedViewItem(SavedView view) : ObservableObject
{
    public SavedView View { get; } = view;
    public string Name => View.Name;
    public bool IsBuiltIn => View.BuiltIn;

    [ObservableProperty]
    public partial int? Count { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}

/// <summary>A list's main action. Disabled with <paramref name="Tip"/> as the reason until it can run.</summary>
public sealed record ListPrimaryAction(string Label, string Icon, bool IsEnabled, string Tip, Action? Run = null);

public sealed record GroupOption(string? Key, string Label)
{
    public override string ToString() => Label;
}
