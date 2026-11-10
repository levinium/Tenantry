# Mail security: cmdlet facts and feature gaps

Research notes for Tenantry's Exchange Online and Defender (email & collaboration) features. Sources are Microsoft Learn pages, read 2026-11-10. Nothing here was run against a tenant. Facts marked **(unverified)** came from memory or a non-Learn source and should be checked before code depends on them.

Cmdlet reference base URL: `https://learn.microsoft.com/powershell/module/exchangepowershell/<cmdlet-name-lowercase>`. A link written as `[ref]` below points there.

---

## Part A: cmdlet facts

### A1. Release-QuarantineMessage ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/release-quarantinemessage))

Parameter sets (a parameter not in a set can't be used with that set):

| Set | Required | Optional | Notes |
|---|---|---|---|
| OrgReleaseToUser | `-User <string[]>` | `-Identity`, `-Identities`, `-AllowSender`, `-ReportFalsePositive`, `-Force` | Release to chosen addresses. |
| OrgReleaseToAll | `-ReleaseToAll` | same as above | Release to all original recipients. |
| Identities | `-Identities <id[]>` | `-Identity`, **`-ActionType`**, `-AllowSender`, `-ReportFalsePositive`, `-Force` | Only set that takes `-ActionType`. |
| IdentityOnly | `-Identity` | `-AllowSender`, `-ReportFalsePositive`, `-Force` | |

| Fact | Detail |
|---|---|
| `-ActionType` values | `Deny`, `Release` ("release messages or approve requests to release messages"), `Request`. There is no `Approve` value: approving a user's request is `Release`. |
| Approve or deny a user request | Portal: Release / Deny on the Email tab ([admin quarantine page](https://learn.microsoft.com/defender-office-365/quarantine-admin-manage-messages-files)). PowerShell: `-Identities <ids> -Identity 000 -ActionType Release` or `-ActionType Deny`. "You can deny release for all recipients only. You can't deny release for specific recipients." |
| `-User` | Original recipients "or any other email addresses in the organization". Multiple addresses allowed. Recipients already released via `-ReleaseToAll` are skipped. |
| `-ReleaseToAll` | Skips recipients already released. Required for quarantine type `SPOMalware` (files). "You can't use the ReleaseToAll switch and the User parameter in the same command." |
| `-ReportFalsePositive` | Reports to Microsoft as false positive. "Available only for quarantined spam messages." |
| `-AllowSender` | Future messages from the sender aren't quarantined. Doesn't override a mail flow rule or blocked sender. |
| `-Force` | Needed to re-release an already released message. |
| Bulk (`-Identities`) | Yes. Format `GUID1\GUID2,...`. **"When you use this parameter, the Identity parameter is required, but the value is ignored. For example, use the value 000."** Same quirk on Delete- and Export-. |
| Portal bulk limit | Up to 100 selected messages per bulk action (admin quarantine page). |
| Duplicate delivery | Copies with the same Message-ID and received time released to the same mailbox: the second is skipped. |
| Release semantics | Re-delivers (new delivery timestamp), not restore in place. Can't release to the same recipient twice. |
| High confidence phishing | Recipients can only *request* release, never release, whatever the quarantine policy says ([anti-spam config](https://learn.microsoft.com/defender-office-365/anti-spam-policies-configure)). |
| Who can act | Quarantine Administrator, Security Administrator, Organization Management role groups (or Entra Security Administrator). Submit-to-Microsoft needs Security Administrator. Global Reader / Security Reader / Security Operator are read + preview + download. |
| Block sender | Portal-only, adds to the *signed-in admin's own* Blocked Senders list, and only appears when the view is filtered to Recipient = Only me. Not an org block. Use Tenant Allow/Block List for org blocks. |

### A2. Get-QuarantineMessage ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/get-quarantinemessage))

| Fact | Detail |
|---|---|
| Pending release requests | `-ReleaseStatus Requested`. All values: `Approved`, `Denied`, `Error`, `NotReleased`, `PreparingToRelease`, `Released`, `Requested`. |
| System-released | `SystemReleased = True` when Microsoft released it for a service issue (filter client-side). |
| Date range | Default last 16 days. Max 30 days; older `-StartReceivedDate` is ignored, not an error. |
| Paging | `-PageSize` 1-1000 (default 100), `-Page` 1-1000. |
| Per-recipient fields | `RecipientAddress`, `QuarantineUser`, `ReleasedUser` are populated **only** when you call with `-Identity` (Details set) and format the full object. The Summary list doesn't carry them. |
| Who requested release, and when | **Not documented on Learn.** No requester or request-time property is named. Check a live `-Identity` result for a `Requested` item before building UI on it. Microsoft also raises one alert per message for release requests (portal link covers the past 7 days). |
| `PermissionTo*` properties | Reflect the *caller's* rights, not the recipient's. To show what end users can do, read the assigned quarantine policy (`Get-QuarantinePolicy`). |
| Useful filters | `-QuarantineTypes` (`Bulk, DataLossPrevention, FileTypeBlock, HighConfPhish, Malware, Phish, Spam, SPOMalware, TransportRule`), `-PolicyTypes`, `-PolicyName`, `-Direction`, `-RecipientTag` (e.g. Priority Account), `-Reported`, `-EntityType` (`Email, SharePointOnline, Teams, DataLossPrevention`), `-IncludeMessagesFromBlockedSenderAddress`, `-StartExpiresDate/-EndExpiresDate`. |
| `$null` identity trap | A `$null` or nonexistent `-Identity` returns **all** objects. Guard empty IDs before calling. |

### A3. Get-QuarantineMessageHeader and Export-QuarantineMessage

| | Get-QuarantineMessageHeader ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/get-quarantinemessageheader)) | Export-QuarantineMessage ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/export-quarantinemessage)) |
|---|---|---|
| Input | `-Identity` only (no bulk), `-RecipientAddress`, `-EntityType` | `-Identity` or `-Identities` (+ dummy `-Identity 000`), `-RecipientAddress`, `-EntityType` |
| Output | Header text exactly as in the message (RFC 5322, not unfolded). Fails if the item isn't in quarantine. | Object with `Eml` (Base64) and `BodyEncoding`. Decode Base64 to bytes and write `.eml`. |
| Options | none | `-CompressOutput` (.zip: folder named GUID1, file named GUID2), `-ForceConversionToMime` (plain text to MIME; no effect if already Base64), `-Password` (SecureString) or `-PasswordV2` (plain string; easier over REST), `-ReasonForExport` |
| Files (SharePoint/OneDrive/Teams) | Not described for files; headers are an email concept | Files export as Base64 |
| Preview | Use `Preview-QuarantineMessage`: "for messages only, not quarantined files" | |
| Size limits | **None stated on Learn.** Over REST, large Base64 payloads may hit response limits **(unverified)**. | |
| Retention reminders | SharePoint/OneDrive files leave quarantine after 30 days but stay blocked in place. Quarantined Teams messages: 30 days, then permanently removed. Teams quarantine is currently AdminOnlyAccess. |

### A4. Delete-QuarantineMessage ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/delete-quarantinemessage))

| Fact | Detail |
|---|---|
| Bulk | `-Identities id1,id2,... -Identity 000` (Identity required, value ignored). |
| `-HardDelete` | Permanent. Without it, "deleted, but is potentially recoverable." |
| Confirm | Built-in prompt. Send `-Confirm:$false`. |
| Other | `-RecipientAddress <string[]>`, `-EntityType`. `-WhatIf` is "reserved for internal Microsoft use"; don't use it as a dry run. |

### A5. Message trace and historical search

| Cmdlet | Range | Per query | Results | Throttle | Notes |
|---|---|---|---|---|---|
| Get-MessageTraceV2 ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/get-messagetracev2)) | **last 90 days** | 10 days of data | `-ResultSize` 1-5000 (default 1000) | 100 queries / rolling 5 min | No paging. To continue, pass the last row's Recipient address as `-StartingRecipientAddress` and its Received time as `-EndDate`. `MessageTraceId` is required for messages to more than 1000 recipients. Times are UTC. `-Subject` + `-SubjectFilterType Contains/StartsWith/EndsWith`. |
| Get-MessageTraceDetailV2 ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/get-messagetracedetailv2)) | 90 days | 10 days | n/a | 100 / 5 min | **Required:** `-MessageTraceId` and `-RecipientAddress`. `-Event` (RECEIVE, SEND, FAIL, DELIVER, EXPAND, TRANSFER, DEFER). |
| Get-MessageTrace ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/get-messagetrace)) | 10 days | | 1000 per page, Page/PageSize | | Learn: "replaced by Get-MessageTraceV2 ... will eventually be deprecated." Exchange Team blog put the start of deprecation at 2025-09-01 **(not on Learn)**. Treat as retiring; use V2. |
| Start-HistoricalSearch ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/start-historicalsearch)) | 1-4 hours old up to 90 days | | CSV, max 100,000 rows | **250 searches / 24 h** (cancelled ones count) | Required: `-ReportTitle`, `-ReportType`, `-StartDate`, `-EndDate`, plus one of `-MessageID`, `-RecipientAddress`, `-SenderAddress` (max 100 addresses each). DL addresses may miss messages; use individuals. EndDate with no time means 0:00, so that day is excluded. |
| Get-HistoricalSearch ([ref](https://learn.microsoft.com/powershell/module/exchangepowershell/get-historicalsearch)) | lists searches from the last 10 days | | | | `-JobId` for detail. Output fields aren't listed on Learn; check for status and file URL on a live job **(unverified)**. |
| Stop-HistoricalSearch | | | | | Cancels by JobId; still counts toward the 250 quota. |

Historical search `-ReportType` values: `ATPReport`, `ConnectorReport`, `DLP`, `MessageTrace`, `MessageTraceDetail`, `OutboundSecurityReport`, `P2SenderAttribution`, `SPAM`, `Spoof`, `TransportRule`, `UnifiedDLP`.

Delivery: `-NotifyAddress` takes internal addresses only (accepted domain). Without it, the result is only reachable from EAC > Mail flow > Message trace. Learn doesn't describe a download API.

V2 `-Status` values: `Delivered`, `Expanded`, `Failed`, `FilteredAsSpam`, `GettingStatus`, `Pending`, `Quarantined`. The old cmdlet also had `None`.

**Takeaway:** V2 already covers 90 days in 10-day windows. Tenantry's 10-day limit can become 90 days by chunking, so historical search is only needed for the CSV report types.

### A6. Common NDR / enhanced status codes

Source: [Email nondelivery reports (NDRs) and SMTP errors in Exchange Online](https://learn.microsoft.com/troubleshoot/exchange/email-delivery/ndr/non-delivery-reports-in-exchange-online).

| Code | Meaning | Admin action |
|---|---|---|
| 4.4.7 | Message expired in queue; the remote server didn't accept it in time | Usually the receiving side. Check their MX/DNS and server health; trace for DEFER events. |
| 4.4.316 | Connection refused to an external server | Remote network issue. Wait, or confirm the destination's MX. |
| 4.5.3 | More than 200 envelope recipients from the same domain | The sending app must chunk recipients. |
| 4.7.500-699 / 4.7.850-899 | Temporarily restricted, suspicious activity | Usually lifts on its own. If it keeps happening, look for a compromised account or bad relay. |
| 4.7.26 / 5.7.25 | IPv6 message without SPF/DKIM pass, or no reverse DNS | Fix the sender's SPF/DKIM or PTR. |
| 5.1.0 | Sender denied (often a reply from a saved/offline copy) | Re-send from a fresh message; check address resolution. |
| 5.1.1 | Bad destination mailbox (typo, deleted, stale cache, bad legacyExchangeDN) | Check the address; clear the sender's autocomplete; add an X500 address after migrations. |
| 5.1.8 | Bad outbound sender: account blocked for spam (usually compromised) | Secure the account, then unblock it under Restricted entities (`Remove-BlockedSenderAddress`). |
| 5.1.10 | Recipient not found | Check the address; for internal recipients check the object and directory sync. |
| 5.1.90 / 5.2.2 | Sender hit the recipient or message rate limit | Check for compromise; bulk senders should use a proper bulk service. |
| 5.2.121 / 5.2.122 | Recipient per-hour receive limit exceeded | The sending mailer should slow down. |
| 5.4.1 | Relay access denied, or recipient address rejected (Directory-Based Edge Blocking) | Check accepted domain and MX setup; confirm the recipient exists. |
| 5.4.14 (5.4.6 on-prem) | Routing loop | Fix connector, forwarding or accepted-domain type loops. |
| 5.7.1 | Delivery not authorized / unable to relay / client not authenticated | Check group delivery restrictions, relay connector setup, SMTP auth. |
| 5.7.12, 5.7.133-136 | Recipient (mailbox, group, public folder, mail user) rejects external senders | Turn off "only accept from internal senders" if that isn't intended. |
| 5.7.23 | SPF violation | Fix the sending domain's SPF record. |
| 5.7.57 | Client not authenticated to send anonymous mail (smtp.office365.com) | Fix the device or app's SMTP AUTH settings, or use a connector. |
| 5.7.64 | TenantAttribution; relay access denied (inbound connector) | Fix the on-prem inbound connector certificate or IP. |
| 5.7.124 | Sender not in the group's allowed-senders list | Add the sender, or relax the restriction. |
| 5.7.367 | Forwarded/relayed mail failed SPF/DKIM downstream | Common with third-party gateways; review the forwarding path. |
| 5.7.501-503 | Account banned for spam | Fix the account, reset credentials, contact support. |
| 5.7.509 | Sender domain fails DMARC and its policy is reject | Sender's admin fixes SPF/DKIM alignment. |
| 5.7.511 | Sending IP banned | Delist via Microsoft's delist portal. |
| 5.7.520 | External auto-forwarding blocked by the outbound spam policy | Allow forwarding for those users in a custom outbound policy, or explain the block. |
| 5.7.705 / 5.7.708 | Tenant over threshold; sending banned | Clean up compromise or open relay, then contact support. |
| 5.7.750 | Sending from unregistered domains | Add and verify those domains as accepted domains. |

---

## Part B: threat policies

General model ([anti-spam config](https://learn.microsoft.com/defender-office-365/anti-spam-policies-configure), [anti-phishing EOP config](https://learn.microsoft.com/defender-office-365/anti-phishing-policies-eop-configure)):

- The portal shows one "policy". PowerShell keeps two objects: the **policy** (settings) and the **rule** (name, priority, enabled state, recipient conditions and exceptions). Name, priority, state and recipients live on the rule.
- Portal create makes both with the same name. In PowerShell, removing one does **not** remove the other, so a Tenantry delete must remove both.
- `Set-*Policy` has no `-Name`; renaming in the portal renames only the rule.
- `-MakeDefault` on `Set-*Policy` (PowerShell only) promotes a policy to default.
- Default policy: always on, applies to everyone, priority **Lowest** (unchangeable), no rule object, name can't change, can't be disabled or deleted. Every other setting is editable.
- Preset-linked policies (named "Standard Preset Security Policy" / "Strict Preset Security Policy") can't be edited, enabled, disabled or deleted individually.
- Priority: 0 is highest. No two rules share a value. `Set-*Rule -Priority N` shifts the others down by one. New rules go to the bottom unless `-Priority` is given on `New-*Rule`. Processing stops at the first matching policy for a recipient.
- Recipient conditions: values within one condition are OR; different condition types are AND; all exceptions are OR. Dynamic distribution groups aren't supported.
- Changes take up to 30 minutes to apply.

| Policy | Policy cmdlets | Rule cmdlets | License | Default policy |
|---|---|---|---|---|
| Anti-spam inbound | `Get/New/Set/Remove-HostedContentFilterPolicy` | `Get/New/Set/Remove/Enable/Disable-HostedContentFilterRule` | EOP (all) | Yes, "Default" |
| Anti-spam outbound | `Get/New/Set/Remove-HostedOutboundSpamFilterPolicy` | `Get/New/Set/Remove/Enable/Disable-HostedOutboundSpamFilterRule` | EOP | Yes. Not part of presets; the default covers preset users. |
| Anti-phishing | `Get/New/Set/Remove-AntiPhishPolicy` | `Get/New/Set/Remove/Enable/Disable-AntiPhishRule` | Spoof: EOP. Impersonation + thresholds: **MDO P1** | Yes |
| Anti-malware | `Get/New/Set/Remove-MalwareFilterPolicy` | `Get/New/Set/Remove/Enable/Disable-MalwareFilterRule` | EOP | Yes |
| Safe Links | `Get/New/Set/Remove-SafeLinksPolicy` | `Get/New/Set/Remove/Enable/Disable-SafeLinksRule` | **MDO P1** | No default. "Built-in protection" preset is the fallback. |
| Safe Attachments | `Get/New/Set/Remove-SafeAttachmentPolicy` + global `Get/Set-AtpPolicyForO365` | `Get/New/Set/Remove/Enable/Disable-SafeAttachmentRule` | **MDO P1** | No default; Built-in protection is the fallback. |
| Quarantine policy | `Get/New/Set/Remove-QuarantinePolicy` | none (assigned by `*QuarantineTag` params on other policies) | EOP | Built-ins `AdminOnlyAccessPolicy`, `DefaultFullAccessPolicy`, `DefaultFullAccessWithNotificationPolicy` can't be removed. |
| Connection filter | `Get/Set-HostedConnectionFilterPolicy -Identity Default` | none | EOP | **Only** the default; no custom policies, no scoping. |
| Preset security | n/a (policies are hidden and fixed) | `Get/New/Set/Enable/Disable-EOPProtectionPolicyRule`, `...-ATPProtectionPolicyRule`, `Get/New/Set-ATPBuiltInProtectionRule` | Standard/Strict EOP part: EOP. ATP part + Built-in: MDO | Built-in protection applies to everyone; only exceptions can be set. |

Policy order when one recipient is in several ([preset policies](https://learn.microsoft.com/defender-office-365/preset-security-policies)): Strict preset, then Standard preset, then MDO evaluation policies, then custom policies by priority, then Built-in protection (Safe Links/Attachments) or the default policies (spam/phish/malware).

### Key settings by portal section

**Anti-spam inbound** (`HostedContentFilterPolicy`)

| Portal section | Parameters |
|---|---|
| Bulk email threshold & spam properties | `BulkThreshold` (1-9), `MarkAsSpam*` / `IncreaseScoreWith*` ASF switches, `TestModeAction`, `EnableLanguageBlockList` + `LanguageBlockList`, `EnableRegionBlockList` + `RegionBlockList` |
| Actions | `SpamAction`, `HighConfidenceSpamAction`, `PhishSpamAction`, `HighConfidencePhishAction` (Quarantine only), `BulkSpamAction`, matching `*QuarantineTag`, `QuarantineRetentionPeriod`, `InlineSafetyTipsEnabled`, `SpamZapEnabled`, `PhishZapEnabled` |
| Allow & block list | `AllowedSenders`, `AllowedSenderDomains`, `BlockedSenders`, `BlockedSenderDomains` (Microsoft recommends the Tenant Allow/Block List instead) |

Standard vs Strict: BulkThreshold 6 vs 5; Spam and Bulk actions MoveToJmf vs Quarantine.

**Anti-spam outbound** (`HostedOutboundSpamFilterPolicy`) **(parameters from memory, unverified)**

| Section | Parameters |
|---|---|
| Message limits | `RecipientLimitExternalPerHour`, `RecipientLimitInternalPerHour`, `RecipientLimitPerDay`, `ActionWhenThresholdReached` (Alert / BlockUserForToday / BlockUser) |
| Forwarding rules | `AutoForwardingMode` (Automatic / On / Off). Off produces NDR 5.7.520. |
| Notifications | `BccSuspiciousOutboundMail` + `BccSuspiciousOutboundAdditionalRecipients`, `NotifyOutboundSpam` + `NotifyOutboundSpamRecipients` |

**Anti-phishing** (`AntiPhishPolicy`)

| Section | Parameters | License |
|---|---|---|
| Phishing threshold & protection | `EnableSpoofIntelligence` | EOP |
| | `PhishThresholdLevel` (1-4), impersonation: `EnableTargetedUserProtection` + `TargetedUsersToProtect`, `EnableOrganizationDomainsProtection`, `EnableTargetedDomainsProtection` + `TargetedDomainsToProtect`, `EnableMailboxIntelligence`, `EnableMailboxIntelligenceProtection`, `ExcludedSenders`, `ExcludedDomains` | MDO P1 |
| Actions | `HonorDmarcPolicy`, `DmarcQuarantineAction` (MoveToJmf/Quarantine), `DmarcRejectAction` (Quarantine/Reject), `AuthenticationFailAction`, `SpoofQuarantineTag` | EOP |
| | `TargetedUserProtectionAction`, `TargetedDomainProtectionAction`, `MailboxIntelligenceProtectionAction` + matching quarantine tags, impersonation safety tips | MDO P1 |
| Safety tips & indicators | `EnableFirstContactSafetyTips`, `EnableUnauthenticatedSender` ("?"), `EnableViaTag` | EOP |

**Anti-malware** (`MalwareFilterPolicy`) **(parameter names from memory, unverified)**

| Section | Parameters |
|---|---|
| Protection settings | `EnableFileFilter` (common attachments filter) + `FileTypes` + `FileTypeAction` (Reject / Quarantine), `ZapEnabled`, `QuarantineTag` (default AdminOnlyAccessPolicy) |
| Notifications | `EnableInternalSenderAdminNotifications` + `InternalSenderAdminAddress`, `EnableExternalSenderAdminNotifications` + `ExternalSenderAdminAddress`, `CustomNotifications` + custom From/subject/body |

**Safe Links** (`SafeLinksPolicy`, MDO P1) **(unverified, except the preset differences)**

| Section | Parameters |
|---|---|
| URL & click protection | `EnableSafeLinksForEmail`, `EnableSafeLinksForTeams`, `EnableSafeLinksForOffice`, `ScanUrls`, `DeliverMessageAfterScan`, `EnableForInternalSenders`, `DisableUrlRewrite`, `DoNotRewriteUrls`, `TrackClicks`, `AllowClickThrough` |
| Notification | `EnableOrganizationBranding`, `CustomNotificationText`, `UseTranslatedNotificationText` |

Built-in protection differs from Standard/Strict: AllowClickThrough on, DisableUrlRewrite on, EnableForInternalSenders off.

**Safe Attachments** (`SafeAttachmentPolicy`, MDO P1)

| Section | Parameters |
|---|---|
| Policy | `Enable`, `Action` (Block / DynamicDelivery / Allow; older Monitor/Replace values retired **(unverified)**), `QuarantineTag`, `Redirect` + `RedirectAddress` |
| Global settings (`Set-AtpPolicyForO365`) | `EnableATPForSPOTeamsODB` (files in SharePoint, OneDrive, Teams), `EnableSafeDocs`, `AllowSafeDocsOpen`. Related: `Set-SPOTenant -DisallowInfectedFileDownload $true` (SharePoint PowerShell, not EXO). |

**Quarantine policies** ([quarantine policies](https://learn.microsoft.com/defender-office-365/quarantine-policies))

| Item | Detail |
|---|---|
| Permissions | `-EndUserQuarantinePermissionsValue` 0-236, a bitmask: ViewHeader 128, Download 64, AllowSender 32 (unused), BlockSender 16, RequestRelease 8, Release 4, Preview 2, Delete 1. Pass the decimal value. RequestRelease and Release are mutually exclusive. |
| Notifications | `-EsnEnabled $true` turns on quarantine notifications for that policy. |
| Global settings | `Get-QuarantinePolicy -QuarantinePolicyType GlobalQuarantinePolicy \| Set-QuarantinePolicy` with `-EndUserSpamNotificationFrequency` (4 hours / daily / weekly), `-EndUserSpamNotificationCustomFromAddress`, `-OrganizationBrandingEnabled`, `-MultiLanguageSetting`, `-MultiLanguageCustomDisclaimer`, `-ESNCustomSubject`, `-MultiLanguageSenderName`. |
| Before removing | Check every `*QuarantineTag` on spam, phish, malware and Safe Attachments policies. |

**Connection filter** ([connection filter](https://learn.microsoft.com/defender-office-365/connection-filter-policies-configure))

| Setting | Detail |
|---|---|
| `IPAllowList` / `IPBlockList` | Max 1,273 entries each. Single IP, range, or CIDR /24-/32. **No IPv6** (use Tenant Allow/Block List). Allow beats block. Use `@{Add=...;Remove=...}` to edit without overwriting. |
| `EnableSafeList` | Microsoft-managed safe list on or off. |
| Gotchas | Allow-list IPs are still scanned for malware and high confidence phishing. Block-listed sources don't appear in message trace. |

---

## Part C: features Tenantry doesn't have yet, ranked

Already covered: quarantine (list, preview, release, delete, block sender), message trace (10 days + detail), mail flow rules, Tenant Allow/Block List, mailboxes/groups/contacts.

Ranked by value to a small or medium org admin:

| # | Feature | What it does | Read / write API | License | Value | Why |
|---|---|---|---|---|---|---|
| 1 | Trace to 90 days | Extend the current trace from 10 to 90 days | `Get-MessageTraceV2` in 10-day chunks; `Start/Get/Stop-HistoricalSearch` for CSV reports | EOP | **High** | Cheap change; "where did last month's email go" is a common ticket. |
| 2 | Quarantine release requests + bulk actions | Queue of `ReleaseStatus Requested`, approve/deny in bulk, export .eml, view header | `Get-QuarantineMessage -ReleaseStatus Requested`, `Release-QuarantineMessage -Identities ... -ActionType Release/Deny`, `Export-QuarantineMessage`, `Get-QuarantineMessageHeader` | EOP | **High** | Users request release daily; the portal workflow is slow. |
| 3 | Restricted entities (blocked senders) | List and unblock users blocked for outbound spam (NDR 5.1.8) | `Get-BlockedSenderAddress`, `Remove-BlockedSenderAddress` **(unverified on Learn this pass)** | EOP | **High** | Compromised-account cleanup; the portal page is hard to find. |
| 4 | Threat policy editor (Part B) | View and edit anti-spam, anti-phish, malware, outbound, connection filter, quarantine policies; preset on/off and scoping | Cmdlets in Part B | EOP (+MDO P1 for Safe Links/Attachments and impersonation) | **High** | The core of "email & collaboration" and it's spread across many portal pages. |
| 5 | DKIM + email auth health | Show DKIM status, CNAMEs to publish, enable, rotate; check SPF/DMARC DNS | `Get/New/Set-DkimSigningConfig`, `Rotate-DkimSigningConfig` **(unverified)**; DNS lookups for SPF/DMARC | EOP | **High** | Bulk-sender rules at the big mailbox providers made this a frequent setup task. |
| 6 | User-reported messages + admin submissions | See what users reported; submit email, URL or file to Microsoft; set the report mailbox | Graph `security/threatSubmission/emailThreats` (create documented in beta; app permission `ThreatSubmission.ReadWrite.All`); `Get/Set-ReportSubmissionPolicy`, `*-ReportSubmissionRule` **(unverified)** | EOP | Medium-High | Closes the loop on false positives and phish reports. |
| 7 | Connectors | Inbound/outbound connectors (scanners, apps, third-party filters) | `Get/New/Set/Remove-InboundConnector`, `...-OutboundConnector`, `Validate-OutboundConnector` **(unverified)** | EOP | Medium | Rarely changed, but a broken connector stops mail; read-only view plus validate is the main value. |
| 8 | Spoof intelligence insight | Spoofed senders allowed or blocked, with allow/block override | `Get-SpoofIntelligenceInsight` (read); overrides through Tenant Allow/Block List spoof entries (already in Tenantry) | EOP | Medium | Explains "why did this vendor's mail go to junk". Learn pages disagree on the window (7 vs 30 days). |
| 9 | Remote domains | Per-domain auto-reply, auto-forward, NDR, TNEF and format settings | `Get/New/Set/Remove-RemoteDomain` **(unverified)** | EOP | Medium | Controls external forwarding and out-of-office leaks; it's the second lever next to outbound spam `AutoForwardingMode`. |
| 10 | Org mail settings | SMTP AUTH on/off, message recall, external sender tag, plus-addressing etc. | `Get/Set-OrganizationConfig` (e.g. `-MessageRecallEnabled`, `-RecallReadMessagesEnabled` per [cloud recall](https://learn.microsoft.com/exchange/mail-flow-best-practices/work-with-cloud-based-message-recall)), `Get/Set-TransportConfig` (e.g. `SmtpClientAuthenticationDisabled`), `Get/Set-ExternalInOutlook` **(unverified)** | EOP | Medium | A handful of toggles with large effects; good for a "security posture" page. |
| 11 | Alert policies + alerts | View alerts (malicious file, release request, restricted user) and edit alert policies | Graph `security/alerts_v2` (read); `Get/New/Set-ProtectionAlert` is **Security & Compliance PowerShell**, a different endpoint from EXO InvokeCommand | EOP (some alerts need MDO/E5) | Medium | Useful, but S&C needs a second connection path. Check whether S&C has a REST InvokeCommand equivalent first. |
| 12 | Accepted domains | List domains, type (Authoritative/InternalRelay), MX status | `Get-AcceptedDomain`, `Set-AcceptedDomain` (type); adding a domain goes through Graph `domains` | EOP | Low-Medium | Read-only context for other features; domains rarely change. |
| 13 | Mail flow reports | Inbound/outbound volume, spam/malware counts | `Get-MailflowStatusReport` and `Get-MailDetail*Report` **(unverified)**; Graph `reports` covers usage, not mail flow | EOP | Low-Medium | Nice dashboards; parts of the old reporting web service were retired in 2026, so check what still works. |
| 14 | Journal rules | Copy mail to an external archive | `Get/New/Set/Remove-JournalRule` **(unverified)** | EOP (external journaling mailbox) | Low | Few small orgs journal; retention policies replace it. |
| 15 | Impersonation insight | Users and domains detected as impersonated | Portal only; no cmdlet found on Learn | MDO P1 | Low | No API; impersonation isn't on by default. |
| 16 | DMARC reports | Aggregate (rua) reports for your domains | No Microsoft 365 API. Reports go to the rua address in DNS. | n/a | Low | Better done by a DMARC service; Tenantry can check the DNS record (see #5). |
| 17 | Message recall (admin) | Users recall their own messages; admins only set org toggles | `Set-OrganizationConfig -MessageRecallEnabled / -RecallReadMessagesEnabled` | EOP | Low | No admin recall action exists; folded into #10. Threat Explorer "soft delete" needs MDO P2. |

### Surprises and gotchas

- `-Identities` (bulk) on Release, Delete and Export also **requires a dummy `-Identity`** (Learn uses `000`). Build this into the InvokeCommand payload.
- `-ActionType` has no `Approve`: approval is `Release`. It exists only in the Identities parameter set, so approve/deny is always the bulk form.
- Get-QuarantineMessage: `$null` Identity returns everything; dates older than 30 days are silently ignored; per-recipient fields only appear on `-Identity` calls.
- Learn documents no field for who requested release, or when.
- Admin "Block sender" in quarantine blocks the sender for the *admin's own mailbox* only.
- `Delete-QuarantineMessage -WhatIf` is reserved for Microsoft; there's no dry run.
- Removing a policy in PowerShell leaves its rule behind, and the reverse. Always remove both.
- Connection filter has no IPv6 and a 1,273-entry cap. Blocked-IP mail doesn't show in trace.
- Alert policy cmdlets are Security & Compliance PowerShell, not Exchange Online, so they may not work over the current REST endpoint.

---

## Part D: reported messages and submissions

Read 2026-11-10 from Learn. This section replaces the **(unverified)** marks on row 6 of Part C. Link keys used below:

| Key | URL |
| --- | --- |
| [ts] | https://learn.microsoft.com/graph/api/resources/security-threatsubmission?view=graph-rest-beta |
| [ets] | https://learn.microsoft.com/graph/api/resources/security-emailthreatsubmission?view=graph-rest-beta |
| [ets-list] | https://learn.microsoft.com/graph/api/security-emailthreatsubmission-list?view=graph-rest-beta |
| [ets-post] | https://learn.microsoft.com/graph/api/security-emailthreatsubmission-post-emailthreats?view=graph-rest-beta |
| [ets-review] | https://learn.microsoft.com/graph/api/security-emailthreatsubmission-review?view=graph-rest-beta |
| [result] | https://learn.microsoft.com/graph/api/resources/security-submissionresult?view=graph-rest-beta |
| [adminreview] | https://learn.microsoft.com/graph/api/resources/security-submissionadminreview?view=graph-rest-beta |
| [url-post] | https://learn.microsoft.com/graph/api/security-urlthreatsubmission-post-urlthreats?view=graph-rest-beta |
| [file-post] | https://learn.microsoft.com/graph/api/security-filethreatsubmission-post-filethreats?view=graph-rest-beta |
| [policy] | https://learn.microsoft.com/graph/api/resources/security-emailthreatsubmissionpolicy?view=graph-rest-beta |
| [policy-update] | https://learn.microsoft.com/graph/api/security-emailthreatsubmissionpolicy-update?view=graph-rest-beta |
| [sec-overview] | https://learn.microsoft.com/graph/api/resources/security-api-overview?view=graph-rest-beta |
| [perms] | https://learn.microsoft.com/graph/permissions-reference |
| [mdo-admin] | https://learn.microsoft.com/defender-office-365/submissions-admin |
| [mdo-usersettings] | https://learn.microsoft.com/defender-office-365/submissions-user-reported-messages-custom-mailbox |
| [mdo-review] | https://learn.microsoft.com/defender-office-365/submissions-admin-review-user-reported-messages |
| [mdo-report] | https://learn.microsoft.com/defender-office-365/submissions-report-messages-files-to-microsoft |
| [rsp] | https://learn.microsoft.com/powershell/module/exchangepowershell/set-reportsubmissionpolicy |
| [rsr] | https://learn.microsoft.com/powershell/module/exchangepowershell/new-reportsubmissionrule |
| [alertpol] | https://learn.microsoft.com/defender-xdr/alert-policies |
| [alerts] | https://learn.microsoft.com/graph/api/security-list-alerts_v2?view=graph-rest-1.0 |

### D1. Graph threat submission API: status and listing

| Fact | Detail | Source |
| --- | --- | --- |
| Version | **Beta only.** Every threatSubmission page has only the `graph-rest-beta` moniker; asking for `view=graph-rest-1.0` serves the beta page. The .NET v1.0 SDK has no types for it; use the beta SDK or raw HTTP. | [ts], [ets-list] |
| Clouds | Global service only (not GCC High, DoD, 21Vianet). | [ets-list] |
| List email submissions | `GET /beta/security/threatSubmission/emailThreats`. Supports `$filter`, `$top`, `$skipToken`, `$count`. | [ets-list] |
| Get one | `GET /beta/security/threatSubmission/emailThreats/{id}` | [sec-overview] |
| URL and file lists | `GET .../threatSubmission/urlThreats`, `GET .../threatSubmission/fileThreats` | [sec-overview] |
| User-reported messages included? | **Yes.** The overview says the API "adds unified result support, **user submission query support**, tenant-allow block list support, admin review support and app-only mode support". The `review` action exists only for submissions "submitted by end users". | [sec-overview], [ets-review] |
| Telling them apart | `source`: `administrator`, `user`, `unknownFutureValue`. Supports `$filter=source eq 'user'`. | [ts] |
| Filterable properties | `category`, `createdBy/email`, `createdDateTime` (ge/lt), `source`, `status` | [ts] |

Learn does not say that every row on the portal's **User reported** tab appears in Graph (for example, reports sent to the reporting mailbox only, or from a non-Microsoft add-in). Test this in a tenant before relying on it.

### D2. emailThreatSubmission properties

Base properties come from threatSubmission [ts]; email properties from emailThreatSubmission [ets].

| Property | Type | Values / notes | Source |
| --- | --- | --- | --- |
| id | String | Submission ID | [ts] |
| tenantId | String | Taken from the token on POST | [ts] |
| createdDateTime | DateTimeOffset | Filterable | [ts] |
| contentType | submissionContentType | `email`, `url`, `file`, `app` | [ts] |
| category | submissionCategory | What the submitter reported it as: `notJunk`, `spam`, `phishing`, `malware` | [ts] |
| source | submissionSource | `administrator`, `user` | [ts] |
| clientSource | submissionClientSource | `microsoft`, `other` | [ts] |
| createdBy | submissionUserIdentity | `createdBy.user.{identity, displayName, email}` (example JSON) | [ts], [ets-list] |
| status | longRunningOperationStatus | `notStarted`, `running`, `succeeded`, `failed`, `skipped` | [ts] |
| result | submissionResult | See D3 | [result] |
| adminReview | submissionAdminReview | `reviewBy` (String), `reviewDateTime`, `reviewResult` (submissionResultCategory). `null` until an admin reviews. | [adminreview] |
| recipientEmailAddress | String | Recipient, SMTP format | [ets] |
| internetMessageId | String | From the header | [ets] |
| subject | String | Example responses use `emailSubject` instead; handle both | [ets], [ets-list] |
| sender | String | Sender address | [ets] |
| senderIP | String | | [ets] |
| receivedDateTime | DateTimeOffset | | [ets] |
| originalCategory | submissionCategory | Original category | [ets] |
| attackSimulationInfo | attackSimulationInfo | Non-null if the mail was a phishing simulation | [ets] |
| tenantAllowOrBlockListAction | tenantAllowOrBlockListAction | `action` (allow/block), `expirationDateTime`, `note`, `results[]` (one per TABL entry: `identity`, `value`, `entryType`, `status`) | [ets], [ets-list] |
| messageUrl | String | emailUrlThreatSubmission only | [ets-post] |
| fileContent | String | emailContentThreatSubmission only (base64 .eml in the example) | [ets-post] |

Example list item, from [ets-list] with placeholders swapped:

```json
{
  "@odata.type": "#microsoft.graph.security.emailThreatSubmission",
  "id": "49c5ef5b-1f65-444a-e6b9-08d772ea2059",
  "createdDateTime": "2021-10-10T03:30:18.6890937Z",
  "contentType": "email",
  "category": "spam",
  "source": "administrator",
  "status": "succeeded",
  "createdBy": { "user": { "identity": "<object-id>", "displayName": "Admin", "email": "admin@contoso.example" } },
  "recipientEmailAddress": "user@contoso.example",
  "emailSubject": "This is a spam",
  "sender": "test@contoso.example",
  "senderIP": "127.0.0.1",
  "receivedDateTime": "2021-10-09T03:30:18.6890937Z",
  "internetMessageId": "some-id@contoso.example",
  "originalCategory": "notSpam",
  "result": {
    "category": "notSpam",
    "detail": "allowedByTenant",
    "userMailboxSetting": "isFromDomainInDomainSafeList,isJunkMailRuleEnabled",
    "detectedUrls": ["contoso.example"],
    "detectedFiles": [{ "fileName": "test.ps1", "fileHash": "<hash>" }]
  },
  "adminReview": null,
  "attackSimulationInfo": null,
  "tenantAllowOrBlockListAction": null
}
```

### D3. submissionResult

| Field | Notes | Source |
| --- | --- | --- |
| category | Long enum: `notJunk`, `spam`, `phishing`, `malware`, `allowedByPolicy`, `blockedByPolicy`, `spoof`, `unknown`, `noResultAvailable`, then newer values (`beingAnalyzed`, `notSubmittedToMicrosoft`, `phishingSimulation`, `allowedDueToOrganizationOverride`, `blockedDueToOrganizationOverride`, `allowedDueToUserOverride`, `blockedDueToUserOverride`, `itemNotfound`, `threatsFound`, `noThreatsFound`, `domainImpersonation`, `userImpersonation`, `brandImpersonation`, `authenticationFailure`, `spoofedBlocked`, `spoofedAllowed`, `bulk`, `reasonLostInTransit`). **The newer values only come back with the header `Prefer: include-unknown-enum-members`.** | [result] |
| detail | About 70 values, each with a fixed admin-facing sentence on Learn. Useful ones: `underInvestigation`, `willNotifyOnceDone`, `checkUserReportedSettings` (reports aren't going to Microsoft), `messageNotFound`/`itemDeleted`, `allowedByTenantAllowBlockList`, `blockedByExchangeTransportRule`, `allowedByUserSetting`, `itemFoundClean`, `itemFoundMalicious`, `itemFoundSpam`, `itemFoundBulk`, `unableToMakeDecision`, `simulatedThreat`. Map these to UI text from the Learn table. | [result] |
| userMailboxSetting | Comma-separated flags, e.g. `isFromAddressInAddressSafeList`, `isJunkMailRuleEnabled`, `junkMailDeletion`, `customRule` | [result] |
| detectedUrls | String[] | [result] |
| detectedFiles | `{fileName, fileHash}`[] | [result] |

### D4. Creating admin submissions

| Kind | Endpoint | `@odata.type` | Required body | Source |
| --- | --- | --- | --- | --- |
| Email by mailbox link | `POST /beta/security/threatSubmission/emailThreats` | `#microsoft.graph.security.emailUrlThreatSubmission` | `category`, `recipientEmailAddress`, `messageUrl` (Graph message URL, e.g. `https://graph.microsoft.com/beta/users/{userId}/messages/{messageId}`) | [ets-post] |
| Email by content | same | `#microsoft.graph.security.emailContentThreatSubmission` | `category`, `recipientEmailAddress`, `fileContent` (base64 of the .eml) | [ets-post] |
| Optional on email | | | `tenantAllowOrBlockListAction: {action, expirationDateTime, note}` adds TABL entries for the message's URLs, files or sender | [ets-post] |
| URL | `POST /beta/security/threatSubmission/urlThreats` | `#microsoft.graph.urlThreatSubmission` (as written on Learn; the response uses `#microsoft.graph.security.urlThreatSubmission`) | `category`, `webUrl` | [url-post] |
| File | `POST /beta/security/threatSubmission/fileThreats` | `#microsoft.graph.security.fileContentThreatSubmission` | `category`, `fileName`, `fileContent` (base64) | [file-post] |
| File by URL | | `fileUrlThreatSubmission` | "reserved and not supported as of today" | [file-post] |

| Behaviour | Detail | Source |
| --- | --- | --- |
| `category` on POST | `notJunk`, `spam`, `phishing`, `malware`. False positives use `notJunk`. | [ts], [url-post] |
| Response | `201 Created` with the submission. URL and file examples return `"status": "running"` and `"result": {"detail": "underInvestigation"}`; poll `GET .../{id}` until `status` is `succeeded` or `failed`. | [url-post], [file-post] |
| What's stored | "the threat submission service just stores some metadata about the email. The email content is not stored." | [ets-post] |
| How long results take | **Learn gives no time.** Analysis includes human graders. Treat it as asynchronous; `willNotifyOnceDone` is one possible detail. | [mdo-admin], [result] |
| Age limit | Admins can submit email up to 30 days old if it's still in the mailbox. On-premises mailboxes: under 7 days. | [mdo-admin] |
| Throttling (portal) | 150 submissions per 15 minutes; the same item at most once per 15 minutes and three times per 24 hours. Learn states this for the portal; assume Graph shares it. | [mdo-admin] |
| Allow/block expiry (portal) | Block entry default 30 days; allow entry default "45 days after last used date"; maximum specific date 30 days out. | [mdo-admin] |

Fit with Tenantry's existing data:

| Starting point | Approach | Notes |
| --- | --- | --- |
| Quarantined message | `Export-QuarantineMessage` (Part A3) gives the .eml; post it as `emailContentThreatSubmission`. | A quarantined message isn't in the mailbox, so `messageUrl` doesn't fit. The portal accepts a network message ID instead; Graph has no network-message-ID property. |
| Message trace row (delivered) | Find the message in the recipient's mailbox by `internetMessageId`, then post its Graph URL as `emailUrlThreatSubmission`. | Learn doesn't say which mail permission the submission service needs to fetch `messageUrl` from another user's mailbox. **Test it.** |

### D5. Permissions, roles, licences

| Permission (delegated) | What it allows | Source |
| --- | --- | --- |
| ThreatSubmission.Read | Read the submissions and policies **owned by the signed-in user** | [perms] |
| ThreatSubmission.ReadWrite | Same, plus create submissions | [perms] |
| ThreatSubmission.Read.All | Read **all the organization's** submissions and policies | [perms] |
| ThreatSubmission.ReadWrite.All | Read all, create. **Least privileged for `review`** (no lower scope works) | [perms], [ets-review] |
| ThreatSubmissionPolicy.ReadWrite.All | Read and update the user-reported settings policy (D7) | [perms], [policy-update] |

All five need admin consent [perms]. **Add `ThreatSubmission.ReadWrite.All`** (delegated) so an admin sees user reports from everyone, can submit and can review. Add `ThreatSubmissionPolicy.ReadWrite.All` only if the settings card writes through Graph instead of EXO.

| Role / licence | Detail | Source |
| --- | --- | --- |
| Graph role check | The threatSubmission pages don't list required Entra roles. Portal equivalents: Security Administrator or Security Reader (Entra or Email & collaboration role group). Submitting by network message ID in the portal also needs "Email & collaboration metadata (read)" under unified RBAC. | [mdo-admin] |
| Review / notify (portal) | Organization Management or Security Administrator role group; Entra Global Administrator, Security Administrator or Global Reader | [mdo-review] |
| Licence | The Submissions page and user reporting need Exchange Online mailboxes. EOP is enough for email; Teams reports need MDO Plan 1 or 2. AIR auto-investigation and auto-notify need MDO Plan 2. | [mdo-admin], [mdo-usersettings] |
| GCC/GCC High/DoD | Only "My reporting mailbox only" is allowed; admin submissions get authentication and policy checks only | [mdo-usersettings], [mdo-admin] |

### D6. Marking a user report reviewed and telling the reporter

| Question | Answer | Source |
| --- | --- | --- |
| Graph action | `POST /beta/security/threatSubmission/emailThreats/{id}/review` with `{"category": "phishing"}`. Values: notSpam, junk, phishing, malware (case-insensitive). Returns `204`. Works only for email submissions from end users (`source eq 'user'`). | [ets-review] |
| Result after review | `adminReview.{reviewBy, reviewDateTime, reviewResult}` is filled in | [adminreview] |
| Does it email the reporter? | **Learn doesn't say so for the Graph action.** The policy has `isReviewEmailNotificationEnabled`: "whether an email notification is sent to the end user who reported the email when it has been reviewed by the admin". Assume the review action plus that flag behaves like the portal's **Mark as and notify**. Test it. | [policy] |
| Portal equivalent | **Mark as and notify** with verdicts No threats found, Phishing, Spam. Marks the message and "automatically" emails the reporter. Doesn't change the message's filtering verdict. | [mdo-review] |
| Order | Learn's portal flow: submit to Microsoft first (when reports go to the mailbox only), then Mark as and notify. Only false-positive or false-negative reports can be marked. | [mdo-admin], [mdo-review] |
| Submitting a user report to Microsoft | Portal: **Submit to Microsoft for analysis** on the User reported tab sets "Converted to admin submission" to Yes and creates a row on the Emails tab. Learn shows no Graph "convert" action. Re-post the message as a new admin submission instead. | [mdo-admin] |

### D7. Where user reports go: EXO cmdlets and Graph policy

| Element | Role | Source |
| --- | --- | --- |
| `DefaultReportSubmissionPolicy` | The only policy (name can't change). Turns Outlook reporting on/off, Microsoft on/off, mailbox on/off, pop-ups, notifications. **It doesn't exist until someone opens the portal settings page or runs `New-ReportSubmissionPolicy`**, so `Get-` can return nothing. | [mdo-usersettings] |
| `DefaultReportSubmissionRule` | Holds `-SentTo` (the reporting mailbox). Needed **only** when reports go to a mailbox. The portal creates it the first time a mailbox is saved. You can name it anything, but Learn says use `DefaultReportSubmissionRule`. | [mdo-usersettings], [rsr] |
| Sync gotcha | The portal writes the address to both rule and policy. `Set-ReportSubmissionRule -SentTo` does **not** update the policy's address fields; set both. Removing the policy leaves the rule, and the reverse. | [mdo-usersettings], [rsr] |
| Default mailbox | Blank means the Global Admin's mailbox, and it isn't shown anywhere until the first user reports a message | [mdo-usersettings] |
| Mailbox limits | Must be an Exchange Online mailbox. "Distribution groups and routing to an external or on-premises mailbox aren't allowed." Make it a SecOps mailbox in the advanced delivery policy, and exclude it from DLP. | [mdo-usersettings] |

Mode mapping (built-in Report button, `-EnableThirdPartyAddress $false`):

| Portal "Send reported items to" | EnableReportToMicrosoft | Report{Junk,NotJunk,Phish}ToCustomizedAddress | Report{Junk,NotJunk,Phish}Addresses | Rule | EnableUserEmailNotification | Source |
| --- | --- | --- | --- | --- | --- | --- |
| Microsoft and my reporting mailbox (default) | `$true` | `$true` (all three) | mailbox (all three, same value) | `-SentTo` mailbox | (default) | [mdo-usersettings] |
| My reporting mailbox only | `$false` | `$true` | mailbox | `-SentTo` mailbox | `$true` **required** | [mdo-usersettings], [rsp] |
| Microsoft only | `$true` | `$false` | `$null` | remove: `Get-ReportSubmissionRule \| Remove-ReportSubmissionRule` | `$true` in Learn's example | [mdo-usersettings] |
| Reporting turned off | `$false` | `$false` | `$null` | remove | n/a; also `-PreSubmitMessageEnabled $true -PostSubmitMessageEnabled $true` (Learn says this combination is needed) | [mdo-usersettings] |

Non-Microsoft add-in (`-EnableThirdPartyAddress $true`, `-ThirdPartyReportAddresses <mbx>`, rule `-SentTo <mbx>`): only "mailbox only" (`EnableReportToMicrosoft $false`) or "Microsoft and mailbox" (`$true`). "Microsoft only" isn't available. Learn's text differs on whether the three `*ToCustomizedAddress` flags should be `$true` or `$false` in this mode. [mdo-usersettings], [rsp]

Learn's example for Microsoft and mailbox (address swapped):

```powershell
$usersub = "reportedmessages@contoso.example"
Set-ReportSubmissionPolicy -Identity DefaultReportSubmissionPolicy -EnableReportToMicrosoft $true -EnableThirdPartyAddress $false -ThirdPartyReportAddresses $null -ReportJunkToCustomizedAddress $true -ReportJunkAddresses $usersub -ReportNotJunkToCustomizedAddress $true -ReportNotJunkAddresses $usersub -ReportPhishToCustomizedAddress $true -ReportPhishAddresses $usersub -PreSubmitMessageEnabled $false -PostSubmitMessageEnabled $false
Set-ReportSubmissionRule -Identity DefaultReportSubmissionRule -SentTo $usersub   # or New-ReportSubmissionRule -Name DefaultReportSubmissionRule -ReportSubmissionPolicy DefaultReportSubmissionPolicy -SentTo $usersub
```

Other policy parameters for the card ([rsp] unless noted):

| Parameter | Meaning |
| --- | --- |
| ReportJunkAddresses / ReportNotJunkAddresses / ReportPhishAddresses | Must be set together, to the same value. Set `$null` when the matching `*ToCustomizedAddress` is `$false`. |
| Report*ToCustomizedAddress | Must be set together in the same command |
| PreSubmitMessageEnabled | "Ask the user to confirm before reporting". **The portal creates the policy with `$false`; `New-ReportSubmissionPolicy` defaults to `$true`.** [mdo-usersettings] |
| PostSubmitMessageEnabled | Success pop-up after reporting. Same default mismatch. |
| MultiLanguagePre/PostSubmitMessage* + MultiLanguageSetting | Custom pop-up text for up to 7 languages. Arrays must line up, and unused parameters need blank placeholders. [mdo-usersettings] |
| EnableUserEmailNotification | Send result emails after an admin reviews. Required `$true` for "mailbox only". |
| JunkReviewResultMessage / NotJunkReviewResultMessage / PhishingReviewResultMessage | Custom body per verdict |
| NotificationFooterMessage | Footer for all verdicts |
| EnableCustomNotificationSender + NotificationSenderAddress | Custom From address, which must be in Exchange Online. Default sender `submissions@messaging.microsoft.com`. [mdo-usersettings] |
| EnableOrganizationBranding | Use the org logo from the M365 theme |
| DisableQuarantineReportingOption | `$true` stops users reporting from quarantine (default `$false`) |
| ReportChatMessageEnabled / ReportChatMessageToCustomizedAddressEnabled / ReportChatMessageAddresses | Teams reporting; doesn't use the rule |
| EnableCustomizedMsg, DisableUserSubmissionOptions, UserSubmissionOptions, OnlyShowPhishingDisclaimer, NotificationsForSubmissionAirInvestigationsEnabled | "Reserved for internal Microsoft use"; leave alone |

Graph alternative (beta): `GET/PATCH /beta/security/threatSubmission/emailThreatSubmissionPolicies/DefaultReportSubmissionPolicy` [policy], [policy-update]. Fields: `isReportToMicrosoftEnabled`, `isReportToCustomizedEmailAddressEnabled`, `customizedReportRecipientEmailAddress`, `isAskMeEnabledForUsers`, `isAlwaysReportEnabledForUsers`, `isNeverReportEnabledForUsers`, `isCustomizedMessageEnabled`, `isCustomizedMessageEnabledForPhishing`, `isReviewEmailNotificationEnabled`, `isCustomizedNotificationSenderEnabled`, `customizedNotificationSenderEmailAddress`, `isOrganizationBrandingEnabled`, `isReportFromQuarantineEnabled`. It has no third-party, Teams or per-language fields, and Learn doesn't say whether PATCH also creates or updates the rule. **Use EXO for writes.**

### D8. Other places user reports show up

| Source | API? | Notes | Source |
| --- | --- | --- | --- |
| Graph `security/alerts_v2` | Yes (v1.0, `SecurityAlert.Read.All`; roles Security Reader, Global Reader, Security Operator, Security Administrator) | Default alert policies **Email reported by user as malware or phish** (E3/Business Premium/MDO P1 and up; triggers AIR in P2), **...as junk** and **...as not junk** (E1/E3/E5). One alert per report, with no verdict fields. Good as a fallback signal. | [alerts], [alertpol] |
| User-reported messages report, Threat Explorer (P2), AIR results (P2) | Portal | Learn lists them as other views of user reports. Learn shows no API for them. | [mdo-report] |
| Reporting mailbox | Graph mail on that mailbox | Raw copies, with subject prefixes `1|`/`Junk:`, `2|`/`Not junk:`, `3|`/`Phishing:` for third-party tools. Last resort. | [mdo-usersettings] |

### D9. Build summary

| Feature | Use | Gotchas |
| --- | --- | --- |
| Submissions list (incl. user reported) | `GET /beta/security/threatSubmission/emailThreats?$filter=source eq 'user'` (and `'administrator'`), plus urlThreats and fileThreats; send the `Prefer: include-unknown-enum-members` header | Beta only. Check that mailbox-only reports appear. Map `result.detail` to the Learn text. |
| Submit to Microsoft | Quarantine: export .eml, then `emailContentThreatSubmission`. Trace: `emailUrlThreatSubmission` with a Graph message URL. URL: `urlThreats`. Attachment: `fileThreats`. | Asynchronous, so poll `status`. 30-day limit. Throttled. |
| Review + notify | `POST .../emailThreats/{id}/review` | Needs `ThreatSubmission.ReadWrite.All`. Whether the reporter gets an email depends on `isReviewEmailNotificationEnabled` / `EnableUserEmailNotification`: test it. |
| Report destination card | `Get/Set-ReportSubmissionPolicy` and `Get/New/Set/Remove-ReportSubmissionRule` over EXO | The policy or rule may not exist yet. Keep policy and rule addresses in sync. The mailbox must be an EXO mailbox, not a group. Watch the Pre/PostSubmit default mismatch. |
