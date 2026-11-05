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
