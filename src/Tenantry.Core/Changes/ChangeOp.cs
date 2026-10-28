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
[JsonDerivedType(typeof(DeleteDevice), "deleteDevice")]
[JsonDerivedType(typeof(RenameDevice), "renameDevice")]
[JsonDerivedType(typeof(SetPrimaryUser), "setPrimaryUser")]
[JsonDerivedType(typeof(SetDeviceOwnership), "setDeviceOwnership")]
[JsonDerivedType(typeof(SetLostMode), "setLostMode")]
[JsonDerivedType(typeof(SetAutopilotGroupTag), "setAutopilotGroupTag")]
[JsonDerivedType(typeof(DeleteAutopilotDevice), "deleteAutopilotDevice")]
[JsonDerivedType(typeof(SecretViewed), "secretViewed")]
[JsonDerivedType(typeof(SetOneDriveAccess), "setOneDriveAccess")]
[JsonDerivedType(typeof(ReleaseQuarantined), "releaseQuarantined")]
[JsonDerivedType(typeof(DeleteQuarantined), "deleteQuarantined")]
[JsonDerivedType(typeof(SetSenderListed), "setSenderListed")]
[JsonDerivedType(typeof(AddListEntry), "addListEntry")]
[JsonDerivedType(typeof(RemoveListEntry), "removeListEntry")]
[JsonDerivedType(typeof(UpdateListEntry), "updateListEntry")]
[JsonDerivedType(typeof(SetRuleEnabled), "setRuleEnabled")]
[JsonDerivedType(typeof(SetRuleMode), "setRuleMode")]
[JsonDerivedType(typeof(SetRulePriority), "setRulePriority")]
[JsonDerivedType(typeof(RemoveRule), "removeRule")]
[JsonDerivedType(typeof(CreateRule), "createRule")]
[JsonDerivedType(typeof(UpdateRule), "updateRule")]
[JsonDerivedType(typeof(CreateUser), "createUser")]
[JsonDerivedType(typeof(CreateGroup), "createGroup")]
[JsonDerivedType(typeof(SetSiteAdmin), "setSiteAdmin")]
[JsonDerivedType(typeof(SetSiteGroupMember), "setSiteGroupMember")]
[JsonDerivedType(typeof(SetSiteSetting), "setSiteSetting")]
[JsonDerivedType(typeof(SetSiteArchived), "setSiteArchived")]
[JsonDerivedType(typeof(DeleteSite), "deleteSite")]
[JsonDerivedType(typeof(RestoreSite), "restoreSite")]
[JsonDerivedType(typeof(CreateSite), "createSite")]
[JsonDerivedType(typeof(AddRoleAssignment), "addRoleAssignment")]
[JsonDerivedType(typeof(CreateCustomRole), "createCustomRole")]
[JsonDerivedType(typeof(UpdateCustomRole), "updateCustomRole")]
[JsonDerivedType(typeof(DeleteCustomRole), "deleteCustomRole")]
[JsonDerivedType(typeof(SetRolePolicyRule), "setRolePolicyRule")]
[JsonDerivedType(typeof(ExtendRoleAssignment), "extendRoleAssignment")]
[JsonDerivedType(typeof(ActivateRole), "activateRole")]
[JsonDerivedType(typeof(ReviewRoleRequest), "reviewRoleRequest")]
[JsonDerivedType(typeof(SetCaPolicyState), "setCaPolicyState")]
[JsonDerivedType(typeof(DeleteCaPolicy), "deleteCaPolicy")]
[JsonDerivedType(typeof(CreateCaPolicy), "createCaPolicy")]
[JsonDerivedType(typeof(UpdateCaPolicy), "updateCaPolicy")]
[JsonDerivedType(typeof(CreateNamedLocation), "createNamedLocation")]
[JsonDerivedType(typeof(UpdateNamedLocation), "updateNamedLocation")]
[JsonDerivedType(typeof(DeleteNamedLocation), "deleteNamedLocation")]
[JsonDerivedType(typeof(RemoveAppCredential), "removeAppCredential")]
[JsonDerivedType(typeof(AddAppSecret), "addAppSecret")]
[JsonDerivedType(typeof(SetAppOwner), "setAppOwner")]
[JsonDerivedType(typeof(SetEnterpriseAppSetting), "setEnterpriseAppSetting")]
[JsonDerivedType(typeof(AddAppUserAssignment), "addAppUserAssignment")]
[JsonDerivedType(typeof(RemoveAppUserAssignment), "removeAppUserAssignment")]
[JsonDerivedType(typeof(RevokeAppGrant), "revokeAppGrant")]
[JsonDerivedType(typeof(GrantAdminConsent), "grantAdminConsent")]
[JsonDerivedType(typeof(DeleteApp), "deleteApp")]
[JsonDerivedType(typeof(RestoreApp), "restoreApp")]
[JsonDerivedType(typeof(PurgeApp), "purgeApp")]
[JsonDerivedType(typeof(CreateAppRegistration), "createAppRegistration")]
[JsonDerivedType(typeof(SetAuthMethodConfig), "setAuthMethodConfig")]
[JsonDerivedType(typeof(SetSecurityDefaults), "setSecurityDefaults")]
[JsonDerivedType(typeof(SetAppRisk), "setAppRisk")]
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
/// <param name="Chosen">
/// A password the admin typed, or null to generate one (shown once after applying). A typed one
/// is kept in memory only: never serialized into the change log, never in a summary or ToString.
/// </param>
public sealed record ResetPassword(ChangeTarget User, bool MustChangeAtNextSignIn, [property: JsonIgnore] string? Chosen = null) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "password";
    public override string Label => "Password";
    public override string Before => "Current Password";
    public override string After => (Chosen is null ? "A new generated password" : "A password you chose")
        + (MustChangeAtNextSignIn ? " (they change it at next sign-in)" : "");
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;

    /// <summary>Never prints the password (a record's ToString would list every property).</summary>
    public override string ToString() => Summary;
}

/// <summary>A license chosen for a new user.</summary>
public sealed record NewUserLicense(string SkuId, string Name);

/// <summary>A group a new user joins: through Graph, or Exchange for distribution lists and mail-enabled security groups.</summary>
public sealed record NewUserGroup(ChangeTarget Group, bool ExchangeManaged);

/// <summary>
/// Creates a user, then gives them their licenses, manager and groups in the same step (the rest
/// needs the account to exist first). The password is generated in the tenant's style when applied
/// and shown once, or one the admin typed (kept in memory only, never logged). The write guard
/// checks the new sign-in name and every group they join.
/// </summary>
/// <param name="User">Kind "user": Id and Address are the new sign-in name (the real ID comes when it's made).</param>
/// <param name="Details">Optional Graph text properties (department, jobTitle, officeLocation, mobilePhone, employeeId, companyName).</param>
public sealed record CreateUser(
    ChangeTarget User,
    string GivenName,
    string Surname,
    string MailNickname,
    string UsageLocation,
    bool SignInAllowed,
    bool MustChangePassword,
    IReadOnlyList<NewUserLicense> Licenses,
    IReadOnlyList<NewUserGroup> Groups,
    string? ManagerId,
    string? ManagerName,
    IReadOnlyDictionary<string, string> Details,
    [property: JsonIgnore] string? Chosen = null) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "create";
    public override string Label => $"New User {User.Name}";
    public override string Before => "None";

    public override string After => string.Join(" · ", new[]
    {
        User.Address,
        Licenses.Count == 0 ? "No license" : string.Join(", ", Licenses.Select(l => l.Name)),
        Groups.Count switch { 0 => null, 1 => $"In {Groups[0].Group.Name}", var n => $"In {n} groups" },
        ManagerName is { } m ? $"Reports to {m}" : null,
        SignInAllowed ? null : "Sign-in blocked",
    }.OfType<string>());

    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
    public override IReadOnlyList<ChangeTarget> Guarded => [User, .. Groups.Select(g => g.Group)];

    /// <summary>Never prints a typed password (a record's ToString would list every property).</summary>
    public override string ToString() => Summary;
}

