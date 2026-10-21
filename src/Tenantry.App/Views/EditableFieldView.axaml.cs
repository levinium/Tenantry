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
        // A choice goes to Pending changes when the list closes; closing it untouched ends the edit.
        ChoiceEditor.DropDownClosed += (_, _) => _field?.StageCommand.Execute(null);

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
                if (_field?.HasChoices == true)
                {
                    // The list opens straight away: one click to choose.
                    ChoiceEditor.Focus();
                    ChoiceEditor.IsDropDownOpen = true;
                    return;
                }
                Editor.Focus();
                Editor.SelectAll();
            }, DispatcherPriority.Input);
    }
}
