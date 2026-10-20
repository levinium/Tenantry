# Tenantry Phase 0: Feasibility Research

Research date: 2026-10-20. Sources are Microsoft Learn, PowerShell Gallery / NuGet package contents, and official GitHub repos unless marked "community". Package facts marked "(package inspection)" come from downloading the published package and reading its files directly.

Versions examined:

| Component | Version | Published |
|---|---|---|
| ExchangeOnlineManagement (EXO V3) | 3.10.1 | 2026-07-24 |
| PnP.PowerShell | 3.4.1 (stable) | 2026-08-19 |
| Microsoft.PowerShell.SDK | 7.6.7 (net10.0) | 2026-10-13 |
| Microsoft.SharePointOnline.CSOM | 16.1.27709.12000 | 2026-09-25 |

## Summary table

| # | Question | Verdict |
|---|---|---|
| 1a | Bundle ExchangeOnlineManagement in installer | **Partial.** License text is permissive but inconsistent; closed source. Prefer download-on-first-run. |
| 1b | Bundle PnP.PowerShell | **Partial.** PnP code is MIT; bundled SharePoint CSOM DLLs carry a Microsoft EULA with distribution conditions. |
| 1c | Microsoft.PowerShell.SDK | **Yes.** MIT. |
| 2a | `Connect-ExchangeOnline -AccessToken` with our own app's delegated token | **Yes.** Delegated `Exchange.Manage` on Office 365 Exchange Online, scope `https://outlook.office365.com/.default`, plus `-UserPrincipalName`. |
| 2b | `Connect-IPPSSession -AccessToken` | **Partial.** Parameter exists since 3.8.0. Delegated custom-app scope is undocumented and needs a spike. **Not available on macOS/Linux at all.** |
| 3 | EXO V3 in a hosted runspace, cross-platform | **Partial.** Works on Windows. EXO works on macOS/Linux, but IPPS does not. Expect MSAL assembly conflicts in-process, so run EXO out of process. |
| 4 | `Connect-PnPOnline -AccessToken` with our delegated token | **Yes, with limits.** It takes one token with a SharePoint audience. Graph-backed PnP cmdlets fail, and nothing refreshes the token. PnP isolates its dependencies in its own ALC. |
| 5 | Bootstrap client ID | **Partial.** No Microsoft rule allows or forbids it. PnP PowerShell, CLI for Microsoft 365 and CIPP all bootstrap with the Azure PowerShell or Azure CLI first-party IDs. A publisher-owned multi-tenant bootstrap app is the cleanest option. |
| 5b | Programmatic tenant-wide consent | **Yes.** `POST /oauth2PermissionGrants` (AllPrincipals) and `POST /servicePrincipals/{id}/appRoleAssignedTo`. |
| 6 | Graph coverage | Mixed. Details in section 6. Message trace is now **GA in Graph v1.0 with delegated support**. |

---

## 1. Redistribution

### 1a. ExchangeOnlineManagement: Partial

Evidence:

- The PSGallery manifest `LicenseUri` is `http://aka.ms/azps-license`. It redirects to `https://raw.githubusercontent.com/Azure/azure-powershell/master/LICENSE.txt`, whose header reads: "MICROSOFT SOFTWARE LICENSE TERMS / MICROSOFT AZURE POWERSHELL ... BY USING THE SOFTWARE, YOU ACCEPT THESE TERMS." Apache License 2.0 text follows, and Apache 2.0 section 4 permits "reproduce and distribute copies of the Work".
  - https://www.powershellgallery.com/packages/ExchangeOnlineManagement
  - https://raw.githubusercontent.com/Azure/azure-powershell/master/LICENSE.txt
- The `license.txt` shipped inside the 3.10.1 package says something different (package inspection, full text):
  > MIT License 2.0
  > Copyright (c) 2019 Exchange Online Manageability Team
  > Permission is hereby granted, free of charge, to any person obtaining a copy of this software ... to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software.

  This is a truncated MIT text. It has no notice-retention clause and no warranty disclaimer. "MIT License 2.0" is not a real SPDX license.
- The manifest copyright is "(c) 2021 Microsoft. All rights reserved." The module has no public source repo. Release notes say "For issues related to the module, contact Microsoft support."
- The package bundles many third-party and Microsoft DLLs under `netCore/` (MSAL 4.83.1, Azure.Core, OData, Newtonsoft.Json and others), each with its own license.

Caveats:

- Both texts grant distribution rights on their face. But the metadata is contradictory, the module is closed source, and the Azure PowerShell terms name a different product. A lawyer would call this ambiguous, not clean.
- **Recommendation:** do not vendor it in the installer. Have the app download the exact pinned version from the PSGallery v2 API (`https://www.powershellgallery.com/api/v2/package/ExchangeOnlineManagement/<ver>`) on first use, verify a pinned SHA-256, and store it in the app's data folder. That is the same as the user running `Save-Module`, and it removes the redistribution question. It also lets users pick up Microsoft's fixes without an app release.

### 1b. PnP.PowerShell: Partial

Evidence:

