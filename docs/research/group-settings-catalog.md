# Group settings catalog

Every setting an admin can view or change on a group in the Microsoft 365 admin center (MAC), the Microsoft Entra admin center and the Exchange admin center (EAC), with the API that reads and writes it. Sources are Microsoft Learn pages read 2026-11-05 (list at the end). Nothing here was tested against a tenant. Where Learn is silent, the row says **Undocumented**.

Examples use `contoso.example`.

## Group kinds and abbreviations

| Abbrev | Kind | Graph `groupTypes` / `mailEnabled` / `securityEnabled` | Graph can write? | Exchange cmdlets |
| --- | --- | --- | --- | --- |
| M365 | Microsoft 365 group | `["Unified"]` / true / true or false | Yes | `Get/Set-UnifiedGroup`, `*-UnifiedGroupLinks` |
| M365-dyn | Microsoft 365 group, dynamic membership | `["Unified","DynamicMembership"]` | Yes | as M365 (membership read-only) |
| SG | Cloud security group, assigned | `[]` / false / true | Yes | none (not a recipient) |
| SG-dyn | Cloud security group, dynamic | `["DynamicMembership"]` / false / true | Yes | none |
| MESG | Mail-enabled security group | `[]` / true / true | **No, read-only** | `Get/Set-DistributionGroup`, `*-DistributionGroupMember` |
| DL | Distribution list (distribution group) | `[]` / true / false | **No, read-only** | `Get/Set-DistributionGroup`, `*-DistributionGroupMember` |
| DDL | Dynamic distribution group | not in Graph or Entra at all | **No** | `Get/Set-DynamicDistributionGroup`, `Get-DynamicDistributionGroupMember` |

"Kinds" columns below use these abbreviations. "Mail kinds" means M365, MESG, DL, DDL.

## Gotchas summary

1. **Graph is read-only for MESG and DL.** The groups overview lists them as "No (read-only)". Every PATCH on those kinds (description included) must go through Exchange `Set-DistributionGroup`. DDLs are not in Graph at all.
2. **Exchange-stored Graph properties must be PATCHed alone.** `accessType`, `allowExternalSenders`, `autoSubscribeNewMembers`, `hideFromAddressLists`, `hideFromOutlookClients`, `isFavorite`, `isSubscribedByMail`, `unseen*` must each go in a PATCH with no other directory properties, need `Group.ReadWrite.All` (`Directory.ReadWrite.All` is not accepted), and return `200 OK` instead of `204`. The update doc says these (and other non-core group APIs) are **delegated-only**. App-only tokens cannot set them.
3. **Exchange-stored Graph properties are GET-by-id only.** They need `$select` and are "Supported only on the Get group API (`GET /groups/{ID}`)": they cannot be read from `GET /groups` lists. They are also excluded from delta queries. A list view needs one GET per group, or Exchange `Get-UnifiedGroup`.
4. **Don't set `autoSubscribeNewMembers` in the POST that creates the group.** Set it in a later PATCH. It also can't be `true` while Exchange `SubscriptionEnabled` is `$false`, and `SubscriptionEnabled` itself is Exchange-PowerShell-only.
5. **Several M365 settings have no Graph v1.0 equivalent.** Exchange only: moderation, accept/reject sender lists as Exchange sees them, MailTip, `GrantSendOnBehalfTo`, Send As, custom attributes, `AlwaysSubscribeMembersToCalendarEvents`, `SubscriptionEnabled`, `CalendarMemberReadOnly`, `ConnectorsEnabled`, `RequireSenderAuthenticationEnabled`. The welcome message is Exchange-only in v1.0 (`welcomeMessageEnabled` exists only in beta).
6. **The display name limit differs.** Graph allows 256 characters. `Set-UnifiedGroup -DisplayName` documents a 64-character limit, and `Set-DistributionGroup` documents 256. The app should enforce 64 for M365 when writing via Exchange.
7. **`mailNickname` rules differ between pages.** The resource page excludes `` @ () \ [] " ; : <> , SPACE``. The update page also excludes `.`. Exchange `-Alias` allows periods when they are surrounded by other characters. Validate against the stricter Graph set when writing via Graph.
8. **Some properties are immutable after creation.** These are `isAssignableToRole`, `visibility = HiddenMembership` (M365 only), `resourceBehaviorOptions`, `uniqueName` (settable once if null), group type (Entra: delete and recreate), and `assignedLabels` on cloud **security** groups. Exchange `HiddenGroupMembershipEnabled` can't be undone.
9. **Sensitivity labels are delegated-only.** `assignedLabels` updates need delegated auth plus a Purview-supported admin role. App-only is not supported. Labels require Entra ID P1, `EnableMIPLabels=True` and a label sync. They take up to 24 h after sync to appear. Exchange equivalent: `Set-UnifiedGroup -SensitivityLabelId`, read back as `SensitivityLabel`.
10. **Dynamic membership needs P1 licenses.** Entra ID P1 (or Intune for Education) is required for each unique user in dynamic groups. A tenant can have at most 15,000 dynamic groups. Rules are capped at 3,072 characters. M365 dynamic groups hold users only. Users and devices can't be mixed. Converting assigned to dynamic removes members who don't match the rule.
11. **Rule validation APIs are beta only.** `POST /beta/groups/{id}/evaluateDynamicMembership` and `POST /beta/groups/evaluateDynamicMembership` test one member against a rule. They are **delegated-only** and beta (not for production per Microsoft). No documented API does a pure syntax check. The portal's "Validate rules" tab takes 20 users or devices at a time.
12. **Role-assignable groups are locked down.** `isAssignableToRole` can only be set at creation, needs Privileged Role Administrator, forces `securityEnabled=true` and Private/Hidden visibility. It forbids dynamic membership and nesting. The tenant limit is 500. Membership changes need `RoleManagement.ReadWrite.Directory`; `Group.ReadWrite.All` "won't work".
13. **Restore depends on group kind.** M365 and cloud security groups are soft-deleted for 30 days (not customizable). Soft delete for cloud security groups is **preview**. Graph's delete doc says distribution groups "are permanently deleted immediately". MESG restore is **undocumented**. No documented restore exists for DDLs.
14. **Naming policy applies to M365 groups only.** It needs Entra ID P1. Global Administrator and User Administrator are exempt. Blocked words are exact-match, case-insensitive, max 5,000. Use `POST /groups/{id}/validateProperties` (or `/directoryObjects/validateProperties` before create). It returns only the first failure. DLs have a separate Exchange naming policy (`DistributionGroupNameBlockedWordList` on `Set-OrganizationConfig`). `Set-DistributionGroup -IgnoreNamingPolicy` exempts a DL.
15. **Propagation delays.** MAC: "up to 30 minutes before users outside the organization can email the group". Graph member adds on new groups can 400 until replication catches up. DDL membership refreshes at least every 24 h; recalculation after a rule change takes up to 2 h when the rule matches more than 5,000 recipients. Restoring a group with SharePoint content takes up to 24 h. Group-based licensing processing time is unspecified.
16. **Set-UnifiedGroup has no bypass-moderation or notification parameters.** It lacks `BypassModerationFromSendersOrMembers`, `SendModerationNotifications`, `ReportToManagerEnabled`, `ReportToOriginatorEnabled`, `SendOofMessageToOriginatorEnabled`, `MemberJoinRestriction` and `MemberDepartRestriction`. `Get-UnifiedGroup` does expose a `BypassModerationFromSendersOrMembers` property, but no documented Set parameter writes it.
17. **Send As and Send on behalf.** For one trustee, set one or the other, not both: "If you configure both, it will default to Send as" (archived MAC doc). Guests can't be granted either.
18. **Group-based licensing sources disagree.** Graph says it is available "only for security groups and Microsoft 365 Groups with securityEnabled set to true". The MAC page says "security groups, mail-enabled groups, or Microsoft 365 groups". Nested groups aren't supported: only first-level members get licenses.

