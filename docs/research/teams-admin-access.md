# Teams admin access without hosting PowerShell

Research date: 2026-11-13. Question: can a .NET desktop admin app, signing in delegated through its own multi-tenant public-client registration (MSAL), manage the settings behind the MicrosoftTeams module's `*-Cs*` cmdlets (meeting, messaging, app permission/setup, calling, events, update policies, federation/guest settings, policy assignment) by calling the service over REST, the way it already calls Exchange Online's `InvokeCommand`?

Short answer: yes, technically. The token side is documented and supported. The REST surface is not. Details and sources below.

## 1. How the MicrosoftTeams module talks to the service

**Host.** `*-Cs*` cmdlets are AutoRest-generated wrappers over the "Teams ConfigAPI" at `https://api.interfaces.records.teams.microsoft.com`. That host is a service principal name of the first-party app **Skype and Teams Tenant Admin API** (appId `48ac35b8-9aa8-4d74-927d-1f4a14a0b239`). Other SPNs on that app are `ring0.` and `msit.` variants.
- Michev, "Teams Remote PowerShell updates and new API endpoints" (May 2022, updated Jul 2023): https://michev.info/blog/post/3870/teams-remote-powershell-updates-and-new-api-endpoints
- First-party app inventory: https://github.com/maciejporebski/azure-ad-first-party-apps-permissions/blob/master/apps/Skype%20and%20Teams%20Tenant%20Admin%20API.md

**Unlike Exchange, there is no generic "run a cmdlet" endpoint.** Each cmdlet maps to a resource-style REST call (Michev: "The old InvokeCommand method is not used for Teams"). The generated client lives in `Microsoft.Teams.ConfigAPI.Cmdlets.private.dll` (about 940 internal cmdlets). Since module 4.x, PowerShell remoting/WinRM is gone, and 7.3.0 (Aug 2025) removed the last `Get-CsOnlinePowerShellEndpoint` cmdlets.

**Observed path namespaces** (Michev, CIPP):

| Area | Path under `https://api.interfaces.records.teams.microsoft.com/` |
| --- | --- |
| Policy/config objects (meeting, messaging, app permission/setup, calling, events, update, client config...) | `Skype.Policy/configurations/{Type}` (list) and `Skype.Policy/configurations/{Type}/configuration/{Identity}` (one) |
| Legacy policy write surface the module still uses for some writes | `Skype.Policy/tenants/policies` (CIPP: returns 403 `40301` under GDAP) |
| Group policy assignment | `Skype.Policy/groupPolicyAssignments` |
| Users (`Get-CsOnlineUser`) | `Teams.User/users/{upn}?$skipuserpolicies=False&defaultpropertyset=Extended` |
| Tenant + service discovery | `Teams.Tenant/tenants` |
| Emergency locations | `Skype.Ncs/locations` |
| Resource accounts | `Teams.PlatformService/v2/ApplicationInstances` |
| Call queues | `Teams.VoiceApps/callqueues` |

**Request shape.** Ordinary REST with JSON: GET with query-string parameters; PUT/POST/PATCH/DELETE with a JSON body. `{Type}` is the cmdlet noun without `Cs`, for example `TeamsMeetingPolicy`, `TeamsMessagingPolicy`, `TeamsAppPermissionPolicy`, `TeamsAppSetupPolicy`, `TeamsCallingPolicy`, `TeamsUpdateManagementPolicy`, `TeamsClientConfiguration`. Some nouns differ: `Set-CsTenantFederationConfiguration` maps to type `TenantFederationSettings`. `Identity` is `Global`, `Tag:<name>`, or the policy name. Headers seen from the module and the Teams admin center:

```http
GET https://api.interfaces.records.teams.microsoft.com/Skype.Policy/configurations/TeamsMeetingPolicy/configuration/Global
Authorization: Bearer {token for 48ac35b8-.../.default}
Accept: application/json
x-ms-correlation-id: {guid}
x-authz-scope: tenant
X-Requested-With: XMLHttpRequest          (admin center; CIPP sends it on raw paths)
X-MS-CmdletName: Get-CsUser_Get            (module; Michev says some responses depend on it)
X-MS-Target-Uri: https://admin0e.online.lync.com/   (legacy-backed cmdlets only)
```

Set = PUT to the same URL with only the changed properties (merge). CIPP reads first and includes the object's `Key` envelope when present. New = PUT to a new identity. Remove = DELETE.

