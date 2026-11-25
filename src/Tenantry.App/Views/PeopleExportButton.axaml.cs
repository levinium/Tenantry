using Avalonia.Controls;
using Tenantry.ViewModels.Records;

namespace Tenantry.App.Views;

public partial class PeopleExportButton : UserControl
{
    public PeopleExportButton() => InitializeComponent();

    /// <summary>Reads the list again, so the choices and the nested tickbox match what's shown now.</summary>
    void OnOpening(object? sender, EventArgs e) => (DataContext as PeopleExporter)?.Refresh();
}
