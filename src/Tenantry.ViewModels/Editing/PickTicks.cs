using CommunityToolkit.Mvvm.ComponentModel;

namespace Tenantry.ViewModels.Editing;

/// <summary>One offered option in a picker's list, with its tick when several can be added.</summary>
public sealed partial class PickRow(PickOption option, bool isTicked) : ObservableObject
{
    public PickOption Option => option;

    [ObservableProperty]
    public partial bool IsTicked { get; set; } = isTicked;
}

/// <summary>
/// The ticks in an Add popup where several can be added: a click ticks or unticks a row, Shift
/// ticks (or unticks) every shown row from the last one clicked, and ticks stay while typing
/// narrows the list. Add then takes them all, in the order ticked.
/// </summary>
public sealed class PickTicks
{
    readonly List<PickOption> _ticked = [];
    PickRow? _anchor;

    public IReadOnlyList<PickOption> Ticked => _ticked;

    public bool Any => _ticked.Count > 0;

    public string AddText => _ticked.Count > 0 ? $"Add {_ticked.Count:N0}" : "Add";

    public string Text => _ticked.Count switch
    {
        0 => "Click names to tick them; Shift-click ticks a range.",
        1 => $"1 ticked: {_ticked[0].Name}",
        _ => $"{_ticked.Count:N0} ticked",
    };

    /// <summary>Rows for a fresh list of options, ticked where already chosen.</summary>
    public IReadOnlyList<PickRow> RowsFor(IEnumerable<PickOption> options)
    {
        var ticked = _ticked.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        _anchor = null;
        return options.Select(o => new PickRow(o, ticked.Contains(o.Id))).ToArray();
    }

    public void Click(IReadOnlyList<PickRow> rows, PickRow row, bool shift)
    {
        var tick = !row.IsTicked;
        if (shift && _anchor is not null && IndexOf(rows, _anchor) is >= 0 and var from && IndexOf(rows, row) is >= 0 and var to)
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                Set(rows[i], tick);
        else
            Set(row, tick);
        _anchor = row;
    }

    /// <summary>Everything ticked, in the order ticked; the ticks are cleared.</summary>
    public PickOption[] Take()
    {
        var chosen = _ticked.ToArray();
        _ticked.Clear();
        _anchor = null;
        return chosen;
    }

    public void Clear(IEnumerable<PickRow> rows)
    {
        _ticked.Clear();
        _anchor = null;
        foreach (var row in rows)
            row.IsTicked = false;
    }

    void Set(PickRow row, bool tick)
    {
        row.IsTicked = tick;
        var at = _ticked.FindIndex(o => o.Id == row.Option.Id);
        if (tick && at < 0)
            _ticked.Add(row.Option);
        else if (!tick && at >= 0)
            _ticked.RemoveAt(at);
    }

    static int IndexOf(IReadOnlyList<PickRow> rows, PickRow row)
    {
        for (var i = 0; i < rows.Count; i++)
            if (ReferenceEquals(rows[i], row))
                return i;
        return -1;
    }
}
