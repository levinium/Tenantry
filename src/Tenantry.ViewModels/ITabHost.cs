namespace Tenantry.ViewModels;

/// <summary>Opens and closes tabs in the main window (the rule editor opens in its own tab).</summary>
public interface ITabHost
{
    /// <summary>Switches to the first open tab <paramref name="existing"/> accepts, or adds the one <paramref name="create"/> makes.</summary>
    void OpenTab(Func<TabViewModel, bool> existing, Func<TabViewModel> create);

    void CloseTab(TabViewModel tab);
}
