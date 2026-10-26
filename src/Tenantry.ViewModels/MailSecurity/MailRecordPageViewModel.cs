namespace Tenantry.ViewModels.MailSecurity;

/// <summary>
/// A quarantined message or a block/allow entry as its own page (a tab): the same details and
/// actions as its preview, with room to read. The page owns its content and lets go when closed.
/// </summary>
/// <param name="Key">What the page shows ("quarantine:{identity}"), so opening it again switches to it.</param>
public sealed class MailRecordPageViewModel(string key, string title, object content) : TabViewModel, IDisposable
{
    public string Key { get; } = key;

    public override string Title { get; } = title;

    /// <summary>A <see cref="QuarantinePreviewViewModel"/> or a <see cref="ListEntryPreviewViewModel"/>.</summary>
    public object Content { get; } = content;

    public void Dispose() => (Content as IDisposable)?.Dispose();
}
