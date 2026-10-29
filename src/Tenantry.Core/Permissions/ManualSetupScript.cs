using System.Text;

namespace Tenantry.Core.Permissions;

/// <summary>
/// The guided manual setup: a Microsoft Graph PowerShell script that does exactly what the
/// automatic wizard does, for admins who would rather run (and read) it themselves. Generated
/// from <see cref="TenantryPermissions"/>, so it can't drift from the automatic path.
/// </summary>
public static class ManualSetupScript
{
    public const string PortalAppRegistrations =
        "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade";

    public static string Generate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Tenantry: create the app registration in your tenant.");
        sb.AppendLine("# Needs the Microsoft.Graph PowerShell module (Install-Module Microsoft.Graph -Scope CurrentUser)");
        sb.AppendLine("# and Cloud Application Administrator or higher. Safe to run again: it updates an existing");
        sb.AppendLine("# Tenantry registration instead of creating a second one.");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Connect-MgGraph -Scopes 'Application.ReadWrite.All','DelegatedPermissionGrant.ReadWrite.All','AppRoleAssignment.ReadWrite.All' -NoWelcome");
        sb.AppendLine();
        sb.AppendLine("$wanted = [ordered]@{");
        foreach (var api in TenantryPermissions.All)
            sb.AppendLine($"    '{api.ResourceAppId}' = @({string.Join(", ", api.DelegatedScopes.Select(s => $"'{s}'"))})");
        sb.AppendLine("}");
        sb.AppendLine();
        // Microsoft's service principals some APIs need may not exist in a tenant yet (Microsoft's
        // own instructions add them the same way).
        foreach (var api in TenantryPermissions.All.Where(a => a.CreateIfMissing))
            sb.AppendLine($"if (-not (Get-MgServicePrincipal -Filter \"appId eq '{api.ResourceAppId}'\")) {{ New-MgServicePrincipal -AppId '{api.ResourceAppId}' | Out-Null }}   # {api.ResourceName}");
        sb.AppendLine();
        sb.AppendLine("$access = @(); $resources = @{}");
        sb.AppendLine("foreach ($resourceAppId in $wanted.Keys) {");
        sb.AppendLine("    $sp = Get-MgServicePrincipal -Filter \"appId eq '$resourceAppId'\" -Property id,appId,displayName,oauth2PermissionScopes");
        sb.AppendLine("    if (-not $sp) { Write-Warning \"API $resourceAppId is not in this tenant; skipping.\"; continue }");
        sb.AppendLine("    $resources[$resourceAppId] = $sp");
        sb.AppendLine("    $ids = foreach ($name in $wanted[$resourceAppId]) {");
        sb.AppendLine("        $scope = $sp.Oauth2PermissionScopes | Where-Object { $_.Value -ceq $name } | Select-Object -First 1");
        sb.AppendLine("        if ($scope) { @{ Id = $scope.Id; Type = 'Scope' } } else { Write-Warning \"$($sp.DisplayName): $name not found\" }");
        sb.AppendLine("    }");
        sb.AppendLine("    $access += @{ ResourceAppId = $resourceAppId; ResourceAccess = @($ids) }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("$app = Get-MgApplication -Filter \"tags/any(t:t eq 'TenantryAdminApp')\" | Select-Object -First 1");
        sb.AppendLine("$publicClient = @{ RedirectUris = @('http://localhost') }");
        sb.AppendLine("if ($app) {");
        sb.AppendLine("    Update-MgApplication -ApplicationId $app.Id -IsFallbackPublicClient -PublicClient $publicClient -RequiredResourceAccess $access");
        sb.AppendLine("} else {");
        sb.AppendLine("    $app = New-MgApplication -DisplayName 'Tenantry' -SignInAudience AzureADMyOrg -Tags 'TenantryAdminApp' `");
        sb.AppendLine("        -IsFallbackPublicClient -PublicClient $publicClient -RequiredResourceAccess $access");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("$sp = Get-MgServicePrincipal -Filter \"appId eq '$($app.AppId)'\"");
        sb.AppendLine("if (-not $sp) {");
        sb.AppendLine("    for ($try = 1; -not $sp; $try++) {");
        sb.AppendLine("        try { $sp = New-MgServicePrincipal -AppId $app.AppId -AppRoleAssignmentRequired }");
        sb.AppendLine("        catch { if ($try -ge 8) { throw }; Start-Sleep -Seconds 5 }   # new apps take a moment to replicate");
        sb.AppendLine("    }");
        sb.AppendLine("} else {");
        sb.AppendLine("    Update-MgServicePrincipal -ServicePrincipalId $sp.Id -AppRoleAssignmentRequired");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Only assigned users can sign in. Assign yourself; add other admins or a group later.");
        sb.AppendLine("$me = (Invoke-MgGraphRequest -Method GET -Uri 'https://graph.microsoft.com/v1.0/me?$select=id').id");
        sb.AppendLine("$assigned = Get-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -All | Where-Object { $_.PrincipalId -eq $me }");
        sb.AppendLine("if (-not $assigned) {");
        sb.AppendLine("    New-MgServicePrincipalAppRoleAssignedTo -ServicePrincipalId $sp.Id -PrincipalId $me -ResourceId $sp.Id `");
        sb.AppendLine("        -AppRoleId '00000000-0000-0000-0000-000000000000' | Out-Null");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Tenant-wide consent for the delegated permissions.");
        sb.AppendLine("$grants = Get-MgOauth2PermissionGrant -Filter \"clientId eq '$($sp.Id)'\" -All");
        sb.AppendLine("foreach ($resourceAppId in $resources.Keys) {");
        sb.AppendLine("    $resource = $resources[$resourceAppId]");
        sb.AppendLine("    $names = ($wanted[$resourceAppId] | Where-Object { $n = $_; $resource.Oauth2PermissionScopes.Value -ccontains $n }) -join ' '");
        sb.AppendLine("    if (-not $names) { continue }");
        sb.AppendLine("    $grant = $grants | Where-Object { $_.ResourceId -eq $resource.Id -and $_.ConsentType -eq 'AllPrincipals' } | Select-Object -First 1");
        sb.AppendLine("    if ($grant) { Update-MgOauth2PermissionGrant -OAuth2PermissionGrantId $grant.Id -Scope $names }");
        sb.AppendLine("    else { New-MgOauth2PermissionGrant -ClientId $sp.Id -ConsentType AllPrincipals -ResourceId $resource.Id -Scope $names | Out-Null }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("$tenantId = (Get-MgContext).TenantId");
        sb.AppendLine("Write-Host ''");
        sb.AppendLine("Write-Host \"Tenant ID:      $tenantId\"");
        sb.AppendLine("Write-Host \"Application ID: $($app.AppId)\"");
        sb.AppendLine("Write-Host 'Paste both into Tenantry to finish.'");
        return sb.ToString();
    }
}
