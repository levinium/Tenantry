using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Tenantry.App.Controls;

/// <summary>
/// Edits that take effect without a button: on a text box, Enter (unless it takes new lines) or
/// leaving the box runs <c>Commit</c>, and Esc runs <c>Cancel</c>. With <c>EnterUpdates</c>, Enter
/// also pushes the text to a binding that otherwise updates only when the box loses focus. With
/// <c>Idle</c>, a pause in typing (<see cref="IdleDelay"/>) pushes the text and runs <c>IdleCommit</c>.
/// </summary>
public static class EditCommit
{
    public static readonly AttachedProperty<ICommand?> CommitProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("Commit", typeof(EditCommit));

    public static readonly AttachedProperty<ICommand?> CancelProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("Cancel", typeof(EditCommit));

    public static readonly AttachedProperty<bool> EnterUpdatesProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("EnterUpdates", typeof(EditCommit));

    public static readonly AttachedProperty<bool> IdleProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Idle", typeof(EditCommit));

    public static readonly AttachedProperty<ICommand?> IdleCommitProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("IdleCommit", typeof(EditCommit));

    /// <summary>How long a pause in typing counts as done.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);

    static readonly AttachedProperty<DispatcherTimer?> TimerProperty =
        AvaloniaProperty.RegisterAttached<TextBox, DispatcherTimer?>("Timer", typeof(EditCommit));

    static readonly AttachedProperty<bool> HookedProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("Hooked", typeof(EditCommit));

    public static ICommand? GetCommit(TextBox box) => box.GetValue(CommitProperty);
    public static void SetCommit(TextBox box, ICommand? value) => box.SetValue(CommitProperty, value);
    public static ICommand? GetCancel(TextBox box) => box.GetValue(CancelProperty);
    public static void SetCancel(TextBox box, ICommand? value) => box.SetValue(CancelProperty, value);
    public static bool GetEnterUpdates(TextBox box) => box.GetValue(EnterUpdatesProperty);
    public static void SetEnterUpdates(TextBox box, bool value) => box.SetValue(EnterUpdatesProperty, value);
    public static bool GetIdle(TextBox box) => box.GetValue(IdleProperty);
    public static void SetIdle(TextBox box, bool value) => box.SetValue(IdleProperty, value);
    public static ICommand? GetIdleCommit(TextBox box) => box.GetValue(IdleCommitProperty);
    public static void SetIdleCommit(TextBox box, ICommand? value) => box.SetValue(IdleCommitProperty, value);

    static EditCommit()
    {
        CommitProperty.Changed.AddClassHandler<TextBox>((box, _) => Hook(box));
        CancelProperty.Changed.AddClassHandler<TextBox>((box, _) => Hook(box));
        EnterUpdatesProperty.Changed.AddClassHandler<TextBox>((box, _) => Hook(box));
        IdleProperty.Changed.AddClassHandler<TextBox>((box, _) => Hook(box));
    }

    static void Hook(TextBox box)
    {
        if (box.GetValue(HookedProperty))
            return;
        box.SetValue(HookedProperty, true);
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        box.LostFocus += (_, _) =>
        {
            StopTimer(box);
            Run(GetCommit(box));
        };
        // Only typing restarts the wait: text set from the view model while the box isn't focused doesn't.
        box.TextChanged += (_, _) =>
        {
            if (GetIdle(box) && box.IsFocused)
                RestartTimer(box);
        };
        box.DetachedFromVisualTree += (_, _) => StopTimer(box);
    }

    static void RestartTimer(TextBox box)
    {
        var timer = box.GetValue(TimerProperty);
        if (timer is null)
        {
            timer = new DispatcherTimer { Interval = IdleDelay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                PushText(box);
                Run(GetIdleCommit(box));
            };
            box.SetValue(TimerProperty, timer);
        }
        timer.Stop();
        timer.Start();
    }

    static void StopTimer(TextBox box) => box.GetValue(TimerProperty)?.Stop();

    static void PushText(TextBox box) => BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();

    static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (e.Key == Key.Enter && !box.AcceptsReturn)
        {
            StopTimer(box);
            if (GetEnterUpdates(box))
                PushText(box);
            Run(GetCommit(box));
            e.Handled = GetCommit(box) is not null || GetEnterUpdates(box);
        }
        else if (e.Key == Key.Escape && GetCancel(box) is { } cancel)
        {
            StopTimer(box);
            Run(cancel);
            e.Handled = true;
        }
    }

    static void Run(ICommand? command)
    {
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }
}
