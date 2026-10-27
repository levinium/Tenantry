namespace Tenantry.Core.Directory;

/// <summary>How a site setting is sent to SharePoint (its CSOM type on SiteProperties).</summary>
public enum SitePropertyType
{
    Enum,
    Int32,
    Int64,
    Boolean,
    String,
}

/// <summary>
/// The site settings Tenantry changes, by the names SharePoint's SiteProperties uses (Set-SPOSite's
/// parameters), and how their values read.
/// </summary>
public static class SiteSettings
{
    public const string Sharing = "SharingCapability";
    public const string LinkType = "DefaultSharingLinkType";
    public const string LinkPermission = "DefaultLinkPermission";
    public const string GuestExpiryDays = "ExternalUserExpirationInDays";
    public const string GuestExpiryOverride = "OverrideTenantExternalUserExpirationPolicy";
    public const string Quota = "StorageMaximumLevel";
    public const string QuotaWarning = "StorageWarningLevel";
    public const string Lock = "LockState";

    public static SitePropertyType TypeOf(string property) => property switch
    {
        Sharing or LinkType or LinkPermission => SitePropertyType.Enum,
        GuestExpiryDays => SitePropertyType.Int32,
        Quota or QuotaWarning => SitePropertyType.Int64,
        GuestExpiryOverride => SitePropertyType.Boolean,
        _ => SitePropertyType.String,
    };

    /// <summary>DefaultSharingLinkType: 0 the organization's default, 1 specific people, 2 the organization, 3 anyone.</summary>
    public static string LinkTypeName(int value) => value switch
    {
        1 => "Specific People",
        2 => "People in the Organization",
        3 => "Anyone With the Link",
        _ => "The Organization's Default",
    };

    /// <summary>DefaultLinkPermission: 0 the organization's default, 1 view, 2 edit.</summary>
    public static string LinkPermissionName(int value) => value switch
    {
        1 => "View",
        2 => "Edit",
        _ => "The Organization's Default",
    };

    public static string GuestExpiryName(int? days) => days is { } d ? $"After {d:N0} Days" : "The Organization's Setting";

    /// <summary>Megabytes as SharePoint counts them, in gigabytes or terabytes.</summary>
    public static string Storage(long mb) => mb >= 1024L * 1024 ? $"{mb / (1024.0 * 1024):0.##} TB" : $"{mb / 1024.0:0.##} GB";
}
