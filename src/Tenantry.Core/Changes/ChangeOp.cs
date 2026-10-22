using System.Text.Json.Serialization;

namespace Tenantry.Core.Changes;

/// <summary>What a change touches: enough to name it, find it, and judge it against the write guard.</summary>
/// <param name="Kind">"user", "group" or "mailbox".</param>
/// <param name="Address">Sign-in name or email address, when it has one.</param>
public sealed record ChangeTarget(string Kind, string Id, string Name, string? Address);

/// <summary>
/// One change, typed. Each says what it changes (<see cref="Target"/> and <see cref="Key"/>,
/// which together identify the slot it fills, so staging the same thing twice replaces rather
/// than stacks), how it reads to a person (before → after), and whether it needs a second look.
/// Plain records, so they serialise into the change log as they were applied.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(SetUserText), "setUserText")]
[JsonDerivedType(typeof(SetSignInAllowed), "setSignIn")]
[JsonDerivedType(typeof(SetTestModeChange), "setTestMode")]
[JsonDerivedType(typeof(ResetPassword), "resetPassword")]
[JsonDerivedType(typeof(RevokeSessions), "revokeSessions")]
[JsonDerivedType(typeof(SetLicenseAssigned), "setLicense")]
[JsonDerivedType(typeof(SetLicenseApps), "setLicenseApps")]
[JsonDerivedType(typeof(RemoveAppAssignment), "removeAppAssignment")]
[JsonDerivedType(typeof(SetGroupMembership), "setGroupMembership")]
[JsonDerivedType(typeof(SetMailboxAccess), "setMailboxAccess")]
[JsonDerivedType(typeof(SetManager), "setManager")]
[JsonDerivedType(typeof(SetMailAlias), "setMailAlias")]
[JsonDerivedType(typeof(SetPrimaryAddress), "setPrimaryAddress")]
[JsonDerivedType(typeof(SetForwarding), "setForwarding")]
[JsonDerivedType(typeof(SetAutoReply), "setAutoReply")]
[JsonDerivedType(typeof(SetHiddenFromAddressList), "setHiddenFromAddressList")]
[JsonDerivedType(typeof(SetMailboxValue), "setMailboxValue")]
[JsonDerivedType(typeof(SetEmailApp), "setEmailApp")]
[JsonDerivedType(typeof(SetArchive), "setArchive")]
[JsonDerivedType(typeof(EnableAutoExpandingArchive), "enableAutoExpandingArchive")]
[JsonDerivedType(typeof(SetLitigationHold), "setLitigationHold")]
[JsonDerivedType(typeof(SetAllowedSender), "setAllowedSender")]
[JsonDerivedType(typeof(SetInboxRuleEnabled), "setInboxRuleEnabled")]
[JsonDerivedType(typeof(RemoveInboxRule), "removeInboxRule")]
[JsonDerivedType(typeof(SetFolderPermission), "setFolderPermission")]
[JsonDerivedType(typeof(SetMobileDevice), "setMobileDevice")]
[JsonDerivedType(typeof(RestoreDeletedItem), "restoreDeletedItem")]
[JsonDerivedType(typeof(ConvertMailbox), "convertMailbox")]
[JsonDerivedType(typeof(RemoveAuthMethod), "removeAuthMethod")]
[JsonDerivedType(typeof(AddPhoneMethod), "addPhoneMethod")]
[JsonDerivedType(typeof(CreateTemporaryAccessPass), "createTemporaryAccessPass")]
[JsonDerivedType(typeof(RemoveRoleAssignment), "removeRoleAssignment")]
[JsonDerivedType(typeof(RevokeAppConsent), "revokeAppConsent")]
[JsonDerivedType(typeof(SetUserRisk), "setUserRisk")]
[JsonDerivedType(typeof(SetDeviceEnabled), "setDeviceEnabled")]
[JsonDerivedType(typeof(RunDeviceAction), "runDeviceAction")]
[JsonDerivedType(typeof(SecretViewed), "secretViewed")]
public abstract record ChangeOp
{
    [JsonIgnore]
    public abstract ChangeTarget Target { get; }

    /// <summary>Identifies the slot within the target ("department", "signin").</summary>
    [JsonIgnore]
    public abstract string Key { get; }

    /// <summary>What is changing, as a person reads it ("Department").</summary>
    [JsonIgnore]
    public abstract string Label { get; }

