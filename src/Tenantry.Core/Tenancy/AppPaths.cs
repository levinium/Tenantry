namespace Tenantry.Core.Tenancy;

/// <summary>
/// Where Tenantry keeps per-user data: tenant profiles, token caches and, later, each tenant's
/// local database. Always outside any repository. <c>TENANTRY_HOME</c> overrides the location,
/// which tests use to stay away from a developer's real profiles.
/// </summary>
public static class AppPaths
{
    public const string HomeVariable = "TENANTRY_HOME";

    public static string Root
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(HomeVariable);
            var root = string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tenantry")
                : overridden;
            System.IO.Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string TenantsFile => Path.Combine(Root, "tenants.json");

    /// <summary>Folder for one tenant profile's own files (token cache, database).</summary>
    public static string TenantFolder(string profileId)
    {
        var folder = Path.Combine(Root, "tenants", profileId);
        System.IO.Directory.CreateDirectory(folder);
        return folder;
    }
}