/// <summary>
/// Creates a group, distribution list or shared mailbox with its first owners and members (for a
/// shared mailbox, the people given Full access and Send as). Microsoft 365 and security groups are
/// made through Graph; distribution lists, mail-enabled security groups and shared mailboxes through
/// Exchange. The write guard checks the new group's name or address and everyone added.
/// </summary>
/// <param name="Group">Kind "group": Id is the alias until it's made; Address the email (none for a security group).</param>
/// <param name="MembershipRule">For a dynamic security group: who belongs (Entra's rule syntax).</param>
/// <param name="OutsideSenders">For a distribution list: accept mail from outside the organization.</param>
public sealed record CreateGroup(
    ChangeTarget Group,
    Directory.GroupKind Kind,
    string MailNickname,
    string? Description,
    string? MembershipRule,
    bool IsPrivate,
    bool OutsideSenders,
    IReadOnlyList<ChangeTarget> Owners,
    IReadOnlyList<ChangeTarget> Members) : ChangeOp
{
    public override ChangeTarget Target => Group;
    public override string Key => "create";
    public override string Label => $"New {Directory.GroupSummary.KindName(Kind)} {Group.Name}";
    public override string Before => "None";

    public override string After => string.Join(" · ", new[]
    {
        Group.Address,
        MembershipRule is not null ? "Members by rule" : Members.Count switch { 0 => null, 1 => $"{Members[0].Name}", var n => $"{n} {(Kind == Directory.GroupKind.SharedMailbox ? "people with access" : "members")}" },
        Owners.Count switch { 0 => null, 1 => $"Owner {Owners[0].Name}", var n => $"{n} owners" },
    }.OfType<string>());

    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
    public override IReadOnlyList<ChangeTarget> Guarded => [Group, .. Owners, .. Members];
}

