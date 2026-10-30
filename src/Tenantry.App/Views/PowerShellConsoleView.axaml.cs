using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tenantry.ViewModels;

namespace Tenantry.App.Views;

/// <summary>
/// The console's keys: Enter runs (Shift+Enter adds a line), Tab completes (Shift+Tab goes back
/// through the matches), Up and Down bring back earlier commands from the first or last line.
/// The output follows new lines unless the admin has scrolled up to read.
/// </summary>
public partial class PowerShellConsoleView : UserControl
{
    PowerShellConsoleViewModel? _model;
    bool _following = true;

    public PowerShellConsoleView()
    {
        InitializeComponent();
        CommandBox.AddHandler(KeyDownEvent, OnCommandKey, RoutingStrategies.Tunnel);
        OutputScroller.ScrollChanged += (_, e) =>
        {
            // A change in extent is new output: follow it if we were at the bottom.
            if (e.ExtentDelta.Y != 0 && _following)
                OutputScroller.ScrollToEnd();
            else if (e.OffsetDelta.Y != 0)
                _following = OutputScroller.Offset.Y >= OutputScroller.Extent.Height - OutputScroller.Viewport.Height - 24;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
            _model.FocusRequested -= OnFocusRequested;
        _model = DataContext as PowerShellConsoleViewModel;
        if (_model is not null)
            _model.FocusRequested += OnFocusRequested;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => CommandBox.Focus(), DispatcherPriority.Background);
    }

    void OnFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        CommandBox.Focus();
        CommandBox.CaretIndex = CommandBox.Text?.Length ?? 0;
    }, DispatcherPriority.Background);

    async void OnCommandKey(object? sender, KeyEventArgs e)
    {
        if (_model is null)
            return;
        var text = CommandBox.Text ?? "";
        var caret = CommandBox.CaretIndex;
        switch (e.Key)
        {
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                if (_model.RunCommand.CanExecute(null))
                {
                    _following = true;
                    await _model.RunCommand.ExecuteAsync(null);
                }
                return;
            case Key.Tab:
                e.Handled = true;
                if (await _model.CompleteAsync(text, caret, backwards: e.KeyModifiers.HasFlag(KeyModifiers.Shift)) is { } done)
                {
                    CommandBox.Text = done.Text;
                    CommandBox.CaretIndex = done.Cursor;
                }
                return;
            case Key.Up when !text[..caret].Contains('\n'):
                if (_model.HistoryBack() is { } older)
                {
                    e.Handled = true;
                    _model.Input = older;
                    CommandBox.CaretIndex = older.Length;
                }
                return;
            case Key.Down when !text[caret..].Contains('\n'):
                if (_model.HistoryForward() is { } newer)
                {
                    e.Handled = true;
                    _model.Input = newer;
                    CommandBox.CaretIndex = newer.Length;
                }
                return;
        }
    }
}
