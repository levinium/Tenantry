using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Tenantry.App.Controls;

/// <summary>
/// Drag-to-reorder for an ItemsControl. Rows are dragged by an element with the "draghandle"
/// class, a visible grip, so the gesture is discoverable.
///
/// While dragging, a lifted copy of the row (class "dragghost": shadow and accent outline)
/// follows the pointer. The row's own slot stays behind as a tinted gap (class
/// "dragslot") and the other rows slide aside to show where it will land. On release the copy
/// glides into the gap and only then is the move committed, so nothing jumps.
///
/// Rows are moved with render transforms only; the collection changes once, at the end. Moving
/// items live would recreate the row being held and drop the pointer capture mid-drag.
/// </summary>
public static class DragReorder
{
    /// <param name="move">Called on release with the dragged item and its new index.</param>
    /// <param name="fixedLeading">Leading rows that always stay first; nothing can land above them.</param>
    public static void Attach(ItemsControl list, Action<object, int> move, int fixedLeading = 0)
    {
        DragSession? session = null;
        var settling = false;

        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (session is not null || settling || e.Source is not Visual source
                || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
                return;
            var handle = source as Control ?? source.FindAncestorOfType<Control>();
            while (handle is not null && !handle.Classes.Contains("draghandle"))
                handle = handle.GetVisualParent() as Control;
            if (handle is null || ContainerOf(list, handle) is not { DataContext: { } item } row)
                return;
            session = new DragSession(list, row, item, fixedLeading, e.GetPosition(list));
            e.Pointer.Capture(handle);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        list.AddHandler(InputElement.PointerMovedEvent, (_, e) => session?.Move(e),
            RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (session is not { } ending)
                return;
            // Clear the session first: releasing capture raises PointerCaptureLost, which cancels.
            session = null;
            settling = true;
            e.Pointer.Capture(null);
            ending.Drop((item, index) =>
            {
                settling = false;
                if (index >= 0)
                    move(item, index);
            });
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) =>
        {
            session?.Cancel();
            session = null;
        });
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

    sealed class DragSession(ItemsControl list, Control row, object item, int fixedLeading, Point pressedAt)
    {
        static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(170);
        static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);
        const double StartDistance = 4;
        const double EdgeZone = 32;

        readonly int _from = list.IndexFromContainer(row);
        readonly ScrollViewer? _scroller = list.FindAncestorOfType<ScrollViewer>();
        bool _started;
        int _target;
        double[] _tops = [];
        double[] _heights = [];
        Control?[] _containers = [];
        double _step;
        double _grabOffset;
        Border? _ghost;
        Canvas? _overlay;
        Visual? _content;
        double _contentOpacity = 1;
        Point _lastPointer;
        DispatcherTimer? _autoScroll;
        double _scrollSpeed;

        public void Move(PointerEventArgs e)
        {
            if (!_started)
            {
                var delta = e.GetPosition(list) - pressedAt;
                if (Math.Abs(delta.X) < StartDistance && Math.Abs(delta.Y) < StartDistance)
                    return;
                Start();
            }
            if (_overlay is null)
                return;
            _lastPointer = e.GetPosition(_overlay);
            Track();
        }

        void Start()
        {
            _started = true;
            _target = _from;
            var count = list.ItemCount;
            _tops = new double[count];
            _heights = new double[count];
            _containers = new Control?[count];
            for (var i = 0; i < count; i++)
            {
                var container = list.ContainerFromIndex(i);
                _containers[i] = container;
                if (container?.TranslatePoint(default, list) is { } top)
                {
                    _tops[i] = top.Y;
                    _heights[i] = container.Bounds.Height;
                }
            }
            // How far the other rows move to make room: the dragged row's height plus the gap
            // the panel puts between rows.
            _step = count < 2 ? _heights[_from]
                : _from + 1 < count ? _tops[_from + 1] - _tops[_from]
                : _tops[_from] - _tops[_from - 1] - _heights[_from - 1] + _heights[_from];
            _grabOffset = pressedAt.Y - _tops[_from];

            _overlay = OverlayLayer.GetOverlayLayer(list);
            if (_overlay is null)
                return;

            // The ghost is a live copy of the row built from the list's own item template, so it
            // renders crisply at any display scaling. (A bitmap snapshot came out magnified on
            // scaled displays.)
            _ghost = new Border
            {
                Classes = { "dragghost" },
                Width = row.Bounds.Width,
                Height = row.Bounds.Height,
                Child = new ContentControl { Content = item, ContentTemplate = list.ItemTemplate },
            };
            var origin = row.TranslatePoint(default, _overlay) ?? default;
            Canvas.SetLeft(_ghost, origin.X);
            Canvas.SetTop(_ghost, origin.Y);
            _overlay.Children.Add(_ghost);

            _content = row.GetVisualChildren().FirstOrDefault();
            if (_content is not null)
            {
                _contentOpacity = _content.Opacity;
                _content.Opacity = 0;
            }
            row.Classes.Add("dragslot");
            foreach (var container in _containers)
            {
                if (container is not null)
                    container.Transitions = [new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Slide, Easing = new CubicEaseOut() }];
            }
        }

