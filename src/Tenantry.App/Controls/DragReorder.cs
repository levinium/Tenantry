using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Tenantry.App.Controls;

/// <summary>
/// Drag-to-reorder for an ItemsControl. Rows are dragged by an element with the "draghandle"
/// class, a visible grip, so the gesture is discoverable. While dragging, the row carries the
/// "dragging" class and an accent line ("dropabove" / "dropbelow" on a neighbour) shows where it
/// will land; the move happens once, on release. Moving rows live was avoided on purpose: the
/// collection change can recreate the row being held and drop the pointer capture mid-drag.
/// </summary>
public static class DragReorder
{
    /// <param name="move">Called on release with the dragged item and its new index.</param>
    public static void Attach(ItemsControl list, Action<object, int> move)
    {
        object? item = null;
        Control? row = null;
        Control? marked = null;
        var insertAt = -1;

        void Unmark()
        {
            marked?.Classes.Remove("dropabove");
            marked?.Classes.Remove("dropbelow");
            marked = null;
        }

        void Reset()
        {
            Unmark();
            row?.Classes.Remove("dragging");
            item = null;
            row = null;
            insertAt = -1;
        }

        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Visual source || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
                return;
            var handle = source as Control ?? source.FindAncestorOfType<Control>();
            while (handle is not null && !handle.Classes.Contains("draghandle"))
                handle = handle.GetVisualParent() as Control;
            if (handle is null)
                return;
            row = ContainerOf(list, handle);
            item = row?.DataContext;
            if (row is null || item is null)
            {
                Reset();
                return;
            }
            row.Classes.Add("dragging");
            e.Pointer.Capture(handle);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        list.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (item is null)
                return;
            var y = e.GetPosition(list).Y;
            var count = list.ItemCount;
            insertAt = count;
            for (var i = 0; i < count; i++)
            {
                if (list.ContainerFromIndex(i) is not { } container || container.TranslatePoint(default, list) is not { } top)
                    continue;
                if (y < top.Y + container.Bounds.Height / 2)
                {
                    insertAt = i;
                    break;
                }
            }
            Unmark();
            marked = insertAt < count ? list.ContainerFromIndex(insertAt) : list.ContainerFromIndex(count - 1);
            marked?.Classes.Add(insertAt < count ? "dropabove" : "dropbelow");
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (item is null)
                return;
            var dragged = item;
            var from = list.Items.Cast<object>().ToList().IndexOf(dragged);
            var target = insertAt < 0 ? from : insertAt > from ? insertAt - 1 : insertAt;
            e.Pointer.Capture(null);
            Reset();
            if (from >= 0 && target != from)
                move(dragged, target);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => Reset());
    }

    static Control? ContainerOf(ItemsControl list, Control inside)
    {
        Control? current = inside;
        while (current is not null)
        {
            if (list.IndexFromContainer(current) >= 0)
                return current;
            current = current.GetVisualParent() as Control;
        }
        return null;
    }
}
