using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tenantry.ViewModels.Editing;

namespace Tenantry.App.Views;

public partial class EditableFieldView : UserControl
{
    EditableField? _field;

    public EditableFieldView()
    {
        InitializeComponent();
        // Enter stages, Esc cancels.
        Editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_field is null)
                return;
            if (e.Key == Key.Enter)
            {
                _field.StageCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _field.CancelEditCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) =>
        {
            if (_field is not null)
                _field.PropertyChanged -= OnFieldChanged;
            _field = DataContext as EditableField;
            if (_field is not null)
                _field.PropertyChanged += OnFieldChanged;
        };
    }

    // The box takes focus, with its text selected, the moment editing starts.
    void OnFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditableField.IsEditing) && _field?.IsEditing == true)
            Dispatcher.UIThread.Post(() =>
            {
                Editor.Focus();
                Editor.SelectAll();
            }, DispatcherPriority.Input);
    }
}
