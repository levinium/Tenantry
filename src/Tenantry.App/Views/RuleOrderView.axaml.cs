using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tenantry.App.Controls;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.App.Views;

public partial class RuleOrderView : UserControl
{
    bool _shift;

    public RuleOrderView()
    {
        InitializeComponent();
        DragReorder.Attach(RuleList, (item, index) =>
        {
            if (item is RuleOrderRow row && DataContext is RuleOrderViewModel vm)
                vm.Drop(row, index);
        });
        // Click events carry no key state, so Shift is noted as the press goes down.
        RuleList.AddHandler(PointerPressedEvent, (_, e) => _shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel);
        RuleList.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is CheckBox { Tag: RuleOrderRow row } box && DataContext is RuleOrderViewModel vm)
            {
                vm.Tick(row, _shift);
                // The box shows the row's state (one-way), which the ranges may have set differently.
                box.IsChecked = row.IsTicked;
                e.Handled = true;
            }
            _shift = false;
        });
    }
}
