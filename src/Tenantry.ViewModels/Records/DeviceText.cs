using System.Text.RegularExpressions;

namespace Tenantry.ViewModels.Records;

/// <summary>Intune's codes ("nonCompliant", "DefaultDeviceCompliancePolicy.RequireRemainContact") in plain words.</summary>
public static partial class DeviceText
{
    /// <summary>"nonCompliant" → "Non Compliant"; the last part of a dotted name only.</summary>
    public static string Words(string code)
    {
        var last = code[(code.LastIndexOf('.') + 1)..];
        var spaced = Boundary().Replace(last, " ");
        return spaced.Length == 0 ? code : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex Boundary();

    /// <summary>The built-in compliance checks every device gets, in words an admin recognises.</summary>
    static readonly Dictionary<string, string> KnownSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RequireRemainContact"] = "Has checked in with Intune recently",
        ["RequireDeviceCompliancePolicyAssigned"] = "Has a compliance policy assigned",
        ["RequireUserExistence"] = "Its enrolled user still exists",
        ["BitLockerEnabled"] = "BitLocker is on",
        ["SecureBootEnabled"] = "Secure Boot is on",
        ["CodeIntegrityEnabled"] = "Code integrity is on",
        ["StorageRequireEncryption"] = "Storage is encrypted",
        ["PasswordRequired"] = "A password is required",
        ["OsMinimumVersion"] = "Minimum OS version",
        ["DefenderEnabled"] = "Microsoft Defender is on",
        ["RtpEnabled"] = "Real-time protection is on",
        ["AntivirusRequired"] = "Antivirus is required",
        ["FirewallEnabled"] = "Firewall is on",
    };

    public static string SettingName(string code)
    {
        var last = code[(code.LastIndexOf('.') + 1)..];
        return KnownSettings.TryGetValue(last, out var known) ? known : Words(code);
    }

    /// <summary>A policy's or setting's state, as a badge.</summary>
    public static (string Text, BadgeKind Kind) State(string state) => state switch
    {
        "compliant" => ("Compliant", BadgeKind.Success),
        "remediated" => ("Fixed", BadgeKind.Success),
        "nonCompliant" => ("Not Compliant", BadgeKind.Danger),
        "error" => ("Error", BadgeKind.Danger),
        "conflict" => ("Conflict", BadgeKind.Warning),
        "inGracePeriod" => ("In Grace Period", BadgeKind.Warning),
        "notApplicable" => ("Not Applicable", BadgeKind.Neutral),
        "notAssigned" => ("Not Assigned", BadgeKind.Neutral),
        "unknown" or "" => ("Unknown", BadgeKind.Neutral),
        _ => (Words(state), BadgeKind.Neutral),
    };

    /// <summary>An assigned app's install state on the device.</summary>
    public static (string Text, BadgeKind Kind) InstallState(string state) => state switch
    {
        "installed" => ("Installed", BadgeKind.Success),
        "failed" => ("Failed", BadgeKind.Danger),
        "uninstallFailed" => ("Uninstall Failed", BadgeKind.Danger),
        "notInstalled" => ("Not Installed", BadgeKind.Warning),
        "pendingInstall" => ("Installing", BadgeKind.Info),
        "notApplicable" => ("Not Applicable", BadgeKind.Neutral),
        "unknown" or "" => ("Unknown", BadgeKind.Neutral),
        _ => (Words(state), BadgeKind.Neutral),
    };

    public static string Intent(string intent) => intent switch
    {
        "requiredInstall" or "requiredAndAvailableInstall" => "Required",
        "availableInstall" or "availableInstallWithoutEnrollment" => "Available",
        "requiredUninstall" => "Removed by Policy",
        "exclude" => "Excluded",
        "" => "",
        _ => Words(intent),
    };

    /// <summary>How it enrolled, in today's product names (Intune's codes still say "Azure AD").</summary>
    public static string Enrollment(string type) => type switch
    {
        "windowsAzureADJoin" => "Entra Join",
        "windowsBulkAzureDomainJoin" => "Bulk Entra Join",
        "windowsAzureADJoinUsingDeviceAuth" => "Entra Join (Device Authentication)",
        "windowsAutoEnrollment" => "Automatic Enrollment",
        "windowsCoManagement" => "Co-Management",
        "windowsBulkUserless" => "Bulk Enrollment",
        "userEnrollment" => "User Enrollment",
        "deviceEnrollmentManager" => "Device Enrollment Manager",
        "appleBulkWithUser" => "Automated Device Enrollment",
        "appleBulkWithoutUser" => "Automated Device Enrollment (No User)",
        "appleUserEnrollment" => "Apple User Enrollment",
        "appleUserEnrollmentWithServiceAccount" => "Apple User Enrollment",
        "azureAdJoinUsingAzureVmExtension" => "Entra Join (Azure VM)",
        "androidEnterpriseDedicatedDevice" => "Android Dedicated Device",
        "androidEnterpriseFullyManaged" => "Android Fully Managed",
        "androidEnterpriseCorporateWorkProfile" => "Android Corporate Work Profile",
        "unknown" => "Unknown",
        _ => Words(type),
    };

    /// <summary>"16 GB", "512 GB", "1.5 TB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.#} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024):0} KB",
    };
}
