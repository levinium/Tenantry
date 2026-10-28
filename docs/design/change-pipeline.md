# Change pipeline

Every edit in Tenantry follows the same path:

1. **Stage:** it goes into the **Pending changes** tray.
2. **Review:** each change shows as before → after.
3. **Apply:** the write guard checks it, Microsoft 365 carries it out, and Tenantry reads it back to confirm.
4. **Log:** the attempt and its outcome are recorded.

Screens never call Microsoft 365 directly.

## Changes

Each change is a typed `ChangeOp` record and carries:
- its **target**: kind, id, name and address;
- a **key** for the slot it fills, such as `department` or `signin`;
- **before** and **after** text, as a person reads them;
- a **caution** flag, for changes that take something away.

Records serialise as-is into the change log.

**Staging rules** (`ChangePipeline.Stage`):
- Staging the same target and key again replaces the earlier change but keeps its original
  "before", so the review always shows where things started.
- Staging back to that original drops the change.

## Applying

- Changes apply one at a time, in the order they were staged.
- Each one is checked by the write guard, executed, and logged.
- Successes leave the tray. Failures and refusals stay, with the reason, ready to retry or discard.
- When any change is a caution change (blocking sign-in, and later removing access or
  licenses), the Apply button first turns into a confirmation that names how many changes take
  something away. A second click applies.
- Typed confirmation is reserved for deletes and offboarding.

## Read-back

`GraphChangeExecutor` writes with PATCH, then reads the property back:
- **Lag:** Microsoft 365 can lag, so the read is retried after 2 s and 4 s.
- **Cache:** what it reads is merged into the tenant's local copy, so lists and open records show
  the change at once.
- **Still not visible:** the change is reported as *applied, not yet visible* rather than failed.
- **Clearing:** an emptied text value is sent as `null`, which clears it.

### Groups, mailbox access and manager

| Change | Sent as | Read back |
|---|---|---|
| Member or owner of a Microsoft 365 or security group | Graph `POST …/members/$ref` / `DELETE …/members/{id}/$ref` (owners alike) | The group's member (or owner) list, into the local copy |
| Member or owner of a distribution list or mail-enabled security group | Exchange `Add-` / `Remove-DistributionGroupMember`, `Set-DistributionGroup -ManagedBy @{Add/Remove}` | The same Graph list, which shows these groups too |
| Full access / Send as on a mailbox | Exchange `Add-` / `Remove-MailboxPermission`, `Add-` / `Remove-RecipientPermission` | The mailbox's permissions, into the local copy |
| Send on behalf | Exchange `Set-Mailbox -GrantSendOnBehalfTo @{Add/Remove}` | `Get-Mailbox`, into the local copy |
| Manager | Graph `PUT` / `DELETE users/{id}/manager/$ref` | `users/{id}/manager` |

- People are named to Exchange by address. Multi-valued parameters use Exchange's
  `#Exchange.GenericHashTable` form with `Add` or `Remove`.
- The write guard checks **both** sides of a membership or access change: the group or mailbox,
  and the person added or removed.
- Dynamic groups' members follow their rule and can't be edited. A Microsoft 365 or security
  group that has owners must keep one, so the last owner can't be removed until another is added.
### Mail (4b-1)

| Change | Sent as | Read back |
|---|---|---|
| Add or remove an address (alias) | `Set-Mailbox -EmailAddresses @{Add/Remove='smtp:…'}` | `Get-Mailbox` (list properties) into the local copy |
| Primary address | `Set-Mailbox -WindowsEmailAddress` (the old primary stays as an alias) | `PrimarySmtpAddress` |
| Forwarding on or off | `Set-Mailbox -ForwardingSmtpAddress -ForwardingAddress $null -DeliverToMailboxAndForward` | `ForwardingSmtpAddress` |
| Address list | `Set-Mailbox -HiddenFromAddressListsEnabled` | the same property |
| Automatic replies | `Set-MailboxAutoReplyConfiguration` (messages as HTML, scheduled times in UTC) | state, and the internal message's text |

- Forwarding to an address outside the tenant's domains is a caution change, and the form warns
  before it's staged.
- Exchange stores reply messages as HTML with its own source line breaks. The tab shows only the
  text (`AutoReply.PlainText`).
- Exchange is eventually consistent: a read straight after a confirmed change can come from a
  server that hasn't caught up yet (seen live). The executor stores the row it confirmed.

- Send on behalf needs a mail recipient. Exchange refuses it for an account with no mailbox, and
  Tenantry says so in plain words.

