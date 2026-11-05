using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tenantry.ViewModels.Editing;

namespace Tenantry.App.Views;

/// <summary>
/// "Add …" as a button. Its popup searches for what to add (a <see cref="PersonPicker"/>), puts
/// the cursor in the search box, and closes once something is chosen.
/// </summary>
public partial class AddButton : UserControl
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<AddButton, string>(nameof(Text), "Add");

    /// <summary>The button's words ("Add to Group").</summary>
    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly StyledProperty<string> HintProperty =
        AvaloniaProperty.Register<AddButton, string>(nameof(Hint), "Choose from the list, or type to narrow it. It goes to Pending Changes.");

    /// <summary>One line above the popup's search box.</summary>
    public string Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public static readonly StyledProperty<bool> MultipleProperty = AvaloniaProperty.Register<AddButton, bool>(nameof(Multiple), true);

    /// <summary>Several can be ticked and added at once (the default); off where only one makes sense.</summary>
    public bool Multiple
    {
        get => GetValue(MultipleProperty);
        set => SetValue(MultipleProperty, value);
    }

    PersonPicker? _picker;

    public AddButton()
    {
        InitializeComponent();
        var flyout = (Flyout)Open.Flyout!;
        CancelButton.Click += (_, _) => flyout.Hide();
        flyout.Opened += (_, _) =>
        {
            if (_picker is not null)
                _picker.Multiple = Multiple;
            // The list shows at once (what's already there is left out, so it's read fresh each time).
            _picker?.Browse();
            Dispatcher.UIThread.Post(() =>
                (flyout.Content as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(), DispatcherPriority.Input);
        };
        flyout.Closed += (_, _) =>
        {
            if (_picker is not null)
            {
                _picker.ClearTicks();
                _picker.Query = "";
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_picker is not null)
            _picker.Picked -= OnPicked;
        _picker = DataContext as PersonPicker;
        if (_picker is not null)
            _picker.Picked += OnPicked;
    }

    void OnPicked(object? sender, EventArgs e) => Open.Flyout?.Hide();
}
