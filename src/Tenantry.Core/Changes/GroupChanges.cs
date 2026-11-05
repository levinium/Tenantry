using Tenantry.Core.Directory;

namespace Tenantry.Core.Changes;

/// <summary>
/// A change to a group itself (not who is in it). Microsoft 365 and security groups change through
/// Graph; distribution lists and mail-enabled security groups through Exchange, which Graph can't
/// write. The target is the group (kind "group"), judged by its address or name.
/// </summary>
public abstract record GroupChange(ChangeTarget Group, GroupKind Kind) : ChangeOp
{
    public override ChangeTarget Target => Group;
    public bool ThroughExchange => GroupRules.ThroughExchange(Kind);
}

/// <summary>
/// One text setting: <see cref="DisplayName"/>, <see cref="Description"/>, <see cref="Alias"/> or
/// <see cref="Visibility"/> (Microsoft 365 groups' Private or Public).
/// </summary>
public sealed record SetGroupSetting(ChangeTarget Group, GroupKind Kind, string Setting, string SettingLabel, string? Old, string? New) : GroupChange(Group, Kind)
{
    public const string DisplayName = "displayName";
    public const string Description = "description";
    public const string Alias = "mailNickname";
    public const string Visibility = "visibility";

    public override string Key => "setting:" + Setting;
    public override string Label => SettingLabel;
    public override string Before => Show(Old);
    public override string After => Show(New);

    /// <summary>A new alias changes the address people know it by; making a group public opens it to everyone.</summary>
    public override bool IsCaution => Setting == Alias || (Setting == Visibility && New == "Public");

    public override bool IsNoChange => string.Equals(Old?.Trim() ?? "", New?.Trim() ?? "", StringComparison.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetGroupSetting e ? this with { Old = e.Old } : this;
}

/// <summary>
/// How a Microsoft 365 or security group gets its members: chosen (assigned), or by a rule Entra keeps
/// applying (dynamic), which can be paused. Any change here can add or remove members: a caution change.
/// </summary>
/// <param name="Unified">A Microsoft 365 group (its groupTypes keep "Unified").</param>
public sealed record SetMembershipRule(ChangeTarget Group, GroupKind Kind, bool OldDynamic, string? OldRule, bool OldPaused,
    bool NewDynamic, string? NewRule, bool NewPaused) : GroupChange(Group, Kind)
{
    public override string Key => "rule";
    public override string Label => "Membership";
    public override string Before => Describe(OldDynamic, OldRule, OldPaused);
    public override string After => Describe(NewDynamic, NewRule, NewPaused)
                                    + (NewDynamic && !OldDynamic ? " (members who don't match are removed)" : "")
                                    + (!NewDynamic && OldDynamic ? " (current members stay)" : "");
    public override bool IsCaution => true;

    public override bool IsNoChange => OldDynamic == NewDynamic
                                       && (!NewDynamic || (string.Equals(OldRule?.Trim(), NewRule?.Trim(), StringComparison.Ordinal) && OldPaused == NewPaused));

    public override ChangeOp Since(ChangeOp earlier) =>
        earlier is SetMembershipRule e ? this with { OldDynamic = e.OldDynamic, OldRule = e.OldRule, OldPaused = e.OldPaused } : this;

    static string Describe(bool dynamic, string? rule, bool paused) =>
        !dynamic ? "Assigned (chosen by hand)" : $"By rule{(paused ? ", paused" : "")}: {rule}";
}

/// <summary>
/// Deletes a group. Microsoft 365 and security groups can be restored for 30 days; distribution lists
/// and mail-enabled security groups are gone at once. The group's name must be typed.
/// </summary>
public sealed record DeleteGroup(ChangeTarget Group, GroupKind Kind, string Detail) : GroupChange(Group, Kind)
{
    public override string Key => "delete";
    public override string Label => $"Delete {GroupRules.Noun(Kind)}";
    public override string Before => Detail;
    public override string After => GroupRules.Restorable(Kind) ? "Deleted (restorable for 30 days)" : "Deleted for good (it can't be restored)";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Group.Name;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// One Exchange mail setting on a group: Set-UnifiedGroup for a Microsoft 365 group, else
/// Set-DistributionGroup. <see cref="Value"/> is what's sent (true, "Internal", a list of Entra IDs);
/// the texts are how it reads. A list is sent whole (Exchange's overwrite form, the one documented
/// for Microsoft 365 groups).
/// </summary>
public sealed record SetGroupMailValue(ChangeTarget Group, GroupKind Kind, string Parameter, string ParameterLabel, string Old, string New,
    System.Text.Json.Nodes.JsonNode? Value, bool Caution = false) : GroupChange(Group, Kind)
{
    public override string Key => "mail:" + Parameter;
    public override string Label => ParameterLabel;
    public override string Before => Old;
    public override string After => New;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => string.Equals(Old, New, StringComparison.Ordinal);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetGroupMailValue e ? this with { Old = e.Old } : this;

    /// <summary>The cmdlet for this kind of group.</summary>
    public string Cmdlet => Kind == GroupKind.Microsoft365 ? "Set-UnifiedGroup" : "Set-DistributionGroup";
}

/// <summary>Adds or removes one of a group's extra email addresses (an alias).</summary>
public sealed record SetGroupAddress(ChangeTarget Group, GroupKind Kind, string Address, bool Old, bool New) : GroupChange(Group, Kind)
{
    public override string Key => "address:" + Address.ToLowerInvariant();
    public override string Label => $"Email Address {Address}";
    public override string Before => Old ? "One of its addresses" : "Not one of its addresses";
    public override string After => New ? "Added" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetGroupAddress e ? this with { Old = e.Old } : this;
}

/// <summary>Makes another address the group's primary one (replies come from it); the old one stays as an alias.</summary>
public sealed record SetGroupPrimaryAddress(ChangeTarget Group, GroupKind Kind, string Old, string New) : GroupChange(Group, Kind)
{
    public override string Key => "primary";
    public override string Label => "Primary Email Address";
    public override string Before => Old;
    public override string After => New;
    public override bool IsCaution => true;
    public override bool IsNoChange => string.Equals(Old, New, StringComparison.OrdinalIgnoreCase);
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetGroupPrimaryAddress e ? this with { Old = e.Old } : this;
}

/// <summary>Lets someone send as the group (Add- or Remove-RecipientPermission), or stops them.</summary>
public sealed record SetGroupSendAs(ChangeTarget Group, GroupKind Kind, ChangeTarget Person, bool Old, bool New) : GroupChange(Group, Kind)
{
    public override string Key => "sendas:" + Person.Id;
    public override string Label => $"Send As for {Person.Name}";
    public override string Before => Old ? "Allowed" : "Not Allowed";
    public override string After => New ? "Allowed" : "Removed";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override IReadOnlyList<ChangeTarget> Guarded => [Group, Person];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetGroupSendAs e ? this with { Old = e.Old } : this;
}

/// <summary>Brings back a deleted Microsoft 365 or security group with its members and owners.</summary>
public sealed record RestoreGroup(ChangeTarget Group, GroupKind Kind, string Detail) : GroupChange(Group, Kind)
{
    public override string Key => "restore";
    public override string Label => $"Restore {GroupRules.Noun(Kind)}";
    public override string Before => Detail;
    public override string After => "Restored, with its members and owners";
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}
