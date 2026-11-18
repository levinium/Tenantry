using System.Reflection;
using Avalonia.Controls;

namespace Tenantry.App.Lists;

/// <summary>
/// Ticks or clears many rows of a grid at once. The grid's public way, one SelectedItems.Add or
/// Remove a row, searches the whole list for each row and compares the whole selection after each
/// one: ticking half of 33,000 devices took 18 minutes. Its own Select All works on ranges of rows
/// and compares once, so this uses the same internal steps for any set of rows. If a later grid
/// version renames them, <see cref="TrySet"/> says no and the caller goes one row at a time
/// (the speed check at scale would show it).
/// </summary>
static class GridSelection
{
    const BindingFlags Inside = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>While above zero the grid holds its SelectionChanged; back to zero raises it once.</summary>
    static readonly PropertyInfo? Hold = typeof(DataGrid).GetProperty("NoSelectionChangeCount", Inside);

    static readonly PropertyInfo? Changed = typeof(DataGrid).GetProperty("SelectionHasChanged", Inside);

    /// <summary>Selects or clears a range of slots (rows and group headers), redrawing the rows shown.</summary>
    static readonly MethodInfo? Slots = typeof(DataGrid).GetMethod("SelectSlots", Inside, [typeof(int), typeof(int), typeof(bool)]);

    static readonly MethodInfo? SlotOf = typeof(DataGrid).GetMethod("SlotFromRowIndex", Inside, [typeof(int)]);

    static bool Available => Hold is not null && Changed is not null && Slots is not null && SlotOf is not null;

    /// <summary>
    /// Sets these rows (positions in the grid's items, in the order shown) to ticked or not,
    /// each run of neighbours as one range, with one SelectionChanged at the end.
    /// </summary>
    public static bool TrySet(DataGrid grid, IEnumerable<int> rows, bool select)
    {
        if (!Available)
            return false;
        var sorted = rows.Order().ToArray();
        if (sorted.Length == 0)
            return true;
        Hold!.SetValue(grid, (int)Hold.GetValue(grid)! + 1);
        try
        {
            for (var start = 0; start < sorted.Length;)
            {
                var end = start;
                while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1)
                    end++;
                Slots!.Invoke(grid, [SlotOf!.Invoke(grid, [sorted[start]]), SlotOf.Invoke(grid, [sorted[end]]), select]);
                start = end + 1;
            }
            Changed!.SetValue(grid, true);
        }
        finally
        {
            Hold.SetValue(grid, (int)Hold.GetValue(grid)! - 1);
        }
        return true;
    }
}
