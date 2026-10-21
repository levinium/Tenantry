using System.ComponentModel;

namespace Tenantry.ViewModels;

/// <summary>
/// The open tenant's sync as it happens: shown in the sidebar and in Settings, so both say the
/// same thing at the same moment.
/// </summary>
public interface ISyncStatus : INotifyPropertyChanged
{
    /// <summary>A sync (check for changes, Refresh, or Settings' buttons) is running.</summary>
    bool IsSyncing { get; }

    /// <summary>What it's doing now ("Reading mailboxes…", "Shared mailbox access: 5 of 13…").</summary>
    string? SyncStep { get; }

    /// <summary>The background read, if running ("Mailbox sizes: 120 of 604").</summary>
    string? BackgroundText { get; }
}
