using System.Text.Json;

namespace Tenantry.Core.Directory;

/// <summary>
/// How often a tenant's local copy is brought up to date, set per tenant in Settings.
/// </summary>
/// <param name="ChangesMinutes">Check for changed users, groups and licenses while the tenant is
/// open; 0 checks only on opening it and on Refresh.</param>
/// <param name="DetailsMinutes">Re-read mailboxes, mailbox access, last sign-in times, domains
/// and Teams (each a full read).</param>
/// <param name="MailboxDetailsMinutes">Read every mailbox's automatic-reply state (one Exchange
/// call per mailbox, in the background); 0 turns it off. (Named before sizes had their own setting.)</param>
/// <param name="MailboxSizesMinutes">Read every mailbox's size (one call per mailbox, in the
/// background); 0 turns it off. Sizes change slowly, so less often than replies by default.</param>
public sealed record SyncSettings(int ChangesMinutes = 10, int DetailsMinutes = 60, int MailboxDetailsMinutes = 60, int MailboxSizesMinutes = 240)
{
    public static readonly SyncSettings Default = new();

    public bool RepliesOn => MailboxDetailsMinutes > 0;

    public bool SizesOn => MailboxSizesMinutes > 0;
}

public interface ISyncSettingsStore
{
    SyncSettings Load();
    void Save(SyncSettings settings);
}

public sealed class MemorySyncSettingsStore(SyncSettings? initial = null) : ISyncSettingsStore
{
    SyncSettings _settings = initial ?? SyncSettings.Default;
    public SyncSettings Load() => _settings;
    public void Save(SyncSettings settings) => _settings = settings;
}

/// <summary>Kept in the tenant's folder as sync.json; unreadable or missing means the defaults.</summary>
public sealed class JsonSyncSettingsStore(string tenantFolder) : ISyncSettingsStore
{
    string Path => System.IO.Path.Combine(tenantFolder, "sync.json");

    SyncSettings? _loaded;

    /// <summary>Read once, then kept: the sync asks for these on every check.</summary>
    public SyncSettings Load()
    {
        if (_loaded is not null)
            return _loaded;
        try
        {
            _loaded = File.Exists(Path) ? JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(Path)) ?? SyncSettings.Default : SyncSettings.Default;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _loaded = SyncSettings.Default;
        }
        return _loaded;
    }

    public void Save(SyncSettings settings)
    {
        System.IO.Directory.CreateDirectory(tenantFolder);
        File.WriteAllText(Path, JsonSerializer.Serialize(settings));
        _loaded = settings;
    }
}

/// <summary>One part of the local copy, for Settings: what it holds, when it was last read, how often.</summary>
public sealed record SyncArea(string Name, string Holds, DateTimeOffset? LastSyncedAt, string Every);