## Microsoft 365 admin center: Teams & groups > Active teams & groups

Learn documents these items. It does not publish a full field-by-field list of the MAC group pane. Labels are quoted from the pages cited.

### General tab

| Setting (portal label) | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Name ("Edit name and description") | M365 (MAC); MESG/DL also shown in MAC (field list undocumented) | Graph `GET /v1.0/groups/{id}` `displayName` (default property). EXO: `Get-UnifiedGroup` / `Get-DistributionGroup` `DisplayName` | M365/SG: Graph `PATCH /groups/{id}` `{"displayName":"..."}`. M365 alt: `Set-UnifiedGroup -DisplayName`. MESG/DL: `Set-DistributionGroup -DisplayName` | Graph max 256 characters and can't be cleared. `Set-UnifiedGroup` documents max 64. The naming policy applies to M365. DL naming policy is separate. |
| Description | M365, SG, MESG, DL | Graph `description`. EXO: `Get-UnifiedGroup` `Notes`; `Get-DistributionGroup` `Description` (MultiValuedProperty, EXO only) | M365/SG: PATCH `description`. M365 alt: `Set-UnifiedGroup -Notes`. MESG/DL: `Set-DistributionGroup -Description` | Graph PATCH on MESG/DL fails because Graph is read-only for them. |
| Primary email address ("edit email address") | Mail kinds | Graph `mail` (read-only). EXO `PrimarySmtpAddress` | M365: `Set-UnifiedGroup -PrimarySmtpAddress addr`. MESG/DL: `Set-DistributionGroup -PrimarySmtpAddress`. DDL: `Set-DynamicDistributionGroup -PrimarySmtpAddress` | Graph `mail` and `proxyAddresses` are read-only. `-PrimarySmtpAddress` and `-EmailAddresses` can't be combined in one command. MAC says the email domain can be changed after creation. |
| Email aliases (proxy addresses) | Mail kinds | Graph `proxyAddresses` (read-only, `SMTP:` = primary). EXO `EmailAddresses` (ProxyAddressCollection) | `Set-UnifiedGroup` / `Set-DistributionGroup` / `Set-DynamicDistributionGroup -EmailAddresses @{Add="smtp:a@contoso.example"; Remove="smtp:b@contoso.example"}` | Only SMTP syntax is validated. `SMTP:` may appear once. Unprefixed values are SMTP. M365 also has `SPO:` addresses; don't remove them. |
| Privacy (Public / Private) | M365 (MESG wizard also shows Privacy per EAC doc) | Graph `visibility` (default prop). Graph `accessType` (`$select`, GET by id, values `none/private/secret/public`). EXO `AccessType` | Graph PATCH `visibility` = `Public`/`Private`. EXO `Set-UnifiedGroup -AccessType Public|Private` | `HiddenMembership` can only be set at create (M365). An empty value is interpreted as Public. Role-assignable groups are always Private. A sensitivity label may control privacy. `accessType` must be PATCHed alone. |
| Sensitivity label | M365 (MAC display undocumented) | Graph `assignedLabels` (`$select`). EXO `Get-UnifiedGroup` `SensitivityLabel` | See Entra section | See gotcha 9. |
| Teams (team exists) | M365 | Graph `resourceProvisioningOptions` contains `Team`. `GET /groups/{id}/team` | Create team: `PUT /groups/{id}/team` (Teams API). Archive: `team: archive` / `unarchive` | Note only. Team settings are not group settings. `isArchived` is read via `/team`. |

