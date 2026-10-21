using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Tenancy;

namespace Tenantry.ViewModels.Lists;

/// <summary>
/// List choices that belong to the person rather than to one list: every list follows them, so
/// opening the preview in Users leaves it open in Groups too, and they are saved for next time.
/// </summary>
public sealed partial class SharedListPreferences : ObservableObject
{
    readonly IAppPreferencesStore _store;

    public SharedListPreferences(IAppPreferencesStore store)
    {
        _store = store;
        IsPreviewOpen = store.Load().PreviewOpen;
    }

    [ObservableProperty]
    public partial bool IsPreviewOpen { get; set; }

    partial void OnIsPreviewOpenChanged(bool value) => _store.Save(_store.Load() with { PreviewOpen = value });
}