/// <summary>Gives or takes away one license (assignLicense). Taking one away is a caution change.</summary>
public sealed record SetLicenseAssigned(ChangeTarget User, string SkuId, string SkuName, bool Old, bool New) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "license:" + SkuId;
    public override string Label => SkuName;
    public override string Before => Old ? "Assigned" : "Not Assigned";
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
    public override string Label => $"{SkuName} Apps";
    public override string Before => Describe(OldDisabled);
    public override string After => Describe(NewDisabled);
    public override bool IsCaution => NewDisabled.Except(OldDisabled).Any();
    public override bool IsNoChange => OldDisabled.ToHashSet().SetEquals(NewDisabled);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetLicenseApps e ? this with { OldDisabled = e.OldDisabled } : this;

    string Describe(IReadOnlyList<string> disabled) => disabled.Count == 0
        ? "All Apps On"
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
    public override string Before => Old ? Role.ToString() : $"Not {Article} {Role}";
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
    public override string Label => $"{RightLabel(Right)}: {Person.Name}";
    public override string Before => Old ? "Allowed" : "Not Allowed";
    public override string After => New ? "Allowed" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override IReadOnlyList<ChangeTarget> Guarded => [Mailbox, Person];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetMailboxAccess e ? this with { Old = e.Old } : this;

    /// <summary>
    /// The right as the local copy stores it ("Full access, Send as"); also what <c>GroupAccess.RightsOf</c>
    /// parses, so it stays as it is. Labels on screen use <see cref="RightLabel"/>.
    /// </summary>
    public static string RightName(MailboxRight right) => right switch
    {
        MailboxRight.FullAccess => "Full access",
        MailboxRight.SendAs => "Send as",
        _ => "Send on behalf",
    };

    /// <summary>The right as a label on screen (Title Case).</summary>
    public static string RightLabel(MailboxRight right) => right switch
    {
        MailboxRight.FullAccess => "Full Access",
        MailboxRight.SendAs => "Send As",
        _ => "Send on Behalf",
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
    public override string Label => $"Email Address {Address}";
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
    public override string Label => "Primary Email Address";
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
    public override string Label => "Automatic Replies";
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
    public override string Label => "Address List";
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
    public override string Label => "Online Archive";
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
    public override string Label => "Auto-Expanding Archive";
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
    public override string Label => "Litigation Hold";
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
    public override string Label => $"Allowed Sender {Sender.Name}";
    public override string Before => Old ? "Allowed" : "Not on the List";
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
    public override string Label => "Sign-In";
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
    public override string Label => $"Inbox Rule \"{RuleName}\"";
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
    public override string Label => $"Inbox Rule \"{RuleName}\"";
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
    public override string Label => $"{FolderName} Access for {PersonName}";
    public override string Before => Old is null ? "No Access" : Directory.FolderRoles.NameOf(Old);
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
    public override string Label => $"Deleted Item \"{Subject}\"";
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
    public override string Label => "Mailbox Type";
    public override string Before => OldShared ? "Shared Mailbox" : "User Mailbox";
    public override string After => NewShared ? "Shared Mailbox" : "User Mailbox";
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
    public override string Label => $"Sign-In Method {MethodName}";
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
    public override string Label => PhoneType switch { "alternateMobile" => "Alternate Phone", "office" => "Office Phone", _ => "Mobile Phone" };
    public override string Before => "Not Set";
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

/// <summary>
/// Takes away a directory role someone (or a group, or an app) holds directly. One made through PIM
/// (<see cref="ViaPim"/>) is removed through PIM, by its role and scope.
/// </summary>
/// <param name="User">Who holds it (kind "user", "group" or "app").</param>
public sealed record RemoveRoleAssignment(ChangeTarget User, string AssignmentId, string RoleName, string? RoleId = null, string? ScopeId = null,
    Directory.RoleGrantKind Kind = Directory.RoleGrantKind.Active, bool ViaPim = false) : ChangeOp
{
    public override ChangeTarget Target => User;
    public override string Key => "role:" + AssignmentId;
    public override string Label => $"Role {RoleName}";
    public override string Before => Kind == Directory.RoleGrantKind.Eligible ? "Eligible" : "Assigned";
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
    public override string Label => $"App Access {AppName}";
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
    public override string Before => "At Risk";
    public override string After => Compromised ? "Confirmed Compromised (Risk High)" : "Dismissed";
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
    public override string Label => "Device Sign-In";
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

    // Added in 6c (2026-10-27). The log stores these as numbers: add new ones at the end only.

    /// <summary>Microsoft Defender quick scan (Windows).</summary>
    QuickScan,

    /// <summary>Microsoft Defender full scan (Windows).</summary>
    FullScan,

    /// <summary>Microsoft Defender downloads its latest security intelligence (Windows).</summary>
    UpdateDefinitions,

    /// <summary>The device uploads its logs, for download from the Intune admin center (Windows).</summary>
    CollectDiagnostics,

    /// <summary>Reports where the device is (Windows, supervised iOS/iPadOS in lost mode, Android Enterprise).</summary>
    Locate,

    /// <summary>Reinstalls Windows, removing apps the manufacturer added; their files are kept or removed (<see cref="RunDeviceAction.KeepUserData"/>).</summary>
    FreshStart,

    /// <summary>Returns a Windows device to a business-ready state, keeping its Entra join and Intune enrollment.</summary>
    AutopilotReset,

    /// <summary>Makes a new BitLocker recovery key and backs it up (Windows).</summary>
    RotateBitLockerKeys,
}

/// <summary>
/// An Intune remote action on a managed device. All but Sync and the Defender updates interrupt
/// or remove something: caution changes. The ones that erase (wipe, Fresh Start, Autopilot reset)
/// can't be undone, so the admin also types the device's name. Those and retire share one slot:
/// staging one replaces another.
/// </summary>
/// <param name="Device">The Intune managed device (Kind "manageddevice").</param>
/// <param name="KeepUserData">Fresh Start: keep the user's files.</param>
public sealed record RunDeviceAction(ChangeTarget Device, DeviceCommand Command, bool KeepUserData = false) : ChangeOp
{
    public bool Removes => Command is DeviceCommand.Retire or DeviceCommand.Wipe or DeviceCommand.FreshStart or DeviceCommand.AutopilotReset;

    public bool Erases => Command is DeviceCommand.Wipe or DeviceCommand.FreshStart or DeviceCommand.AutopilotReset;

    public override ChangeTarget Target => Device;
    public override string Key => Removes ? "remove" : "action:" + Command;
    public override string Label => Command switch
    {
        DeviceCommand.Sync => "Sync with Intune",
        DeviceCommand.Restart => "Restart",
        DeviceCommand.Lock => "Lock",
        DeviceCommand.RotateLocalAdminPassword => "Local Admin Password",
        DeviceCommand.Retire => "Retire",
        DeviceCommand.Wipe => "Wipe",
        DeviceCommand.QuickScan => "Defender Quick Scan",
        DeviceCommand.FullScan => "Defender Full Scan",
        DeviceCommand.UpdateDefinitions => "Defender Definitions",
        DeviceCommand.CollectDiagnostics => "Collect Diagnostics",
        DeviceCommand.Locate => "Locate",
        DeviceCommand.FreshStart => "Fresh Start",
        DeviceCommand.AutopilotReset => "Autopilot Reset",
        _ => "BitLocker Recovery Keys",
    };
    public override string Before => Command switch
    {
        DeviceCommand.RotateLocalAdminPassword => "Current Password",
        DeviceCommand.RotateBitLockerKeys => "Current Keys",
        DeviceCommand.UpdateDefinitions => "Current Definitions",
        _ when Removes => "Managed",
        _ => "",
    };
    public override string After => Command switch
    {
        DeviceCommand.Sync => "Checks in now",
        DeviceCommand.Restart => "Restarts now",
        DeviceCommand.Lock => "Locks now",
        DeviceCommand.RotateLocalAdminPassword => "A new password",
        DeviceCommand.Retire => "Retired: work data removed, no longer managed",
        DeviceCommand.Wipe => "Wiped: erased back to factory settings",
        DeviceCommand.QuickScan => "Scans now (quick)",
        DeviceCommand.FullScan => "Scans now (full)",
        DeviceCommand.UpdateDefinitions => "The latest definitions",
        DeviceCommand.CollectDiagnostics => "Uploads its logs",
        DeviceCommand.Locate => "Reports its location",
        DeviceCommand.FreshStart => KeepUserData ? "Windows reinstalled, their files kept" : "Windows reinstalled, their files removed",
        DeviceCommand.AutopilotReset => "Reset to business-ready, still enrolled",
        _ => "New keys, backed up",
    };
    public override bool IsCaution => Command is not (DeviceCommand.Sync or DeviceCommand.UpdateDefinitions or DeviceCommand.CollectDiagnostics);
    public override string? ConfirmPhrase => Erases ? Device.Name : null;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Renames a managed device (Windows restarts to take the new name; iOS/iPadOS needs supervision).</summary>
public sealed record RenameDevice(ChangeTarget Device, string Old, string New) : ChangeOp
{
    public override ChangeTarget Target => Device;
    public override string Key => "name";
    public override string Label => "Device Name";
    public override string Before => Old;
    public override string After => New;
    public override bool IsNoChange => string.Equals(Old, New, StringComparison.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is RenameDevice r ? this with { Old = r.Old } : this;
}

/// <summary>Makes someone the device's primary user in Intune (the person its apps and policies follow).</summary>
/// <param name="OldUser">The current primary user's name, for the review; null when it has none.</param>
public sealed record SetPrimaryUser(ChangeTarget Device, string? OldUser, ChangeTarget NewUser) : ChangeOp
{
    public override ChangeTarget Target => Device;
    public override string Key => "primaryuser";
    public override string Label => "Primary User";
    public override string Before => OldUser ?? "Nobody";
    public override string After => NewUser.Name;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetPrimaryUser p ? this with { OldUser = p.OldUser } : this;
}

/// <summary>Marks a managed device company-owned or personal (Intune treats personal devices more lightly).</summary>
public sealed record SetDeviceOwnership(ChangeTarget Device, bool OldCompany, bool NewCompany) : ChangeOp
{
    public override ChangeTarget Target => Device;
    public override string Key => "ownership";
    public override string Label => "Ownership";
    public override string Before => OldCompany ? "Company-Owned" : "Personal";
    public override string After => NewCompany ? "Company-Owned" : "Personal";
    public override bool IsNoChange => OldCompany == NewCompany;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetDeviceOwnership o ? this with { OldCompany = o.OldCompany } : this;
}

/// <summary>
/// Turns lost mode on (a supervised iPhone or iPad locks and shows a message and phone number) or
/// off. A caution change when turning on.
/// </summary>
public sealed record SetLostMode(ChangeTarget Device, bool On, string? Message = null, string? Phone = null) : ChangeOp
{
    public override ChangeTarget Target => Device;
    public override string Key => "lostmode";
    public override string Label => "Lost Mode";
    public override string Before => On ? "Off" : "On";
    public override string After => On ? $"On: \"{Message}\"{(string.IsNullOrEmpty(Phone) ? "" : $", call {Phone}")}" : "Off";
    public override bool IsCaution => On;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Sets an Autopilot device's group tag (what Autopilot profiles and dynamic groups match on).</summary>
/// <param name="Entry">The Autopilot device (Kind "autopilot"), named by its serial number.</param>
public sealed record SetAutopilotGroupTag(ChangeTarget Entry, string? Old, string? New) : ChangeOp
{
    public override ChangeTarget Target => Entry;
    public override string Key => "grouptag";
    public override string Label => "Group Tag";
    public override string Before => string.IsNullOrEmpty(Old) ? "None" : Old;
    public override string After => string.IsNullOrEmpty(New) ? "None" : New;
    public override bool IsNoChange => string.Equals(Old ?? "", New ?? "", StringComparison.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetAutopilotGroupTag t ? this with { Old = t.Old } : this;
}

/// <summary>Removes a device from Autopilot (it won't be set up by Autopilot again). A caution change.</summary>
public sealed record DeleteAutopilotDevice(ChangeTarget Entry, string Detail) : ChangeOp
{
    public override ChangeTarget Target => Entry;
    public override string Key => "remove";
    public override string Label => "Remove from Autopilot";
    public override string Before => Detail;
    public override string After => "Removed from Autopilot";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// Deletes a device's records: from Intune when it's managed there, then from Entra. Typically a
/// stale device nobody uses. It isn't wiped or retired first; a device still in use would have to
/// register again. A caution change.
/// </summary>
/// <param name="Device">The Entra device (Kind "device"), or the Intune device (Kind "manageddevice") for one Entra doesn't list.</param>
/// <param name="ManagedId">Its Intune ID, when Intune manages it too.</param>
/// <param name="LastSeen">When it was last seen, for the review ("Last Seen 3 Feb 2026").</param>
public sealed record DeleteDevice(ChangeTarget Device, string? ManagedId, string LastSeen) : ChangeOp
{
    public override ChangeTarget Target => Device;
    public override string Key => "remove";
    public override string Label => "Delete Device";
    public override string Before => LastSeen;
    public override string After => Device.Kind == "manageddevice" ? "Deleted from Intune"
        : ManagedId is null ? "Deleted from Entra" : "Deleted from Intune and Entra";
    public override bool IsCaution => true;
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

// ---- Mail & security (5a) -------------------------------------------------------------------

/// <summary>
/// A quarantined message, released to its recipients or deleted. The write guard judges each
/// recipient: in Test mode only mail addressed to test objects can be touched.
/// </summary>
/// <param name="Message">Kind "quarantine": Id is Exchange's identity, Name the subject.</param>
public abstract record QuarantineChange(ChangeTarget Message, string Sender, IReadOnlyList<string> Recipients) : ChangeOp
{
    public override ChangeTarget Target => Message;
    public override string Key => "quarantine";
    public override IReadOnlyList<ChangeTarget> Guarded => [.. Recipients.Select(r => new ChangeTarget("mailbox", r, r, r))];
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Releases a quarantined message to its recipients and, when asked, allows its sender from now on.</summary>
public sealed record ReleaseQuarantined(ChangeTarget Message, string Sender, IReadOnlyList<string> Recipients, bool AllowSender)
    : QuarantineChange(Message, Sender, Recipients)
{
    public override string Label => $"Quarantined \"{Message.Name}\"";
    public override string Before => "Held";
    public override string After => AllowSender ? $"Released, and {Sender} allowed from now on" : "Released to its recipients";
}

/// <summary>Deletes a quarantined message for good: a caution change.</summary>
public sealed record DeleteQuarantined(ChangeTarget Message, string Sender, IReadOnlyList<string> Recipients)
    : QuarantineChange(Message, Sender, Recipients)
{
    public override string Label => $"Quarantined \"{Message.Name}\"";
    public override string Before => "Held";
    public override string After => "Deleted";
    public override bool IsCaution => true;
}

/// <summary>
/// An entry on the Tenant Allow/Block List (5b). Its target is the value itself (Kind "listentry",
/// Address the value), so in Test mode only values starting with the test marker can change.
/// </summary>
/// <param name="Entry">Kind "listentry": Id "{kind}:{value}", Name and Address the value.</param>
public abstract record ListEntryChange(ChangeTarget Entry, Directory.ListKind Kind) : ChangeOp
{
    public override ChangeTarget Target => Entry;
    public override string Key => "entry";
    public override string Label => $"{Directory.AllowBlock.KindName(Kind)} {Entry.Name}";
    public override ChangeOp Since(ChangeOp earlier) => this;

    public static ChangeTarget TargetFor(Directory.ListKind kind, string value) =>
        new("listentry", $"{kind}:{value.ToLowerInvariant()}", value, value);
}

/// <summary>Adds an entry: blocked (mail, links or files matching it are stopped) or allowed.</summary>
/// <param name="Infrastructure">Spoofed senders only: the sending infrastructure.</param>
/// <param name="SpoofType">Spoofed senders only: "External" or "Internal".</param>
public sealed record AddListEntry(ChangeTarget Entry, Directory.ListKind Kind, bool Block, Directory.ListExpiry Expiry, string? Notes,
    string? Infrastructure = null, string? SpoofType = null) : ListEntryChange(Entry, Kind)
{
    public override string Before => "Not Listed";
    public override string After => (Block ? "Blocked" : "Allowed") + (Kind == Directory.ListKind.Spoof ? $" from {Infrastructure}" : $", {Expiry.Text}");
    public override bool IsCaution => Block;
    public override bool IsNoChange => false;
}

/// <summary>Takes an entry off the list.</summary>
public sealed record RemoveListEntry(ChangeTarget Entry, Directory.ListKind Kind, string Identity, bool WasBlocked) : ListEntryChange(Entry, Kind)
{
    public override string Before => WasBlocked ? "Blocked" : "Allowed";
    public override string After => "Removed";
    public override bool IsCaution => !WasBlocked;
    public override bool IsNoChange => false;
}

/// <summary>Changes an entry's expiry or notes (senders, links, files, IPs).</summary>
public sealed record UpdateListEntry(ChangeTarget Entry, Directory.ListKind Kind, string Identity, Directory.ListExpiry OldExpiry, Directory.ListExpiry NewExpiry,
    string? OldNotes, string? NewNotes) : ListEntryChange(Entry, Kind)
{
    public override string Before => $"{OldExpiry.Text}{(string.IsNullOrEmpty(OldNotes) ? "" : $"; note \"{OldNotes}\"")}";
    public override string After => $"{NewExpiry.Text}{(string.IsNullOrEmpty(NewNotes) ? "" : $"; note \"{NewNotes}\"")}";
    public override bool IsNoChange => OldExpiry == NewExpiry && (OldNotes ?? "") == (NewNotes ?? "");
    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateListEntry e ? this with { OldExpiry = e.OldExpiry, OldNotes = e.OldNotes } : this;
}

// ---- Mail flow rules (5c) -------------------------------------------------------------------

/// <summary>
/// A change to one mail flow rule. Rules have no address, so the write guard judges the rule's
/// name: in Test mode only rules named zz-tenantry-test-… can change.
/// </summary>
/// <param name="Rule">Kind "rule": Id is Exchange's identity, Name the rule's name.</param>
public abstract record RuleChange(ChangeTarget Rule) : ChangeOp
{
    public override ChangeTarget Target => Rule;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

public sealed record SetRuleEnabled(ChangeTarget Rule, bool Old, bool New) : RuleChange(Rule)
{
    public override string Key => "enabled";
    public override string Label => $"Mail Flow Rule \"{Rule.Name}\"";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsCaution => !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetRuleEnabled e ? this with { Old = e.Old } : this;
}

/// <summary>Enforce (acts on mail) or Test (only records matches). Switching to Enforce is a caution change.</summary>
public sealed record SetRuleMode(ChangeTarget Rule, Directory.RuleMode Old, Directory.RuleMode New) : RuleChange(Rule)
{
    public override string Key => "mode";
    public override string Label => $"Mail Flow Rule \"{Rule.Name}\" Mode";
    public override string Before => Directory.MailFlowRule.ModeName(Old);
    public override string After => Directory.MailFlowRule.ModeName(New);
    public override bool IsCaution => New == Directory.RuleMode.Enforce;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetRuleMode e ? this with { Old = e.Old } : this;
}

/// <summary>Moves a rule in the order rules run (0 first). Exchange moves the others to make room.</summary>
public sealed record SetRulePriority(ChangeTarget Rule, int Old, int New) : RuleChange(Rule)
{
    public override string Key => "priority";
    public override string Label => $"Mail Flow Rule \"{Rule.Name}\" Order";
    public override string Before => $"Runs {Old + 1}{Ordinal(Old + 1)}";
    public override string After => $"Runs {New + 1}{Ordinal(New + 1)}";
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetRulePriority e ? this with { Old = e.Old } : this;

    static string Ordinal(int n) => (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
}

/// <summary>Deletes a rule for good: a caution change.</summary>
/// <param name="Was">How it stood, for the review ("On, Enforce").</param>
public sealed record RemoveRule(ChangeTarget Rule, string Was) : RuleChange(Rule)
{
    public override string Key => "remove";
    public override string Label => $"Mail Flow Rule \"{Rule.Name}\"";
    public override string Before => Was;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

/// <summary>A new rule from the editor (5d). New rules normally start in Test mode; one that starts in Enforce is a caution change.</summary>
/// <param name="Rule">Kind "rule": Id and Name are the new rule's name (Exchange gives it an identity when it's made).</param>
/// <param name="Parameters">Every New-TransportRule parameter except Name, as a JSON object.</param>
/// <param name="Sentence">The rule in a sentence, for the review.</param>
/// <param name="Enforces">Starts in Enforce mode.</param>
public sealed record CreateRule(ChangeTarget Rule, string Parameters, string Sentence, bool Enforces) : RuleChange(Rule)
{
    public override string Key => "create";
    public override string Label => $"New Mail Flow Rule \"{Rule.Name}\"";
    public override string Before => "None";
    public override string After => Sentence;
    public override bool IsCaution => Enforces;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>An edit from the editor (5d): only the parameters that changed; cleared ones are null (switches false).</summary>
/// <param name="Rule">Kind "rule": Id is Exchange's identity, Name the rule's name before the edit.</param>
/// <param name="Changes">Set-TransportRule parameters as a JSON object.</param>
/// <param name="Enforces">The edit switches the rule to Enforce.</param>
public sealed record UpdateRule(ChangeTarget Rule, string Changes, string OldSummary, string NewSummary, bool Enforces) : RuleChange(Rule)
{
    public override string Key => "edit";
    public override string Label => $"Mail Flow Rule \"{Rule.Name}\"";
    public override string Before => OldSummary;
    public override string After => NewSummary;
    public override bool IsCaution => Enforces;
    public override bool IsNoChange => Changes is "{}" or "";
    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateRule e ? this with { OldSummary = e.OldSummary } : this;
}

/// <summary>
/// Kept so older change-log entries still read: quarantine's "Block the sender" now stages
/// <see cref="AddListEntry"/>. Adds a sender to the Tenant Allow/Block List as blocked, or allowed.
/// </summary>
/// <param name="Sender">Kind "sender": Id and Address are the address or domain.</param>
/// <param name="RemoveAfterDays">Allowed entries expire (Microsoft's rule); null keeps a block for good.</param>
public sealed record SetSenderListed(ChangeTarget Sender, bool Block, string? Notes, int? RemoveAfterDays = null) : ChangeOp
{
    public override ChangeTarget Target => Sender;
    public override string Key => "listed";
    public override string Label => $"Sender {Sender.Name}";
    public override string Before => "Not Listed";
    public override string After => Block ? "Blocked" : RemoveAfterDays is { } d ? $"Allowed for {d} Days" : "Allowed";
    public override bool IsCaution => Block;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

// ---- OneDrive (4d-1) ------------------------------------------------------------------------

/// <summary>
/// Gives someone full access to their OneDrive (admin rights on the site: a manager taking over a
/// leaver's files), or takes it away. Taking it away is a caution change.
/// </summary>
/// <param name="Login">SharePoint's sign-in name for the person (what the change is sent with).</param>
public sealed record SetOneDriveAccess(ChangeTarget User, string SiteUrl, string SiteId, ChangeTarget Person, string Login, bool Old, bool New) : ChangeOp
{
    /// <summary>SharePoint's sign-in name for a person in the organization.</summary>
    public static string LoginFor(string userPrincipalName) => "i:0#.f|membership|" + userPrincipalName;

    public override ChangeTarget Target => User;
    public override string Key => "onedrive:" + Login.ToLowerInvariant();
    public override string Label => $"OneDrive Access: {Person.Name}";
    public override string Before => Old ? "Full Access" : "No Access";
    public override string After => New ? "Full Access" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override IReadOnlyList<ChangeTarget> Guarded => [User, Person];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetOneDriveAccess e ? this with { Old = e.Old } : this;
}

// ---- Sites (7c) -----------------------------------------------------------------------------

/// <summary>
/// A change to one site or OneDrive. Its target's address is the last part of the site's address
/// (…/sites/finance → "finance"), so in Test mode only sites whose address starts with the test
/// marker can change, whatever their title says.
/// </summary>
/// <param name="Site">Kind "site": Id is SharePoint's site ID, Name the title, Address the alias.</param>
public abstract record SiteChange(ChangeTarget Site, string SiteUrl) : ChangeOp
{
    public override ChangeTarget Target => Site;
    public override ChangeOp Since(ChangeOp earlier) => this;

    public static ChangeTarget TargetFor(Directory.SiteSummary site) => new("site", site.Id, site.Title, site.Alias);
}

/// <summary>
/// Gives someone admin rights on a site (full control, and they see everything in it), or takes
/// them away. Adding yourself is how an admin sees inside a site they aren't a member of. Only the
/// site changes, so only it is guarded.
/// </summary>
/// <param name="Login">SharePoint's sign-in name for the person.</param>
public sealed record SetSiteAdmin(ChangeTarget Site, string SiteUrl, ChangeTarget Person, string Login, bool Old, bool New) : SiteChange(Site, SiteUrl)
{
    public override string Key => "admin:" + Login.ToLowerInvariant();
    public override string Label => $"Site Admin {Person.Name}";
    public override string Before => Old ? "Site Admin" : "Not a Site Admin";
    public override string After => New ? "Site Admin" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetSiteAdmin e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Adds someone to one of a site's SharePoint groups (its Owners, Members or Visitors, or one of its
/// own), or takes them out. A group-connected site's owners and members change through its
/// Microsoft 365 group instead (<see cref="SetGroupMembership"/>).
/// </summary>
public sealed record SetSiteGroupMember(ChangeTarget Site, string SiteUrl, int GroupId, string GroupName, ChangeTarget Person, string Login, bool Old, bool New)
    : SiteChange(Site, SiteUrl)
{
    public override string Key => $"sitegroup:{GroupId}:{Login.ToLowerInvariant()}";
    public override string Label => $"{GroupName}: {Person.Name}";
    public override string Before => Old ? "In the Group" : "Not in the Group";
    public override string After => New ? "Added" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetSiteGroupMember e ? this with { Old = e.Old } : this;
}

/// <summary>
/// One site setting (external sharing, the default link, guest expiry, storage quota, lock), sent
/// as SharePoint's SiteProperties values: <see cref="Values"/> is a JSON object of property → value,
/// since some settings are two properties (guest expiry is a switch and a number of days).
/// </summary>
/// <param name="Setting">Which setting ("sharing", "quota"…): the slot, so restaging one replaces it.</param>
public sealed record SetSiteSetting(ChangeTarget Site, string SiteUrl, string Setting, string SettingLabel, string Old, string New, string Values,
    bool Caution = false) : SiteChange(Site, SiteUrl)
{
    public override string Key => "setting:" + Setting;
    public override string Label => SettingLabel;
    public override string Before => Old;
    public override string After => New;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => string.Equals(Old, New, StringComparison.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetSiteSetting e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Archives a site (Microsoft 365 Archive: kept, out of reach, stored more cheaply) or reactivates
/// it. Both are caution changes: an archived site can't be opened, and reactivating one archived
/// more than 7 days ago is charged.
/// </summary>
public sealed record SetSiteArchived(ChangeTarget Site, string SiteUrl, bool Archive) : SiteChange(Site, SiteUrl)
{
    public override string Key => "archive";
    public override string Label => "Archive";
    public override string Before => Archive ? "Active" : "Archived";
    public override string After => Archive ? "Archived: kept, but can't be opened" : "Reactivated (charged after 7 days archived)";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

/// <summary>
/// Deletes a site into SharePoint's recycle bin (restorable for 93 days). A group-connected site is
/// deleted with its Microsoft 365 group (its Team, mailbox and calendar too; restorable for 30
/// days). The admin types the site's title to confirm.
/// </summary>
/// <param name="GroupId">The Microsoft 365 group to delete instead, for a group-connected site.</param>
/// <param name="Detail">How it stood, for the review ("Team site · 12 GB").</param>
public sealed record DeleteSite(ChangeTarget Site, string SiteUrl, string? GroupId, string Detail) : SiteChange(Site, SiteUrl)
{
    public override string Key => "delete";
    public override string Label => GroupId is null ? $"Delete Site {Site.Name}" : $"Delete Site and Group {Site.Name}";
    public override string Before => Detail;
    public override string After => GroupId is null ? "Deleted (restorable for 93 days)" : "Deleted with its Microsoft 365 group (restorable for 30 days)";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Site.Name;
    public override bool IsNoChange => false;
}

/// <summary>Brings back a deleted site (or the deleted Microsoft 365 group it belongs to, which brings back its site).</summary>
public sealed record RestoreSite(ChangeTarget Site, string SiteUrl, string? GroupId, string Detail) : SiteChange(Site, SiteUrl)
{
    public override string Key => "restore";
    public override string Label => $"Deleted Site {Site.Name}";
    public override string Before => Detail;
    public override string After => GroupId is null ? "Restored" : "Restored with its Microsoft 365 group";
    public override bool IsNoChange => false;
}

/// <summary>
/// A new site: a team site (made as a Microsoft 365 group, which gives it a mailbox and calendar
/// too; its owners are the group's) or a communication site (made in SharePoint: the admin who
/// applies it becomes its owner, and <see cref="Owners"/> join its Owners group). Its owners are
/// guarded along with it.
/// </summary>
/// <param name="Site">Kind "site": Id and Address are the new site's alias, Name its title.</param>
public sealed record CreateSite(ChangeTarget Site, string SiteUrl, Directory.SiteKind Kind, string? Description, bool IsPrivate,
    IReadOnlyList<ChangeTarget> Owners) : SiteChange(Site, SiteUrl)
{
    public override string Key => "create";
    public override string Label => $"New {Directory.SiteSummary.KindName(Kind)} {Site.Name}";
    public override string Before => "None";
    public override string After => string.Join(" · ", new[]
    {
        SiteUrl,
        Kind == Directory.SiteKind.TeamSite ? (IsPrivate ? "Private Microsoft 365 group" : "Public Microsoft 365 group") : null,
        Owners.Count switch { 0 => null, 1 => $"Owner {Owners[0].Name}", var n => $"{n} owners" },
    }.OfType<string>());
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [Site, .. Owners];
}

// ---- Admin roles (8a) -----------------------------------------------------------------------

/// <summary>
/// Gives someone (a person, a role-assignable group or an app) a directory role, for the whole
/// organization or one administrative unit. Without PIM it's active and permanent
/// (roleAssignments); through PIM it can be eligible or end on a date. Giving a privileged role is
/// a caution change. The write guard checks who receives it.
/// </summary>
/// <param name="Principal">Who (kind "user", "group" or "app").</param>
/// <param name="ScopeId">"/" or "/administrativeUnits/{id}".</param>
/// <param name="End">When it ends (PIM only); null for permanent.</param>
public sealed record AddRoleAssignment(ChangeTarget Principal, Directory.RolePrincipalType PrincipalType, string RoleId, string RoleName, bool Privileged,
    string ScopeId, string ScopeName, Directory.RoleGrantKind Kind = Directory.RoleGrantKind.Active, DateTimeOffset? End = null, string? Justification = null)
    : ChangeOp
{
    /// <summary>Through PIM: eligible, or ending on a date.</summary>
    public bool ViaPim => Kind == Directory.RoleGrantKind.Eligible || End is not null;

    public override ChangeTarget Target => Principal;
    public override string Key => $"addrole:{RoleId}:{ScopeId}";
    public override string Label => $"Role {RoleName}";
    public override string Before => "Not Assigned";
    public override string After => string.Join(", ", new[]
    {
        Kind == Directory.RoleGrantKind.Eligible ? "Eligible" : "Active",
        ScopeId == "/" ? null : $"for {ScopeName}",
        End is { } end ? $"until {end:d MMM yyyy}" : "permanently",
    }.OfType<string>());
    public override bool IsCaution => Privileged;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// A custom role: one change to it. Roles have no address, so the write guard judges the name: in
/// Test mode only roles named zz-tenantry-test-… change. Custom roles need Entra ID P1.
/// </summary>
/// <param name="Role">Kind "role": Id is the role's ID (its name, for a new one), Name its name.</param>
public abstract record CustomRoleChange(ChangeTarget Role) : ChangeOp
{
    public override ChangeTarget Target => Role;
    public override ChangeOp Since(ChangeOp earlier) => this;

    protected static string Count(int n) => n == 1 ? "1 permission" : $"{n} permissions";
}

public sealed record CreateCustomRole(ChangeTarget Role, string Description, IReadOnlyList<string> Actions) : CustomRoleChange(Role)
{
    public override string Key => "create";
    public override string Label => $"New Custom Role \"{Role.Name}\"";
    public override string Before => "None";
    public override string After => Count(Actions.Count);
    public override bool IsNoChange => false;
}

/// <summary>Renames a custom role, or changes its description or permissions. Taking permissions away is a caution change.</summary>
public sealed record UpdateCustomRole(ChangeTarget Role, string NewName, string OldDescription, string NewDescription, IReadOnlyList<string> OldActions,
    IReadOnlyList<string> NewActions) : CustomRoleChange(Role)
{
    public override string Key => "edit";
    public override string Label => $"Custom Role \"{Role.Name}\"";
    public override string Before => Count(OldActions.Count);
    public override string After => string.Join("; ", new[]
    {
        NewName != Role.Name ? $"named \"{NewName}\"" : null,
        NewDescription != OldDescription ? "new description" : null,
        NewActions.Except(OldActions).Count() is var added and > 0 ? $"{added} added" : null,
        OldActions.Except(NewActions).Count() is var removed and > 0 ? $"{removed} removed" : null,
    }.OfType<string>()) is { Length: > 0 } what ? $"{Count(NewActions.Count)} ({what})" : Count(NewActions.Count);
    public override bool IsCaution => OldActions.Except(NewActions).Any();
    public override bool IsNoChange => NewName == Role.Name && NewDescription == OldDescription && OldActions.ToHashSet().SetEquals(NewActions);
    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateCustomRole e ? this with { OldDescription = e.OldDescription, OldActions = e.OldActions } : this;
}

/// <summary>Deletes a custom role; Entra refuses while anyone holds it. The admin types its name to confirm.</summary>
public sealed record DeleteCustomRole(ChangeTarget Role, string Detail) : CustomRoleChange(Role)
{
    public override string Key => "delete";
    public override string Label => $"Custom Role \"{Role.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Role.Name;
    public override bool IsNoChange => false;
}

// ---- Privileged Identity Management (8a-2) ---------------------------------------------------

/// <summary>
/// One rule of a role's PIM settings (activation length, what activating needs, approval, how long
/// assignments last), sent whole as Graph's rule JSON. The role is the target, judged by name, so
/// Test mode refuses every built-in role. Loosening a rule (no MFA, no approval) is a caution change.
/// </summary>
/// <param name="Role">Kind "role": Id is the role's ID, Name its name.</param>
public sealed record SetRolePolicyRule(ChangeTarget Role, string PolicyId, string RuleId, string SettingLabel, string Old, string New, string Rule,
    bool Loosens = false) : ChangeOp
{
    public override ChangeTarget Target => Role;
    public override string Key => "rule:" + RuleId;
    public override string Label => $"{Role.Name}: {SettingLabel}";
    public override string Before => Old;
    public override string After => New;
    public override bool IsCaution => Loosens;
    public override bool IsNoChange => string.Equals(Old, New, StringComparison.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetRolePolicyRule e ? this with { Old = e.Old } : this;
}

/// <summary>Moves a time-limited assignment's end (PIM's adminUpdate). The write guard checks who holds it.</summary>
public sealed record ExtendRoleAssignment(ChangeTarget Principal, string RoleId, string RoleName, string ScopeId, Directory.RoleGrantKind Kind,
    DateTimeOffset? OldEnd, DateTimeOffset NewEnd) : ChangeOp
{
    public override ChangeTarget Target => Principal;
    public override string Key => $"extendrole:{RoleId}:{ScopeId}:{Kind}";
    public override string Label => $"Role {RoleName} Ends";
    public override string Before => OldEnd is { } o ? $"{o:d MMM yyyy}" : "Never";
    public override string After => $"{NewEnd:d MMM yyyy}";
    public override bool IsNoChange => OldEnd == NewEnd;
    public override ChangeOp Since(ChangeOp earlier) => earlier is ExtendRoleAssignment e ? this with { OldEnd = e.OldEnd } : this;
}

/// <summary>
/// The signed-in admin switches one of their eligible roles on for a while (selfActivate), or off
/// again early (selfDeactivate). The target is the admin, so Test mode refuses it.
/// </summary>
public sealed record ActivateRole(ChangeTarget Me, string RoleId, string RoleName, string ScopeId, bool On, int Hours = 0, string? Justification = null,
    string? Ticket = null) : ChangeOp
{
    public override ChangeTarget Target => Me;
    public override string Key => $"activate:{RoleId}:{ScopeId}";
    public override string Label => $"Role {RoleName}";
    public override string Before => On ? "Eligible" : "Activated";
    public override string After => On ? $"Activated for {Directory.RolePolicyRules.Words(TimeSpan.FromHours(Hours))}" : "Deactivated";
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Approves or denies someone's request to activate a role. The write guard checks who asked.</summary>
/// <param name="Request">Kind "rolerequest": Id the request, Name "who: role", Address the requester's sign-in name.</param>
public sealed record ReviewRoleRequest(ChangeTarget Request, string ApprovalId, bool Approve, string Justification) : ChangeOp
{
    public override ChangeTarget Target => Request;
    public override string Key => "review";
    public override string Label => $"Request {Request.Name}";
    public override string Before => "Waiting for approval";
    public override string After => Approve ? "Approved" : "Denied";
    public override bool IsCaution => Approve;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

// ---- Conditional Access (8b) ----------------------------------------------------------------

/// <summary>
/// A change to one Conditional Access policy. Policies have no address, so the write guard judges
/// the name: in Test mode only policies named zz-tenantry-test-… change.
/// </summary>
/// <param name="Policy">Kind "capolicy": Id is Entra's policy ID (its name, for a new one), Name its name.</param>
public abstract record CaPolicyChange(ChangeTarget Policy) : ChangeOp
{
    public override ChangeTarget Target => Policy;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>On (enforced), Report-Only (only recorded) or Off. Switching a policy On is a caution change.</summary>
public sealed record SetCaPolicyState(ChangeTarget Policy, string Old, string New) : CaPolicyChange(Policy)
{
    public override string Key => "state";
    public override string Label => $"Policy \"{Policy.Name}\"";
    public override string Before => Directory.CaPolicy.StateName(Old);
    public override string After => Directory.CaPolicy.StateName(New);
    public override bool IsCaution => New == "enabled" || Old == "enabled";
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetCaPolicyState e ? this with { Old = e.Old } : this;
}

/// <summary>Deletes a policy (Entra keeps it 30 days in deleted items). The admin types its name to confirm.</summary>
public sealed record DeleteCaPolicy(ChangeTarget Policy, string Detail) : CaPolicyChange(Policy)
{
    public override string Key => "delete";
    public override string Label => $"Policy \"{Policy.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Policy.Name;
    public override bool IsNoChange => false;
}

/// <summary>A new policy from the editor, as Graph's JSON. One that starts On is a caution change.</summary>
/// <param name="Policy">Kind "capolicy": Id and Name are the new policy's name.</param>
/// <param name="Json">The whole policy (without read-only properties).</param>
/// <param name="Description">The policy in a line, for the review.</param>
public sealed record CreateCaPolicy(ChangeTarget Policy, string Json, string Description) : CaPolicyChange(Policy)
{
    public override string Key => "create";
    public override string Label => $"New Policy \"{Policy.Name}\"";
    public override string Before => "None";
    public override string After => Description;
    public override bool IsCaution => Json.Contains("\"state\":\"enabled\"", StringComparison.Ordinal);
    public override bool IsNoChange => false;
}

/// <summary>
/// An edit from the editor: the policy's name, state, conditions and controls as the editor left them
/// (everything it didn't touch kept as Entra had it). Switching it On is a caution change.
/// </summary>
public sealed record UpdateCaPolicy(ChangeTarget Policy, string Json, string OldSummary, string NewSummary, bool TurnsOn) : CaPolicyChange(Policy)
{
    public override string Key => "edit";
    public override string Label => $"Policy \"{Policy.Name}\"";
    public override string Before => OldSummary;
    public override string After => NewSummary;
    public override bool IsCaution => TurnsOn;
    public override bool IsNoChange => OldSummary == NewSummary && !TurnsOn;
    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateCaPolicy e ? this with { OldSummary = e.OldSummary } : this;
}

/// <summary>
/// A change to a named location. Locations have no address, so the write guard judges the name:
/// in Test mode only locations named zz-tenantry-test-… change.
/// </summary>
/// <param name="Location">Kind "location": Id is Entra's ID (the name, for a new one), Name its name.</param>
public abstract record NamedLocationChange(ChangeTarget Location) : ChangeOp
{
    public override ChangeTarget Target => Location;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>A new named location. Marking one trusted is a caution change: policies that leave out trusted places then skip it.</summary>
public sealed record CreateNamedLocation(ChangeTarget Location, string Json, string Description, bool Trusted) : NamedLocationChange(Location)
{
    public override string Key => "create";
    public override string Label => $"New Location \"{Location.Name}\"";
    public override string Before => "None";
    public override string After => Description;
    public override bool IsCaution => Trusted;
    public override bool IsNoChange => false;
}

/// <summary>A named location's name, places or trust changed. Making it trusted is a caution change.</summary>
public sealed record UpdateNamedLocation(ChangeTarget Location, string Json, string OldSummary, string NewSummary, bool BecomesTrusted) : NamedLocationChange(Location)
{
    public override string Key => "edit";
    public override string Label => $"Location \"{Location.Name}\"";
    public override string Before => OldSummary;
    public override string After => NewSummary;
    public override bool IsCaution => BecomesTrusted;
    public override bool IsNoChange => OldSummary == NewSummary;
    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateNamedLocation e ? this with { OldSummary = e.OldSummary } : this;
}

/// <summary>Deletes a named location (Entra refuses while a policy names it).</summary>
public sealed record DeleteNamedLocation(ChangeTarget Location, string Detail) : NamedLocationChange(Location)
{
    public override string Key => "delete";
    public override string Label => $"Location \"{Location.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

// ---- Apps (8c-2) ------------------------------------------------------------------------------

/// <summary>
/// A change to an app: an app registration (kind "appregistration", Id = the application's object
/// ID) or an enterprise app (kind "enterpriseapp", Id = the service principal's ID). The app is
/// judged by its name, so Test mode allows only zz-tenantry-test- apps.
/// </summary>
public abstract record AppChange(ChangeTarget App) : ChangeOp
{
    public const string Registration = "appregistration";
    public const string Enterprise = "enterpriseapp";

    public override ChangeTarget Target => App;
    public override ChangeOp Since(ChangeOp earlier) => this;
    public bool IsRegistration => App.Kind == Registration;
}

/// <summary>Removes a client secret or certificate. Anything still signing in with it stops working, so the admin types the app's name.</summary>
/// <param name="Detail">The credential in a line ("Expires 1 Jun 2027"), for the review.</param>
public sealed record RemoveAppCredential(ChangeTarget App, string KeyId, bool IsSecret, string CredentialName, string Detail) : AppChange(App)
{
    public override string Key => "credential:" + KeyId;
    public override string Label => $"{(IsSecret ? "Client Secret" : "Certificate")} \"{CredentialName}\"";
    public override string Before => Detail;
    public override string After => "Removed";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => App.Name;
    public override bool IsNoChange => false;
}

/// <summary>Adds a client secret. Its value is shown once after applying and never stored.</summary>
public sealed record AddAppSecret(ChangeTarget App, string Description, int Months) : AppChange(App)
{
    public override string Key => "newsecret:" + Description;
    public override string Label => "New Client Secret";
    public override string Before => "None";
    public override string After => $"\"{Description}\", expires in {Months} months (shown to you once)";
    public override bool IsNoChange => false;
}

/// <summary>Makes someone an owner of the app (they can then change its settings and credentials), or takes that away.</summary>
public sealed record SetAppOwner(ChangeTarget App, ChangeTarget Owner, bool Add) : AppChange(App)
{
    public override string Key => "owner:" + Owner.Id;
    public override string Label => $"Owner {Owner.Name}";
    public override string Before => Add ? "Not an owner" : "Owner";
    public override string After => Add ? "Owner" : "Not an owner";
    public override bool IsCaution => !Add;
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [App, Owner];
}

/// <summary>An enterprise app's on/off settings: sign-in, assignment required, visible to people.</summary>
/// <param name="Setting">"enabled", "assignment" or "visible".</param>
public sealed record SetEnterpriseAppSetting(ChangeTarget App, string Setting, bool Old, bool New) : AppChange(App)
{
    public override string Key => "setting:" + Setting;
    public override string Label => Setting switch
    {
        "enabled" => "Sign-In",
        "assignment" => "Assignment Required",
        _ => "Visible to People",
    };
    public override string Before => Words(Old);
    public override string After => Words(New);

    string Words(bool on) => Setting switch
    {
        "enabled" => on ? "On" : "Off: nobody can sign in",
        "assignment" => on ? "Yes: only people and groups given access" : "No: anyone in the organization",
        _ => on ? "Yes" : "No: hidden from My Apps",
    };

    /// <summary>Turning sign-in off, or requiring assignment, can stop people signing in.</summary>
    public override bool IsCaution => (Setting == "enabled" && !New) || (Setting == "assignment" && New);
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetEnterpriseAppSetting e ? this with { Old = e.Old } : this;
}

/// <summary>Gives a person or group access to an enterprise app, as one of its roles.</summary>
/// <param name="PrincipalType">"User" or "Group".</param>
public sealed record AddAppUserAssignment(ChangeTarget App, ChangeTarget Principal, string PrincipalType, string RoleId, string RoleName) : AppChange(App)
{
    public override string Key => $"assign:{Principal.Id}:{RoleId}";
    public override string Label => $"Access for {Principal.Name}";
    public override string Before => "None";
    public override string After => RoleName;
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [App, Principal];
}

/// <summary>Takes a person's or group's access to an enterprise app away.</summary>
public sealed record RemoveAppUserAssignment(ChangeTarget App, string AssignmentId, string PrincipalName, string RoleName) : AppChange(App)
{
    public override string Key => "assignment:" + AssignmentId;
    public override string Label => $"Access for {PrincipalName}";
    public override string Before => RoleName;
    public override string After => "Removed";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

/// <summary>
/// Takes back permissions an enterprise app was given: a delegated consent (an admin's for
/// everyone, or one person's) or an application permission it holds.
/// </summary>
/// <param name="What">The permissions in a line ("Microsoft Graph: User.Read, Mail.Send").</param>
/// <param name="Who">"Everyone (admin consent)", a person's name, or "The app itself".</param>
public sealed record RevokeAppGrant(ChangeTarget App, string GrantId, bool IsApplicationPermission, string What, string Who) : AppChange(App)
{
    public override string Key => "grant:" + GrantId;
    public override string Label => $"Permission {What}";
    public override string Before => $"Allowed for {Who}";
    public override string After => "Revoked";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

/// <summary>One permission an admin consents to: on an API (its service principal ID), delegated (by scope name) or application (by app role ID).</summary>
public sealed record ConsentItem(string ResourceId, string Resource, string PermissionId, string Name, bool IsApplication);

/// <summary>
/// Grants admin consent for the permissions an app registration asks for (as the admin center's
/// "Grant admin consent" does): delegated ones for everyone, application ones to the app itself.
/// Makes the app's enterprise app first when it has none here. A caution change.
/// </summary>
public sealed record GrantAdminConsent(ChangeTarget App, string AppId, IReadOnlyList<ConsentItem> Items) : AppChange(App)
{
    public override string Key => "consent";
    public override string Label => "Admin Consent";
    public override string Before => "Not given for these";
    public override string After => string.Join(", ", Items.Select(i => i.Name));
    public override bool IsCaution => true;
    public override bool IsNoChange => Items.Count == 0;
}

/// <summary>
/// Deletes an app registration (it goes to Deleted Apps for 30 days, and its enterprise app with
/// it) or an enterprise app (its consents and assignments go too). The admin types the app's name.
/// </summary>
public sealed record DeleteApp(ChangeTarget App, string Detail) : AppChange(App)
{
    public override string Key => "delete";
    public override string Label => IsRegistration ? $"App Registration \"{App.Name}\"" : $"Enterprise App \"{App.Name}\"";
    public override string Before => Detail;
    public override string After => IsRegistration ? "Deleted (restorable for 30 days)" : "Deleted";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => App.Name;
    public override bool IsNoChange => false;
}

/// <summary>Brings back a deleted app registration.</summary>
public sealed record RestoreApp(ChangeTarget App, string Detail) : AppChange(App)
{
    public override string Key => "restore";
    public override string Label => $"Deleted App \"{App.Name}\"";
    public override string Before => Detail;
    public override string After => "Restored";
    public override bool IsNoChange => false;
}

/// <summary>Deletes a deleted app registration for good. The admin types its name.</summary>
public sealed record PurgeApp(ChangeTarget App, string Detail) : AppChange(App)
{
    public override string Key => "purge";
    public override string Label => $"Deleted App \"{App.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted permanently";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => App.Name;
    public override bool IsNoChange => false;
}

/// <summary>A new app registration, with its enterprise app so it can be used here.</summary>
/// <param name="App">Kind "appregistration"; Id and Name are the new app's name.</param>
/// <param name="Platform">"Web", "Single-Page App" or "Mobile and Desktop" for <paramref name="RedirectUri"/>, or null.</param>
public sealed record CreateAppRegistration(ChangeTarget App, string Audience, string? Platform, string? RedirectUri) : AppChange(App)
{
    public override string Key => "create";
    public override string Label => $"New App \"{App.Name}\"";
    public override string Before => "None";
    public override string After => Directory.AppRegistration.AudienceText(Audience) + (RedirectUri is null ? "" : $"; {Platform} redirect {RedirectUri}");
    public override bool IsNoChange => false;
}

// ---- Authentication methods (8d-1) ----------------------------------------------------------

/// <summary>
/// One sign-in method (kind "authmethod", Id = the method's ID such as "Fido2") or one of the
/// policy's sections (kind "authpolicy", Id = "registrationCampaign", "systemCredentialPreferences"
/// or "reportSuspiciousActivitySettings"), sent whole as Graph's JSON. Each edit replaces the last.
/// The policy is the organization's, so Test mode refuses it (its name isn't a test name).
/// </summary>
/// <param name="Caution">Switching a method on or off, or turning on an easily intercepted one.</param>
public sealed record SetAuthMethodConfig(ChangeTarget Method, string Json, string OldSummary, string NewSummary, bool Caution) : ChangeOp
{
    public override ChangeTarget Target => Method;
    public override string Key => "config";
    public override string Label => Method.Kind == "authpolicy" ? Method.Name : $"Sign-In Method {Method.Name}";
    public override string Before => OldSummary;
    public override string After => NewSummary;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => OldSummary == NewSummary;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetAuthMethodConfig e ? this with { OldSummary = e.OldSummary } : this;
}

/// <summary>Security defaults: Microsoft's basic protections for tenants without Conditional Access. A caution change either way.</summary>
public sealed record SetSecurityDefaults(bool Old, bool New) : ChangeOp
{
    public static readonly ChangeTarget Policy = new("securitydefaults", "securityDefaults", "Security Defaults", null);

    public override ChangeTarget Target => Policy;
    public override string Key => "enabled";
    public override string Label => "Security Defaults";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsCaution => true;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetSecurityDefaults e ? this with { Old = e.Old } : this;
}

/// <summary>
/// Identity Protection for an app (8d-2): confirm it was compromised (its risk goes to high, so
/// risk-based policies act on it: a caution change) or dismiss the risk. Guarded by the app's name.
/// </summary>
public sealed record SetAppRisk(ChangeTarget App, bool Compromised) : AppChange(App)
{
    public override string Key => "risk";
    public override string Label => $"Risk for App \"{App.Name}\"";
    public override string Before => "At Risk";
    public override string After => Compromised ? "Confirmed Compromised (Risk High)" : "Dismissed";
    public override bool IsCaution => Compromised;
    public override bool IsNoChange => false;
}