### Mailbox settings (4b-2)

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Recipients per message, largest message sent or received | `Set-Mailbox -RecipientLimits / -MaxSendSize / -MaxReceiveSize` ("25 MB") | the property; Exchange appends the bytes, so a prefix match counts | no |
| Only senders inside the organization | `Set-Mailbox -RequireSenderAuthenticationEnabled` | the property | when turned on |
| Allowed senders | `Set-Mailbox -AcceptMessagesOnlyFromSendersOrMembers @{Add/Remove=…}` | the list contains (or not) the sender | when removing |
| Email apps (OWA, Outlook, mobile, EWS, POP, IMAP, SMTP sending) | `Set-CASMailbox -<App>Enabled`; SMTP is `-SmtpClientAuthenticationDisabled`, inverted | `Get-CASMailbox` into the local copy | when turned off |
| Online archive | `Enable-Mailbox -Archive` / `Disable-Mailbox -Archive` | `ArchiveStatus` | when turned off (contents go after 30 days) |
| Auto-expanding archive | `Enable-Mailbox -AutoExpandingArchive` | `AutoExpandingArchiveEnabled` | always (it can't be undone) |
| Litigation hold | `Set-Mailbox -LitigationHoldEnabled -LitigationHoldDuration <days or Unlimited> -RetentionComment` | `LitigationHoldEnabled` | always |
| Retention policy | `Set-Mailbox -RetentionPolicy` | the property | no |

- The Mail tab reads these live: `Get-Mailbox` with the extra properties, `Get-CASMailbox`,
  `Get-TransportConfig` (the organization's SMTP default, used when a mailbox doesn't set its own),
  `Get-RetentionPolicy` names, and `Get-MailboxStatistics -Archive` only when an archive exists
  (it errors otherwise).
- Litigation hold needs Exchange Online Plan 2 or the Exchange Online Archiving add-on. Exchange
  refuses it on other plans with a clear message, which Tenantry shows as is.
- Auto-expanding archiving was not tested live, because it can't be turned off again.

### Mailbox contents (4b-3)

The Mail tab is split into pages: Addresses & delivery, Limits & apps, Archive & holds, Rules &
sharing, Mobile devices, and Recovery & trace. Each page reads its own data the first time it's
shown, and again after changes to that mailbox apply (the pipeline's `Applied` event).

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Inbox rule on or off | `Enable-InboxRule` / `Disable-InboxRule -Identity mailbox\ruleId` | `Get-InboxRule -Identity` | no |
| Delete an inbox rule | `Remove-InboxRule` | the rule is no longer found | always |
| Calendar access | `Add-`, `Set-` or `Remove-MailboxFolderPermission` on `address:\<calendar path>` | `Get-MailboxFolderPermission -User` (a removed entry is "not found") | when removing or setting "No access" |
| Block or unblock a phone | `Set-CASMailbox -ActiveSyncBlockedDeviceIDs @{Add/Remove}` | the blocked list | when blocking |
| Remove a phone | `Remove-MobileDevice` | the device is no longer found | always |
| Wipe a phone (all, or work data only) | `Clear-MobileDevice [-AccountOnly]` | none: the device confirms when it next connects | always, plus typed confirmation |
| Restore a deleted item | `Restore-RecoverableItems -EntryID` | the item is no longer in the recoverable list | no |
| Convert to shared or back | `Set-Mailbox -Type Shared/Regular` | `RecipientTypeDetails`, for up to a minute | when converting to shared |

- **Read-only pages.** Message trace uses `Get-MessageTraceV2` (`Get-MessageTrace` is retired) for
  up to 10 days per search, and `Get-MessageTraceDetailV2` for one message's steps. Deleted items
  come from `Get-RecoverableItems`, which needs the Mailbox Import Export role (Organization
  Management has it).
- **Typed confirmation.** A change can carry a `ConfirmPhrase`, such as the device's name for a
  wipe. After the caution click, the tray asks for each phrase, and Apply stays disabled until
  every one matches.
- **Changes that depend on others.** A change can name the slot it depends on (`DependsOn`).
  When both apply together and the first doesn't go through, the second is skipped with the
  reason.
  - Seen live: a convert to shared failed, and the license removal staged with it still ran,
    leaving a user mailbox without a license. Now the license removals and the sign-in block
    staged by the Convert popup depend on the conversion.
- **Routing to the mailbox's server.** `Set-Mailbox -Type` must run on the server holding the
  mailbox. Exchange answers "Cmdlet needs proxy" otherwise. The REST runner then retries once with
  that mailbox as `X-AnchorMailbox`, as the PowerShell module does.
- **Converting shows slowly.** Live, a mailbox read as shared after about 1 s, but back as a user
  mailbox only after about 15 s. The read-back waits up to a minute.
- **Folder permissions need a security principal.** Exchange refuses a shared mailbox's account as
  the person ("isn't valid to use for permissions"). The picker sends the person's mail address,
  which is what Exchange matches.
- **Not tested live, because there's nothing to act on:**
  - wiping and removing a phone (the test user has none);
  - restoring a deleted item (the app can't create mail to delete).

  Their parameters are covered by unit tests. Blocking and unblocking were tested live with a
  made-up device ID.

### Security & sign-in (4c-1)

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Remove a sign-in method | `DELETE users/{id}/authentication/{collection}/{id}` | the method list no longer has it | always |
| Add a phone | `POST …/authentication/phoneMethods` (`+1 2065551234`: country code, space, number) | `phoneMethods` has that type | no |
| Temporary Access Pass | `POST …/temporaryAccessPassMethods`; the pass comes back once and is shown in the tray, never stored | none | no |
| Remove a role | `DELETE roleManagement/directory/roleAssignments/{id}` | not found | always |
| Revoke an app consent | `DELETE oauth2PermissionGrants/{id}` | not found | always |
| Confirm compromised / dismiss risk | `POST identityProtection/riskyUsers/confirmCompromised` or `dismiss` | none | when confirming |

- **Read-only cards:**
  - **Sign-in log:** `auditLogs/signIns` filtered to the person, needs Entra ID P1.
  - **Conditional Access:** each policy is judged against the person, their groups (via
    `transitiveMemberOf`), their roles and guest status. Exclusions win.
  - **Risk and eligible roles:** need Entra ID P2. Without it the card says so.
- **MFA registration:** comes from `reports/authenticationMethods/userRegistrationDetails`. It's
  synced for everyone (one paged read, a few seconds) and re-read by a record check. It drives
  the MFA and Admin role columns, the "No MFA registered" and "Admins without MFA" views, and a
  health chip.
- **The method list lags:** Microsoft's combined list of methods lagged a newly added phone or
  pass by up to half a minute (seen live). The tab reads it again 20 s after a method change
  applies.
- **Graph headers:** requests now carry `User-Agent` and `ocp-client-name/version`. BitLocker
  keys and LAPS refuse requests without them.
### Devices (4c-2)

The tab joins two lists on the Entra device ID: the person's Entra devices (registered or owned)
and their Intune devices (managed, by primary user).

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Disable or enable in Entra | `PATCH devices/{objectId}` `accountEnabled` | the property | when disabling |
| Sync, restart, lock | `POST deviceManagement/managedDevices/{id}/syncDevice`, `rebootNow` or `remoteLock` | none: the device acts when it next checks in | all but sync |
| Rotate the local admin password | beta `…/rotateLocalAdminPassword` | none | yes |
| Retire or wipe (one slot: staging one replaces the other) | `…/retire`, or `…/wipe` with `keepEnrollmentData` and `keepUserData` false | none | yes; a wipe also asks for the device's name |

- **Secrets:**
  - BitLocker keys (`informationProtection/bitlocker/recoveryKeys`) and the LAPS password
    (`directory/deviceLocalCredentials`, base64) list their metadata only.
  - Each secret is read when Show is pressed, stays on screen for 60 s, and is logged as
    `SecretViewed` through `ChangePipeline.Record`, beside Microsoft's own audit of the read.
- **Not tested live:**
  - The admin can't create a test device: Microsoft refuses `POST /devices` with this app's
    permissions.
  - The tenant has no test device enrolled in Intune.

  So these changes are covered by unit tests. The write guard refuses them on any device whose
  name doesn't start with the test marker.

### Devices list (6a)

The Devices area lists every device from the local copy. Three reads keep it current:

| Part | Read | How often |
|---|---|---|
| Entra devices | `devices/delta` (changes only, like users and groups) | every check for changes |
| Intune devices | `deviceManagement/managedDevices`, read whole (Intune has no delta) | hourly, or on Refresh |
| BitLocker keys backed up | `informationProtection/bitlocker/recoveryKeys?$select=id,deviceId` (never the key) | hourly, or on Refresh |

- **Joining:** the two device lists join on the Entra device ID. A device only Intune knows (its
  Entra ID is all zeros, or not synced yet) is listed as "Intune only", keyed `intune:{id}`.
  Where both say something, Intune's word wins.
- **Encryption:** Intune's `isEncrypted` depends on a compliance policy asking for it. Measured
  live, half the laptops with a backed-up key reported "not encrypted". A backed-up BitLocker key
  therefore counts as encrypted.
- **Refresh:** the list's Refresh button reads only these three parts (`ITenantSync.SyncDevicesAsync`).
- **Missing access:** with no Intune role, devices show from Entra alone, with a note.
- **Bulk actions** (the selection panel, and the preview for one device):
  - sync and restart, disable or enable, retire, and wipe, through the 4c-2 changes;
  - Delete Stale Devices (`DeleteDevice`): it offers devices not seen for 90, 180 or 365 days
    and skips any seen more recently.
- **Deleting a device:** removes the Intune record first, so Intune doesn't recreate the Entra
  one, then `DELETE devices/{id}`. A record that's already gone counts as deleted. Deleting is a
  caution change.
- **Gating:** "Manage Devices" needs Intune Administrator or Cloud Device Administrator.
- **Not tested live:** `POST /devices` is refused, so no throwaway device can be made to delete.
  The guard's refusal of a non-test device was checked live.

### Device page (6b)

Opening a device opens its page in a tab. The header shows what it is and its state. Below it are
tabs: Overview, Users, Compliance, Apps, Groups, Defender and Recovery Keys. Each tab reads live
the first time it's shown, with a wheel while it reads. Something that can't be shown leaves a
note instead: for example a device Intune doesn't manage, or no Intune role.

| Tab | Read | Notes |
|---|---|---|
| Overview | beta `managedDevices/{id}?$select=hardwareInformation,usersLoggedOn,…` | v1.0 refuses `usersLoggedOn`. Intune writes "never" as 0001-01-01 or 9999-12-31, and empty as 0 or "": all shown as nothing. |
| Users | the same read, plus `devices/{id}/registeredOwners` and `registeredUsers` | one row per person, with every role they have |
| Compliance | `…/deviceCompliancePolicyStates` and `…/deviceConfigurationStates`; each policy's `…/{id}/settingStates` read when it opens | Settings don't `$expand`. A policy with a problem comes first, already open. Intune writes "no error" as "0". |
| Apps | beta `users/{primary user}/mobileAppIntentAndStates/{id}`; beta `…/detectedApps` | v1.0 has no `detectedApps`. 404 means no apps assigned. Installed apps are searchable. |
| Groups | `devices/{id}/memberOf/microsoft.graph.group` | Devices join or leave security groups through `SetGroupMembership` (member type "device"). Dynamic and Exchange-managed groups are locked. |
| Defender | `…/windowsProtectionState` and `…/detectedMalwareState` | 404 means the device doesn't report (not Windows) |
| Recovery Keys | as on the user's Devices tab | each Show is logged |

- **Actions** are the selection panel's. For one device, "Delete Device" replaces "Delete Stale
  Devices" (`BulkActions.ForDevices(…, one: true)`).
- **Words:** Intune's codes read as words through `DeviceText`: dotted setting IDs, states, and
  enrollment types renamed from "Azure AD" to Entra.
- **Not tested live:** the tenant has no test device, so joining and leaving groups is covered by
  unit tests and a click-through check. Every read was checked live, read-only.

### More device actions and Autopilot (6c)

Device actions are grouped: Everyday, Microsoft Defender, Settings, and Erase or Remove
(`BulkAction.Group`). An action that doesn't apply to a device skips it and says why ("not
Windows"). Rename, Change Primary User and Lost Mode are offered for one device only.

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Lock, locate | `…/remoteLock`, `…/locateDevice` | none | yes |
| Quick or full scan; update definitions | `…/windowsDefenderScan` `quickScan`; `…/windowsDefenderUpdateSignatures` | none | scans |
| Collect diagnostics | beta `…/createDeviceLogCollectionRequest` (predefined) | none | no |
| Rotate BitLocker keys | beta `…/rotateBitLockerKeys` | none | yes |
| Fresh Start | `…/cleanWindowsDevice` `keepUserData` | none | yes; its name typed |
| Autopilot reset | `…/wipe` with `keepEnrollmentData` true | none | yes; its name typed |
| Rename (`RenameDevice`) | beta `…/setDeviceName`; Windows names: 15 letters, digits, hyphens | none (applied at check-in) | no |
| Primary user (`SetPrimaryUser`) | beta `…/users/$ref` | `userId` | no |
| Ownership (`SetDeviceOwnership`) | PATCH `managedDeviceOwnerType` | the property | no |
| Lost mode (`SetLostMode`) | beta `…/enableLostMode` (message, phone) or `…/disableLostMode` | none | turning on |
| Group tag (`SetAutopilotGroupTag`) | `windowsAutopilotDeviceIdentities/{id}/updateDeviceProperties` | `groupTag` | no |
| Remove from Autopilot (`DeleteAutopilotDevice`) | `DELETE windowsAutopilotDeviceIdentities/{id}` | none | yes |

- **Shared slot:** wipe, Fresh Start, Autopilot reset and retire share the "remove" slot, so
  staging one replaces another.
- **Command numbers:** `DeviceCommand` values are stored as numbers in the change log. New
  commands are added at the end only.
- **Autopilot page:** Devices is now an area with two page tabs, Devices and Autopilot. Autopilot
  is read live from beta `windowsAutopilotDeviceIdentities` when first shown, and again on
  Refresh. Expanding `deploymentProfile` on the list fails with a 500 (seen live), so profile
  names come from `windowsAutopilotDeploymentProfiles/{id}/assignedDevices`. Its views: no profile assigned, never set up, enrolled, no group
  tag, not contacted in 180+ days. An enrolled device's preview links to its device page.
- **Permissions:** Autopilot needs `DeviceManagementServiceConfig.ReadWrite.All`. Wipe, retire,
  locate, lost mode and key rotation need `DeviceManagementManagedDevices.PrivilegedOperations.All`.
  Both were added 2026-10-27; a tenant grants them from the missing-permissions banner.
- **Not tested live:** the tenant has no test device or Autopilot entry, so these writes are
  covered by unit tests and click-through checks.

### Sites and OneDrives (7a)

The SharePoint & OneDrive area has two page tabs over one local copy of every site collection:
Sites, and OneDrives.

- **Source:** Graph can't list every site with storage and sharing for a delegated admin. The
  list comes from the SharePoint admin site's
  `Tenant.GetSitePropertiesFromSharePointByFilters` (CSOM; what Get-SPOSite calls), with
  OneDrives included. Measured live: 557 rows in one 3.6 s page.
- **Reading:** read whole, at most hourly or on Refresh (Sites' own Refresh reads only sites).
  Each row is stored small (`DirectorySync.SiteRow`), not SharePoint's 150-odd properties.
- **CSOM spellings:** dates are `/Date(y,m,d,h,m,s,ms)/` with months from 0 (year 1 means none),
  and IDs are `/Guid(…)/`.
- **Owners:** SharePoint returns no owner names. A OneDrive's owner is a sign-in name, matched to
  the person in the directory (someone gone still shows the sign-in name). A group's site is
  named after its group.
- **Archived OneDrives:** Microsoft archives a OneDrive left unlicensed for 93 days, so most
  unlicensed people's OneDrives are archived (seen live). The preview says reactivating is
  charged.
- **Sites views:** Teams, team, communication and channel sites, hubs, shared outside the
  organization, no activity in 180+ days, storage nearly full, locked, archived, system.
- **OneDrives views:** archived, no activity in 180+ days, over 10 GB, nearly full, shared
  outside the organization, locked.
- **Missing role:** without SharePoint Administrator the part is skipped with a note.

### Site page (7b)

Opening a site or OneDrive opens its page. Tabs: Overview (storage, plus activity from Microsoft's
30-day usage report), Permissions, Sharing and Libraries. The last three read the site's own REST
API (`{site}/_api/web/…`). The tenant's SharePoint token covers every host, OneDrives included.

- **No access:** SharePoint shows a site's contents only to its members and admins. Being a
  SharePoint or Global Administrator isn't enough (seen live: "Forbidden" on most sites), and an
  archived site can't be read at all. Each tab says which, in plain words
  (`UnauthorizedAccessException` → the note).
- **Permissions:** site admins (`SiteUsers?$filter=IsSiteAdmin eq true`), then the Owners, Members
  and Visitors groups (`AssociatedOwnerGroup…`), then the site's own groups.
- **Claims:** sorted by `GraphUserRecords.Principal`:
  - people and guests (`#ext#`, `urn:spo:guest`);
  - a Microsoft 365 group's members or owners (`federateddirectoryclaimprovider|{id}[_o]`);
  - Entra security groups;
  - "Everyone" and "Everyone except external users";
  - SharePoint's own accounts, which are hidden.
- **Sharing links:** each link is a group `SharingLinks.{item}.{kind}.{link}`: Anonymous… means
  anyone, Organization… means people in the organization, Flexible means specific people (its
  members are the people). Guests are site users with guest claims.
- **Libraries:** `lists?$filter=Hidden eq false` with item counts. A library opens to its 10
  newest files. In a big library SharePoint refuses to sort (the list threshold, seen at 57,891
  files), and the row says so.
- **Activity:** the sites report is matched by site ID. The OneDrive report has no address, so it
  is matched by the owner's sign-in name (seen live). Reports are cached for 30 minutes.

### Site actions (7c)

The site page's Overview tab, the site preview and the selection panel (several sites checked)
share one action list (`BulkActions.ForSites`). The Permissions tab is edited in place: a picker on
each card adds people, each row has Remove, and staged rows show Adding or Removing with Undo. The
Sites list's New Site button opens a one-popup form, and the area's Deleted Sites page lists what
can be restored.

- **Guard:** a site's target address is the last part of its address (`…/sites/finance` →
  `finance`), so in Test mode only `zz-tenantry-test-` sites change, whatever their title says.
- **`SetSiteAdmin`:** CSOM `Tenant.SetSiteAdmin`, read back from `GetSiteAdministrators`. "Add
  Yourself as Site Admin" also sits on each tab's no-access note (`SiteAccessException` →
  `LiveSection.NoteActionText`); once applied, the tabs read again. SharePoint won't remove a
  site's primary owner (seen live), so that row has no Remove.
- **`SetSiteGroupMember`:** the site's REST API (`ensureuser`, `sitegroups/GetById({id})/users`,
  `removeByLoginName`). A group-connected site's Owners and Members are its Microsoft 365 group's,
  so those cards stage `SetGroupMembership` on the group. SharePoint only knows people with a
  SharePoint license (or invited guests): adding an unlicensed person is refused with "could not be
  found" (seen live), and the result says why.
- **`SetSiteSetting`:** one setting as SiteProperties values (`Values`, JSON), sent with
  `SetProperty` and `Update` on `GetSitePropertiesByUrl` (as Set-SPOSite does), then read back into
  the local copy. Types come from `SiteSettings.TypeOf`. Settings: external sharing (choices stop at
  the organization's level; opening up is a caution), default link and its permission, guest
  expiry (`OverrideTenantExternalUserExpirationPolicy` plus days), storage limit (warning at 90%;
  below what the site holds is skipped), lock (Read-Only or No Access are cautions).
- **`SetSiteArchived`:** `Tenant.ArchiveSiteByUrl` / `UnarchiveSiteByUrl`. Needs Microsoft 365
  Archive with pay-as-you-go billing; without it SharePoint refuses (seen live) and the result
  says where to set it up.
- **`DeleteSite`:** typed confirmation (the site's title). A group-connected site is deleted by
  deleting its group (`DELETE groups/{id}`); SharePoint refuses to delete the site alone. Others go
  to the recycle bin (`Tenant.RemoveSite`). Channel sites, OneDrives and SharePoint's own sites are
  skipped with the reason.
- **`RestoreSite`:** `Tenant.RestoreDeletedSite`, or `directory/deletedItems/{id}/restore` for a
  deleted group. The Deleted Sites page joins `GetDeletedSitePropertiesFromSharePoint` with deleted
  Microsoft 365 groups by alias, since SharePoint keeps no title or group for a deleted site.
- **`CreateSite`:** a team site is a Microsoft 365 group (`POST groups`); its site appears within
  moments and is read through the admin site (Graph's `groups/{id}/sites/root` is refused to an
  admin who isn't a member, seen live). A communication site uses `SPSiteManager/create`. Whoever
  applies it becomes its owner; naming an unlicensed owner fails outright (seen live), so chosen
  owners join its Owners group afterwards.
- **Each CSOM method was checked to exist** by aiming it at a test address that doesn't exist:
  SharePoint answered "not found", never "method doesn't exist".

### Admin roles (8a-1)

The Security & Compliance area's Admin Roles page lists every directory role with who holds it,
read live (`IRoleRecords.GetRolesAsync`) when first shown, on Refresh, and after role changes
apply. A role opens as a page: Assignments (who, where, how long; add and remove) and Permissions
(what it allows, grouped by what each acts on). New Custom Role opens an editor.

- **Reads:** role definitions from beta (only beta has `isPrivileged`; beta refuses pages under
  20). With PIM, active and eligible assignments come from the schedule instances; without
  Entra ID P2 every PIM read answers 400 `AadPremiumLicenseRequired` (seen live), so active
  assignments come from `roleAssignments` and the page says why eligible ones are missing. Some
  assignments point at roles the list leaves out (seen live: 4 apps'); those roles are read one by
  one.
- **`AddRoleAssignment`:** without PIM, `POST roleAssignments` (active, permanent), read back by
  principal and role. Eligible or time-limited ones go through PIM's schedule requests
  (`adminAssign`, with an expiry). Giving a privileged role is a caution change. The guard judges
  who receives it.
- **`RemoveRoleAssignment`:** `DELETE roleAssignments/{id}`, or PIM's `adminRemove` by role and
  scope for one made through PIM. The only Global Administrator can't be removed here; an
  activated role ends on its own.
- **Custom roles** (`CreateCustomRole`, `UpdateCustomRole`, `DeleteCustomRole`): role definitions
  with `rolePermissions.allowedResourceActions`, chosen from the `microsoft.directory` resource
  actions (beta; about 800, privileged ones marked). Guarded by name. Deleting asks for the name.
  The editor can start from another role's permissions, and Shift-click ticks a range.

### Privileged Identity Management (8a-2)

PIM needs Entra ID P2. Without it most reads answer 400 `AadPremiumLicenseRequired` and the
approver and own-role reads answer 403 "The tenant has not onboarded to PIM" (both seen live); every
PIM page then says P2 is needed. The demo can act as a P2 tenant (`ConnectDemo(premium: true)`) for
screenshots and checks.

- **Role settings** (the role page's Settings tab): read from the role's management policy
  (`roleManagementPolicyAssignments`, `$expand=policy($expand=rules)`). `RolePolicyRules` reads the
  six rules the portal shows (activation length; what activating needs: MFA, reason, ticket;
  approval and approvers; how long eligible and active assignments last; what giving one active
  needs) and rewrites only the fields a change means to. Each edit stages `SetRolePolicyRule` (one
  per rule, so editing it again replaces it, and putting it back drops it); loosening a rule is a
  caution change. Sent with `PATCH policies/roleManagementPolicies/{id}/rules/{ruleId}`.
- **`ExtendRoleAssignment`:** PIM's `adminUpdate` with a new end ("Extend a Year" on a time-limited
  assignment).
- **`ActivateRole`** (My Roles): `selfActivate` with a duration (up to the role's activation
  length) and, as its settings need, a reason and ticket; `selfDeactivate` to end it early. PIM wants
  the admin's object ID, read from `/me` when only the sign-in name is known. A request needing
  approval reports "sent for approval".
- **`ReviewRoleRequest`** (Approvals): requests from
  `roleAssignmentScheduleRequests/filterByCurrentUser(on='approver')` (beta), answered by
  patching the approval step assigned to the admin (`roleAssignmentApprovals/{id}/steps/{id}`). The
  guard judges who asked.

### Conditional Access (8b-1)

Security & Compliance opens on Conditional Access: every policy in plain words (who, apps,
conditions, what it requires), read live when shown, on Refresh and after changes apply. A policy
opens as a page with its state switch, Delete, and the JSON Entra keeps. What If tests a sign-in.

- **Kept as Graph's JSON** (`CaPolicy.Json`): `CaText` reads it into labelled lines; the 8b-2
  editor will change only what it understands, so settings Tenantry doesn't know survive.
- **Names:** the IDs a policy holds are named in a few batched reads: people and groups
  (`directoryObjects/getByIds`), roles (role definitions' template IDs), apps (`servicePrincipals`
  by appId, 15 at a time), named locations and authentication strengths. Anyone no longer there is
  said so.
- **`SetCaPolicyState`:** `PATCH policies/{id}` with On, Report-Only or Off, read back. Switching
  On (or away from On) is a caution change. **`DeleteCaPolicy`** asks for the policy's name; Entra
  keeps deleted policies in its own deleted items (`identity/conditionalAccess/deletedItems`, beta),
  not the directory's.
- **Guard:** policies have no address, so the name is judged: only `zz-tenantry-test-` policies
  change in Test mode.
- **What If:** beta `identity/conditionalAccess/evaluate` with `signInIdentity`, `signInContext`
  and `signInConditions` (the older field names answer 400; "any app" isn't accepted, so the page
  offers common apps by ID). Each policy comes back with whether it applies and, if not,
  `analysisReasons` flags, put into words.
- **Live:** a policy is listed a few seconds after it's made; the live test waits for it.

### Conditional Access editor (8b-2)

New Policy (on the list) and Edit Policy (on a policy's page) open the editor as a tab: name and
state; who (everyone, or chosen people, groups, admin roles and guests; who's left out); target
resources (every cloud app, Office 365, admin portals, chosen apps searched live, or a user action);
conditions (platforms, locations, kinds of app, user and sign-in risk, device filter,
authentication flows); grant (block, or allow requiring any or all of the controls, an
authentication strength, terms of use); session controls. The policy reads back in words beside it.

- **Starts safe:** a new policy starts from blank or one of Microsoft's templates
  (`identity/conditionalAccess/templates`, 23 on the live tenant), in report-only, leaving the
  signed-in admin out. Blocking everyone while On without leaving yourself out isn't staged.
- **Keeps what it doesn't cover:** the editor works on the policy's JSON and rewrites only the
  parts its controls describe; anything else (insider risk, token protection, settings Microsoft
  adds later) is sent back as it was.
- **Staging:** each valid edit stages one `CreateCaPolicy` or `UpdateCaPolicy`, replacing the
  last; problems (as Entra would refuse, or a lockout) keep it out of Pending changes and are
  listed. Turning a policy On is a caution change.
- **Graph:** `ForWrite` strips read-only properties and OData annotations, and sends an
  authentication strength by ID. A made policy is waited for (it's listed after a few seconds).
- **Terms of use** need Tenantry's Agreement.Read.All permission (added; to be granted).
- **Live:** a policy from a real Microsoft template, narrowed to the test person and Off, made,
  edited (browser only, report-only), deleted and purged.

### Named locations and authentication strengths (8b-3)

Two more page tabs in Security & Compliance, read with the policies.

- **Named Locations:** cards for each location (IP ranges or countries, trusted, which policies
  use it). Add Location and Edit open one form: name, kind, places one per line, Trusted (IP
  ranges), and for countries "include unknown" and "judge by the Authenticator app's GPS". Places
  are checked before staging (CIDR with a prefix that fits; two-letter country codes; unique name).
- **Staging:** `CreateNamedLocation`, `UpdateNamedLocation` (making a location trusted is a caution
  change) and `DeleteNamedLocation`, guarded by name. A location a policy names can't be deleted;
  its Delete is off and says why. Pending ones show Adding, Changing or Deleting with Undo.
- **Graph:** `identity/conditionalAccess/namedLocations`; create and PATCH send the `@odata.type`
  (`ipNamedLocation` with `iPv4CidrRange`/`iPv6CidrRange`, or `countryNamedLocation`), then the
  location is read back by name. `CaPlaces.Read` turns that JSON back into a location (demo and
  pending rows).
- **Authentication Strengths:** cards for Microsoft's built-in strengths and any custom ones, each
  method combination in words, and how many policies require it. Read-only.
- **Live:** a test IP location (documentation range, not trusted) made, renamed with a second
  range, and deleted; the guard refused a delete of the real location with 0 requests sent.

### App registrations and enterprise apps (8c-1)

Two more page tabs in Security & Compliance, filled by one live read (applications with their
owners, then service principals; about 2 s for several hundred) and refreshed together.

- **App Registrations list:** credential state (Current, Expires Soon, Some Expired, Expired,
  None), next expiry, who can sign in, permission and owner counts. Views for expired or expiring
  credentials, no owners, and apps other organizations can sign in to.
- **Enterprise Apps list:** where each comes from (this organization: its app ID matches a
  registration here; Microsoft: owned by Microsoft's tenants; otherwise third party), sign-in on or
  off, assignment required, visible, single sign-on. The default view matches Entra's (apps tagged
  as integrated); other views: third party, this organization's, sign-in off, SSO set up, all.
- **App pages** share one layout: a header with flags and Open in Admin Center, plus a link to the
  app's other half when both are here. Tabs are cards of rows. A registration has Overview
  (IDs, audience, redirect URIs), Secrets & Certificates (status, hint or thumbprint, dates), API
  Permissions (named from each API's scopes and app roles, with whether admin consent covers
  each) and Owners. An enterprise app has Overview (including last sign-in), Users & Groups,
  Permissions (admin consent, application permissions, and people who consented for themselves,
  with their scopes) and Owners.
- **Reads:** a page's details load once, in parallel, when a tab needs them (about 1 s). Last
  sign-in comes from the beta sign-in activity report, which takes several seconds, so it fills in
  on its own afterwards. A certificate's `customKeyIdentifier` is already a hex thumbprint.

### App actions (8c-2)

Every change targets the app (kind `appregistration` or `enterpriseapp`) and is guarded by its
name, plus the person for owner and access changes. Rows waiting in Pending changes show
Adding, Removing or Revoking with Undo.

- **Secrets & Certificates:** Add Secret (what it's for; 6, 12 or 24 months). The value comes back
  once as `ApplyResult.Reveal` and the tray shows it as "Client Secret … for …"; it's never stored
  or logged. Removing a secret or certificate needs the app's name typed. A secret goes through
  `removePassword`. A certificate is removed by PATCHing `keyCredentials` with the others, as Graph
  returned them (`removeKey` needs a proof signed by the app's own key).
- **Owners** (both kinds): pick a person to add; Remove is a caution change.
- **API Permissions:** Grant Admin Consent for the permissions not yet consented (a caution
  change). It makes the enterprise app if needed, adds delegated scopes to the existing
  AllPrincipals grant per API (or makes one), and adds only the app role assignments it lacks.
- **Enterprise app Settings:** switches for sign-in, assignment required and visible (the
  `HideApp` tag; the other tags go back unchanged). Flipping one back takes it out of Pending
  changes. Sign-in off and assignment required on are caution changes.
- **Users & Groups:** pick a person or group, as one of the app's roles; Remove.
- **Permissions:** Revoke a whole admin consent, an application permission, or one person's consent.
- **Delete:** an app registration (it goes to Deleted Apps for 30 days, with its enterprise app) or
  an enterprise app (not Microsoft's own). Both need the name typed. A deleted app's open page closes.
- **Deleted Apps** page: Restore, or Delete Permanently (name typed).
- **New App Registration** (the list's button): name, who can sign in, and an optional redirect
  (https, or http only for localhost). It's made with its enterprise app.
- **Live:** a test app made, given a secret (removed), an owner (removed), each setting switched
  and back, access for the test person (removed), admin consent for User.Read (revoked), then
  deleted, restored, deleted and purged. The guard refused a real app with 0 requests sent.
  Certificate removal is covered by tests only, since the test app had no certificate.

### Authentication methods (8d-1)

A Security & Compliance page tab: every sign-in method on the left, then the policy's settings
(registration campaign, system-preferred MFA, report suspicious activity, security defaults); the
chosen one's editor on the right.

- **Read** through beta `policies/authenticationMethodsPolicy`, which lists all 13 methods the
  admin center shows (v1.0 leaves out hardware tokens, password and federated credentials), plus
  v1.0 security defaults. Group IDs anywhere in the policy are named in one batched read.
- **Editor:** state (On/Off; sections also Microsoft Managed), who (everyone, or chosen groups, minus
  left-out groups), and typed settings from a catalog (`AuthMethods.Catalog`, `AuthSetting` with a
  JSON path). Certificates and federated credentials say their rules live in the admin center.
- **Staging:** each edit stages one `SetAuthMethodConfig` per method or section, replacing the last;
  editing back to the original takes it out; problems (a Temporary Access Pass lifetime outside
  its range, nobody chosen) keep it out. Switching a method on or off, or widening a weak one
  (text, call, email), is a caution change. The PATCH carries only the type, state, targets and
  the top-level objects the catalog edits, so the rest stays as it was. New targets copy the shape
  of the method's existing ones (the Authenticator's mode, a passkey's profiles).
- **Security defaults:** one switch, `SetSecurityDefaults`, a caution change; Entra refuses it while
  Conditional Access policies are on, and the demo does the same.
- **Permissions:** Policy.ReadWrite.AuthenticationMethod and Policy.ReadWrite.SecurityDefaults
  (plus IdentityRiskEvent.Read.All and IdentityRiskyServicePrincipal.ReadWrite.All for 8d-2).
  Role: Authentication Policy Administrator (`ManageAuthPolicy`).
- **Live:** read-only. The policy is the organization's, so Test mode refuses every change to it
  (checked: 0 requests sent).

### Identity Protection (8d-2)

Three page tabs in Security & Compliance, read together (`IRiskRecords`), with Refresh.

- **Risky Users:** level, state, detail, last updated. Views: at risk, high risk, confirmed
  compromised, everyone with history. The preview lists what was detected for that person and offers
  Confirm Compromised (caution) and Dismiss Risk (`SetUserRisk`, the op the person's Security tab
  uses); once staged it offers Undo. Checking rows opens bulk Confirm and Dismiss (Dismiss skips
  people not at risk).
- **Risk Detections:** each detection in words (unfamiliar sign-in properties, anonymous IP,
  leaked credentials…), with level, place, address and when; views for still at risk, high, last 7 days.
- **Risky Apps:** service principals at risk, with Confirm and Dismiss (`SetAppRisk`, guarded by the
  app's name; `identityProtection/riskyServicePrincipals/confirmCompromised|dismiss`).
- **Gating:** without Entra ID P2 Graph answers 403 "not licensed": the page says it needs P2. A
  missing permission answers 403 "required scopes are missing": the page says to grant
  permissions. Each list says which. Risky apps need Workload ID Premium. The demo answers the
  same way unless it acts as a P2 tenant. Actions need Security Administrator and P2 (`ManageRisk`).
- **Live:** read only. The tenant has no P2: risky users say so, and detections and apps asked for the new
  permission (not granted yet).

### Audit log search (8e)

An Audit Log page tab in Security & Compliance, searching Purview's unified audit log through
Graph's `security/auditLog/queries` (AuditLogsQuery.Read.All; the admin needs the Audit Logs role
in Purview, or Compliance Administrator). Searching changes nothing, so it doesn't go through
Pending changes.

- **New search:** quick ranges (24 hours, 7, 30 or 90 days) or chosen dates (the end day is
  included, up to now; within 180 days), people (picked, sent as sign-in names), activities (one
  per line), record types (in words), and more filters: keyword, IP addresses, objects, a name.
  Only filters given are sent.
- **Waiting:** Purview runs a search in the background, slowly: live, a one-day Entra search sat
  queued about 6 minutes, then was still running after 40 more. Searches are listed with Queued, Running, Done or Failed; unfinished ones are
  checked every 20 seconds while Tenantry is open, with a wheel. Past searches (Purview keeps them)
  are listed when the page opens.
- **Results:** a finished search's records (up to 10,000; narrow the search for more) open in the
  standard list: who, activity, when, service, object, address, record type, with filters,
  sorting and CSV export. The preview shows the whole record, indented.
- **Live:** searches listed, started and checked through Tenantry's reader. A second search waited
  about 40 minutes queued behind the first (Purview seems to run one at a time). The finished probe
  search (one day of Entra records) read back 300 records, each with person, time, record type in
  words and the whole record; paging was complete at every page size. Purview's count said 497:
  it's an estimate, so the list says "About".

### New User and New Group

The Users and Groups lists' buttons open one-popup forms; adding stages one change.

- **`CreateUser`:** `POST users` (name, sign-in name, usage location, password, optional job and
  contact fields), then in the same step its licenses (`assignLicense`), manager and groups
  (Graph, or `Add-DistributionGroupMember` for distribution lists). The account counts as made even
  if a later part fails; the result names what to finish by hand. The password is generated in the
  tenant's style when applied and shown once, or typed (memory only, never logged). "Copy from a
  colleague" fills department, title, office, manager, direct group memberships (not dynamic ones)
  and licenses with seats left.
- **`CreateGroup`:** Microsoft 365 and security groups (assigned or dynamic by rule) through
  `POST groups`, owners and members bound in the same request (up to 20 each; the rest added after).
  Distribution lists and mail-enabled security groups through `New-DistributionGroup` (members,
  owners, outside senders). Shared mailboxes through `New-Mailbox -Shared`, then Full access and Send
  as for each person, retried while Exchange catches up with the new mailbox.
- **Guard:** the new object's sign-in name or address (or name) and every group or person added.
- **Local copy:** what's made is read back through the same refresh a record page uses, so the
  lists show it at once.
- **Live test (2026-10-27):** the guard refused a non-test name; a test user (blocked, unlicensed,
  manager, test group) and one of each group kind (owners, members, shared mailbox access) were
  made through the pipeline, read back, and then permanently deleted. Found and fixed: Exchange
  takes only mail-enabled people as a distribution list's owners or members, so the form offers only
  those. Mailbox permissions take a few seconds to show after they're granted.

### Import from CSV

Import from CSV (beside New User) reads a file in Tenantry's template only (`ImportUsersViewModel.Columns`;
unknown columns are refused). Each row is matched by sign-in name or email:

- **New:** one `CreateUser` (details, licenses, groups, manager, and the file's password when
  given, checked against Microsoft's rules; generated otherwise).
- **Existing:** only what the file gives and differs: `SetUserText` per field, `SetManager`,
  `SetSignInAllowed`, `ResetPassword` (file password), `SetLicenseAssigned` to add, and
  `SetGroupMembership` to join. Blank cells leave things as they are; nothing is removed.
- **Checks:** domain, sign-in name form, duplicates in the file, licenses by name with seats counted
  across the whole file, groups by name or email (dynamic groups and mailboxes refused), managers
  must already exist, Yes/No values, countries. Rows with problems are shown and left out.

Add Members from CSV (a group's people editor) takes a "Sign-In Name" column and stages a
membership add for each person not yet in the group.

Live test (2026-10-27): a file creating a test user in the test group, changing a test user's
department and adding another to the test group went through the pipeline; a non-test row was
refused; everything was then put back and the created user deleted.

### Microsoft 365 Apps (Devices tab)

The Devices tab ends with a Microsoft 365 Apps card:

- **Activated on:** Microsoft's activation report (`beta/reports/getOffice365ActivationsUserDetail`,
  JSON): per product, how many Windows and Mac computers and how many phones and tablets, against the
  limit of five computers. The report counts devices; it doesn't name them. It's also synced daily
  for the Users list (Office on Computers, Office on Phones and Tablets, Office Last Activated; view
  "Office on 4+ Computers"). With names concealed in reports nothing can be matched, and a note says
  where to show them.
- **Signed in from:** the last 30 days of the sign-in log (interactive and background, with
  `$select`, which measured several times faster), grouped by Entra device ID, or for a device
  Entra doesn't know, by system and browser without versions. Devices also in the list above say so.
- **Actions:** Sign Out Everywhere (Revoke sessions, to Pending changes) and Deactivate in the
  Admin Center, which opens the person's page there. Microsoft offers other apps no API to list
  or deactivate a single installation; the admin center uses a private one.

### OneDrive (4d-1)

Graph reads the drive (`users/{id}/drive`: quota, web address, site ID); a 404 means no OneDrive
yet. Graph can't change who has access to a OneDrive, so `SharePointAdmin` calls the tenant's
SharePoint admin site (`{tenant}-admin.sharepoint.com`). It uses one token for the tenant's
SharePoint (`https://{tenant}.sharepoint.com/.default`), which covers the admin site too, and it
reads the SharePoint host once from Graph `sites/root`.

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Give someone full access, or take it away | CSOM `Tenant.SetSiteAdmin(siteUrl, loginName, bool)` | `POST _api/SPO.Tenant/GetSiteAdministrators?siteId='…'` | when taking away |

- **CSOM over HTTP:** an XML request posted to `_vti_bin/client.svc/ProcessQuery`. The answer is
  a JSON array: a header with `ErrorInfo` when SharePoint refused, then each action's id followed
  by its result. The Tenant type ID `{268004ae-ef6b-4e9b-8425-127220d84719}` was read from the
  CSOM assemblies' metadata.
- **Setting up a OneDrive isn't possible from Tenantry.** SharePoint refused
  `ProfileLoader.CreatePersonalSiteEnqueueBulk` and `Tenant.RequestPersonalSites` with "Attempted
  to perform an unauthorized operation". The calls were made by a Global Administrator, with both
  token audiences, and the target was allowed to sign in. Microsoft's own module
  (`Request-SPOPersonalSite`) succeeded for the same admin, so SharePoint keeps this action for
  Microsoft's tools. For a OneDrive that doesn't exist yet, the tab shows that command with a Copy
  button and a Check again button.
- **Who has access:** `GetSiteAdministrators` lists the owner and everyone given full access
  (email, loginName, name, userPrincipalName). Files shared one by one aren't listed.
- **Login names:** people are sent as `i:0#.f|membership|{upn}`. Removing someone sends their
  login exactly as the admin list gave it, so non-person admins (a role's claim) can be removed
  too, outside Test mode.
- **Write guard:** both the OneDrive's owner and the person given or losing access must be test
  objects.
- **`GetPersonalSiteUrl`:** this also works, and answers with a `Person.aspx` profile page when
  there is no OneDrive. The tab uses Graph instead, which gives the quota in the same read.

### Activity (4d-1)

The tab only reads:
- **Tenantry's own change log:** entries whose `Guarded` targets include the person. That covers
  changes to them and changes adding or removing them from a group or mailbox.
- **Microsoft's directory audit log:** `auditLogs/directoryAudits`, filtered with
  `targetResources/any(t:t/id eq '{id}')` for changes made to them, or
  `initiatedBy/user/id eq '{id}'` for changes they made. It shows the newest 100.
- **Last activity per app:** the beta `reports/getOffice365ActiveUserDetail(period='D30')` as
  JSON (v1.0 only sends CSV). It covers every user at once, so it's read once per 30 minutes and
  looked up by sign-in name. When the tenant conceals names in reports, no row has an `@`, and
  the tab says how to show them.

### Workflows (4d-2)

A workflow is a checklist popup that plans many existing changes at once, then puts them all into
Pending changes. It adds no new change types, and nothing is sent until the admin applies them.
Each workflow runs for one person (from the record page's Actions menu) or for everyone selected
(from the selection panel).

**Offboarding** (`OffboardingViewModel`):
- **Ticked by default:**
  - block sign-in, sign out everywhere, reset the password;
  - remove sign-in methods, admin roles and app consents;
  - remove them from groups, lists and shared-mailbox access;
  - set an automatic reply, hide them from the address list;
  - convert the mailbox to shared, then remove direct licenses.
- **Unticked:** forward their mail, give the manager the mailbox or the OneDrive, retire their
  Intune devices.
- **The account is never deleted.**
- **Reads:** the planner reads only what ticked steps need, four people at a time: methods, roles,
  consents, licenses, mailbox, manager, OneDrive and devices. Group memberships come from one pass
  over the local copy.
- **Order:** changes are listed person by person in a safe order. License removals `DependsOn` the
  conversion, so a failed conversion keeps the license.
- **What's skipped, with a note:** dynamic groups, a group they're the only owner of, missing
  managers, and anything that couldn't be read.
- **Automatic reply:** filled in per person from `{name}`, `{first name}`, `{organization}` and
  `{manager}`. With no manager set, `{manager}` becomes "us".
- **Confirming:** the tray's usual second click. There's no typed confirmation (Mark, 2026-10-26).

**Compromised-account response** (`CompromiseViewModel`):
- **Checks first:** on opening, it reads each person's possible warning signs and lists them:

  | Warning sign | How it's found | Fix offered |
  |---|---|---|
  | An enabled mailbox rule that forwards or deletes mail | inbox rules | Turn this rule off (ticked) |
  | Mailbox forwarding | the local copy | Turn forwarding off (ticked only when it goes outside the organization) |
  | A sign-in method added in the last 7 days | authentication methods | Remove this method (ticked) |
  | An app consent from the last 7 days | "Consent to application" in the audit log, matched by app name (grants carry no date) | Take away the app's access (ticked) |
  | A sign-in from a country new in the last 7 days | the sign-in log, compared with the 23 days before | none: shown so the admin can look into it |

- **The response itself:** reset the password (they must change it at next sign-in, both ticked),
  require MFA to be registered again (removes every method but the password), sign out
  everywhere. Block sign-in is offered unticked. There's no Temporary Access Pass step (Mark,
  2026-10-26).
- **Duplicates:** a fix already covered by the response, such as a new method that
  re-registering removes anyway, isn't staged twice.

**Passwords:** generated passwords follow the tenant's `PasswordStyle` (Settings › Passwords; kept
in `password-style.json`). After a bulk reset, the tray offers Copy all, Save as file (CSV) and Done
with all. Passwords are still never stored.

### Mail & security (5a)

The sidebar's Mail & security area is one tab (`MailSecurityViewModel`) with page chips. The
Quarantine and Message trace pages are live lists: they are read from Exchange when shown, not
kept in the local copy. They use the usual list framework (views, filters, columns, export), and
their preview pane holds the actions. Rows have no record page, so `HasRecordPages = false` hides
"Open full record".

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Release a quarantined message (optionally allowing its sender) | `Release-QuarantineMessage -ReleaseToAll [-AllowSender]` | `Get-QuarantineMessage -Identity`: `ReleaseStatus` is RELEASED | no |
| Delete a quarantined message | `Delete-QuarantineMessage` | it's gone | yes |
| Block or allow a sender | `New-TenantAllowBlockListItems -ListType Sender`: blocks never expire; allows are removed 45 days after last use | `Get-TenantAllowBlockListItems -Entry` | when blocking |

- **Write guard:**
  - A quarantine change is guarded on every recipient, so in Test mode only mail addressed to
    test objects can be released or deleted.
  - A sender listing is guarded on the sender's address.
- **Previews are plain text:** a quarantined message's body comes from `Preview-QuarantineMessage`
  and is shown through `PlainText.FromHtml`. Scripts, styles and images are dropped, and links
  show their address, so nothing in a malicious message loads or runs.
- **Message trace:** `Get-MessageTraceV2` searches up to 10 days at a time, up to 90 days back,
  with at most 5,000 results. Its `ResultSize` is a real search limit, so `RestCmdletRunner` sends
  it through for this cmdlet (`SearchLimitedCmdlets`). For every other cmdlet the runner pages
  itself; without the exception, a trace stopped at 1,000 (seen live). Each message's steps come
  from `Get-MessageTraceDetailV2`.
- **Coming next:** the mail flow rules list (5c) and the rule editor (5d, built from the catalog
  in docs/research/transport-rule-catalog.json).

### Mail flow rules (5c)

The page lists every transport rule in the order rules run (`Get-TransportRule -ResultSize Unlimited`).
Each rule is described in Exchange's own plain words: its `Description`, split by
`MailFlowRule.ParseDescription` into When, Except When and Then, with Title Case labels. Views:
All Rules, On, Off and In Test Mode.

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Turn on or off | `Enable-TransportRule` / `Disable-TransportRule` | `State` | when turning off |
| Enforce or Test | `Set-TransportRule -Mode Enforce\|Audit\|AuditAndNotify` | `Mode` | when switching to Enforce |
| Move in the order | `Set-TransportRule -Priority`; Exchange shifts the others | `Priority` | no |
| Delete | `Remove-TransportRule` | it's gone | yes |

- **Write guard:** rules have no address, so the guard judges the rule's name. In Test mode only
  rules named `zz-tenantry-test-…` can change.
- **Live test (2026-10-26):** a test rule (off, Test mode, matching only mail to the test user,
  adding a header) was turned on, switched mode and back, moved and moved back, turned off and
  deleted. Every step read back, and nothing was left behind.
- **Creating and editing rules** is the rule editor's job (5d, below).

### Rule engine (5d-1)

The editor works on a `RuleDraft` (`Tenantry.Core/MailFlow`), built from the rule catalog
(`docs/research/transport-rule-catalog.json`, embedded in Tenantry.Core so there is one copy).

- **Reading:** `RuleCodec.Read` turns `Get-TransportRule` output into lines (conditions, exceptions,
  actions: each a catalog entry, its value and its companion values) and rule settings. Sizes become
  byte counts. Disclaimer actions that share a parameter are told apart by their fixed values
  (append or prepend). Anything Tenantry doesn't know is kept in `Kept`: shown read-only, never sent.
- **New rules:** `NewRuleParameters` sends everything the draft sets, plus `-Mode` (Test by default)
  and `-Enabled:$false` when it starts off.
- **Edits:** `Changes(original, edited)` sends only what changed. Removed values go as null, switches
  as false. A line with any change is sent whole, because Exchange needs paired values together (a
  header's value needs its name).

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| New rule | `New-TransportRule` | every sent setting shows | when it starts in Enforce |
| Edit | `Set-TransportRule` with the changes | each changed setting set or cleared as asked | when it switches to Enforce |

Read-back is lenient where Exchange rewrites input (case, one value as a list, addresses resolved):
cleared settings must be unset, set ones set with the same number of values, single values equal.

- **Round-trip check (2026-10-26, read-only):** every rule in the test tenant read into a draft and
  back gave exactly Exchange's settings, and re-reading gave no changes. Only metadata was skipped.
- **Live test (2026-10-26):** the guard refused a rule not named for testing. A test rule (off, Test
  mode, mail to the test user, adds a header) was created, edited (words changed, exception and
  action added, header value changed, stop processing on), edited again (those cleared, renamed) and
  deleted. Every step read back. Exchange fills in four defaults on a new rule (sender address
  location, recipient address type, error action, sub-type); edits start from what Exchange returns,
  so they are never sent by accident.

### Rule editor (5d-2)

New Rule (on the Mail Flow Rules page) and Edit Rule (in a rule's preview) open the editor in its
own tab. Edit reads the rule fresh from Exchange (`IMailSecurity.GetRuleDraftAsync`); if an edit is
already in Pending Changes, the editor carries on from it.

- **Starters:** a new rule begins from a template (blank, outside-sender warning, risky
  attachments, disclaimer, trusted partner, copy mail, approval, outside auto-forwarding). Every new
  rule starts in Test mode, placed after every other rule unless the admin picks "before".
- **The sentence:** When… (all must match), Except When… (any one excuses), Then…. Each line has the
  input its catalog value type needs (`RuleValues.InputFor`): a box, one entry per line, a choice, a
  pick-several list, a size with its unit, a date or a switch, plus companion values (a header's
  value, a disclaimer's fallback). Values are checked as they're typed: patterns compile and stay
  within 128 characters, domains, IP ranges, status codes, `Property:values` lines.
- **Add a part:** every condition and action, grouped as the admin center groups them and
  searchable. Advanced parts are marked; parts that can't work in Exchange Online are listed, greyed,
  with the reason. A part already in the rule (and the second disclaimer) can't be added twice.
- **In plain words:** the rule as a sentence, kept up to date, with what still needs fixing.
- **Pending Changes:** while the rule has no problems it sits in Pending Changes, replaced as it's
  edited: `CreateRule` for a new rule, `UpdateRule` (only the changes) plus `SetRuleMode` for an
  existing one. Done closes the tab and leaves the changes there; Discard Changes takes them out.
  Once they're applied the editor closes itself.
- **More Settings:** start and stop dates, stop processing more rules, where to match the sender
  address, what to do if the rule can't be checked, audit severity. Unset choices show Exchange's
  default. Settings from a newer Exchange are shown read-only and kept.
- **Live test (2026-10-26):** the editor itself (as the app drives it) made a test rule from Blank
  (off, Test mode, mail to the test user, adds a header), which landed last in the order; reopened
  it with Edit, added an exception (attachment size), changed the header value and turned on stop
  processing; each step read back, the editor closed after each apply, and the rule was deleted. A
  read-only round-trip of every rule stayed clean.
- **Timeouts:** a request that times out now fails just that change ("Microsoft didn't answer in
  time"); before, it surfaced as a cancellation and stopped the whole apply.

### Block and allow lists (5b)

The page lists every Tenant Allow/Block List entry in one live list: senders and domains, links,
files (SHA256), IPv6 addresses and spoofed senders. "Add entries" takes one or many values
pasted at once. Each value is checked for its kind's format and against what's already listed,
and becomes one change. Lines that can't be added stay in the box with the reason.

| Change | Sent as | Read back | Caution |
|---|---|---|---|
| Add an entry (senders, links, files, IPs) | `New-TenantAllowBlockListItems -ListType Sender\|Url\|FileHash\|IP -Block\|-Allow`, with `-NoExpiration`, `-ExpirationDate` or `-RemoveAfter 45` | `Get-TenantAllowBlockListItems -Entry` | when blocking |
| Add a spoofed sender | `New-TenantAllowBlockListSpoofItems -SpoofedUser -SendingInfrastructure -SpoofType -Action` | `Get-TenantAllowBlockListSpoofItems` | when blocking |
| Change an entry's expiry or note | `Set-TenantAllowBlockListItems -Ids` | its notes | no |
| Remove an entry | `Remove-TenantAllowBlockListItems -Ids` or `Remove-TenantAllowBlockListSpoofItems -Ids` | it's gone | when it was an allow |

- **Expiry:** blocks default to never expiring. Allows default to 45 days after they last match,
  Microsoft's own rule. Either can run until a date instead.
- **Write guard:** the entry's value is its target's address, so in Test mode only values starting
  with the test marker can change.
- **Quarantine's "Block the sender"** now stages an `AddListEntry`. `SetSenderListed` stays only so
  older change-log entries still read.
- **Live test (2026-10-26):** block, note change and remove for a made-up test sender and link,
  all read back, and nothing left behind.

## Write guard and Test mode

- **What it allows:** while a tenant is in Test mode, only objects whose **address** starts with
  `zz-tenantry-test-` can change (sign-in name for users, email for mailboxes and mail-enabled
  groups). Objects without an address, such as plain security groups, are judged by name. The
  address is used because display names are easy to change: renaming a real user
  "zz-tenantry-test-…" doesn't get past the guard.
- **What a refusal costs:** nothing is sent; the attempt is logged.
- **When it's on:** development builds start every tenant in Test mode. Turning it off is per
  tenant and only through the UI, by typing the tenant's name. The switch is remembered in the
  tenant's `settings.json` and logged.
- **Failing safe:** unreadable settings mean Test mode on.

## Change log

`ChangeLog` keeps every attempt in the tenant's encrypted local store. Each entry records when,
the signed-in account, the change, the outcome (Applied, AppliedNotYetVisible, Failed or
Refused) and the detail. The log never leaves the machine.

## Demo tenant

`DemoChangeExecutor` applies changes to the generated people in memory. The demo has no Test
mode, because nothing in it is real.

## Delivery

- **3a:** the pipeline, plus a user's display name, job title, department, and block/allow
  sign-in. Editable from the preview pane and the record page; gated on User Administrator.
- **4a:** the rest of the Account tab (password reset, sign-out everywhere, manager); licenses
  and apps; group owners and members and mailbox access, editable from the user, the group page
  and the group preview's Manage popup.
- **4a, bulk edit:** every list has a checkbox column (Ctrl- and Shift-click work too), and
  "Select from list" checks the rows named in pasted text. With two or more rows checked, the
  preview pane becomes the **selection panel** (`SelectionPanelViewModel`), which opens on its
  own and holds:
  - who is selected, each with × to uncheck;
  - what they have in common;
  - for users, every editable detail, showing the shared value or "Mixed"
    (`BulkDetailsViewModel`);
  - the bulk actions as rows that open in place.

  Actions are described as data (`BulkAction`: an optional picker, choice and text value, plus a
  planner that reads the current selection). Each plans one change per row and lists the rows it
  skips with the reason. `ChangePipeline.StageAll` stages them together and raises `Changed`
  once. Pending changes are indexed by (target, key), so screens can look up a slot without
  scanning the tray.
- **4d-1:** the OneDrive and Activity tabs (above).
- **Next:** workflows (offboarding, compromised account), then a Change log area.
