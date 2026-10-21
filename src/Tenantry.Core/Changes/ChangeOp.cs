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
