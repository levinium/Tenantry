using CommunityToolkit.Mvvm.ComponentModel;
using Tenantry.Core.Directory;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

/// <summary>Settings: saving how often each part syncs, and showing a sync that's running.</summary>
public sealed partial class SettingsTests
{
    sealed partial class Status : ObservableObject, ISyncStatus
    {
        [ObservableProperty]
        public partial bool IsSyncing { get; set; }

        [ObservableProperty]
        public partial string? SyncStep { get; set; }

        [ObservableProperty]
        public partial string? BackgroundText { get; set; }
    }

    sealed class Sync : ITenantSync
    {
        public DateTimeOffset? LastSyncedAt => null;
        public bool IsPersistent => true;
        public IReadOnlyList<string> Notes => [];
        public int AreaReads;
        public IReadOnlyList<SyncArea> Areas
        {
            get
            {
                AreaReads++;
                return [new SyncArea("Users", "Everyone", null, "Every 10 minutes")];
            }
        }
        public Task<int> SyncAsync(IProgress<string>? progress = null, SyncScope scope = SyncScope.Changes, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    [Fact]
    public void Choices_save_at_once_and_tell_the_app()
    {
        var store = new MemorySyncSettingsStore();
        var changed = 0;
        var settings = new SettingsViewModel("Contoso", store, new Sync(), _ => Task.CompletedTask, () => changed++);

        settings.Changes = SettingsViewModel.ChangesOptions.Single(o => o.Minutes == 0);
        settings.MailboxDetails = SettingsViewModel.MailboxDetailsOptions.Single(o => o.Label == "Off");

        Assert.Equal(new SyncSettings(0, 60, 0, 240), store.Load());
        Assert.Equal(2, changed);
    }

    [Fact]
    public void A_sync_already_running_shows_and_holds_the_buttons_until_it_ends()
    {
        var status = new Status { IsSyncing = true, SyncStep = "Reading mailboxes…", BackgroundText = "Mailbox sizes: 120 of 604" };
        var sync = new Sync();
        var settings = new SettingsViewModel("Contoso", new MemorySyncSettingsStore(), sync, _ => Task.CompletedTask, () => { }, status: status);

        Assert.Equal("Updating now: Reading mailboxes…", settings.LiveText);
        Assert.Equal("Mailbox sizes: 120 of 604", settings.BackgroundText);
        Assert.False(settings.SyncEverythingCommand.CanExecute(null));

        status.SyncStep = "Shared mailbox access: 5 of 13…";
        Assert.Equal("Updating now: Shared mailbox access: 5 of 13…", settings.LiveText);

        var reads = sync.AreaReads;
        status.IsSyncing = false;
        Assert.Null(settings.LiveText);
        Assert.True(settings.SyncEverythingCommand.CanExecute(null));
        Assert.True(sync.AreaReads > reads);

        // Closed: it stops listening.
        settings.Dispose();
        var notified = 0;
        settings.PropertyChanged += (_, _) => notified++;
        status.IsSyncing = true;
        Assert.Equal(0, notified);
    }
}