        /// <summary>Places the ghost under the pointer and works out where the row would land.</summary>
        void Track()
        {
            if (_overlay is null || _ghost is null || list.TranslatePoint(default, _overlay) is not { } listOrigin)
                return;
            var height = _heights[_from];
            var last = _tops.Length - 1;

            // The ghost moves only vertically, stays within the list and within the visible part
            // of the scrolling panel.
            var top = _lastPointer.Y - listOrigin.Y - _grabOffset;
            top = Math.Clamp(top, _tops[Math.Min(fixedLeading, last)] - 6, _tops[last] + _heights[last] - height + 6);
            var ghostTop = listOrigin.Y + top;
            if (_scroller?.TranslatePoint(default, _overlay) is { } view)
                ghostTop = Math.Clamp(ghostTop, view.Y - 6, Math.Max(view.Y - 6, view.Y + _scroller.Bounds.Height - height + 6));
            Canvas.SetTop(_ghost, ghostTop);

            var centre = ghostTop - listOrigin.Y + height / 2;
            var target = 0;
            for (var j = 0; j < _tops.Length; j++)
            {
                if (j != _from && _tops[j] + _heights[j] / 2 < centre)
                    target++;
            }
            target = Math.Max(target, fixedLeading);
            if (target != _target)
            {
                _target = target;
                Shift();
            }
            UpdateAutoScroll();
        }

        void Shift()
        {
            for (var j = 0; j < _containers.Length; j++)
            {
                if (_containers[j] is not { } container)
                    continue;
                var offset = j == _from ? SlotOffset()
                    : _from < _target && j > _from && j <= _target ? -_step
                    : _target < _from && j >= _target && j < _from ? _step
                    : 0;
                container.RenderTransform = TranslateY(offset);
            }
        }

        /// <summary>How far the gap sits from the row's original place.</summary>
        double SlotOffset() => _target > _from
            ? _tops[_target] + _heights[_target] - _heights[_from] - _tops[_from]
            : _tops[_target] - _tops[_from];

        /// <summary>Scrolls the panel while the pointer is near its top or bottom edge.</summary>
        void UpdateAutoScroll()
        {
            _scrollSpeed = 0;
            if (_scroller is not null && _overlay is not null && _scroller.TranslatePoint(default, _overlay) is { } view)
            {
                var y = _lastPointer.Y - view.Y;
                var bottom = _scroller.Bounds.Height;
                if (y < EdgeZone)
                    _scrollSpeed = -Math.Clamp((EdgeZone - y) / 2, 2, 16);
                else if (y > bottom - EdgeZone)
                    _scrollSpeed = Math.Clamp((y - (bottom - EdgeZone)) / 2, 2, 16);
            }
            if (_scrollSpeed == 0)
            {
                _autoScroll?.Stop();
                return;
            }
            _autoScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
            {
                if (_scroller is null || _scrollSpeed == 0)
                    return;
                var max = Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height);
                var next = Math.Clamp(_scroller.Offset.Y + _scrollSpeed, 0, max);
                if (next == _scroller.Offset.Y)
                    return;
                _scroller.Offset = _scroller.Offset.WithY(next);
                Track();
            });
            _autoScroll.Start();
        }

        /// <summary>Glides the ghost into the gap, then reports the move.</summary>
        public void Drop(Action<object, int> done)
        {
            _autoScroll?.Stop();
            if (!_started || _ghost is null || _overlay is null || list.TranslatePoint(default, _overlay) is not { } listOrigin)
            {
                Restore();
                done(item, _started && _target != _from ? _target : -1);
                return;
            }
            _ghost.Transitions = [new DoubleTransition { Property = Canvas.TopProperty, Duration = Settle, Easing = new CubicEaseOut() }];
            Canvas.SetTop(_ghost, listOrigin.Y + _tops[_from] + SlotOffset());
            DispatcherTimer.RunOnce(() =>
            {
                Restore();
                done(item, _target != _from ? _target : -1);
            }, Settle + TimeSpan.FromMilliseconds(10));
        }

        public void Cancel()
        {
            _autoScroll?.Stop();
            Restore();
        }

        /// <summary>Removes the ghost and puts every row back, with no animation.</summary>
        void Restore()
        {
            if (_ghost is not null)
                _overlay?.Children.Remove(_ghost);
            _ghost = null;
            foreach (var container in _containers)
            {
                if (container is null)
                    continue;
                container.Transitions = null;
                container.RenderTransform = null;
            }
            row.Classes.Remove("dragslot");
            if (_content is not null)
                _content.Opacity = _contentOpacity;
        }

        static TransformOperations TranslateY(double y)
        {
            var builder = TransformOperations.CreateBuilder(1);
            builder.AppendTranslate(0, y);
            return builder.Build();
        }
    }
}
