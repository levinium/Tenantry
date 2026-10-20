using System.Security.Cryptography;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Tenantry.Auth;

/// <summary>
/// The key for a tenant's local database, kept in the OS secret store the token cache uses:
/// DPAPI on Windows (a file only this Windows account can decrypt), the login Keychain on macOS,
/// the Secret Service keyring on Linux. One random 256-bit key per tenant profile.
/// </summary>
public static class CacheKeyStore
{
    const int KeyBytes = 32;

    /// <summary>The profile's key, created on first use; null when no secret store is usable.</summary>
    public static byte[]? TryGetOrCreate(string profileId, string profileFolder)
    {
        try
        {
            var storage = Storage.Create(Properties(profileId, profileFolder));
            storage.VerifyPersistence();
            var key = storage.ReadData();
            if (key.Length == KeyBytes)
                return key;
            key = RandomNumberGenerator.GetBytes(KeyBytes);
            storage.WriteData(key);
            return key;
        }
        catch (Exception e) when (e is MsalCachePersistenceException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Forgets the profile's key (the database it opened becomes unreadable noise).</summary>
    public static void Delete(string profileId, string profileFolder)
    {
        try
        {
            Storage.Create(Properties(profileId, profileFolder)).Clear(ignoreExceptions: true);
        }
        catch (Exception e) when (e is MsalCachePersistenceException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Nothing stored, or no secret store: nothing to forget.
        }
    }

    static StorageCreationProperties Properties(string profileId, string profileFolder) =>
        new StorageCreationPropertiesBuilder("cache.key", profileFolder)
            .WithMacKeyChain("Tenantry.CacheKey", profileId)
            .WithLinuxKeyring("org.tenantry.cachekey", MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "Tenantry local database key", new KeyValuePair<string, string>("Product", "Tenantry"),
                new KeyValuePair<string, string>("Profile", profileId))
            .Build();
}
