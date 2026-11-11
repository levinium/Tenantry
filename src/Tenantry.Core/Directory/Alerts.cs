namespace Tenantry.Core.Directory;

/// <summary>
/// An alert policy (Security &amp; Compliance's Get-ProtectionAlert): what it watches for, how severe
/// that is, whether it's on, and who is emailed. Microsoft's own (<see cref="IsSystem"/>) can be
/// switched off and have their recipients changed, nothing more.
/// </summary>
/// <param name="BeingRemoved">Removed, and Security &amp; Compliance hasn't finished (Mode PendingDeletion; it can take a while).</param>
public sealed record AlertPolicy(string Name, string Identity, string Severity, string Category, string? Description, bool Disabled, bool Emails,
    IReadOnlyList<string> NotifyUser, bool IsSystem, bool BeingRemoved = false)
{
    public static string CategoryText(string category) => category switch
    {
        "ThreatManagement" => "Threats",
        "MailFlow" => "Mail Flow",
        "AccessGovernance" => "Access",
        "DataGovernance" => "Data Governance",
        "DataLossPrevention" => "Data Loss Prevention",
        "PermissionsManagement" => "Permissions",
        "ComplianceManagerX" or "ComplianceManager" => "Compliance Manager",
        "InformationGovernance" => "Information Governance",
        "Others" => "Other",
        _ => category,
    };

    /// <summary>Microsoft's description without its version stamp (" -V1.0.0.1").</summary>
    public string? Explanation => Description is null ? null : System.Text.RegularExpressions.Regex.Replace(Description, @"\s*-V\d+(\.\d+)*\s*$", "").Trim();

    /// <summary>"TenantAdmins" is how Exchange says "every global admin"; addresses stay as they are.</summary>
    public string NotifyText => NotifyUser.Count == 0 ? "Nobody" : string.Join(", ", NotifyUser.Select(u => u.Equals("TenantAdmins", StringComparison.OrdinalIgnoreCase) ? "All global admins" : u));
}

/// <summary>An alert that fired (Graph security/alerts_v2): from Defender, Entra, Purview…</summary>
/// <param name="Status">"new", "inProgress", "resolved".</param>
/// <param name="Classification">"truePositive", "falsePositive", "informationalExpectedActivity", or null when not classified.</param>
public sealed record SecurityAlert(string Id, string Title, string Severity, string Status, string? Category, string? Source, DateTimeOffset? Created,
    DateTimeOffset? LastActivity, string? Description, string? AssignedTo, string? Classification, string? Determination, string? PortalUrl, IReadOnlyList<string> Involved)
{
    public bool IsResolved => Status.Equals("resolved", StringComparison.OrdinalIgnoreCase);

    public static string StatusText(string status) => status switch { "new" => "New", "inProgress" => "In Progress", "resolved" => "Resolved", _ => status };

    public static string SourceText(string? source) => source switch
    {
        "microsoftDefenderForOffice365" => "Defender for Office 365",
        "microsoftDefenderForEndpoint" => "Defender for Endpoint",
        "microsoftDefenderForIdentity" => "Defender for Identity",
        "microsoftDefenderForCloudApps" => "Defender for Cloud Apps",
        "azureAdIdentityProtection" or "microsoftEntraIdProtection" => "Entra ID Protection",
        "microsoftDataLossPrevention" => "Data Loss Prevention",
        "microsoft365Defender" => "Microsoft Defender XDR",
        "microsoftInsiderRiskManagement" => "Insider Risk",
        null or "" => "Microsoft",
        _ => source,
    };

    public static string ClassificationText(string? c) => c switch
    {
        "truePositive" => "Real Threat",
        "falsePositive" => "False Alarm",
        "informationalExpectedActivity" => "Expected Activity",
        _ => "Not Classified",
    };
}

/// <summary>Alert policies (Security &amp; Compliance) and the alerts that fired (Graph).</summary>
public interface IAlertRecords
{
    Task<IReadOnlyList<AlertPolicy>> GetAlertPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Alerts from the last 30 days, newest first.</summary>
    Task<IReadOnlyList<SecurityAlert>> GetAlertsAsync(CancellationToken cancellationToken = default);
}
