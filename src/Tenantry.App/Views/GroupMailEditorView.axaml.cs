using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Tenantry.ViewModels.Records;

namespace Tenantry.App.Views;

public partial class GroupMailEditorView : UserControl
{
    public GroupMailEditorView()
    {
        InitializeComponent();
        // The settings are read from Exchange when first shown (page or popup), not for every preview.
        AttachedToVisualTree += (_, _) => _ = (DataContext as GroupMailEditor)?.LoadAsync();
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree())
                _ = (DataContext as GroupMailEditor)?.LoadAsync();
        };
    }
}
