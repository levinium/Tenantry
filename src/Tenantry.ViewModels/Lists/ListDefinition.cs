using Tenantry.Core.Lists;

namespace Tenantry.ViewModels.Lists;

public enum CellKind
{
    /// <summary>Avatar, title and subtitle; first and frozen.</summary>
    Primary,
    Text,
    Badge,
    Badges,
    Date,
    Number,
}

/// <summary>
/// One column: which field it shows, how it draws, and how wide it may get. Fill in the
/// accessors the cell kind uses.
/// </summary>
public sealed class ColumnDef<T>
{
    public required string Key { get; init; }
    public required string Header { get; init; }
    public required CellKind Cell { get; init; }

    /// <summary>The field this column sorts and filters by; defaults to <see cref="Key"/>.</summary>
    public string? Field { get; init; }

    public double MinWidth { get; init; } = 120;
    public double? DefaultWidth { get; init; }
    public bool DefaultVisible { get; init; } = true;

    public Func<T, string?>? Text { get; init; }
    public Func<T, string?>? Subtitle { get; init; }
    public Func<T, string>? Initials { get; init; }
    public Func<T, (string Text, BadgeKind Kind)>? Badge { get; init; }
    public Func<T, IReadOnlyList<string>>? Badges { get; init; }
    public Func<T, DateTimeOffset?>? Date { get; init; }
    public Func<T, double?>? Number { get; init; }

    /// <summary>Shown when the value is empty ("Never", "Unlicensed").</summary>
    public string EmptyText { get; init; } = "";

    /// <summary>Warning tone for a date by age (null age = no date); for highlights like stale sign-ins.</summary>
    public Func<TimeSpan?, BadgeKind?>? AgeTone { get; init; }
}

/// <summary>A row action shown in the row's More menu and its right-click menu.</summary>
public sealed record RowAction<T>(string Label, string Icon, Action<T> Run);

/// <summary>
/// Everything that makes one list different from another. The framework does the rest:
/// filtering, sorting, grouping, views, export, persistence and the UI.
/// </summary>
public sealed class ListDefinition<T>
{
    /// <summary>Stable key for persisted state, for example "users".</summary>
    public required string Key { get; init; }

    public required string Title { get; init; }

    /// <summary>Plural noun for counts: "users".</summary>
    public required string Noun { get; init; }

    public required IReadOnlyList<Field<T>> Fields { get; init; }
    public required IReadOnlyList<ColumnDef<T>> Columns { get; init; }

    /// <summary>Built-in views, shown first; the first is the default.</summary>
    public IReadOnlyList<SavedView> Views { get; init; } = [];

    /// <summary>Stable identity, for keeping the selection across refreshes.</summary>
    public required Func<T, string> Id { get; init; }

    /// <summary>What the preview pane shows for a row (its record page view model works well).</summary>
    public Func<T, object>? Preview { get; init; }

    /// <summary>Extra row actions beyond Preview, Open and copying.</summary>
    public IReadOnlyList<RowAction<T>> Actions { get; init; } = [];

    public string SearchHint { get; init; } = "Search";
}
