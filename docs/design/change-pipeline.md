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
- **Next:** a Change log area.
