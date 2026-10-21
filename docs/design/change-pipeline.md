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

- **3a** (this): the pipeline, plus a user's display name, job title, department, and
  block/allow sign-in. Editable from the preview pane and the record page; gated on User
  Administrator.
- **3b:** licenses; group owners and members (owners sync first; distribution lists through
  Exchange); shared-mailbox access. All editable from the preview.
- **3c:** bulk edit from list multi-select, and a Change log area.