**Regional/tenant routing.** `GET /Teams.Tenant/tenants` returns `serviceDiscovery.Endpoints.ConfigApiEndpoint` (per-tenant ConfigAPI host), `serviceDiscovery.Headers.X-MS-Forest`, `serviceDiscovery.Endpoints.AdminServiceEndpoint`, and `verifiedDomains`. Federation and ACS types are still backed by the legacy `OcsPowershellWebservice`: send `x-ms-target-uri: https://{AdminServiceEndpoint}/OcsPowershellWebservice`, `x-ms-tenant-id: {tenantId}`, and the query `?adminDomain=contoso.onmicrosoft.com`. The default global host works for most tenants without discovery.

**Paging.** Not documented anywhere I found. Policy collections are small and come back as one array in CIPP's `ListAll`. `Teams.User/users` listing paging is unverified; capture it with Fiddler before relying on it, or use Graph `userConfigurations` (section 4).

**Responses** can be gzip-encoded. CIPP calls this out as a cross-platform pitfall. In .NET, set `HttpClientHandler.AutomaticDecompression`.

## 2. Token audience, and whether a third-party app can get it

**Audience:** resource `48ac35b8-9aa8-4d74-927d-1f4a14a0b239`. Request scope `48ac35b8-9aa8-4d74-927d-1f4a14a0b239/.default`, or `https://api.interfaces.records.teams.microsoft.com/user_impersonation`.

**Delegated scopes on that resource** (inventory above): `user_impersonation` (`e60370c1-e451-437e-aa6e-d76df38e5f15`, "Access Microsoft Teams and Skype for Business data as the signed-in user"), plus internal ones (`CEH_service`, `middletier_service`, ...). App roles: `application_access` (`15391dcf-e272-4693-b514-06792e967d66`) and others.

**Third-party app, delegated: documented as supported.** The `Connect-MicrosoftTeams -AccessTokens` docs tell admins "using your own application" to add Microsoft Graph delegated permissions, then "Skype and Teams Tenant Admin API" (under *APIs my organization uses*), add its delegated permissions, and "Grant admin consent to both". So a `requiredResourceAccess` entry is valid:

```json
{ "resourceAppId": "48ac35b8-9aa8-4d74-927d-1f4a14a0b239",
  "resourceAccess": [ { "id": "e60370c1-e451-437e-aa6e-d76df38e5f15", "type": "Scope" } ] }
```

CIPP's multi-tenant app (`signInAudience: AzureADMultipleOrgs`, `isFallbackPublicClient: true`) ships exactly this entry: `Cache_SAMSetup/SAMManifest.json` around line 181 in https://github.com/KelvinTegelaar/CIPP-API. Admin consent is needed, and the signed-in user still needs a Teams admin role, because the service applies RBAC from the token.