### Members tab ("Membership" in the docs)

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Owners | M365, SG, MESG, DL, DDL | Graph `GET /groups/{id}/owners`. EXO `ManagedBy` (DL/MESG/DDL), `Get-UnifiedGroupLinks -LinkType Owners` | M365/SG: Graph `POST /groups/{id}/owners/$ref`, `DELETE .../owners/{id}/$ref`. MESG/DL: `Set-DistributionGroup -ManagedBy @{Add=..;Remove=..}`. DDL: `Set-DynamicDistributionGroup -ManagedBy` (single value) | Graph limit is 100 owners. Graph add-owner only works for security and M365 groups. DL owners must be mailboxes, mail users or MESGs. Owners aren't automatically members. Editing a DL you don't own needs `-BypassSecurityGroupManagerCheck`. EAC says a DL and an MESG need at least one owner, and an MESG needs at least one member. A DDL has exactly one owner. |
| Members | M365, SG, MESG, DL | Graph `GET /groups/{id}/members` (`transitiveMembers` for nested). EXO `Get-UnifiedGroupLinks -LinkType Members`, `Get-DistributionGroupMember` | M365/SG: Graph `POST /groups/{id}/members/$ref`. MESG/DL: `Add-/Remove-/Update-DistributionGroupMember` | Dynamic groups reject manual adds. M365 members are users only. DL members can't be M365 groups or non-mail-enabled users. EAC member filtering is unreliable above 5,000 members. New groups may 400 on member add until replicated; retry. |
| DDL members (view) | DDL | `Get-DynamicDistributionGroupMember`. Preview: `Get-Recipient -RecipientPreviewFilter <filter> -ResultSize 100` | n/a (rule-based) | Membership list is stored, so it can be up to 24 h stale. |

### Settings tab

| Setting (portal label) | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| "Let people outside the organization to email this group" (also "Allow external senders to email this group") | M365 (MAC). MESG in EAC | Graph `allowExternalSenders` (`$select`, GET by id, delegated). EXO `RequireSenderAuthenticationEnabled` (inverse sense) | Graph PATCH `{"allowExternalSenders":true}` alone, delegated, `Group.ReadWrite.All`. EXO `Set-UnifiedGroup -RequireSenderAuthenticationEnabled $false`. MESG/DL/DDL: `Set-DistributionGroup` / `Set-DynamicDistributionGroup -RequireSenderAuthenticationEnabled` | Default false (external blocked). Up to 30 min to take effect. Learn doesn't explicitly state that Graph `allowExternalSenders` and EXO `RequireSenderAuthenticationEnabled` are the same attribute; the descriptions match. Verify in a test tenant. |
| "Send copies of group conversations and events to group members" | M365 | Graph `autoSubscribeNewMembers` (`$select`, GET by id). EXO `AutoSubscribeNewMembers` | Graph PATCH alone, delegated only, not in create POST. EXO `Set-UnifiedGroup -AutoSubscribeNewMembers` / `-AutoSubscribeNewMembers:$false` (switch) | Affects only members added after it is enabled. Internal members only; guests are always subscribed. Can't be true while `SubscriptionEnabled` is false. Members can unfollow. |
| Hide from my organization's global address list | Mail kinds | Graph `hideFromAddressLists` (M365, `$select`, GET by id). EXO `HiddenFromAddressListsEnabled` | Graph PATCH alone (M365). EXO `Set-UnifiedGroup` / `Set-DistributionGroup` / `Set-DynamicDistributionGroup -HiddenFromAddressListsEnabled $true` | For M365, the group still shows in members' Outlook nav unless `HiddenFromExchangeClientsEnabled` is also set. Hidden DLs still accept mail typed by address. |

## Microsoft Entra admin center: Groups > group

### Properties

