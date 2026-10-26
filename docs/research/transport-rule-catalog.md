# Exchange Online mail flow rule catalog

Companion to `transport-rule-catalog.json`. Sources read 2026-10-26 (list at the end).

## What the JSON holds

Top-level keys: `meta`, `valueTypes`, `conditions`, `actions`, `properties`.

- **conditions** (56): `group`, `eac`, `eacPath`, `parameter`, `exceptParameter`, `valueType`, `enumValues`, `extraParameters`, `availability`, `eacVisible`, `senderAddressLocationApplies`, `notes`.
- **actions** (32): `group`, `eac`, `eacPath`, `parameter`, `valueType`, `enumValues`, `extraParameters`, `fixedParameters`, `availability`, `eacVisible`, `terminal`, `notes`.
- **properties** (16): `name`, `eac`, `parameter`, `valueType`, `enumValues`, `default`, `cmdlets`, `availability`, `notes`.

Every condition has an exception with the same settings. The exception parameter is the condition parameter with `ExceptIf` in front. `extraParameters` entries carry their own `exceptParameter`.

`enumValues` is either a list of strings or a list of `{value, eac}` objects. `value` is what PowerShell takes and `eac` is the label the admin center shows.

`availability`: `exo`, `legacy` (works but superseded), `reserved` (internal Microsoft use), `dlpOnPremOnly`, `onPremOnly`, `edgeOnly`, `deprecated`. A full Exchange Online editor should offer `exo` and `legacy` entries only.

## Counts

| Conditions group | Count |
| --- | --- |
| The sender | 10 (1 reserved) |
| The recipient | 9 (1 reserved) |
| The subject or body | 4 |
| Any attachment | 10 |
| Any recipient | 2 |
| The message | 8 |
| The sender and the recipient | 4 |
| Message properties | 4 |
| A message header | 2 |
| Cmdlet only | 3 (SCLOver works in EXO; 2 DLP on-prem only) |
| **Total** | **56** (52 usable in Exchange Online, including SCLOver) |

| Actions group | Count |
| --- | --- |
| Forward the message for approval | 2 |
| Redirect the message to | 3 |
| Block the message | 3 |
| Add recipients | 4 |
| Apply a disclaimer to the message | 2 (Append, Prepend) |
| Modify the message properties | 4 |
| Modify the message security | 6 (2 legacy OME, 1 cmdlet-only) |
| Prepend the subject | 1 |
| Notify | 2 |
| Not available in Exchange Online | 5 |
| **Total** | **32** (27 usable in Exchange Online) |

Properties: 16. StopRuleProcessing and SetAuditSeverity are actions in the cmdlet model, but the EAC shows them as rule settings, so they are listed under properties.

## valueType vocabulary

| valueType | Meaning |
| --- | --- |
| none | Presence-only predicate or action. Send `$true`. |
| bool | User-chosen `$true`/`$false`. |
| addresses | One or more recipients (mailbox, mail user, contact, Microsoft 365 group). |
| address | Exactly one recipient (incident report target). |
| groups | One or more groups (DG, mail-enabled security, Microsoft 365). |
| betweenGroups | Two group lists set together (BetweenMemberOf1 + 2). |
| managerOf | Sender/Recipient enum plus manager addresses. |
| words | Word[]: whole word, case-insensitive, no wildcards. `*` and `@` are literal. |
| patterns | Pattern[]: case-insensitive regex. |
| domains | SMTP domains. Each also matches its subdomains. |
| scope | InOrganization / NotInOrganization. |
| adAttributeWords | `"Attribute:w1,w2"` strings. |
| adAttributePatterns | `"Attribute:re1,re2"` strings. |
| adComparison | ADAttribute plus Equal/NotEqual. |
| managerRelationship | Manager / DirectReport. |
| ipRanges | IPv4 single address, `a-b` range, or CIDR. |
| size | ByteQuantifiedSize, such as `20 MB` (B, KB, MB, GB, TB). |
| scl | -1 or 0-9. |
| importance | Low / Normal / High. |
| messageType | OOF, AutoForward, Encrypted, Calendaring, PermissionControlled, Voicemail, Signed, ApprovalRequest, ReadReceipt. |
| classification | Message classification identity. |
| characterSets | Character set names. |
| headerWords / headerPatterns | Header name plus words or regex. |
| documentPropertyWords | `"PropertyName:w1,w2"` over Office document properties. |
| sensitiveInfoTypes | DLP hashtables. Not functional in Exchange Online. |
| supervisionList | Live@edu Allow/Block. Reserved. |
| string, html, headerName, headerNameValue | Text inputs. `html` accepts tags and `%%tokens%%`. |
| dsnCode | 5.7.1 or 5.7.900-5.7.999. |
| smtpStatusCode | 400-500 (Edge only). |
| enum / enumMulti | One value, or several values, from enumValues. |
| connector, rmsTemplate, omeTemplate | Pickers backed by Get-OutboundConnector, Get-RMSTemplate, Get-OMEConfiguration. |
| dateTime, int | Dates and integers. |

## How rules combine

