using CommunityToolkit.Mvvm.ComponentModel;

namespace Tenantry.ViewModels;

/// <summary>One tab in the main window: a list page or a record page.</summary>
public abstract class TabViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>List pages stay open; record pages can be closed.</summary>
    public virtual bool CanClose => true;
}