| Setting (portal label) | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Group type | all Entra kinds | Graph `groupTypes`, `mailEnabled`, `securityEnabled` | Not changeable in Entra: "you must delete the group and create a new one". Graph `securityEnabled` is listed as updatable | Changing `securityEnabled` on an existing M365 group via PATCH is listed but its effects are **undocumented**. Treat it as risky. |
| Group name | M365, SG (MESG/DL read-only in Entra) | `displayName` | PATCH `displayName` | Can't start with a space (Entra doc). Naming policy applies (M365). |
| Group description | M365, SG | `description` | PATCH `description` | As above. |
| Group email address | M365 (set at create) | `mail` | see MAC row | Read-only in Graph. |
| Microsoft Entra roles can be assigned to the group | M365, SG | Graph `isAssignableToRole` (default prop) | Create only: POST with `isAssignableToRole:true`, `securityEnabled:true`, visibility Hidden/Private, no `DynamicMembership` | Immutable. Needs Privileged Role Administrator, Entra ID P1 and `RoleManagement.ReadWrite.Directory`. Limit 500 per tenant. No nesting. Delete via Graph needs PRA or creator. |
| Membership type (Assigned / Dynamic User / Dynamic Device) | M365, SG | `groupTypes` contains `DynamicMembership`. `membershipRule`. `membershipRuleProcessingState` | PATCH `groupTypes` (keep `Unified` if present) + `membershipRule` + `membershipRuleProcessingState:"On"`. To static: remove `DynamicMembership`, set state `Paused` (Entra's own sample) | `groupTypes`/`membershipRule` aren't in the v1.0 update page's property table, but Entra's conversion doc uses `Update-MgGroup` with them. Locked if role-assignable. Members who don't match the rule are removed on conversion, and access can lapse until processing finishes. M365 dynamic groups support users only. |
| Object ID | all | `id` | n/a | |
| Sensitivity label | M365 (Entra UI). Cloud SG via Graph | Graph `assignedLabels` (`$select`, `[{labelId, displayName}]`). EXO `SensitivityLabel` | Graph PATCH `{"assignedLabels":[{"labelId":"guid"}]}`, delegated only, plus supported Purview admin role. Cloud SG least privilege: `Group.ManageProtection.All` (delegated). EXO `Set-UnifiedGroup -SensitivityLabelId guid` | Entra ID P1 required. Immutable once set on cloud SGs. Label must be published to the signed-in user and scoped to Groups & Sites. Changing label settings in Purview doesn't re-apply to labeled groups. |
| Preferred data location | M365 (multi-geo) | `preferredDataLocation` | PATCH, with role User Account Admin / Directory Writer / Exchange Admin / SharePoint Admin. EXO `Set-UnifiedGroup -MailboxRegion` | Only meaningful in multi-geo tenants. |
| Classification (classic) | M365 | `classification` | PATCH (value must be in `ClassificationList` directory setting). EXO `Set-UnifiedGroup -Classification` | Superseded once sensitivity labels are enabled. |
| Theme | M365 | `theme` | Not in the update page's table. **Undocumented** whether PATCH works | Values Teal/Purple/Green/Blue/Pink/Orange/Red. |
| Language | M365 | Graph `preferredLanguage` (ISO 639-1, e.g. `en-US`). EXO `Language` | Graph: not in the update table (**undocumented** as writable). EXO `Set-UnifiedGroup -Language da-DK` (.NET CultureInfo) | Learn doesn't state whether Graph `preferredLanguage` and EXO `Language` share storage. |
| Hide from Outlook clients | M365 | Graph `hideFromOutlookClients` (`$select`, GET by id). EXO `HiddenFromExchangeClientsEnabled` | Graph PATCH alone. EXO `Set-UnifiedGroup -HiddenFromExchangeClientsEnabled` / `:$false` | Enabling also sets `HiddenFromAddressListsEnabled=True` and removes the subscribe option. Not shown on the Entra Properties page; listed here because it is a group-level setting. |
| Nesting allowed | SG (beta) | beta `disableNesting` (`$select`) | beta PATCH, least privilege `Group-NestingSupport.ReadWrite.All` | Beta only. Read-only for M365 and role-assignable groups. |

### Dynamic membership rules

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Rule text (rule builder / text box) | M365-dyn, SG-dyn | `membershipRule` | PATCH `membershipRule` | Max 3,072 characters. The builder handles at most 5 expressions and users only; device rules need the text box. `memberOf` and `employeeHireDate` are preview. Property names are case-sensitive. `Direct Reports for "{id}"` can't be combined with other rules. |
| Pause processing | M365-dyn, SG-dyn | `membershipRuleProcessingState` (`On`/`Paused`) | PATCH `membershipRuleProcessingState` | |
| Processing status | M365-dyn, SG-dyn | beta `membershipRuleProcessingStatus` (`$select`, GET by id, read-only, null when paused) | n/a | Beta only. Stored outside the main store, so not in delta. |
| Validate rules (20 users/devices at a time) | M365-dyn, SG-dyn | beta `POST /groups/{id}/evaluateDynamicMembership {"memberId":"..."}` (existing rule). beta `POST /groups/evaluateDynamicMembership {"memberId":"...","membershipRule":"..."}` (unsaved rule) | n/a | Beta. **Delegated only** (`Group.Read.All`+`User.Read.All`/`Device.Read.All` for an existing group, `User.Read.All` or `Device.Read.All` for an ad-hoc rule). One member per call. The response gives `membershipRuleEvaluationResult` and per-expression `membershipRuleEvaluationDetails`. Portal role: Groups Administrator; indirect role assignment isn't supported. No documented syntax-only validator. An invalid rule on save fails with an error (Entra doc). |

### Expiration (tenant policy plus per-group renew)

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Group lifetime (days) | M365 only | `GET /groupLifecyclePolicies` → `groupLifetimeInDays` | PATCH policy | One policy per tenant. Minimum 30 days. Requires Entra ID P1 for members of the groups it covers. Roles: Groups Admin or User Admin. |
| Email for ownerless groups | M365 | `alternateNotificationEmails` (semicolon-separated) | PATCH policy | |
| Enable expiration for (All / Selected / None) | M365 | `managedGroupTypes` | PATCH policy. Selected: `POST /groupLifecyclePolicies/{id}/addGroup {"groupId":..}` / `removeGroup` | Selected list max 500 groups. |
| Per-group expiry / renew | M365 | `expirationDateTime`, `renewedDateTime` (default props). `GET /groups/{id}/groupLifecyclePolicies` | `POST /groups/{id}/renew` | Groups with activity auto-renew about 35 days before expiry, within 24 h of nearing expiry. Owners are notified at 30, 15 and 1 days. Deletion happens 1 day after expiry. Existing groups older than the lifetime get 35 days when the policy is first enabled. Restore within 30 days. A restored dynamic group repopulates in up to 24 h. |

### Licenses (group-based licensing)

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Assigned licenses + disabled service plans | SG, M365 with `securityEnabled=true` (Graph); MAC also lists mail-enabled groups | `assignedLicenses` (`$select`, read-only) | `POST /groups/{id}/assignLicense {"addLicenses":[{"skuId":..,"disabledPlans":[..]}],"removeLicenses":[]}` → 202. Least privilege `LicenseAssignment.ReadWrite.All` | Roles: Directory Writers, Groups Admin, License Admin, User Admin. Nested members aren't licensed. Users without usage location inherit the tenant's. MAC assigns to at most 20 groups at a time. |
| Processing state | same | `licenseProcessingState` (`$select`): `QueuedForProcessing` / `ProcessingInProgress` / `ProcessingComplete` | n/a | Asynchronous. Time is unspecified for large groups. |
| Members with license errors | same | `GET /groups/{id}/membersWithLicenseErrors`. Filter `GET /groups?$filter=hasMembersWithLicenseErrors eq true` (never returned on GET). Per-user detail: user `licenseAssignmentStates` (not fetched here) | Reprocess: MAC "Reprocess" (20 users at a time). Graph group-level reprocess: **undocumented** in the group resource | Error causes: not enough licenses, conflicting plans, dependency, usage location, duplicate proxy address, multiple products failing together. Remove licenses before deleting a licensed group. |

### Group-based role assignment

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Assigned roles | role-assignable M365/SG | Role assignments with `principalId` = group id (roleManagement API; not fetched in detail) | roleManagement assignment APIs; PIM for eligible | Group must have `isAssignableToRole=true` from creation. Membership changes need `RoleManagement.ReadWrite.Directory`. Changing members' credentials needs Privileged Authentication Admin. Members of role groups can't use some new-EAC areas (known issue). |

### Delete and restore

| Action | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Delete | M365, SG (Graph). MESG/DL (`Remove-DistributionGroup`). DDL (`Remove-DynamicDistributionGroup`) | n/a | Graph `DELETE /groups/{id}` (`Group.ReadWrite.All`) | Role-assignable: PRA or creator plus `RoleManagement.ReadWrite.Directory`. Distribution groups are "permanently deleted immediately" (Graph delete doc). |
| Deleted groups list | M365, SG | `GET /directory/deletedItems/microsoft.graph.group`. EXO `Get-UnifiedGroup -IncludeSoftDeletedGroups` | n/a | 30 days, not customizable. |
| Restore | M365, cloud SG (preview), role-assignable | n/a | `POST /directory/deletedItems/{id}/restore` | Groups Admin / User Admin (not role-assignable) / PRA (role-assignable). Owners can restore their own. SharePoint content takes up to 24 h. Not supported: EDU tenants with OneDrive storage, or classic audience targeting (SG soft delete). **DL: not restorable. MESG and DDL: undocumented.** |
| Permanently delete | M365, SG | n/a | `DELETE /directory/deletedItems/{id}` | Full purge may take 24 h. |

## Exchange admin center: Recipients > Groups > group

EAC shows M365, DL, MESG and DDL. Tabs: **General**, **Members**, **Settings** (with sub-sections). MESG docs also show an "Email options" section.

### General

| Setting (EAC label) | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Name | Mail kinds | `DisplayName` | `Set-*Group -DisplayName` | Max 64 (Unified) or 256 (DL, DDL). |
| Description | Mail kinds | M365 `Notes`. DL/MESG `Description`. DDL `Notes` | M365 `-Notes`. DL/MESG `-Description` (MultiValuedProperty). DDL `-Notes` | Graph can't write it for MESG/DL. |
| Alias | Mail kinds | `Alias` | `-Alias` | Max 64. `$ & ' \` { } \|` must be escaped. `&` isn't supported for Entra Connect sync. Alias with special characters blocks DL upgrade. |
| Name (Exchange object name) | DL, MESG, DDL | `Name` | `Set-DistributionGroup -Name` / `Set-DynamicDistributionGroup -Name` | Max 64. Unique. |
| Simple display name | DL, MESG | `SimpleDisplayName` | `-SimpleDisplayName` | Restricted character set. |
| Email addresses (Primary email address + Aliases) | Mail kinds | `PrimarySmtpAddress`, `EmailAddresses` | see MAC row | |

### Members

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Owners | Mail kinds | see MAC row | see MAC row | |
| Members | M365, DL, MESG | see MAC row | M365: `Add-UnifiedGroupLinks -LinkType Members`. DL/MESG: `Add-DistributionGroupMember` | Supported DL member types: UserMailbox, SharedMailbox, RoomMailbox, EquipmentMailbox, MailUser, MailContact, MailUniversalDistributionGroup, MailUniversalSecurityGroup. |
| Membership rules (recipient types + conditions) | DDL | `IncludedRecipients`, `Conditional*`, `RecipientFilter` | Precanned: `-IncludedRecipients MailboxUsers,MailContacts -ConditionalDepartment @{Add=..}` (values ORed within an attribute, ANDed across). Custom: `-RecipientFilter "<OPATH>"` (PowerShell only) | Precanned and custom filters can't be converted into each other. EAC can't edit custom filters. Leading-wildcard `-like '*abc'` isn't supported. Values must match exactly (case-insensitive). |

### Settings: general settings

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Hide this group from the global address list | Mail kinds | `HiddenFromAddressListsEnabled` | `-HiddenFromAddressListsEnabled $true` | |
| Allow external senders to email this group | M365, MESG | `RequireSenderAuthenticationEnabled` (inverse) | `-RequireSenderAuthenticationEnabled $false` | DL/DDL show this under Delivery management "Sender options". |
| Hide from Outlook clients / subscription / welcome / calendar | M365 | `HiddenFromExchangeClientsEnabled`, `AutoSubscribeNewMembers`, `AlwaysSubscribeMembersToCalendarEvents`, `SubscriptionEnabled`, welcome message flag (Get-UnifiedGroup output name **undocumented**; beta Graph `welcomeMessageEnabled`), `CalendarMemberReadOnly` (needs `-IncludeAllProperties`) | `Set-UnifiedGroup` switches: `-HiddenFromExchangeClientsEnabled`, `-AutoSubscribeNewMembers`, `-AlwaysSubscribeMembersToCalendarEvents`, `-SubscriptionEnabled`, `-UnifiedGroupWelcomeMessageEnabled`, `-CalendarMemberReadOnly`; use `:$false` to turn off | Whether the EAC exposes each of these is **undocumented**; PowerShell is the documented path. `AutoSubscribeNewMembers` overrides `AlwaysSubscribeMembersToCalendarEvents`. Both affect only future internal members. `SubscriptionEnabled:$false` requires `AutoSubscribeNewMembers:$false` first. The welcome switch is on by default and doesn't control Teams or Viva Engage mail. |

### Settings: delivery management

| Setting (EAC label) | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Sender options: Only allow messages from people inside my organization / Allow messages from people inside and outside my organization | DL, MESG, DDL (M365 uses the checkbox above) | `RequireSenderAuthenticationEnabled` | `-RequireSenderAuthenticationEnabled $true/$false` | Default is internal only. With internal-only set, mail contacts in the allowed list are still rejected. |
| Specified senders (accept list) | Mail kinds | `AcceptMessagesOnlyFromSendersOrMembers` (GUIDs unless `-IncludeAcceptMessagesOnlyFromSendersOrMembersWithDisplayNames` on Get-UnifiedGroup), plus `AcceptMessagesOnlyFrom`, `AcceptMessagesOnlyFromDLMembers` | DL/MESG/DDL: `-AcceptMessagesOnlyFrom @{Add=..;Remove=..}` and `-AcceptMessagesOnlyFromDLMembers @{..}`, or overwrite with `-AcceptMessagesOnlyFromSendersOrMembers a,b`. M365: `Set-UnifiedGroup -AcceptMessagesOnlyFromSendersOrMembers` (overwrite syntax only documented) | Don't mix the combined parameter with its component parameters in one command. Blank means everyone. Graph M365 equivalent: `acceptedSenders` navigation (`POST /groups/{id}/acceptedSenders/$ref`), which governs "posts or calendar events". Learn doesn't say whether it is the same list as Exchange's. |
| Rejected senders | Mail kinds | `RejectMessagesFromSendersOrMembers`, `RejectMessagesFrom`, `RejectMessagesFromDLMembers` | `-RejectMessagesFrom @{Add=..}`, `-RejectMessagesFromDLMembers @{..}` (add/remove), or overwrite `-RejectMessagesFromSendersOrMembers` | Not shown in EAC per the docs read. Graph M365: `rejectedSenders` navigation. |
| Block when in Bcc | DL, MESG | `BccBlocked` | `Set-DistributionGroup -BccBlocked $true` | EXO only. Not in EAC per docs. |

### Settings: message approval (moderation)

| Setting (EAC label) | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Require moderator approval for messages sent to this group | Mail kinds | `ModerationEnabled` | `-ModerationEnabled $true` | |
| Group moderators | Mail kinds | `ModeratedBy` | `-ModeratedBy a,b` (M365 docs show overwrite only) | Moderators must be mailboxes, mail users or mail contacts. `Set-UnifiedGroup` and DDL require at least one moderator when enabling. A DL with none falls back to owners (EAC says owners approve). An MESG needs at least one moderator. |
| Add senders who don't require message approval | DL, MESG, DDL (M365: read only) | `BypassModerationFromSendersOrMembers` | DL/MESG/DDL `-BypassModerationFromSendersOrMembers @{Add=..}` | **No `Set-UnifiedGroup` parameter documented**, although EAC shows the section for all group kinds per the docs read. |
| Notify a sender if their message isn't approved: Only sender / Only senders in your organization / No notifications | DL, MESG (DDL in EAC) | `SendModerationNotifications` (`Always` / `Internal` / `Never`) | `Set-DistributionGroup -SendModerationNotifications Internal` | Not a `Set-UnifiedGroup` parameter. The EAC label "Only sender" means `Always` (the default). For DDL, the `Set-DynamicDistributionGroup` parameter wasn't confirmed (page truncated). |
| Nested moderation | DL, MESG | `BypassNestedModerationEnabled` | `-BypassNestedModerationEnabled $true` | PowerShell only. |

### Settings: manage delegates (group delegation)

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Send As | Mail kinds | `Get-RecipientPermission -Identity grp` | `Add-RecipientPermission grp -AccessRights SendAs -Trustee user -Confirm:$false`. `Remove-RecipientPermission` | `SendAs` is the only valid right. Trustee must be a mailbox user, mail user or security group. The cmdlet prompts by default. Meeting invites from M365 groups still show "on behalf of". |
| Send on behalf | Mail kinds | `GrantSendOnBehalfTo` (GUIDs unless `-IncludeGrantSendOnBehalfToWithDisplayNames`) | `-GrantSendOnBehalfTo @{Add=..;Remove=..}` | Delegate must be a mailbox, mail user or MESG. Replies go to the group. Don't grant both rights to one trustee (Send As wins). |

### Settings: membership approval

| Setting (EAC label) | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Joining the group: Open / Closed / Owner approval | DL, MESG | `MemberJoinRestriction` | `Set-DistributionGroup -MemberJoinRestriction Open|Closed|ApprovalRequired` | `Open` isn't allowed on universal security groups. `ApprovalRequired` is "effective only on universal distribution groups". Default `Closed`. M365 equivalent is Privacy (`AccessType`). |
| Leaving the group: Open / Closed | DL, MESG | `MemberDepartRestriction` | `-MemberDepartRestriction Open|Closed` | `Open` isn't allowed on universal security groups (MESG default `Closed`). DL default `Open`. |

### Settings: mail tip

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| MailTip | Mail kinds | `MailTip` | `-MailTip "text"` | Max 175 displayed characters. HTML is wrapped automatically. Kept in sync with `default:` in translations. |
| MailTip translations | Mail kinds | `MailTipTranslations` | `-MailTipTranslations @{Add="ES:texto"; Remove="FR:texte"}` | Two-letter ISO 639 code. Doesn't change the default tip. |

### Other Exchange-only properties (PowerShell, not in EAC per docs read)

| Setting | Kinds | Read | Write | Constraints and gotchas |
| --- | --- | --- | --- | --- |
| Delivery reports to manager / originator | DL, MESG, DDL | `ReportToManagerEnabled`, `ReportToOriginatorEnabled` | `Set-DistributionGroup` / `Set-DynamicDistributionGroup` same names | Set exactly one to `$true`. Defaults: manager false, originator true. Not on `Set-UnifiedGroup`. DDL params are inferred from the truncated page; verify. |
| Send OOF to originator | DL, MESG, DDL | `SendOofMessageToOriginatorEnabled` | `-SendOofMessageToOriginatorEnabled $true` | Default false. Not on `Set-UnifiedGroup`. |
| Custom attributes 1-15 | Mail kinds | `CustomAttribute1`..`15` | `-CustomAttributeN "value"` | Max 1,024 characters each. Graph `onPremisesExtensionAttributes` is documented only as synced from AD. Whether it maps to Exchange custom attributes for cloud groups is **undocumented**. |
| Extension custom attributes 1-5 | Mail kinds | `ExtensionCustomAttribute1`..`5` | `-ExtensionCustomAttributeN @{Add=..}` | Up to 1,300 values. `-eq` filters mean "contains". |
| Connectors | M365 | n/a | `Set-UnifiedGroup -ConnectorsEnabled` | Needs org-level `ConnectorsEnabled`. |
| Information barrier mode | M365 | `InformationBarrierMode` | `-InformationBarrierMode Explicit|Implicit|Open|OwnerModerated` | |
| Audit log age | M365 | `AuditLogAgeLimit` | `-AuditLogAgeLimit 60.00:00:00` | Default 90 days. |
| Data encryption policy | M365 | `DataEncryptionPolicy` | `-DataEncryptionPolicy <id>|$null` | Customer Key feature. |
| Hidden group membership | M365 (create only), DL | `HiddenGroupMembershipEnabled` | DL: `Set-DistributionGroup -HiddenGroupMembershipEnabled` (one-way) | Can't be changed on an existing M365 group, and can't be reverted on a DL. |
| Room list | DL | `RecipientTypeDetails` | `-RoomList` | All members must be room mailboxes. A converted room list can't be upgraded to M365. |
| Max send/receive size | M365 | `MaxSendSize`, `MaxReceiveSize` | `Set-UnifiedGroup -MaxSendSize/-MaxReceiveSize` | On-prem-only for DL/DDL per `Set-DistributionGroup` docs. |

### Upgrade distribution list to Microsoft 365 group

| Item | Detail |
| --- | --- |
| Check eligibility | `Get-EligibleDistributionGroupForMigration`. A DL that isn't returned can't be upgraded. |
| Upgrade | `Upgrade-DistributionGroup -DlIdentities dl@contoso.example[,..]` (cloud only, DLs only, not MESG). EAC: select DL > "Send upgrade request" emails owners, who click Upgrade. Admin needs the Exchange Administrator role and a mailbox. Owners can't start it themselves. Takes 5-10 minutes. Email address is unchanged. |
| Blockers | Nested (has child groups or is a member of another group). On-premises managed. More than 100 owners. No owner. No members. Any member whose RecipientTypeDetails isn't UserMailbox, SharedMailbox, TeamMailbox or MailUser. Forwarding target of a shared mailbox. Used in another DL's sender restriction. Alias with special characters. MESG. DDL. Converted to room list. Any email address policy created with `IncludeUnifiedGroupRecipients` (remove, upgrade, recreate). |
| Reset | `Set-DistributionGroup -ResetMigrationToUnifiedGroup` clears the "migration in progress" flag. |
| Not documented | Which DL settings carry over (moderation, delivery restrictions, MailTip, custom attributes). Learn only says members and email addresses carry over. No rollback is documented. |

## Tenant-level settings the app should read to warn the user

| Setting | Read | Notes |
| --- | --- | --- |
| M365 naming policy | Graph directory setting `Group.Unified`: `PrefixSuffixNamingRequirement`, `CustomBlockedWordsList` (beta cmdlets in docs) | Prefix + name + suffix ≤ 63 characters. Attributes `[Department] [Company] [Office] [StateOrProvince] [CountryOrRegion] [Title]`. Validate with `POST /groups/{id}/validateProperties` or `/directoryObjects/validateProperties`: 204 = ok, 422 = details (example also shows `PropertyConflict` for a duplicate mailNickname). `onBehalfOfUserId` evaluates for another user. GDAP admins can't create groups when the prefix uses caller attributes. |
| Sensitivity labels on groups | `Group.Unified` `EnableMIPLabels` | Off means no label picker. |
| Classification list | `Group.Unified` `ClassificationList` | |
| DL naming policy | `Get-OrganizationConfig` `DistributionGroupNameBlockedWordList` (and DL naming policy) | Separate from Entra. Overridable per group with `-IgnoreNamingPolicy`. |
| Expiration policy | `GET /groupLifecyclePolicies` | Show expiry on M365 rows when one exists. |
| Connectors org switch | `Get-OrganizationConfig` `ConnectorsEnabled` | Gate `Set-UnifiedGroup -ConnectorsEnabled`. |

## Dynamic distribution lists: worth supporting?

Yes, but as an Exchange-only kind with its own editor. DDLs aren't in Graph or Entra and aren't synced to Entra, so Conditional Access can't target them. All reads and writes go through `Get/Set-DynamicDistributionGroup`, which shares most mail settings with DLs (MailTip, moderation, accept lists, hide from GAL, custom attributes, Send As, send on behalf). The DDL-specific features are:

- The membership rule (precanned or OPATH).
- A single owner.
- A preview with `Get-Recipient -RecipientPreviewFilter`.
- Stored membership with a 24 h refresh. From late May 2026, groups with 5,000 or fewer matches populate immediately; larger groups take up to 2 h.

Delete and restore behavior is undocumented.

## Undocumented or unclear (verify in a test tenant)

- Whether Graph `allowExternalSenders` / `hideFromAddressLists` / `hideFromOutlookClients` / beta `welcomeMessageEnabled` are the same stored values as Exchange `RequireSenderAuthenticationEnabled` (inverse) / `HiddenFromAddressListsEnabled` / `HiddenFromExchangeClientsEnabled` / `UnifiedGroupWelcomeMessageEnabled`. The descriptions match, but no page states it.
- Whether Graph `acceptedSenders`/`rejectedSenders` are the same lists as Exchange `AcceptMessagesOnlyFromSendersOrMembers`/`RejectMessagesFromSendersOrMembers`.
- Whether `preferredLanguage`, `theme`, `groupTypes`, `membershipRule` and `membershipRuleProcessingState` are PATCHable in v1.0. They are absent from the update page's table, but Entra's own sample PATCHes the last three.
- The output property name of the welcome-message flag on `Get-UnifiedGroup`.
- `Set-UnifiedGroup` lacks `BypassModerationFromSendersOrMembers` and `SendModerationNotifications` even though `Get-UnifiedGroup` returns the former and EAC shows the section.
- Restore of MESG and DDL. Settings carried over by DL upgrade. Group-level license reprocess via Graph.
- A full field list for the MAC group pane, and exactly which settings EAC shows for M365 groups.
- Dynamic group processing time. Learn gives no SLA except beta `membershipRuleProcessingStatus`.

## Sources

- Graph group resource v1.0: https://learn.microsoft.com/en-us/graph/api/resources/group?view=graph-rest-1.0
- Graph group resource beta: https://learn.microsoft.com/en-us/graph/api/resources/group?view=graph-rest-beta
- Graph update group: https://learn.microsoft.com/en-us/graph/api/group-update?view=graph-rest-1.0
- Graph groups overview: https://learn.microsoft.com/en-us/graph/api/resources/groups-overview?view=graph-rest-1.0
- Graph delete group: https://learn.microsoft.com/en-us/graph/api/group-delete?view=graph-rest-1.0
- Graph restore deleted item: https://learn.microsoft.com/en-us/graph/api/directory-deleteditems-restore?view=graph-rest-1.0
- Graph evaluateDynamicMembership (beta): https://learn.microsoft.com/en-us/graph/api/group-evaluatedynamicmembership?view=graph-rest-beta
- Graph validateProperties: https://learn.microsoft.com/en-us/graph/api/group-validateproperties?view=graph-rest-1.0
- Graph assignLicense: https://learn.microsoft.com/en-us/graph/api/group-assignlicense?view=graph-rest-1.0
- Graph groupLifecyclePolicy: https://learn.microsoft.com/en-us/graph/api/resources/grouplifecyclepolicy?view=graph-rest-1.0
- Set-UnifiedGroup: https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/set-unifiedgroup?view=exchange-ps
- Get-UnifiedGroup: https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/get-unifiedgroup?view=exchange-ps
- Set-DistributionGroup: https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/set-distributiongroup?view=exchange-ps
- Set-DynamicDistributionGroup: https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/set-dynamicdistributiongroup?view=exchange-ps
- Add-RecipientPermission: https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/add-recipientpermission?view=exchange-ps
- Upgrade-DistributionGroup: https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/upgrade-distributiongroup?view=exchange-ps
- Get-EligibleDistributionGroupForMigration: https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/get-eligibledistributiongroupformigration?view=exchange-ps
- Upgrade DLs to Microsoft 365 groups: https://learn.microsoft.com/en-us/exchange/recipients-in-exchange-online/manage-distribution-groups/upgrade-distribution-lists
- Can't upgrade DLs: https://learn.microsoft.com/en-us/troubleshoot/exchange/groups-and-distribution-lists/cannot-upgrade-distribution-lists-to-office-365-groups
- Manage distribution groups (EAC): https://learn.microsoft.com/en-us/exchange/recipients-in-exchange-online/manage-distribution-groups/manage-distribution-groups
- Manage mail-enabled security groups (EAC): https://learn.microsoft.com/en-us/exchange/recipients-in-exchange-online/manage-mail-enabled-security-groups
- Dynamic distribution groups (EAC): https://learn.microsoft.com/en-us/exchange/recipients-in-exchange-online/manage-dynamic-distribution-groups/create-manage-dynamic-distribution-groups
- MAC manage a group: https://learn.microsoft.com/en-us/microsoft-365/admin/create-groups/manage-groups?view=o365-worldwide
- MAC create a group: https://learn.microsoft.com/en-us/microsoft-365/admin/create-groups/create-groups?view=o365-worldwide
- MAC Microsoft 365 groups overview: https://learn.microsoft.com/en-us/microsoft-365/admin/create-groups/office-365-groups?view=o365-worldwide
- MAC group licenses: https://learn.microsoft.com/en-us/microsoft-365/admin/manage/manage-group-licenses?view=o365-worldwide
- Send as / send on behalf for groups (archived): https://learn.microsoft.com/en-us/previous-versions/microsoft-365/solutions/allow-members-to-send-as-or-send-on-behalf-of-group
- Entra manage groups: https://learn.microsoft.com/en-us/entra/fundamentals/how-to-manage-groups
- Entra change static to dynamic: https://learn.microsoft.com/en-us/entra/identity/users/groups-change-type
- Entra dynamic membership rules: https://learn.microsoft.com/en-us/entra/identity/users/groups-dynamic-membership
- Entra validate dynamic rules: https://learn.microsoft.com/en-us/entra/identity/users/groups-dynamic-rule-validation
- Entra restore deleted groups: https://learn.microsoft.com/en-us/entra/identity/users/groups-restore-deleted
- Entra group expiration: https://learn.microsoft.com/en-us/entra/identity/users/groups-lifecycle
- Entra naming policy: https://learn.microsoft.com/en-us/entra/identity/users/groups-naming-policy
- Entra sensitivity labels on groups: https://learn.microsoft.com/en-us/entra/identity/users/groups-assign-sensitivity-labels
- Entra role-assignable groups: https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/groups-concept
