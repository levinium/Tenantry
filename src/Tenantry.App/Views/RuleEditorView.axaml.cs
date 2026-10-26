using Avalonia.Controls;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.App.Views;

public partial class RuleEditorView : UserControl
{
    RuleEditorViewModel? _editor;

    public RuleEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Follow(DataContext as RuleEditorViewModel);
    }

    /// <summary>Choosing a part from a dropdown adds it and closes that dropdown.</summary>
    void Follow(RuleEditorViewModel? editor)
    {
        if (_editor is not null)
        {
            _editor.ConditionPicker.Chosen -= CloseConditions;
            _editor.ExceptionPicker.Chosen -= CloseExceptions;
            _editor.ActionPicker.Chosen -= CloseActions;
        }
        _editor = editor;
        if (editor is null)
            return;
        editor.ConditionPicker.Chosen += CloseConditions;
        editor.ExceptionPicker.Chosen += CloseExceptions;
        editor.ActionPicker.Chosen += CloseActions;
    }

    void CloseConditions(object? sender, EventArgs e) => AddCondition.Flyout?.Hide();
    void CloseExceptions(object? sender, EventArgs e) => AddException.Flyout?.Hide();
    void CloseActions(object? sender, EventArgs e) => AddAction.Flyout?.Hide();
}