    [JsonIgnore]
    public abstract string Before { get; }

    [JsonIgnore]
    public abstract string After { get; }

    /// <summary>
    /// Changes that take something away (blocking sign-in, removing access) ask for a second,
    /// in-place confirmation before they're applied.
    /// </summary>
    [JsonIgnore]
    public virtual bool IsCaution => false;

    /// <summary>
    /// For changes that can't be undone (wiping a phone): what the admin must type before it's
    /// applied, on top of the caution confirmation. Null for everything else.
    /// </summary>
    [JsonIgnore]
    public virtual string? ConfirmPhrase => null;

    /// <summary>
    /// The slot (on the same target) of a change this one must follow: when both are applied
    /// together and that one doesn't go through, this one is skipped. Removing a mailbox's license
    /// depends on converting it to shared first, or the mailbox would be left unlicensed.
    /// </summary>
    [JsonIgnore]
    public string? DependsOn { get; init; }

    /// <summary>True when the change would leave things as they are (staged back to the original).</summary>
    [JsonIgnore]
    public abstract bool IsNoChange { get; }

    /// <summary>Everything the write guard checks: the target, plus anyone a change adds or removes.</summary>
    [JsonIgnore]
    public virtual IReadOnlyList<ChangeTarget> Guarded => [Target];

    /// <summary>The same change, but starting from <paramref name="earlier"/>'s "before" (for restaging).</summary>
    public abstract ChangeOp Since(ChangeOp earlier);

    public string Summary => $"{Label}: {Before} → {After}";

    protected static string Show(string? value) => string.IsNullOrEmpty(value) ? "(empty)" : value;
}

