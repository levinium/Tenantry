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
- Send on behalf needs a mail recipient. Exchange refuses it for an account with no mailbox, and
  Tenantry says so in plain words.

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
- **Next:** bulk edit from list multi-select, and a Change log area.
