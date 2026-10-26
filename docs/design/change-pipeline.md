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
