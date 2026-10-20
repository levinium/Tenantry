namespace Tenantry.Core.Capabilities;

/// <summary>
/// Entra built-in role template IDs (stable across tenants), checked against Microsoft's
/// permissions reference on 2026-10-20.
/// </summary>
public static class DirectoryRoles
{
    public const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    public const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";
    public const string UserAdministrator = "fe930be7-5e62-47db-91af-98c3a49a38b1";
    public const string ExchangeAdministrator = "29232cdf-9323-42fd-ade2-1d097af3e4de";
    public const string ExchangeRecipientAdministrator = "31392ffb-586c-42d1-9346-e59415a2cc4e";
    public const string SharePointAdministrator = "f28a1f50-f6e7-4571-818b-6a12f2af6b6c";
    public const string IntuneAdministrator = "3a2c62db-5318-420d-8d74-23affee5d9d5";
    public const string SecurityAdministrator = "194ae4cb-b126-40b2-bd5b-6091b380977d";
    public const string SecurityReader = "5d6b6bb7-de71-4623-b4af-96380a352509";
    public const string ConditionalAccessAdministrator = "b1be1c3e-b65d-4f19-8427-f6fa0d97feb9";
    public const string AuthenticationAdministrator = "c4e39bd9-1100-46d3-8c65-fb160da0071f";
    public const string PrivilegedAuthenticationAdministrator = "7be44c8a-adaf-4e2a-84d6-ab2649e08a13";
    public const string GroupsAdministrator = "fdd7a751-b60b-444a-984c-02652fe8fa1c";
    public const string ComplianceAdministrator = "17315797-102d-40b4-93e0-432062caca18";
    public const string ReportsReader = "4a5d8f65-41da-4de4-8968-e035b65339cf";
    public const string ApplicationAdministrator = "9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3";
    public const string CloudApplicationAdministrator = "158c047a-c907-4556-b7ef-446551a6b5f7";
    public const string PrivilegedRoleAdministrator = "e8611ab8-c189-46e8-94e1-60213ab1f814";
    public const string HelpdeskAdministrator = "729827e3-9c14-49f7-bb1b-9608f156bbb8";
    public const string TeamsAdministrator = "69091246-20e8-4a56-aa4d-066075b2a7a8";

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [GlobalAdministrator] = "Global Administrator",
        [GlobalReader] = "Global Reader",
        [UserAdministrator] = "User Administrator",
        [ExchangeAdministrator] = "Exchange Administrator",
        [ExchangeRecipientAdministrator] = "Exchange Recipient Administrator",
        [SharePointAdministrator] = "SharePoint Administrator",
        [IntuneAdministrator] = "Intune Administrator",
        [SecurityAdministrator] = "Security Administrator",
        [SecurityReader] = "Security Reader",
        [ConditionalAccessAdministrator] = "Conditional Access Administrator",
        [AuthenticationAdministrator] = "Authentication Administrator",
        [PrivilegedAuthenticationAdministrator] = "Privileged Authentication Administrator",
        [GroupsAdministrator] = "Groups Administrator",
        [ComplianceAdministrator] = "Compliance Administrator",
        [ReportsReader] = "Reports Reader",
        [ApplicationAdministrator] = "Application Administrator",
        [CloudApplicationAdministrator] = "Cloud Application Administrator",
        [PrivilegedRoleAdministrator] = "Privileged Role Administrator",
        [HelpdeskAdministrator] = "Helpdesk Administrator",
        [TeamsAdministrator] = "Teams Administrator",
    };

    public static string NameOf(string templateId) => Names.GetValueOrDefault(templateId, templateId);
}
