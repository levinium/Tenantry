using Avalonia.Controls;

namespace Tenantry.App.Views;

public partial class RulePartPickerView : UserControl
{
    public RulePartPickerView()
    {
        InitializeComponent();
        // The dropdown opens ready to search.
        AttachedToVisualTree += (_, _) => SearchBox.Focus();
    }
}
