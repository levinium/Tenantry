using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tenantry.ViewModels.Records;

namespace Tenantry.App.Views;

public partial class SecuritySectionView : UserControl
{
    SecuritySection? _section;

    public SecuritySectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_section is not null)
                _section.PropertyChanged -= OnSectionChanged;
            _section = DataContext as SecuritySection;
            if (_section is not null)
                _section.PropertyChanged += OnSectionChanged;
        };
    }

    // A form opens with the cursor in its box, wherever it opened (under the buttons or under a row).
    void OnSectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SecuritySection.OpenedForm) && _section?.OpenedForm is { } form)
            Dispatcher.UIThread.Post(() =>
            {
                if (this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.DataContext == form && t.IsEffectivelyVisible) is not { } box)
                    return;
                box.Focus();
                box.CaretIndex = box.Text?.Length ?? 0;
            }, DispatcherPriority.Input);
    }
}