**Known blocks:**
- **AADSTS650057** (resource not listed in the app's requested permissions) when you request the Teams scope without the entry above. Fix: add it and re-consent.
- **AADSTS650052** / missing service principal: some newly created tenants have no SP for the Skype and Teams Tenant Admin API. Fix: create it (`New-MgServicePrincipal -AppId 48ac35b8-9aa8-4d74-927d-1f4a14a0b239`) or let consent create it. Community KB only: https://kb.machsol.com/Knowledgebase/55727/Microsoft-Skype-and-Teams-Admin-API-Lacks-Service-Principal
- **App-only is different.** For certificate or client-credential auth, Microsoft says "There's no need to configure any API permission for 'Skype and Teams Tenant Admin API'. Configuring this permission might result in failures." Instead, give the SP an Entra role (Teams Administrator) plus Graph `Organization.Read.All`: https://learn.microsoft.com/microsoftteams/teams-powershell-application-authentication
- I found no preauthorization (`knownClientApplications`) restriction blocking third-party clients from `user_impersonation`.

**`Connect-MicrosoftTeams -AccessTokens` takes two tokens of the same kind** (both delegated or both app-only), in this order: https://learn.microsoft.com/powershell/module/microsoftteams/connect-microsoftteams
1. Microsoft Graph: scope `https://graph.microsoft.com/.default`. For delegated non-Cs cmdlets the docs list `User.Read.All`, `Group.ReadWrite.All`, `AppCatalog.ReadWrite.All`, `TeamSettings.ReadWrite.All`, `Channel.Delete.All`, `ChannelSettings.ReadWrite.All`, `ChannelMember.ReadWrite.All`. App-only always needs `Organization.Read.All`.
2. Skype and Teams Tenant Admin API: scope `48ac35b8-9aa8-4d74-927d-1f4a14a0b239/.default`.

```powershell
Connect-MicrosoftTeams -AccessTokens @($GraphToken, $TeamsToken)
```

The docs sample uses ROPC/device code, but the module only reads the tokens. Tokens from MSAL interactive (PKCE) should work the same way; this inference is untested. 6.0.0 (Feb 2024) fixed AccessTokens expiry handling. The tokens are not refreshed, so a long session needs a reconnect with fresh tokens (about an hour).

## 3. Who calls the Teams admin service over plain HTTP

**CIPP-API (PowerShell code, but no MicrosoftTeams module; raw HTTP).** Commit `b4ab8d9` (2026-11-02):
- `Modules/CIPPCore/Public/GraphHelper/New-TeamsRequestV2.ps1`
  - Lines 7-12: the module's writes go through `/Skype.Policy/tenants/policies`, "which returns 40301 Forbidden under CSP/GDAP". The Teams admin center (ACMS) uses `/Skype.Policy/configurations/{Type}/configuration/{Identity}`, "which authorizes fine".
  - Line 123: token scope `48ac35b8-9aa8-4d74-927d-1f4a14a0b239/.default` (delegated by default, `-AsApp` optional).
  - Line 128: host `api.interfaces.records.teams.microsoft.com`.
  - Lines 132-143: service discovery via `GET /Teams.Tenant/tenants`.
  - Lines 144-158: base URL, headers (`Authorization`, `Accept`, `x-authz-scope: tenant`, `x-ms-correlation-id`, optional `x-ms-forest`), federation target-uri headers and `?adminDomain=`.
  - Lines 160-189: raw-path mode adds `X-Requested-With: XMLHttpRequest` and `Content-Type: application/json`.
  - Lines 193-261: GET (one or all), PUT merge with read-modify-write `Key` envelope, PUT to create, DELETE.
  - Lines 112-120: noun-to-type aliases (`TenantFederationConfiguration` maps to `TenantFederationSettings`).
- `Modules/CIPPBaselines/Public/Executors/Invoke-CIPPBaselineTeamsRequest.ps1` lines 6-12, 31: every `Set-Cs*` remediation goes through V2 "because the classic Cs* policy surface returns 403 under CSP/GDAP".
- `Modules/CIPPDB/Public/DBCache/Set-CIPPDBCacheCsTeamsMeetingPolicy.ps1` line 27: `Get` of `TeamsMeetingPolicy` / `Global`. Sibling files cover the messaging policy, app permission policy, client configuration, files policy and tenant federation configuration.
- `Modules/CIPPCore/Public/GraphHelper/Get-CippTeamsLocationLookup.ps1` line 28: `Skype.Ncs/locations`.
- `.github/instructions/auth-model.instructions.md` line 88: scope table entry for Teams/Skype.
- I found no CIPP code for **policy assignment** (`Grant-Cs*`) over ConfigAPI. Phone-number work uses Graph `admin/teams/telephoneNumberManagement` (`Remove-CIPPUserTeamsPhoneDIDs.ps1` line 16).

**Microsoft365DSC** uses the MicrosoftTeams module (`Connect-MicrosoftTeams`) for its Teams resources and does not call ConfigAPI itself: https://microsoft365dsc.com/user-guide/get-started/authentication-and-permissions/

**Michev** (above) reproduced module calls with `Invoke-WebRequest` from captured requests.

## 4. What Graph covers today

| Area | Endpoint | Version | Permission (delegated / app) |
| --- | --- | --- | --- |
| Teams, channels, members, team settings | `/teams/{id}`, `/teams/{id}/channels`, `/teams/{id}/members` | v1.0 | `TeamSettings.ReadWrite.All`, `Channel*.All`, `TeamMember.ReadWrite.All` (+ `Group.ReadWrite.All`) |
| Tenant app settings (user requests for app access, personal-scope RSC) | `GET/PATCH /teamwork/teamsAppSettings` | v1.0 | `TeamworkAppSettings.Read(Write).All`; **delegated only** |
| Org app catalog (upload/update/remove custom apps) | `/appCatalogs/teamsApps` | v1.0 | `AppCatalog.ReadWrite.All` |
| Team templates | `/teamwork/teamTemplates` (beta), create from template via `POST /teams` with `template@odata.bind` | beta / v1.0 | `TeamTemplates.Read.All`; `Team.Create` |
| User effective policies, phone numbers, account type | `GET /admin/teams/userConfigurations` | **v1.0 GA** | `TeamsUserConfiguration.Read.All` (both) |
| Policy ID lookup | `GET /admin/teams/policy/getPolicyId(type='TeamsMeetingPolicy',name='AllOn')` | v1.0 | `TeamsPolicyUserAssign.ReadWrite.All` |
| **Assign/unassign policy to users** | `POST /admin/teams/policy/userAssignments/assign` (and `unassign`), body `{ "value": [ { "userId", "policyType", "policyId" } ] }`, 204 | beta (resource also in v1.0 docs) | `TeamsPolicyUserAssign.ReadWrite.All` (both) |
| Phone number assign/unassign | `/admin/teams/telephoneNumberManagement/numberAssignments` | beta | `TeamsTelephoneNumber.ReadWrite.All` |

Sources: https://learn.microsoft.com/graph/api/teamsappsettings-get · https://learn.microsoft.com/graph/api/teamsadministration-teamsadminroot-list-userconfigurations · https://learn.microsoft.com/graph/api/resources/teamsadministration-teamspolicyassignment · https://learn.microsoft.com/graph/api/teamsadministration-teamspolicyuserassignment-assign?view=graph-rest-beta · Message Center MC1169072 (Oct 2025, updated Jan 2026): https://mc.merill.net/message/MC1169072

I checked the permissions for teamsAppSettings, userConfigurations, getPolicyId and assign against their Learn pages. The rows for teams/channels, app catalog, templates and phone numbers (including `unassign`) come from general Graph knowledge; confirm those permission names on each page before writing the manifest.

The Teams admin APIs above are **global cloud only**. Graph does **not** cover the policy *definitions*: meeting/messaging/calling/app setup/app permission/events/update policies, federation/external access, guest config, or group policy assignment. Those still need ConfigAPI or the module.

## 5. Risks

- **Unsupported surface.** ConfigAPI paths, headers and payloads are not published. They are reverse-engineered from the module and the Teams admin center. Microsoft can change them without notice, and the module hides changes that would break a direct client.
- **Two write surfaces.** The module's `Skype.Policy/tenants/policies` and the admin center's `Skype.Policy/configurations/...` behave differently by auth context (CIPP's 403-under-GDAP finding). For direct tenant admins the module path presumably works. Pick the configurations surface and test both read and write per type.
- **Frequent parameter churn.** Breaking renames and removals in policy properties land often. Examples: 7.0.0 (Apr 2025) deprecated `CustomizeFederation`; 7.1.0 (Jun 2025) removed `AllowPublicUsers` and `EnablePublicCloudAccess`; 7.7.0 (Apr 2026) renamed a `Set-CsTenantFederationConfiguration` parameter; 7.8.0 (May 2026) renamed federation-settings parameters; 8.0.0 (Sep 2026). A hard-coded property schema will go stale. Render properties from the GET response instead. https://learn.microsoft.com/microsoftteams/teams-powershell-release-notes
- **Auth changes in the module.** 6.3.0 (Jun 2024) requires PowerShell 7.2+ or 5.1. 7.8.1-preview/7.9.0 (Jul 2026) made WAM the default for user sign-in, with a temporary `-DisableWAM`. `-AccessTokens` sign-in is unaffected. Microsoft could later restrict `user_impersonation` to preauthorized first-party clients; nothing indicates that today.
- **Rate limits.** None published for ConfigAPI. Expect 429s with `Retry-After` and back off. Graph Teams limits apply to the Graph endpoints.
- **Graph policy assignment is beta,** and Microsoft says "Use of these APIs in production applications is not supported".
- **Tenant prerequisites.** The SP may be missing in new tenants (AADSTS650052), and admin consent is needed per tenant.

## Recommendation

1. **Graph first** for what it covers: user effective policies (v1.0), policy ID lookup (v1.0), user policy assign/unassign (beta), teamsAppSettings, app catalog, teams and channels.
2. **Policy definitions and org settings: offer both paths behind one interface.**
   - *Default, supported:* the existing pwsh 7 console. Request the Graph token and the `48ac35b8-.../.default` token through the app's own registration, then pass them in with `Connect-MicrosoftTeams -AccessTokens @($graph, $teams)`.
   - *Optional, faster, unsupported:* a native ConfigAPI client modeled on CIPP's `New-TeamsRequestV2` (`Skype.Policy/configurations`, service discovery, merge PUT). Mark it experimental and fall back to the module when it fails.
3. Add `requiredResourceAccess` for `48ac35b8-9aa8-4d74-927d-1f4a14a0b239` / `user_impersonation`, and handle AADSTS650057/650052 with clear consent and SP-creation guidance.
