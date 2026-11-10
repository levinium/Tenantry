using Avalonia.Controls;
using Avalonia.VisualTree;
using Tenantry.ViewModels.Records;

namespace Tenantry.App.Views;

public partial class GroupLifecycleEditorView : UserControl
{
    public GroupLifecycleEditorView()
    {
        InitializeComponent();
        // Read when first shown (page or popup), not for every preview.
        AttachedToVisualTree += (_, _) => _ = (DataContext as GroupLifecycleEditor)?.LoadAsync();
        DataContextChanged += (_, _) =>
        {
            if (this.IsAttachedToVisualTree())
                _ = (DataContext as GroupLifecycleEditor)?.LoadAsync();
        };
    }
}