/// <summary>
/// Sets one of a user's text properties (Graph property name in <see cref="Property"/>).
/// <c>businessPhones</c> is a list in Graph; Tenantry edits its first entry.
/// </summary>
public sealed record SetUserText(ChangeTarget User, string Property, string PropertyLabel, string? Old, string? New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => Property;
    public override string Label => PropertyLabel;
    public override string Before => Show(Old);
    public override string After => Show(New);
    public override bool IsNoChange => string.Equals(Old ?? "", New ?? "", StringComparison.Ordinal);

    /// <summary>Changing a sign-in name changes how the person signs in: worth a second look.</summary>
    public override bool IsCaution => Property == "userPrincipalName";

    public override ChangeOp Since(ChangeOp earlier) => earlier is SetUserText e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Gives the user a new temporary password. The password itself is shown once to the admin
/// after applying and is never stored (not in the tray, the change log, or the local copy).
/// </summary>
public sealed record ResetPassword(ChangeTarget User, bool MustChangeAtNextSignIn) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "password";
    public override string Label => "Password";
    public override string Before => "Current password";
    public override string After => MustChangeAtNextSignIn ? "New temporary password (must change at next sign-in)" : "New password";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Gives or takes away one license (assignLicense). Taking one away is a caution change.</summary>
public sealed record SetLicenseAssigned(ChangeTarget User, string SkuId, string SkuName, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "license:" + SkuId;
    public override string Label => SkuName;
    public override string Before => Old ? "Assigned" : "Not assigned";
    public override string After => New ? "Assigned" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetLicenseAssigned e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Turns apps (service plans) on or off inside a license the user has, by the license's list
/// of disabled plans. Turning any app off is a caution change.
/// </summary>
/// <param name="PlanNames">Plan id → name, for reading the change.</param>
public sealed record SetLicenseApps(ChangeTarget User, string SkuId, string SkuName, IReadOnlyList<string> OldDisabled, IReadOnlyList<string> NewDisabled,
    IReadOnlyDictionary<string, string> PlanNames) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "apps:" + SkuId;
    public override string Label => $"{SkuName} apps";
    public override string Before => Describe(OldDisabled);
    public override string After => Describe(NewDisabled);
    public override bool IsCaution => NewDisabled.Except(OldDisabled).Any();
    public override bool IsNoChange => OldDisabled.ToHashSet().SetEquals(NewDisabled);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetLicenseApps e ? this with { OldDisabled = e.OldDisabled } : this;

    string Describe(IReadOnlyList<string> disabled) => disabled.Count == 0
        ? "All apps on"
        : "Off: " + string.Join(", ", disabled.Select(id => PlanNames.GetValueOrDefault(id, id)).Order(StringComparer.CurrentCulture));
}

/// <summary>Removes the user's assignment to an enterprise application.</summary>
public sealed record RemoveAppAssignment(ChangeTarget User, string AssignmentId, string AppName) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "app:" + AssignmentId;
    public override string Label => AppName;
    public override string Before => "Assigned";
    public override string After => "Removed";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Signs the user out of every session and app (revokes refresh tokens).</summary>
public sealed record RevokeSessions(ChangeTarget User) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "sessions";
    public override string Label => "Sessions";
    public override string Before => "Signed in where they are";
    public override string After => "Signed out everywhere";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

public enum GroupRole { Member, Owner }

/// <summary>
/// Adds someone to a group, or removes them, as a member or an owner. Microsoft 365 and security
/// groups change through Graph; distribution lists and mail-enabled security groups through
/// Exchange (<see cref="ExchangeManaged"/>). The write guard checks both the group and the person.
/// </summary>
/// <param name="PersonType">Graph's type for the person ("user", "group", "device", …).</param>
public sealed record SetGroupMembership(ChangeTarget Group, ChangeTarget Person, string PersonType, GroupRole Role, bool ExchangeManaged,
    bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => Group;
    public override string Key => $"{Role}:{Person.Id}";
    public override string Label => Role == GroupRole.Owner ? $"Owner {Person.Name}" : $"Member {Person.Name}";
    public override string Before => Old ? Role.ToString() : $"Not {Article} {Role.ToString().ToLowerInvariant()}";
    public override string After => New ? Role.ToString() : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override IReadOnlyList<ChangeTarget> Guarded => [Group, Person];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetGroupMembership e ? this with { Old = e.Old } : this;

    string Article => Role == GroupRole.Owner ? "an" : "a";
}

public enum MailboxRight { FullAccess, SendAs, SendOnBehalf }

/// <summary>
/// Gives someone a right on a mailbox (usually a shared one), or takes it away: Full access
/// (open it), Send as (send as the mailbox) or Send on behalf. Changed through Exchange.
/// </summary>
public sealed record SetMailboxAccess(ChangeTarget Mailbox, ChangeTarget Person, MailboxRight Right, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => Mailbox;
    public override string Key => $"{Right}:{Person.Id}";
    public override string Label => $"{RightName(Right)}: {Person.Name}";
    public override string Before => Old ? "Allowed" : "Not allowed";
    public override string After => New ? "Allowed" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override IReadOnlyList<ChangeTarget> Guarded => [Mailbox, Person];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetMailboxAccess e ? this with { Old = e.Old } : this;

    public static string RightName(MailboxRight right) => right switch
    {
        MailboxRight.FullAccess => "Full access",
        MailboxRight.SendAs => "Send as",
        _ => "Send on behalf",
    };
}

/// <summary>Sets or clears a user's manager. Only the user changes, so only they are guarded.</summary>
public sealed record SetManager(ChangeTarget User, string? OldId, string? OldName, string? NewId, string? NewName) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "manager";
    public override string Label => "Manager";
    public override string Before => OldId is null ? "(none)" : Show(OldName);
    public override string After => NewId is null ? "(none)" : Show(NewName);
    public override bool IsNoChange => string.Equals(OldId, NewId, StringComparison.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetManager e ? this with { OldId = e.OldId, OldName = e.OldName } : this;
}

// ---- Mail (changed through Exchange; the target is the mailbox's account) -----------------

/// <summary>Adds or removes one of a mailbox's extra email addresses (an alias).</summary>
public sealed record SetMailAlias(ChangeTarget User, string Address, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "alias:" + Address.ToLowerInvariant();
    public override string Label => $"Email address {Address}";
    public override string Before => Old ? "One of their addresses" : "Not one of their addresses";
    public override string After => New ? "Added" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetMailAlias e ? this with { Old = e.Old } : this;
}

/// <summary>Makes another address the primary one (what people see in replies); the old one stays as an alias.</summary>
public sealed record SetPrimaryAddress(ChangeTarget User, string Old, string New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "primary";
    public override string Label => "Primary email address";
    public override string Before => Old;
    public override string After => New;
    public override bool IsCaution => true;
    public override bool IsNoChange => string.Equals(Old, New, StringComparison.OrdinalIgnoreCase);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetPrimaryAddress e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Turns mail forwarding on (to an address, keeping a copy or not) or off. Forwarding outside the
/// organization is a caution change: it's how compromised accounts leak mail.
/// </summary>
/// <param name="NewIsExternal">The new address is outside the tenant's domains.</param>
public sealed record SetForwarding(ChangeTarget User, string? OldTo, bool OldKeepCopy, string? NewTo, bool NewKeepCopy, bool NewIsExternal) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "forwarding";
    public override string Label => "Forwarding";
    public override string Before => Describe(OldTo, OldKeepCopy);
    public override string After => Describe(NewTo, NewKeepCopy) + (NewTo is not null && NewIsExternal ? " (outside the organization)" : "");
    public override bool IsCaution => NewTo is not null && NewIsExternal;
    public override bool IsNoChange => string.Equals(OldTo ?? "", NewTo ?? "", StringComparison.OrdinalIgnoreCase) && (NewTo is null || OldKeepCopy == NewKeepCopy);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetForwarding e ? this with { OldTo = e.OldTo, OldKeepCopy = e.OldKeepCopy } : this;

    static string Describe(string? to, bool keep) => to is null ? "Off" : $"To {to}{(keep ? ", keeping a copy" : ", no copy kept")}";
}

/// <summary>Sets a mailbox's automatic replies: off, on, or between two times, with the messages.</summary>
public sealed record SetAutoReply(ChangeTarget User, Directory.AutoReply Old, Directory.AutoReply New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "autoreply";
    public override string Label => "Automatic replies";
    public override string Before => Old.Describe();
    public override string After => New.Describe() + (New.State != Directory.AutoReplyState.Off && New.Internal != Old.Internal ? " (new message)" : "");
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetAutoReply e ? this with { Old = e.Old } : this;
}

/// <summary>Hides a mailbox from the address list (Outlook's directory), or shows it again.</summary>
public sealed record SetHiddenFromAddressList(ChangeTarget User, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "gal";
    public override string Label => "Address list";
    public override string Before => Old ? "Hidden" : "Shown";
    public override string After => New ? "Hidden" : "Shown";
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetHiddenFromAddressList e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Sets one plain Set-Mailbox setting (a limit, the retention policy, internal-only senders).
/// <see cref="Value"/> is what Exchange is sent ("35 MB", 500, true); the texts are how it reads.
/// </summary>
public sealed record SetMailboxValue(ChangeTarget User, string Parameter, string ParameterLabel, string Old, string New,
    System.Text.Json.Nodes.JsonNode? Value, bool Caution = false) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "mbx:" + Parameter;
    public override string Label => ParameterLabel;
    public override string Before => Old;
    public override string After => New;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => string.Equals(Old, New, StringComparison.OrdinalIgnoreCase);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetMailboxValue e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Switches one way of reaching a mailbox on or off (Set-CASMailbox): Outlook on the web, POP,
/// IMAP, SMTP sending… Switching one off can stop someone's app or device working: a caution change.
/// </summary>
/// <param name="Parameter">The Set-CASMailbox parameter ("PopEnabled"; "SmtpClientAuthenticationDisabled" is inverted).</param>
public sealed record SetEmailApp(ChangeTarget User, string Parameter, string AppName, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "mailapp:" + Parameter;
    public override string Label => AppName;
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetEmailApp e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Turns the online archive on or off. Turning it off is a caution change: the archive's contents
/// can no longer be reached, and after 30 days they're gone.
/// </summary>
public sealed record SetArchive(ChangeTarget User, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "archive";
    public override string Label => "Online archive";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off (its contents are deleted after 30 days)";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetArchive e ? this with { Old = e.Old } : this;
}

/// <summary>Turns on auto-expanding archiving. Microsoft offers no way back: always a caution change.</summary>
public sealed record EnableAutoExpandingArchive(ChangeTarget User) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "autoexpand";
    public override string Label => "Auto-expanding archive";
    public override string Before => "Off";
    public override string After => "On (can't be turned off again)";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// Litigation hold on (keeping every item, even deleted ones, for <see cref="NewDays"/> days or
/// indefinitely, with a note) or off. Both ways are caution changes: holds are legal matters.
/// </summary>
public sealed record SetLitigationHold(ChangeTarget User, bool OldOn, int? OldDays, string? OldNote, bool NewOn, int? NewDays, string? NewNote) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "hold";
    public override string Label => "Litigation hold";
    public override string Before => Describe(OldOn, OldDays, OldNote);
    public override string After => Describe(NewOn, NewDays, NewNote);
    public override bool IsCaution => true;
    public override bool IsNoChange => OldOn == NewOn && (!NewOn || (OldDays == NewDays && (OldNote ?? "") == (NewNote ?? "")));
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetLitigationHold e ? this with { OldOn = e.OldOn, OldDays = e.OldDays, OldNote = e.OldNote } : this;

    static string Describe(bool on, int? days, string? note) => !on ? "Off"
        : $"On, keeping items {(days is { } d ? $"{d:N0} days" : "indefinitely")}{(string.IsNullOrWhiteSpace(note) ? "" : $" ({note})")}";
}

/// <summary>
/// Adds someone to (or removes them from) the people and groups a mailbox accepts mail from. While
/// the list has anyone on it, nobody else can send to the mailbox.
/// </summary>
public sealed record SetAllowedSender(ChangeTarget User, ChangeTarget Sender, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "accept:" + Sender.Id.ToLowerInvariant();
    public override string Label => $"Allowed sender {Sender.Name}";
    public override string Before => Old ? "Allowed" : "Not on the list";
    public override string After => New ? "Allowed" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetAllowedSender e ? this with { Old = e.Old } : this;
}

/// <summary>Blocks or allows a user's sign-in (Graph accountEnabled).</summary>
public sealed record SetSignInAllowed(ChangeTarget User, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "signin";
    public override string Label => "Sign-in";
    public override string Before => Old ? "Allowed" : "Blocked";
    public override string After => New ? "Allowed" : "Blocked";
    public override bool IsCaution => !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetSignInAllowed e ? this with { Old = e.Old } : this;
}

// ---- Mailbox contents (4b-3) ----------------------------------------------------------------

/// <summary>Turns an inbox rule on or off (Enable-InboxRule / Disable-InboxRule).</summary>
/// <param name="RuleIdentity">Exchange's "mailbox\ruleId".</param>
public sealed record SetInboxRuleEnabled(ChangeTarget User, string RuleIdentity, string RuleName, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "rule:" + RuleIdentity;
    public override string Label => $"Inbox rule \"{RuleName}\"";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetInboxRuleEnabled e ? this with { Old = e.Old } : this;
}

/// <summary>Deletes an inbox rule (Remove-InboxRule). It can't be brought back: a caution change.</summary>
/// <param name="Was">How the rule read before ("On: forwards to …"), for the review.</param>
public sealed record RemoveInboxRule(ChangeTarget User, string RuleIdentity, string RuleName, string Was) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "rule:" + RuleIdentity;
    public override string Label => $"Inbox rule \"{RuleName}\"";
    public override string Before => Was;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// Someone's access to a folder of this mailbox (its calendar): added, changed or removed
/// (Add-, Set- or Remove-MailboxFolderPermission). Taking access away is a caution change.
/// </summary>
/// <param name="Person">Who, as Exchange takes them: an address, or "Default" / "Anonymous".</param>
/// <param name="Old">Their role before (null: no entry).</param>
/// <param name="New">Their role after (null: entry removed).</param>
public sealed record SetFolderPermission(ChangeTarget User, string FolderIdentity, string FolderName, string Person, string PersonName, string? Old, string? New)
    : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => $"folder:{FolderName.ToLowerInvariant()}:{Person.ToLowerInvariant()}";
    public override string Label => $"{FolderName} access for {PersonName}";
    public override string Before => Old is null ? "No access" : Directory.FolderRoles.NameOf(Old);
    public override string After => New is null ? "Removed" : Directory.FolderRoles.NameOf(New);
    public override bool IsCaution => New is null || string.Equals(New, "None", StringComparison.OrdinalIgnoreCase);
    public override bool IsNoChange => string.Equals(Old, New, StringComparison.OrdinalIgnoreCase);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetFolderPermission e ? this with { Old = e.Old } : this;
}

public enum DeviceAction
{
    /// <summary>Erases the whole device back to factory settings.</summary>
    Wipe,

    /// <summary>Removes only their work mail and data from the device.</summary>
    WipeAccountOnly,

    Block,
    Unblock,

    /// <summary>Forgets the device's partnership; it can set up sync again unless blocked.</summary>
    Remove,
}

/// <summary>
/// Something done to one of their phones or tablets. A wipe can't be undone, so besides the
/// caution confirmation the admin types the device's name (<see cref="ChangeOp.ConfirmPhrase"/>).
/// </summary>
/// <param name="DeviceIdentity">Exchange's identity for the partnership (wipe, remove).</param>
/// <param name="DeviceId">The device's ID (block, unblock).</param>
public sealed record SetMobileDevice(ChangeTarget User, string DeviceIdentity, string DeviceId, string DeviceName, DeviceAction Action) : ChangeOp
{
    public override ChangeTarget Target => User;

    // Wiping or removing and blocking are separate slots: a lost phone is often blocked and wiped.
    public override string Key => (Action is DeviceAction.Block or DeviceAction.Unblock ? "deviceaccess:" : "device:") + DeviceIdentity;

    public override string Label => $"Device {DeviceName}";

    public override string Before => Action switch
    {
        DeviceAction.Unblock => "Blocked",
        DeviceAction.Block => "Allowed",
        _ => "Syncing their mail",
    };

    public override string After => Action switch
    {
        DeviceAction.Wipe => "Wiped: erased back to factory settings",
        DeviceAction.WipeAccountOnly => "Their work mail and data removed from it",
        DeviceAction.Block => "Blocked from syncing",
        DeviceAction.Unblock => "Allowed to sync",
        _ => "Removed (it can set up sync again unless blocked)",
    };

    public override bool IsCaution => Action != DeviceAction.Unblock;
    public override string? ConfirmPhrase => Action is DeviceAction.Wipe or DeviceAction.WipeAccountOnly ? DeviceName : null;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Puts a deleted item back where it was deleted from (Restore-RecoverableItems).</summary>
public sealed record RestoreDeletedItem(ChangeTarget User, string EntryId, string Subject) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "restore:" + EntryId;
    public override string Label => $"Deleted item \"{Subject}\"";
    public override string Before => "Deleted";
    public override string After => "Restored to its folder";
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// Converts a user mailbox to a shared one, or back (Set-Mailbox -Type). Converting to shared is
/// a caution change; removing the licenses it no longer needs is staged as separate changes after it.
/// </summary>
public sealed record ConvertMailbox(ChangeTarget User, bool OldShared, bool NewShared) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "mailboxtype";
    public override string Label => "Mailbox type";
    public override string Before => OldShared ? "Shared mailbox" : "User mailbox";
    public override string After => NewShared ? "Shared mailbox" : "User mailbox";
    public override bool IsCaution => NewShared;
    public override bool IsNoChange => OldShared == NewShared;
    public override ChangeOp Since(ChangeOp earlier) => earlier is ConvertMailbox e ? this with { OldShared = e.OldShared } : this;
}

// ---- Security & sign-in (4c-1) --------------------------------------------------------------

/// <summary>
/// Removes one sign-in method (a phone, the Authenticator app, a security key…). They may need to
/// register again before they can sign in where MFA is required: a caution change.
/// </summary>
/// <param name="Segment">The Graph collection it lives in ("phoneMethods").</param>
public sealed record RemoveAuthMethod(ChangeTarget User, string MethodId, string Segment, string MethodName) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "authmethod:" + MethodId;
    public override string Label => $"Sign-in method {MethodName}";
    public override string Before => "Registered";
    public override string After => "Removed";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Adds a phone number they can use for MFA and password reset.</summary>
/// <param name="PhoneType">"mobile", "alternateMobile" or "office".</param>
public sealed record AddPhoneMethod(ChangeTarget User, string PhoneType, string Number) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "phone:" + PhoneType;
    public override string Label => PhoneType switch { "alternateMobile" => "Alternate phone", "office" => "Office phone", _ => "Mobile phone" };
    public override string Before => "Not set";
    public override string After => Number;
    public override bool IsNoChange => Number.Length == 0;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// A Temporary Access Pass: a time-limited code they sign in with to set up MFA (a new phone, a
/// lost one). Shown to the admin once after applying, never stored.
/// </summary>
public sealed record CreateTemporaryAccessPass(ChangeTarget User, int Minutes, bool OneTime) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "tap";
    public override string Label => "Temporary Access Pass";
    public override string Before => "None";
    public override string After => $"Valid {(Minutes >= 60 ? $"{Minutes / 60} hour{(Minutes >= 120 ? "s" : "")}" : $"{Minutes} minutes")}{(OneTime ? ", one use" : "")} (shown to you once)";
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Takes away a directory role they hold directly.</summary>
public sealed record RemoveRoleAssignment(ChangeTarget User, string AssignmentId, string RoleName) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "role:" + AssignmentId;
    public override string Label => $"Role {RoleName}";
    public override string Before => "Assigned";
    public override string After => "Removed";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Takes back the access they gave an app to their data (a delegated consent).</summary>
public sealed record RevokeAppConsent(ChangeTarget User, string GrantId, string AppName) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "consent:" + GrantId;
    public override string Label => $"App access {AppName}";
    public override string Before => "Allowed";
    public override string After => "Revoked";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// Identity Protection: confirm the account was compromised (raises its risk to high, so risk
/// policies act on it: a caution change) or dismiss the risk.
/// </summary>
public sealed record SetUserRisk(ChangeTarget User, bool Compromised) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "risk";
    public override string Label => "Risk";
    public override string Before => "At risk";
    public override string After => Compromised ? "Confirmed compromised (risk high)" : "Dismissed";
    public override bool IsCaution => Compromised;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

// ---- Devices (4c-2) -------------------------------------------------------------------------

/// <summary>Enables or disables a device in Entra: a disabled device can't be used to sign in. Disabling is a caution change.</summary>
/// <param name="Device">The Entra device object (Kind "device"), named as the device is.</param>
public sealed record SetDeviceEnabled(ChangeTarget Device, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => Device;
    public override string Key => "enabled";
    public override string Label => "Device sign-in";
    public override string Before => Old ? "Enabled" : "Disabled";
    public override string After => New ? "Enabled" : "Disabled";
    public override bool IsCaution => !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetDeviceEnabled e ? this with { Old = e.Old } : this;
}

public enum DeviceCommand
{
    Sync,
    Restart,
    Lock,
    RotateLocalAdminPassword,

    /// <summary>Removes work data and apps, and takes it out of management; their own things stay.</summary>
    Retire,

    /// <summary>Erases the device back to factory settings.</summary>
    Wipe,
}

/// <summary>
/// An Intune remote action on a managed device. All but Sync interrupt or remove something:
/// caution changes. A wipe can't be undone, so the admin also types the device's name.
/// </summary>
/// <param name="Device">The Intune managed device (Kind "manageddevice").</param>
public sealed record RunDeviceAction(ChangeTarget Device, DeviceCommand Command) : ChangeOp
{
    public override ChangeTarget Target => Device;
    public override string Key => Command is DeviceCommand.Retire or DeviceCommand.Wipe ? "remove" : "action:" + Command;
    public override string Label => Command switch
    {
        DeviceCommand.Sync => "Sync with Intune",
        DeviceCommand.Restart => "Restart",
        DeviceCommand.Lock => "Lock",
        DeviceCommand.RotateLocalAdminPassword => "Local admin password",
        DeviceCommand.Retire => "Retire",
        _ => "Wipe",
    };
    public override string Before => Command switch
    {
        DeviceCommand.RotateLocalAdminPassword => "Current password",
        DeviceCommand.Retire or DeviceCommand.Wipe => "Managed",
        _ => "",
    };
    public override string After => Command switch
    {
        DeviceCommand.Sync => "Checks in now",
        DeviceCommand.Restart => "Restarts now",
        DeviceCommand.Lock => "Locks now",
        DeviceCommand.RotateLocalAdminPassword => "A new password",
        DeviceCommand.Retire => "Retired: work data removed, no longer managed",
        _ => "Wiped: erased back to factory settings",
    };
    public override bool IsCaution => Command != DeviceCommand.Sync;
    public override string? ConfirmPhrase => Command == DeviceCommand.Wipe ? Device.Name : null;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// Not a change: a record that someone revealed a secret (a BitLocker key, a local admin
/// password), kept in Tenantry's change log beside Microsoft's own audit.
/// </summary>
public sealed record SecretViewed(ChangeTarget Device, string What) : ChangeOp
{
    public override ChangeTarget Target => Device;
    public override string Key => "viewed:" + What;
    public override string Label => What;
    public override string Before => "Hidden";
    public override string After => "Shown to you";
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}