- Conditions are ANDed. Values inside a single condition are ORed. Exceptions are ORed: any matching exception cancels the rule. Actions are ANDed.
- To get OR across different conditions, create two rules.
- A rule with no conditions applies to every message. The EAC calls this "Apply to all messages", and it can't be combined with other conditions.
- Paired parameters must be sent together: HeaderContainsMessageHeader + HeaderContainsWords, HeaderMatchesMessageHeader + HeaderMatchesPatterns, BetweenMemberOf1 + BetweenMemberOf2, ManagerForEvaluatedUser + ManagerAddresses, SetHeaderName + SetHeaderValue, ADComparisonAttribute (+ optional ADComparisonOperator), RemoveRMSAttachmentEncryption (needs RemoveOMEv2).
- Some actions are terminal or exclusive: Reject, Delete, Quarantine (which stops rule evaluation) and Redirect. One rule can't both reject and redirect.
- The Recipient-group predicates (SentTo and others) act only on the matched recipients. The "Any recipient" predicates and the To/Cc box predicates act on **all** recipients of the message fork.
- Distribution groups are expanded before rules run, so SentTo never matches a DG. Use AnyOfToHeader / AnyOfToCcHeader or SentToMemberOf instead.
- Group membership is cached for about 3 hours with no SLA. Dynamic DG membership can lag up to 24 hours plus 3.
- New or changed rules take up to 30 minutes to apply. There is no rule history, so a change can't be rolled back.
- NDRs, journal reports and arbitration mailbox traffic are never evaluated.

## Limits (Exchange Online)

- 300 rules per org. Each rule is at most 8 KB.
- **20 KB total** across all ContainsWords and MatchesPatterns values in all rules. This budget is shared, so the editor should show the running total.
- At most 100 recipients added to a message by all rules. Forwardee (redirect) limit is 10. A message is redirected by rules at most once. At most 10 moderators.
- Name: 64 characters. Comments: 1024. Reject text: 1024 (on-premises 256). Disclaimer: 5000. GenerateNotification: 5120. SubjectContainsWords and SubjectOrBodyContainsWords: 128 characters.
- Attachment content scanning: see the contradictions below.
- Regex: case-insensitive. Documented constructs are `\s \S \d \D \w \W * | ( ) \ ^ $`. UseLegacyRegex switches to Exchange 2010 syntax. Microsoft support won't help debug custom regex.
- Strings may not have trailing spaces. Header matching runs on the MIME-decoded value, never on raw Base64.

## Not usable in Exchange Online

- **Conditions:** MessageContainsDataClassifications and HasSenderOverride work only in on-premises DLP; use Purview DLP instead. SenderInRecipientList and RecipientInSenderList are reserved. SentToScope values ExternalPartner and ExternalNonPartner are on-premises only.
- **Actions:** NotifySender is on-premises DLP only. SmtpRejectMessage*, Disconnect and LogEventText are Edge only. IncidentReportOriginalMail is deprecated. ApplyOME and RemoveOME are legacy OME.
- **Properties:** DlpPolicy is on-premises only. RuleSubType=Dlp is on-premises only.

## Ambiguities and contradictions found

1. **Attachment scan size.** The conditions page and the cmdlet say AttachmentMatchesPatterns scans the first 150 KB. The limits page prose says 2 MB of extracted text, but its table says 1 MB. These are three different numbers, so the UI should not promise a specific limit.
2. **ManagerAddress vs ManagerAddresses.** The conceptual page says `ManagerAddress`, but the cmdlet parameter is `ManagerAddresses`. The catalog uses the cmdlet name.
3. **SenderIPRanges vs SenderIpRanges.** The spelling differs between pages. Both work because PowerShell is case-insensitive.
4. **NotifySender and "Test with Policy Tips".** The EXO test page tells admins to add "Notify the sender with a Policy Tip". The cmdlet says NotifySender works only on-premises, and the EXO actions page doesn't list it. Mode=AuditAndNotify is still valid, but Policy Tips in Microsoft 365 come from Purview DLP.
5. **SCLOver.** The cmdlet lists it for Exchange Online, but the EXO conditions page and EAC leave it out. It is marked "Cmdlet only" and flagged as unreliable, since SCL isn't the verdict in Exchange Online.
6. **SetAuditSeverity.** The cmdlet describes it as DLP severity, while the EXO overview treats it as a general rule "Severity". It is also required for a rule to show in the transport rule report.
7. **Character set values.** The doc lists the EAC labels `Language/charset` and has an `iso-022-jp` typo. The PowerShell value format isn't stated; the catalog assumes the charset name after the slash.
8. **EAC typos:** "is external/external" (recipient scope), "The message > contains a member of this group" (Cc member), and "Cco" for Bcc in AddManagerAsRecipientType. The test page also labels SentToMemberOf as "The sender > is a member of this group".
9. **UseLegacyRegex and Enabled on Set-TransportRule.** Enabled is New-only (use Enable/Disable-TransportRule). UseLegacyRegex wasn't seen in the Set syntax summary that was read, so verify it before exposing it on edit.
10. **Disclaimer fallback.** The actions page gives Wrap as the default. The cmdlet advises against Wrap for inbound external mail in Microsoft 365 and recommends Reject.

## Sources (read 2026-10-26)

- Conditions and exceptions (updated 2026-09-01): https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/conditions-and-exceptions
- Mail flow rule actions (updated 2026-09-01): https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/mail-flow-rule-actions
- Mail flow rules overview and properties (updated 2026-08-03): https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/mail-flow-rules
- Test a mail flow rule (updated 2026-08-03): https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/test-mail-flow-rules
- Regular expressions in mail flow rules (updated 2025-11-14): https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/regular-expressions-usage-transport-rules
- New-TransportRule (updated 2026-08-12): https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/new-transportrule
- Set-TransportRule (updated 2026-08-12): https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/set-transportrule
- Exchange Online limits (updated 2026-10-19): https://learn.microsoft.com/en-us/office365/servicedescriptions/exchange-online-service-description/exchange-online-limits
