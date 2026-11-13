using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Core.Changes;

/// <summary>
/// A Purview protection change (14a-14f), sent through Purview's cmdlets. The item is the target
/// (kind "purviewobject", Id = kind key and name, Name = its name), so Test mode allows only
/// zz-tenantry-test- items; places, reviewers and segments it names are judged too.
/// </summary>
public abstract record PurviewChange(ChangeTarget Item) : ChangeOp
{
    public static ChangeTarget TargetFor(string kindKey, string name) => new("purviewobject", $"{kindKey}:{name}", name, null);

    /// <summary>A place, person or name a change sends (judged by Test mode by what it is: "All" is never a test object).</summary>
    public static ChangeTarget Named(string value) => new("purviewplace", value, value, value.Contains('@') ? value : null);

    public override ChangeTarget Target => Item;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>One property, set with the kind's Set- cmdlet. Renaming a label's shown name is not guarded (its Name, the identity, can't change).</summary>
/// <param name="Value">What's sent; for sensitive information types a list of name/minCount tables.</param>
/// <param name="Values">Names in it Test mode judges (segments, reviewers).</param>
/// <param name="Also">Other parameters the same Set- needs (a label's header text when turning the header on).</param>
public sealed record SetPurviewSetting(ChangeTarget Item, string KindTitle, string Cmdlet, string Parameter, string SettingLabel, JsonNode? Value, string OldText, string NewText,
    bool Caution = false, IReadOnlyList<string>? Values = null, JsonObject? Also = null) : PurviewChange(Item)
{
    public override string Key => Parameter;
    public override string Label => $"{KindTitle}: {SettingLabel}";
    public override string Before => OldText;
    public override string After => NewText;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => OldText == NewText;
    public override IReadOnlyList<ChangeTarget> Guarded => [Item, .. (Values ?? []).Select(Named)];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetPurviewSetting e ? this with { OldText = e.OldText } : this;
}

/// <summary>
/// A list changed piece by piece: what's added goes to one Set- parameter, what's taken away to
/// another (places, published labels); or, for members, Add-/Remove- cmdlets one at a time.
/// Taking anything away is a caution. Places added or taken away are judged by Test mode.
/// </summary>
/// <param name="MemberCmdlets">For members: the Add- and Remove- cmdlets and their member parameter.</param>
public sealed record SetPurviewList(ChangeTarget Item, string KindTitle, string Cmdlet, string ListLabel, string? AddParameter, string? RemoveParameter,
    IReadOnlyList<string> Old, IReadOnlyList<string> New, bool Guard, PurviewMembers? MemberCmdlets = null) : PurviewChange(Item)
{
    public override string Key => AddParameter ?? MemberCmdlets?.Property ?? ListLabel;
    public override string Label => $"{KindTitle}: {ListLabel}";
    public override string Before => Old.Count == 0 ? "None" : string.Join(", ", Old);
    public override string After => New.Count == 0 ? "None" : string.Join(", ", New);

    public IReadOnlyList<string> Added => [.. New.Where(n => !Old.Contains(n, StringComparer.OrdinalIgnoreCase))];
    public IReadOnlyList<string> Removed => [.. Old.Where(o => !New.Contains(o, StringComparer.OrdinalIgnoreCase))];

    public override bool IsCaution => Removed.Count > 0 || Added.Any(a => a.Equals("All", StringComparison.OrdinalIgnoreCase));
    public override bool IsNoChange => Added.Count == 0 && Removed.Count == 0;
    public override IReadOnlyList<ChangeTarget> Guarded => Guard ? [Item, .. Added.Concat(Removed).Select(Named)] : [Item];
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetPurviewList e ? this with { Old = e.Old } : this;
}

/// <summary>A new Purview item (a label, a policy, a rule…), made with the kind's New- cmdlet and the boxes the form filled in.</summary>
/// <param name="Parameters">Everything New- is sent, name included.</param>
/// <param name="GuardedValues">Places, people and names it names, which Test mode judges.</param>
public sealed record CreatePurviewObject(ChangeTarget Item, string KindTitle, string Cmdlet, JsonObject Parameters, string BodyText, IReadOnlyList<string> GuardedValues)
    : PurviewChange(Item)
{
    public override string Key => "create";
    public override string Label => $"New {KindTitle} \"{Item.Name}\"";
    public override string Before => "None";
    public override string After => BodyText;
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [Item, .. GuardedValues.Select(Named)];
}

/// <summary>Deletes a Purview item. The admin types its name.</summary>
public sealed record DeletePurviewObject(ChangeTarget Item, string KindTitle, string Cmdlet, string Detail) : PurviewChange(Item)
{
    public override string Key => "delete";
    public override string Label => $"{KindTitle} \"{Item.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Item.Name;
    public override bool IsNoChange => false;
}

/// <summary>An organization-wide Purview action (start applying information barriers). Test mode refuses it: it reaches everyone.</summary>
public sealed record RunPurviewAction(ChangeTarget Item, string Title, string Cmdlet, string Description) : PurviewChange(Item)
{
    public static ChangeTarget Organization(string what) => new("purviewobject", "organization:" + what, what, null);

    public override string Key => "action:" + Cmdlet;
    public override string Label => Title;
    public override string Before => "Not run";
    public override string After => Description;
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}