- The repo license is MIT, "Copyright (c) 2026 .NET Foundation". Source: https://raw.githubusercontent.com/pnp/powershell/dev/LICENSE. The PSGallery page lists no license link.
- The package bundles the SharePoint CSOM assemblies under `Core/` (`Microsoft.SharePoint.Client*.dll`, `Microsoft.Online.SharePoint.Client.Tenant.dll`) (package inspection). The CSOM NuGet license (https://go.microsoft.com/fwlink/?LinkId=280198, "MICROSOFT SHAREPOINT CLIENT COMPONENTS") is a Microsoft EULA:
  > Right to Use and Distribute. The code and text files listed below are "Distributable Code." ... Distribution Requirements. For any Distributable Code you distribute, you must add significant primary functionality to it in your programs; ... require distributors and external end users to agree to terms that protect it at least as much as this agreement; display your valid copyright notice on your programs; and indemnify, defend, and hold harmless Microsoft ...
  >
  > Distribution Restrictions. You may not ... modify or distribute the source code of any Distributable Code so that any part of it becomes subject to an Excluded License.

  The "listed below" file list is **missing** from the published .docx. That is another ambiguity.

Caveats:

- PnP's own code is fine to bundle with attribution. The CSOM DLLs add conditions: an end-user EULA pass-through and an indemnity. They are also incompatible with a copyleft license for Tenantry. Keep Tenantry MIT or Apache-2.0 if anything Microsoft-EULA'd ships.
- **Recommendation:** use the same download-on-first-run approach as EXO. Alternatively, ship PnP and show the CSOM EULA in the installer.

### 1c. Microsoft.PowerShell.SDK: Yes

- The NuGet license is MIT, version 7.6.7, target `net10.0`: https://www.nuget.org/packages/Microsoft.PowerShell.SDK. The PowerShell repo is MIT.
- Caveat: the SDK does **not** include the PowerShellGet or PackageManagement modules, which EXO REST connections expect (see section 3). Those modules are separate and MIT (PowerShell/PowerShellGet repo). Bundle them or download them the same way.

---

## 2. EXO and IPPS with a delegated token from our own app

### 2a. Connect-ExchangeOnline -AccessToken: Yes

Evidence:

- Learn cmdlet reference (updated 2026-06-04): https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/connect-exchangeonline?view=exchange-ps
  > **Note**: This parameter is available in module version 3.1.0-Preview1 or later. The AccessToken parameter specifies the OAuth JSON Web Token (JWT) that's used to connect to Exchange Online. Depending on the type of access token, you need to use this parameter with the Organization, DelegatedOrganization, or UserPrincipalName parameters.
- Microsoft's own app-plus-user example (Partner Center GDAP doc, now **archived/retired**, 2023) shows a non-Microsoft multi-tenant app granted delegated `Exchange.Manage` on resource `00000002-0000-0ff1-ce00-000000000000` (Office 365 Exchange Online), a token for scope `https://outlook.office365.com/.default`, and then `Connect-ExchangeOnline -DelegatedOrganization $CustomerTenantId -AccessToken $token.AccessToken`. Source: https://learn.microsoft.com/en-us/previous-versions/powershell/partnercenter/exchange-online-gdap-app
- The current app-only article (2026-08-26) confirms: "Delegated scenarios are supported in Exchange Online ... You can also use multitenant applications". It also uses the admin-consent scope `https://outlook.office365.com/.default`. Source: https://learn.microsoft.com/en-us/powershell/exchange/app-only-auth-powershell-v2?view=exchange-ps
- Community (michev.info, MVP): a custom app with **delegated `Exchange.Manage`** on Office 365 Exchange Online, a token for `https://outlook.office365.com/.default`, then `-AccessToken` plus `-UserPrincipalName`. The author confirms "Nothing wrong with using a custom app". Source: https://michev.info/blog/post/4249/connecting-to-exchange-online-powershell-by-passing-an-access-token
- Package inspection of 3.10.1 found an `ExternalAccessTokenProvider` class in `Microsoft.Exchange.Management.AdminApiProvider.dll`. It also has an `upn_required` string and `UpnIdentifierInToken`, which means a delegated token needs the UPN.

Practical recipe:

- App registration: delegated permission `Exchange.Manage` on **Office 365 Exchange Online** (appId `00000002-0000-0ff1-ce00-000000000000`), with tenant-wide grant (see section 5).
- MSAL scope: `https://outlook.office365.com/.default`.
- Call: `Connect-ExchangeOnline -AccessToken <jwt> -UserPrincipalName <signed-in UPN> -ShowBanner:$false`. Use `-Organization` only for app-only tokens and `-DelegatedOrganization` only for GDAP/CSP.
- What you can do is decided by the **user's** Exchange RBAC (Exchange Administrator and similar roles). The app permission alone grants nothing.

Caveats:

- The module **does not refresh** the token (michev: "it's your responsibility to monitor its validity and renew it"). Tokens last about 60 to 90 minutes. Reconnect with a fresh token before expiry, or catch 401 or "Maximum retry count reached" and reconnect.
- Microsoft's only first-party sample of this flow is on an archived page. The parameter itself is current and documented.
- Do not pass a wrong UPN. The module does not validate it (michev).

### 2b. Connect-IPPSSession -AccessToken: Partial

Evidence:

- Learn reference (updated 2026-02-27): https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/connect-ippssession?view=exchange-ps
  > **Note**: This parameter is available in module version 3.8.0-Preview1 or later. The AccessToken parameter specifies the OAuth JSON Web Token (JWT) that's used to connect to Security and Compliance PowerShell. Depending on the type of access token, you need to use this parameter with the Organization, DelegatedOrganization, or UserPrincipalName parameters.
- The release notes for 3.8.0 say: "New *AccessToken* parameter on **Connect-IPPSSession**." Source: https://learn.microsoft.com/en-us/powershell/exchange/exchange-online-powershell-v2?view=exchange-ps
- For app-only access, Microsoft documents resource **Microsoft Exchange Online Protection** (`00000007-0000-0ff1-ce00-000000000000`), role `Exchange.ManageAsApp` (`455e5cd2-...`). No delegated equivalent is documented.
- Package inspection: the module's IPPS resource host is `ps.compliance.protection.outlook.com`. A community analysis says `-EnableSearchOnlySession` changes the token audience to another resource (`https://dataservice.o365filtering.com`): https://michev.info/blog/post/6742/upcoming-changes-to-the-connect-ippssession-cmdlet-the-enablesearchonlysession-switch

Caveats:

- **Not documented:** which delegated permission and scope a custom app must request for IPPS. **Spike needed.** Request `https://ps.compliance.protection.outlook.com/.default` with a delegated permission on whichever service principal owns that identifier URI, then inspect the `aud` and `scp` claims and test.
- **Connect-IPPSSession is not available on macOS or Linux** (see section 3). Purview features will be Windows-only if they depend on IPPS.
- eDiscovery cmdlets have extra restrictions and need `-EnableSearchOnlySession`, which may need a second token with a different audience.

---

## 3. EXO V3 in a hosted runspace (Microsoft.PowerShell.SDK): Partial

Evidence:

- Supported platforms (Learn, 2026-07-31): https://learn.microsoft.com/en-us/powershell/exchange/exchange-online-powershell-v2?view=exchange-ps
  > Module versions 3.10.0 (June 2026) or later require PowerShell 7.6.0 (March 2026) or later due to .NET 10.0 assembly dependencies.
  >
  > Currently, **Connect-IPPSSession** and therefore Security & Compliance PowerShell isn't available in PowerShell 7 on macOS clients.
  >
  > Currently, **Connect-IPPSSession** and therefore Security & Compliance PowerShell isn't available in PowerShell 7 on Linux clients.
  >
  > REST API connections in Windows require the PowerShellGet module. By dependency, the PowerShellGet module requires the PackageManagement module.

  Only Ubuntu 22.04 and 24.04 LTS are listed as current Linux distributions. macOS 14 or later is required for module 3.10.
- Hosting note (app-only article): "Use the *SkipLoadingFormatData* switch on the **Connect-ExchangeOnline** cmdlet if you get the following error when using the Windows PowerShell SDK to connect: `The term 'Update-ModuleManifest' is not recognized ...`". This is the PowerShellGet dependency surfacing in a hosted SDK.
- Package inspection of 3.10.1 `netCore/`: the module loads its own `Microsoft.Identity.Client.dll` **4.83.1**, plus `Microsoft.Identity.Client.Broker`, `Microsoft.IdentityModel.*` 8.19.2 and `Azure.Core` 1.50.0. It has **no AssemblyLoadContext isolation**: no ALC code in the psm1 or the module DLL, so everything loads into the default ALC. `deps.json` targets `.NETCoreApp,Version=v8.0`, referencing SMA 7.4.14. The module writes a temporary generated module to `%TMP%` (or `-EXOModuleBasePath`).
- Assembly conflict mechanics: https://learn.microsoft.com/en-us/powershell/scripting/dev-cross-plat/resolving-dependency-conflicts
  > PowerShell always loads assemblies into the same context. This presents issues when a module's dependencies conflict with already-loaded DLLs ...
  >
  > Because the module depends on a newer version of the assembly, it won't accept the version that PowerShell already has loaded.

  The recommended mitigations are a custom ALC or out-of-process execution.
- Real-world conflict (community, Office 365 for IT Pros, 2026-06-22): EXO 3.10 and Graph SDK 2.38 cannot share a session.
  - EXO first, then Graph: "Method not found ... WithLogging(Microsoft.IdentityModel.Abstractions.IIdentityLogger, Boolean)".
  - Graph first, then EXO: "Error Acquiring Token: System.NullReferenceException".

  Source: https://office365itpros.com/2026/06/22/powershell-woes-and-cmdlets/

Caveats and recommendation:

- In .NET's default ALC, the first-loaded MSAL version wins. If the host loads MSAL **older** than 4.83.1, EXO fails with a FileLoadException. If the host loads **newer** MSAL, EXO binds to it and may hit removed or changed APIs. Microsoft has no compatibility guarantee here, as the Graph SDK clash shows. Using `-AccessToken` probably avoids most of EXO's MSAL code paths, since the module does no token acquisition. That is unverified and should be tested.
- **Recommendation:** run EXO and IPPS in a **separate worker process** rather than in-process. Use either a small `pwsh`-based worker or a second .NET 10 executable that hosts the SDK with only EXO loaded, and talk to it over stdio/JSON-RPC. Then:
  - the main app keeps its own MSAL and Graph SDK versions;
  - EXO upgrades cannot break the host;
  - a hung EXO call can be killed;
  - PnP can run in the same worker or a third one.
- Ship PowerShellGet and PackageManagement next to the SDK, or always pass `-SkipLoadingFormatData`. Test both.
- Set `-EXOModuleBasePath` to an app-owned writable folder, especially on macOS and Linux.
- Use Microsoft.PowerShell.SDK **7.6.x** to match EXO 3.10's PS 7.6 minimum. The app targets .NET 10, so this is fine.

---

## 4. PnP.PowerShell with our delegated token

### Own app registration since 2024: Yes (confirmed)

- https://pnp.github.io/powershell/articles/registerapplication.html: "as of September 9th, 2024, this has become mandatory step." The PnP Management Shell multi-tenant app (`31359c7f-bd7e-475c-86db-fdb8c937548e`) was deleted, and the constant is still in source as `PnPManagementShellClientId`.

### Connect-PnPOnline -AccessToken: Yes, with limits

Evidence (https://pnp.github.io/powershell/cmdlets/Connect-PnPOnline.html):

> Using this parameter you can provide your own access token. Notice that it is recommend to use one of the other connection methods as this will limits the offered functionality on PnP PowerShell. For instance if the token expires (typically after 1 hour) will not be able to acquire a new valid token ... You are responsible for providing your own valid access token when using this parameter, for the correct audience, with the correct permissions scopes.
>
> Example: `Connect-PnPOnline -Url "https://contoso.sharepoint.com" -AccessToken $token`

- The Access Token parameter set takes no `-ClientId`, so the token can come from any app, including ours.
- Some PnP cmdlets call Microsoft Graph, or both SharePoint and Graph. One token cannot serve both audiences. An open feature request asks for multiple tokens: https://github.com/pnp/powershell/issues/3925. A maintainer noted Graph-backed cmdlets don't make sense with a SharePoint token: https://github.com/pnp/PnP-PowerShell/issues/2631

Scopes:

- Resource **Office 365 SharePoint Online** (`00000003-0000-0ff1-ce00-000000000000`), delegated `AllSites.FullControl` for admin work. Use `AllSites.Manage` or `AllSites.Read` where enough. Add `TermStore.ReadWrite.All` and `User.ReadWrite.All` if needed. PnP's own default for its interactive app is SPO `TermStore.ReadWrite.All AllSites.FullControl User.ReadWrite.All` plus Graph `Group.ReadWrite.All User.ReadWrite.All` (source: `RegisterEntraIDAppForInteractiveLogin.cs`, `GetRequestedScopes`).
- MSAL scope: `https://<tenant>.sharepoint.com/.default`. Tenant-admin cmdlets connect to `https://<tenant>-admin.sharepoint.com`. **Spike needed** to confirm one SharePoint-audience token works for both hosts; it is expected because they share the same resource principal. The signed-in user still needs the SharePoint Administrator role for tenant cmdlets.

Assembly isolation (package inspection of 3.4.1):

- PnP ships `Common/PnP.PowerShell.ALC.dll` and keeps its dependencies under `Common/`: MSAL **4.85.2**, `Microsoft.Identity.Client.Extensions.Msal`, PnP.Core and PnP.Framework. Cmdlets and CSOM live under `Core/`. This is the ALC-isolation pattern from the Learn article above, so PnP is much less likely than EXO to clash with the host's MSAL and Graph SDK. The manifest requires `PowerShellVersion = '7.4.0'`.

Caveats:

- PnP and EXO in one process are still risky because EXO is not isolated. A separate worker per module is safest.
- Handle token expiry yourself: reconnect with a fresh token.
- Prefer calling Graph directly from the host (Graph SDK) and use PnP only for SharePoint-only operations.

---

## 5. Bootstrap: signing a Global Admin in before our app exists

### Options

| Option | Client ID | Who uses it | Notes |
|---|---|---|---|
| Microsoft Azure PowerShell (first-party) | `1950a258-227b-4e31-a9cf-717495945fc2` | **PnP PowerShell** `Register-PnPEntraIDAppForInteractiveLogin`; **CIPP** SAM wizard (device code) | Preauthorized on Graph. Scope `https://graph.microsoft.com/.default` with no consent prompt. Subject to Azure mandatory MFA (Phase 2 since 2025-10-01), which only affects ARM calls. |
| Microsoft Azure CLI (first-party) | `04b07795-8ddb-461a-bbee-02f9e1bf7b46` | **CLI for Microsoft 365** `m365 setup` ("signs in as Azure CLI to your tenant, creates a new app registration") | Same pattern as Azure PowerShell. |
| Microsoft Graph Command Line Tools | `14d82eec-204b-4c2f-b7e8-296a70dab67e` | Microsoft Graph PowerShell SDK | Dynamic consent. The GA consents to `Application.ReadWrite.All` and similar for the Graph CLI app, which leaves a grant behind (removable). Admins commonly block or restrict this app. |
| Graph Explorer | `de8bc8b5-...` | Microsoft's own "grant permissions programmatically" docs | Web app with fixed redirect URIs. Not usable from a desktop MSAL client. |
| **Publisher-owned multi-tenant bootstrap app** | ours | (recommended) | Public client, `http://localhost` redirect. Requests only the bootstrap delegated scopes. The admin sees our name and publisher in the consent prompt. After setup the wizard can delete its service principal from the tenant. |

Evidence:

- PnP source (`src/Commands/Utilities/AzureAuthHelper.cs`, dev branch): `private static string CLIENTID = "1950a258-227b-4e31-a9cf-717495945fc2"; // Well-known Azure Management App Id`. It requests `{graphEndpoint}/.default`, then `POST {graph}/v1.0/applications` and opens `{login}/{tenant}/v2.0/adminconsent?client_id=...` in the browser. Consent is interactive, not via `oauth2PermissionGrants`.
- CIPP source (`Invoke-ExecDeviceCodeLogon.ps1`): the allow-list is `1950a258-...` (Azure PowerShell, "used to create the SAM app before one exists") and `1b730954-1685-4b74-9bfd-dac224a7b894`. Scope `https://graph.microsoft.com/.default`. Front end: `CippSAMDeploy.jsx` uses `applicationId="1950a258-227b-4e31-a9cf-717495945fc2"` with device code. Repos: https://github.com/KelvinTegelaar/CIPP-API, https://github.com/KelvinTegelaar/CIPP
- CLI for Microsoft 365: https://pnp.github.io/cli-microsoft365/cmd/setup/
- Mandatory MFA (Learn, updated 2026-10-06), which lists Azure CLI and Azure PowerShell app IDs as Phase 2 and says "Generally, Microsoft Graph APIs aren't in scope for Azure MFA enforcement": https://learn.microsoft.com/en-us/entra/identity/authentication/concept-mandatory-multifactor-authentication
- Graph CLI app verification (MC804763, 2024; community summary): https://ourcloudnetwork.com/microsoft-graph-command-line-tools-to-become-verified-in-entra/ and https://devblogs.microsoft.com/microsoft365dev/microsoft-graph-powershell-and-cli-are-now-verified/

Caveats on first-party IDs:

- I found **no Microsoft document that permits or forbids** third-party tools using Azure CLI, Azure PowerShell or Graph CLI client IDs. It is widespread practice, including in Microsoft-adjacent PnP projects. But these are Microsoft's apps, whose preauthorized scopes, redirect URIs and CA treatment Microsoft can change at any time. Tenants can disable them (Enterprise apps > Enabled for users to sign in = No) or target them with Conditional Access. Sign-in logs will show "Microsoft Azure PowerShell", not Tenantry, which is a transparency concern for an admin tool.
- The redirect URI must match what the first-party app has registered. MSAL `http://localhost` works for these public clients today, and PnP and CIPP rely on it or on device code. Device code flow is increasingly blocked by CA "authentication flows" policies.
- **Recommendation:** default to a **publisher-owned multi-tenant public-client bootstrap app** that requests only the four bootstrap scopes listed below. Fall back to Azure PowerShell (`1950a258`) only if the tenant blocks unverified or third-party consent. Tenants with "users can consent only to verified publishers" will need an admin, who is the person signing in anyway. Publisher verification for the bootstrap app needs an MPN/Partner Center ID. After setup, the per-tenant app does all work, and the wizard can remove the bootstrap app's service principal and grant from the tenant.

### Programmatic tenant-wide consent: Yes

Evidence:

- `POST /oauth2PermissionGrants` (v1.0): https://learn.microsoft.com/en-us/graph/api/oauth2permissiongrant-post?view=graph-rest-1.0
  - Delegated least-privileged permission: `DelegatedPermissionGrant.ReadWrite.All` (higher: `Directory.ReadWrite.All`).
  - Roles: Application Administrator, Application Developer, Cloud Application Administrator, Directory Writers, Privileged Role Administrator, User Administrator.
  - Body: `clientId` (object id of **our** service principal, "*not* appId"), `"consentType": "AllPrincipals"`, `resourceId` (object id of the resource SP, for example Graph, Office 365 Exchange Online or SharePoint), `scope` (space-separated, at most 3850 chars).
- How-to guide: https://learn.microsoft.com/en-us/graph/permissions-grant-via-msgraph
  > Be careful! Permissions granted programmatically aren't subject to review or confirmation. They take effect immediately.

  For delegated grants: "*Cloud Application Administrator* ... is the least privileged role for creating applications and granting consent to delegated permissions", with "*Application.Read.All* and *DelegatedPermissionGrant.ReadWrite.All*". For app roles: "*Application.Read.All* and *AppRoleAssignment.ReadWrite.All*", via `POST /servicePrincipals/{resource-sp-id}/appRoleAssignedTo`.

Bootstrap token scopes (delegated, all on Microsoft Graph):

- `Application.ReadWrite.All`: create the application and service principal, and add a certificate if ever needed.
- `DelegatedPermissionGrant.ReadWrite.All`: the AllPrincipals grants.
- `AppRoleAssignment.ReadWrite.All`: only if any app roles are granted. Note that granting app roles to Microsoft Graph effectively needs Privileged Role Administrator or Global Administrator.
- Optionally `RoleManagement.ReadWrite.Directory`, if the wizard assigns Entra roles. Not needed for delegated-only operation.

Wizard sequence:

1. `POST /applications` with `isFallbackPublicClient: true`, `publicClient.redirectUris: ["http://localhost"]`, `signInAudience: AzureADMyOrg`, and `requiredResourceAccess` for Graph, Exchange (`00000002-0000-0ff1-ce00-000000000000`, `Exchange.Manage`), SharePoint (`00000003-0000-0ff1-ce00-000000000000`) and, if the IPPS spike succeeds, EOP or compliance.
2. `POST /servicePrincipals { appId }`.
3. Look up the resource SPs by appId: `GET /servicePrincipals(appId='...')`.
4. `POST /oauth2PermissionGrants` once per resource.
5. If the per-tenant message-trace API is used, also `POST /servicePrincipals { appId: "8bd644d1-64a1-4d4b-ae52-2e0cbf64e373" }` (see section 6).

Caveat: newly created apps and SPs take seconds to minutes to replicate. PnP sleeps 10 s after creation and waits before consent. Add retry with backoff on 404/400 for each step.

---

## 6. Graph coverage for delegated admin use

| Capability | Graph? | Endpoint / notes |
|---|---|---|
| Auth methods: list / delete | **Yes (v1.0)** | `GET /users/{id}/authentication/methods`, plus per-type `DELETE` for fido2, microsoftAuthenticator, windowsHelloForBusiness, softwareOath, phone, email, platformCredential, temporaryAccessPass and external. Delegated `UserAuthenticationMethod.ReadWrite.All`; user needs Authentication Administrator, or Privileged Authentication Administrator for admins. |
| Auth methods: add | **Partial** | Admins can **create** phone, email, temporaryAccessPass and qrCodePin in v1.0. Admins **cannot** create Authenticator, FIDO2 or WHfB; users register those themselves. Not yet in v1.0: default method, hardware OATH, security questions, per-user MFA state. Source: https://learn.microsoft.com/en-us/graph/api/resources/authenticationmethods-overview?view=graph-rest-1.0 |
| Auth methods, new MFA freshness rule | **Important** | "Starting January 26, 2026, self-service credential management operations require users to complete multifactor authentication (MFA) if they last authenticated more than 10 minutes ago ... This change applies to both end users and administrators". Handle 403 by re-prompting with claims `{"access_token":{"amr":{"essential":true,"values":["ngcmfa"]}}}`. MSAL `.WithClaims(...)` supports this. |
| Sign-in logs | **Yes (v1.0)** | `GET /auditLogs/signIns`, delegated `AuditLog.Read.All`. Roles: Global Reader, Reports Reader, Security Admin, Security Operator or Security Reader. Showing CA policies also needs `Policy.Read.All`. Source: https://learn.microsoft.com/en-us/graph/api/signin-list?view=graph-rest-1.0. (Not re-verified this session: the API generally needs an Entra ID P1/P2 tenant license.) |
| Directory audit | **Yes (v1.0)** | `GET /auditLogs/directoryAudits`, delegated `AuditLog.Read.All`. |
| Mailbox auto-replies (scheduled) | **Graph: self only, in practice** | `PATCH /users/{id}/mailboxSettings` supports `automaticRepliesSetting` with `status: "Scheduled"`, `scheduledStartDateTime` and `scheduledEndDateTime`. But the delegated `MailboxSettings.ReadWrite` permission has no `.Shared` variant, and community Q&A reports 403 when a delegated admin targets another user. **For admin-sets-anyone's-OOF use EXO** `Set-MailboxAutoReplyConfiguration -AutoReplyState Scheduled -StartTime -EndTime`, or an app-only Graph permission. Sources: https://learn.microsoft.com/en-us/graph/api/user-update-mailboxsettings?view=graph-rest-1.0 and https://learn.microsoft.com/en-us/answers/questions/5551455/how-to-restrict-the-access-of-mailboxsettings-read |
| Mailbox forwarding (ForwardingSmtpAddress) | **No** | Not among the writable `mailboxSettings` properties. Use EXO `Set-Mailbox -ForwardingSmtpAddress/-ForwardingAddress -DeliverToMailboxAndForward`. Inbox-rule forwarding is in Graph (`messageRules`), with the same delegated self-only caveat. |
| Litigation hold | **No** | No Graph endpoint found. Use EXO `Set-Mailbox -LitigationHoldEnabled` (needs an EXO Plan 2 or archiving license). Purview eDiscovery holds exist in Graph (`/security/cases/ediscoveryCases/.../legalHolds`), but those are a different feature. |
| Archive enablement | **No** | Use EXO `Enable-Mailbox -Archive` and `Set-Mailbox -AutoExpandingArchive`. |
| Message trace | **Yes, GA v1.0, delegated supported** | `GET /admin/exchange/tracing/messageTraces` and `.../messageTraces/{id}/getDetailsByRecipient(recipientAddress='...')`. Delegated **and** application `ExchangeMessageTrace.Read.All` (admin consent). **Prerequisite:** create a service principal for Microsoft app `8bd644d1-64a1-4d4b-ae52-2e0cbf64e373`; provisioning "might take several hours". Limits: 90-day lookback, 10 days per query, `$top` up to 5000, 100 requests per 5 min per tenant. **Global cloud only** (not GCC High, DoD or 21Vianet). Sources: https://learn.microsoft.com/en-us/graph/api/messagetracingroot-list-messagetraces?view=graph-rest-1.0 and https://learn.microsoft.com/en-us/exchange/monitoring/trace-an-email-message/graph-api-message-trace |
| Quarantine | **No** | No Graph API found. Use EXO `Get-QuarantineMessage`, `Release-QuarantineMessage` and `Delete-QuarantineMessage` (available via Connect-ExchangeOnline). Since Nov 2025, quarantine lists per recipient and `ReleaseToAll` applies to one user: https://learn.microsoft.com/en-us/defender-office-365/quarantine-admin-manage-messages-files |
| Transport (mail flow) rules | **No** | Use EXO `Get/New/Set/Remove-TransportRule`. |

---

## Spikes recommended before committing

1. **IPPS delegated token from our app.** Find a scope or permission combination that `Connect-IPPSSession -AccessToken -UserPrincipalName` accepts. Windows only.
2. **EXO in a worker process** with `-AccessToken`, on Windows, macOS 14+ and Ubuntu 24.04. Confirm PowerShellGet requirement, `-EXOModuleBasePath` and token-expiry reconnect.
3. **In-process test** with the host's MSAL loaded first (newer than 4.83.1) and EXO `-AccessToken`. This tells you whether a single process is viable at all.
4. **PnP `-AccessToken`** against both the tenant root and the `-admin` host with one SharePoint-audience token.
5. **Bootstrap app**: whether a multi-tenant public client with only the four Graph bootstrap scopes passes consent in a tenant with "verified publishers only" consent settings.

## Items that may go stale

- EXO module versioning moves fast (3.10 raised the floor to PS 7.6). The EXO and Graph SDK MSAL clash was reported in June 2026, and fixes may land.
- Graph auth-method coverage: passkey provisioning on behalf of users is evolving in beta.
- The Partner Center GDAP + EXO sample is archived. Treat it as historical evidence only.
- First-party client ID usage could be restricted by Microsoft without notice.

---

## 7. Follow-up: calling the Exchange and Security & Compliance admin REST endpoints directly (no PowerShell)

Research date: 2026-10-20. Sources used:

- **Decompiled module code.** ExchangeOnlineManagement 3.10.1 `netCore` DLLs, decompiled with ilspycmd. Files: `Microsoft.Exchange.Management.ExoPowershellGalleryModule.dll` and `Microsoft.Exchange.Management.AdminApiProvider.dll`.
- **CIPP-API source.** Commit `8985c1f` (2026-10-18), files `Modules/CIPPCore/Public/GraphHelper/New-ExoRequest.ps1` and `New-ExoBulkRequest.ps1`.
- **Microsoft Learn**, including the new Exchange Online Admin API docs.
- **michev.info** (MVP, community).

### Summary

| # | Question | Verdict |
|---|---|---|
| 7.1 | Exchange `InvokeCommand` REST endpoint, direct from .NET | **Yes, technically.** Endpoint, headers, body and paging are confirmed from module code and CIPP. A delegated token from a customer-created app with `Exchange.Manage` works (CIPP does exactly this). It is **undocumented and unsupported**. |
| 7.2 | Security & Compliance (Purview) REST `InvokeCommand` | **Yes, technically.** Connect-IPPSSession 3.2+ uses the same `/adminapi/beta/{tenant}/InvokeCommand` on a regional `*.ps.compliance.protection.outlook.com` host. The token audience is `https://ps.compliance.protection.outlook.com`. The region is found through a 302 redirect. CIPP uses it for DLP, retention, sensitivity labels and protection alerts. |
| 7.3 | Why IPPS is "unavailable" on macOS/Linux | **Not explained by Microsoft.** The decompiled REST path has no OS gate for IPPS. The OS-specific code is about WAM, certificate thumbprint, and an RPS-era redirect workaround. Calling REST directly from .NET is plain HTTPS, so the module's platform restriction does not apply. Untested on macOS/Linux, and still unsupported. |
| 7.4 | Supportability | **Unsupported** for the `beta/InvokeCommand` surface. Microsoft has since shipped a **documented, preview** "Exchange Online Admin API" (`/adminapi/v2.0/{tenant}/{Endpoint}`), but it covers only 6 endpoints (EWS-replacement scenarios). It has no Security & Compliance coverage. |
| 7.5 | Purview in Graph | **Partial.** Graph has eDiscovery (cases, custodians, searches, holds, review sets, exports), audit log queries, retention labels (delegated only), sensitivity labels (read-only, beta) and security alerts. DLP policies, retention policies, sensitivity label/policy authoring and protection-alert policies are **not** in Graph. |

### 7.1 Exchange Online `InvokeCommand`

**Endpoint and method.**

```
POST https://outlook.office365.com/adminapi/beta/{tenantId}/InvokeCommand[?$select=Prop1,Prop2]
POST https://outlook.office365.com/adminapi/beta/{tenantId}/$batch        (JSON batch, CIPP uses 10 per batch)
```

Evidence for the endpoint:

- Module code. `AppSettingsConstants.DefaultConnectionUri = "https://outlook.office365.com"` and `DefaultIsBetaEndpoint = true`. `AppSettings.GetBaseUri()` returns `connectionUri + "/adminapi" + "/beta"`; the non-beta value is `/v0.5`. The OData metadata embedded in `AdminApiProvider.dll` declares `<ActionImport Name="InvokeCommand" Action="Exchange.InvokeCommand" />` with parameter `CmdletInput` of type `Exchange.CmdletInvokeInputType`.
- CIPP `New-ExoRequest.ps1`: `$URL = "$Resource/adminapi/$ApiVersion/$($tenant.customerId)/InvokeCommand$Select"`, with `$ApiVersion` defaulting to `beta`. `New-ExoBulkRequest.ps1` uses `.../adminapi/beta/{customerId}/$batch` with a batch size of 10.

**Headers.**

- `Authorization: Bearer <jwt>`
- `Content-Type: application/json`
- `X-AnchorMailbox`, used for routing:
  - For delegated calls, `UPN:<signed-in admin UPN>`. The module default is prefix `UPN` (`DefaultRoutingHintPrefix = "UPN"`, `DefaultRoutingHintHeader = "X-AnchorMailbox"`).
  - For app-only calls, `APP:SystemMailbox{bb558c35-97f1-4cb9-8ff7-d53741dc928c}@<tenantId>`.
  - For mailbox-bound cmdlets, use the target mailbox's UPN.
  - CIPP uses `8cc370d3-822a-4ab8-a926-bb94bd0641a9` for `Set-AdminAuditLogConfig`.
- `Prefer: odata.maxpagesize=1000`. CIPP bulk adds `;odata.continue-on-error`.
- Optional:
  - `X-CmdletName: <cmdlet>`. CIPP sets this per batch sub-request.
  - `X-ResponseFormat: json|clixml` (per michev).
  - `Accept: application/json; odata.metadata=minimal`
  - `Accept-Encoding: gzip`
- Response diagnostics headers seen in module code: `X-Warnings`, `x-ms-diagnostics`, `X-FEServer`, `X-BEServer`, and `Retry-After`.

**Body.**

```json
{ "CmdletInput": { "CmdletName": "Get-Mailbox", "Parameters": { "Identity": "user@contoso.com", "ResultSize": "Unlimited" } } }
```

Typed parameter values such as hashtables (`@{Add=...}`) use an `@odata.type` marker, for example `#Exchange.GenericHashTable` (michev).

**Responses, paging and errors.**

- Results come back in `value`. Warnings come back in `@adminapi.warnings`.
- Paging: re-POST the **same body** to `@odata.nextLink` until it is absent. CIPP: "The EXO admin API pages by re-POSTing the same CmdletInput body to the nextLink URL." The Learn v2.0 doc says nextLinks are valid for only 5 to 10 minutes.
- Errors are OData JSON. CIPP reads, in order of precedence, `error.details[].message`, then `error.innererror.internalException.message`, then `error.innererror.message`, then `error.message`.
- Throttling: 429 with `Retry-After`, either on the whole request or on individual sub-requests inside a 200 `$batch` response. CIPP retries sub-requests up to 3 times with backoff.

**Token.**

- Audience `https://outlook.office365.com`, scope `https://outlook.office365.com/.default`.
- Delegated permission: `Exchange.Manage` (`ab4f2b77-0b06-4fc1-a9de-02113fc2ab7c`) on Office 365 Exchange Online (`00000002-0000-0ff1-ce00-000000000000`).
- App-only permission: `Exchange.ManageAsApp` (`dc50a0fb-...`) plus an Entra or Exchange role on the service principal.
- **A delegated token from a customer-created app works.** CIPP's own SAM app is a customer- or partner-created multi-tenant app. Its manifest (`Config/SAMManifest.json`) requests only delegated `Exchange.Manage` on `00000002-0000-0ff1-ce00-000000000000` for Exchange, and of its ~1,318 `New-Exo*Request` call sites, about 1,293 run with a delegated (refresh-token) token. Michev documents the same thing with a custom app.
- What the user can do is still governed by their Exchange RBAC.

**Sources:**

- https://github.com/KelvinTegelaar/CIPP-API/blob/master/Modules/CIPPCore/Public/GraphHelper/New-ExoRequest.ps1
- https://github.com/KelvinTegelaar/CIPP-API/blob/master/Modules/CIPPCore/Public/GraphHelper/New-ExoBulkRequest.ps1
- https://michev.info/blog/post/5822/all-you-need-to-know-about-exchange-online-admin-api-or-how-to-run-cmdlets-without-powershell (posted 2025-11-21, updated 2026-05-02)

### 7.2 Security & Compliance (Purview) REST

**What Connect-IPPSSession does in REST mode** (decompiled module code; module 3.2.0+ "supports REST API mode for virtually all Security & Compliance PowerShell cmdlets"):

1. **Connection URI.** The default is `https://ps.compliance.protection.outlook.com/PowerShell-LiveId` (psm1 line 611). `HttpUtilities.IsEOPConnectionUri()` is true for any host containing `ps.compliance.protection`.
2. **Token audience.** `AppSettings.GetAcquireTokenEndpoint()` strips any regional prefix with `Regex("((\w)+\.)?ps.compliance.protection")`. The scope is therefore `https://ps.compliance.protection.outlook.com/.default` (`scopes = { context.AcquireTokenEndpoint + "/.default" }`).
3. **`-EnableSearchOnlySession`.** `GetAcquireTokenEndpointSearchOnly()` replaces the host with `dataservice.o365filtering.com`, so the token audience becomes `https://dataservice.o365filtering.com`.
4. **Region discovery.** `ConnectionContextUtil.UpdateConnectionUriForEop()` sends a `GET {ConnectionUri}/AdminApi/v1.0/{tenantId}/EXOModuleFile?Version={moduleVersion}`, with `X-AnchorMailbox: UPN:<user>` and `AllowAutoRedirect = false`. On **302**, `TryGetRedirectUriForEop()` takes the first DNS label of the `Location` host and prepends it, giving `https://{prefix}.ps.compliance.protection.outlook.com`.
5. **Cmdlet calls.** They then go to the same `/adminapi/beta/{tenantId}/InvokeCommand` shape on that regional host. The base URI is shared via `AppSettings.GetBaseUri`.

**CIPP's version** (`New-ExoRequest -Compliance`):

- `$Resource = 'https://ps.compliance.protection.outlook.com'` and the token scope is `"$Resource/.default"`.
- Discovery uses `GET $Resource/adminapi/$ApiVersion/{customerId}/EXOBanner('AutogenSession')?Version=$ModuleVersion` with `-MaximumRedirection 0`, then builds `'{0}.ps.compliance.protection.outlook.com' -f <first label of Location host>`. The result is cached per tenant as `ComplianceUrl`.
- Anchor: `UPN:SystemMailbox{bb558c35-97f1-4cb9-8ff7-d53741dc928c}@<initialDomainName>`.
- Note that CIPP's `New-ExoBulkRequest` has the compliance discovery code stubbed out with a comment ("Compliance URL logic (omitted for brevity)"), so its bulk compliance calls go to the un-prefixed host.

**Which token works for Purview:** CIPP's SAM manifest has **no** permission on Microsoft Exchange Online Protection (`00000007-...`) or any compliance resource. It has only Office 365 Exchange Online (`Exchange.Manage` delegated plus `Exchange.ManageAsApp`). Yet it acquires `https://ps.compliance.protection.outlook.com/.default` delegated tokens: 59 of 76 compliance call sites are delegated and 17 are app-only. This is strong evidence that **delegated `Exchange.Manage` on Office 365 Exchange Online is enough for a Security & Compliance delegated token**. That answers the open IPPS spike in section 2b in principle. Confirm the `aud`/`scp` with a real token before relying on it.

**Cmdlets CIPP runs against Purview** (git grep over `New-Exo*Request ... -Compliance`):

- DLP: `Get/New/Set/Remove-DlpCompliancePolicy`, `Get/New/Set-DlpComplianceRule`, `Get/New/Set/Remove-DlpSensitiveInformationType`, `Get/New/Set/Remove-DlpSensitiveInformationTypeRulePackage`.
- Retention: `Get/New/Set/Remove-RetentionCompliancePolicy`, `Get/New/Set-RetentionComplianceRule`.
- Sensitivity labels: `Get/New/Set/Remove-Label`, `Get/New/Set-LabelPolicy`.
- Alert policies: `Get/New/Set/Remove-ProtectionAlert`.
- Also `New-ServicePrincipal`.
- **No** `*-ComplianceSearch*`, `*-CaseHold*` or `*-ComplianceCase` usage was found. eDiscovery via InvokeCommand is unproven, and app-only for eDiscovery cmdlets is explicitly unsupported by Microsoft (section 2).

### 7.3 macOS/Linux restriction

- Microsoft's statement only says IPPS "isn't available in PowerShell 7 on macOS clients" or Linux clients, with no reason given: https://learn.microsoft.com/en-us/powershell/exchange/exchange-online-powershell-v2?view=exchange-ps
- Decompiled 3.10.1 code: every `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` check concerns one of the following, and none blocks IPPS REST on non-Windows:
  - WAM sign-in (`AdminApiProvider` line ~40316);
  - performance counters;
  - URI HTML-encoding for the PSSession URI;
  - `NewExoPSSessionUtils.GetPowerShellConnectionUri()`, an **RPS-era** workaround that, on non-Windows only, pre-resolves the Security & Compliance redirect with a `Basic` header carrying `UPN:Bearer token`. It applies to WinRM/PSSession connections, which were deprecated in October 2023.

  The psm1 only hides `-CertificateThumbprint` off Windows: "We do not want to expose certificate thumprint in Linux as it is not feasible there".
- Microsoft's app-only article contains the tip "If a **Connect-IPPSSession** command presents a sign in prompt, run the command: `$Global:IsWindows = $true`". That points to platform-sensitive sign-in behavior, not a REST limitation.
- **Conclusion:** the restriction is most likely a holdover from the WinRM/RPS era (PSWSMan on Linux/macOS) plus untested sign-in paths. A .NET app that gets its own token with MSAL and calls the regional InvokeCommand endpoint over HTTPS does not touch any of that code. It **should** work on any OS, but this is **unverified**, so make it part of the cross-platform spike. Precedent: CIPP (Azure Functions) and monkey365 (cross-platform PowerShell, `Get-PSExoAdminApiObject.ps1` on the PowerShell Gallery) both call these endpoints without the module.

### 7.4 Supportability

**Undocumented and unsupported** for `/adminapi/beta/.../InvokeCommand`. Michev: "none of these methods are officially supported by Microsoft ... changes can happen at any time and will be undocumented". A Microsoft Q&A answer from an MVP calls it "an unsupported solution": https://learn.microsoft.com/en-us/answers/questions/1664462/rest-api-for-exchange-online

**The newer documented, preview API.** It is called the Exchange Online Admin API. Docs dated 2026-09-25: https://learn.microsoft.com/en-us/exchange/reference/admin-api-overview, https://learn.microsoft.com/en-us/exchange/reference/admin-api-get-started, https://learn.microsoft.com/en-us/exchange/reference/admin-api-authentication

> The features described in this article are currently in Preview, aren't available in all organizations, and are subject to change.
>
> The Exchange Online Admin API is a REST-based administrative surface that enables a focused set of Exchange cmdlets and parameters as POST-only endpoints ... *doesn't* replace the full Exchange administration surface in Exchange Online PowerShell.

- URL: `POST <BaseUrl>/adminapi/v2.0/<TenantID>/<EndpointName>`. The body uses the same `CmdletInput` envelope. `X-AnchorMailbox` is mandatory (`AAD-UPN:`, `AAD-SMTP:`, `OID:`, `MBX:` or `APP:SystemMailbox{bb558c35-...}`). `$select` is supported. Paging uses `ResultSize` and `@odata.nextLink`, which expires after 5 to 10 minutes.
- Permissions are **new**: delegated `Exchange.ManageV2` and app-only `Exchange.ManageAsAppV2` on Office 365 Exchange Online. Scope `https://outlook.office365.com/.default`. Multitenant delegated apps are supported.
- There are only six endpoints: `OrganizationConfig`, `AcceptedDomain`, `Mailbox` (view, plus Send-on-Behalf update), `MailboxFolderPermission` (add/set/remove), `DistributionGroupMember` and `DynamicDistributionGroupMember`. **No Security & Compliance coverage.**
- Microsoft says to retry on 429 and honor `Retry-After`. No numeric limits are published.

**Breakage history:** I found no public CIPP or GitHub issue showing the beta InvokeCommand contract changing. The module itself is the main consumer, so a breaking change would break every PowerShell user too. That gives practical stability, but Microsoft makes no promise.

**Cmdlet coverage:**

- Since RPS retirement (October 2023 for both Exchange and Security & Compliance), every module cmdlet runs over REST. "REST API cmdlets have the same cmdlet names and work just like their remote PowerShell equivalents".
- Remaining exceptions: `Invoke-Command` doesn't work in REST connections (https://learn.microsoft.com/en-us/powershell/exchange/invoke-command-workarounds-rest-api?view=exchange-ps). App-only cannot use `New/Remove-UnifiedGroup` or `Add/Remove-UnifiedGroupLinks`, and app-only eDiscovery cmdlets are unsupported (section 2).
- The nine `Get-EXO*` cmdlets are separate, module-native REST cmdlets with their own endpoints. Through InvokeCommand, use the classic `Get-Mailbox` and similar instead.

**Recommendation for Tenantry:**

- Treat direct InvokeCommand as a **viable fallback path**, with caveats: an unsupported contract, so add a thin adapter layer and contract tests.
- It removes the hosted-PowerShell problems in sections 3 and 4 (assembly conflicts, PS 7.6, PowerShellGet, worker process), and it likely restores IPPS on macOS and Linux.
- Use the documented v2.0 Admin API where its six endpoints suffice.
- Possibly keep the PowerShell worker as a Windows "compatibility mode" if an unsupported path is unacceptable.

### 7.5 Purview capabilities in Microsoft Graph

| Capability | Graph | Permissions (delegated / app) | Notes |
|---|---|---|---|
| eDiscovery (Premium) cases, custodians, non-custodial sources, searches, **legal holds**, review sets, tags, operations/exports | **v1.0** `/security/cases/ediscoveryCases/...` | `eDiscovery.Read.All`, `eDiscovery.ReadWrite.All` (both delegated and app) | Delegated users need the eDiscovery Manager or Administrator role (or Compliance Administrator / Organization Management). Licensing: eDiscovery (Premium) API rights come with E5/A5/G5 or the E5 Compliance / eDiscovery & Audit add-ons. Custodians need an E5-class license. `addToReviewSet` was announced as metered beyond 1 GB per licensed user per month. GA blog: https://devblogs.microsoft.com/microsoft365dev/ediscovery-api-for-microsoft-graph-is-now-generally-available/. Resource: https://learn.microsoft.com/en-us/graph/api/resources/security-ediscoverycase?view=graph-rest-1.0. Holds: https://learn.microsoft.com/en-us/graph/api/security-ediscoverycase-post-legalholds?view=graph-rest-1.0 |
| Audit log search | **v1.0** `POST /security/auditLog/queries` (async; then GET records) | Least `AuditLogsQuery-Entra.Read.All`, up to `AuditLogsQuery.Read.All`, plus per-workload `-Exchange`, `-SharePoint`, `-OneDrive`, `-Endpoint`, `-CRM` (delegated and app) | **Global cloud only.** Tenant daily-submission and concurrency limits scale with licenses. https://learn.microsoft.com/en-us/graph/api/security-auditcoreroot-post-auditlogqueries?view=graph-rest-1.0 |
| Retention labels, file plan descriptors, retention events/event types | **v1.0** `/security/labels/retentionLabels`, `/security/triggers/retentionEvents` | `RecordsManagement.ReadWrite.All` **delegated only** (Application: "Not supported") | Labels only. **Retention policies** (`*-RetentionCompliancePolicy/Rule`) are **not** in Graph; use Security & Compliance. https://learn.microsoft.com/en-us/graph/api/security-labelsroot-post-retentionlabel?view=graph-rest-1.0 |
| Sensitivity labels | **beta, read-only** `GET /security/informationProtection/sensitivityLabels` (also `/me/...`, `/users/{id}/...`) | `InformationProtectionPolicy.Read` (delegated), `InformationProtectionPolicy.Read.All` (app) | No create or update of labels or label policies in Graph; use Security & Compliance `New/Set-Label` and `*-LabelPolicy`. https://learn.microsoft.com/en-us/graph/api/security-informationprotection-list-sensitivitylabels?view=graph-rest-beta |
| DLP alerts | **v1.0** `/security/alerts_v2` (Defender XDR alert stream, filter by `serviceSource`) | `SecurityAlert.Read.All` / `SecurityAlert.ReadWrite.All` | Alerts only. **DLP policies, rules and SITs are not in Graph**; use Security & Compliance. (The DLP `serviceSource` enum value is from the enum reference and was not re-fetched this session.) https://learn.microsoft.com/en-us/graph/api/resources/security-alert?view=graph-rest-1.0 |
| Alert policies (`*-ProtectionAlert`) | **No** | n/a | Security & Compliance only. |

### Updated recommendation

- Build the Exchange and Purview layer as **HTTP clients** over `adminapi/beta/{tenant}/InvokeCommand`, with region discovery for Security & Compliance and one MSAL token per audience:
  - `https://outlook.office365.com/.default`
  - `https://ps.compliance.protection.outlook.com/.default`
  - `https://dataservice.o365filtering.com/.default`, only for search-only eDiscovery cmdlets, if ever needed.
- Prefer Graph where it exists: eDiscovery, audit search, retention labels, message trace, alerts.
- Keep the PowerShell worker idea only as an optional fallback.
- Spike to confirm:
  - a delegated token from **our** app with only `Exchange.Manage` gets `aud=https://ps.compliance.protection.outlook.com` and can run `Get-DlpCompliancePolicy` on Windows, macOS and Linux;
  - 302 region discovery works with a non-module client;
  - paging and `$batch` behave as CIPP shows.

## 8. Spike results (2026-10-20, live tenant, Windows)

Run with `tools/Tenantry.Spike` against a production tenant, delegated sign-in as a Global
Administrator. Every check passed. Findings that change or confirm the design:

| Assumption | Result |
|---|---|
| Azure PowerShell's client (`1950a258-...`) as the setup fallback | **Fails.** Entra returns AADSTS65002: first-party apps need preauthorisation for Graph scopes. Use Microsoft Graph Command Line Tools (`14d82eec-...`) instead, which may request any Graph scope. |
| Graph `oauth2PermissionScopes` names are unique | **No.** Graph publishes `User.ReadBasic.All` twice. Look-ups must tolerate duplicates. |
| `InformationProtectionPolicy.Read.All` is a delegated scope | **No.** It is application-only; the delegated name is `InformationProtectionPolicy.Read`. |
| Exchange REST `InvokeCommand` with our own app's token (`Exchange.Manage` only) | Works. |
| Purview REST with no compliance permission, `Exchange.Manage` only | Works. The token's `aud` is `https://ps.compliance.protection.outlook.com` and its `scp` lists the Exchange grant's scopes. |
| Purview region discovery via the `EXOModuleFile` 302 | Works; the regional host is used for cmdlet calls. |
| Paging via `@odata.nextLink` at `odata.maxpagesize=1000` | Works: over 1,000 recipients took 2 requests (about 38 s; Exchange is slow, so sync must use narrow selects). |
| `ResultSize` limits the total | **No.** The service treats it as a page size: `-ResultSize 2` returned every mailbox two at a time. The runner now strips it, sends it as `maxpagesize`, and stops paging at the limit. |
| One SharePoint token (root audience) works on the `-admin` site | Yes, 200 on both. |
| Graph message trace (`/admin/exchange/tracing/messageTraces`) | Works; no extra service principal provisioning was needed in this tenant. |
| Requiring assignment on the service principal plus assigning the setup admin | Works; sign-in with the new app succeeded with no consent prompt. |
| MSAL encrypted token cache on Windows | Persistent (DPAPI). |

Not yet run: macOS and Linux (WSL) checks, and authentication-method writes (the 10-minute
MFA claims challenge), which need a test-marked user.
