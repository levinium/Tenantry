using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Core.Changes;

/// <summary>
/// An Organization-area change (15c-15f), sent to Graph or the Teams admin API. The item is the
/// target (kind "orgobject", Id = kind key and item ID, Name = its name), so Test mode allows only
/// zz-tenantry-test- items: organization-wide settings (named for what they are) are always refused.
/// </summary>
public abstract record OrgChange(ChangeTarget Item, OrgApi Api, string Url) : ChangeOp
{
    public static ChangeTarget TargetFor(string kindKey, string id, string name) => new("orgobject", $"{kindKey}:{id}", name, null);

    public override ChangeTarget Target => Item;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>One setting: the value at <paramref name="Path"/> becomes <paramref name="Value"/>, sent as <paramref name="Write"/> says.</summary>
public sealed record SetOrgSetting(ChangeTarget Item, OrgApi Api, string Url, OrgWrite Write, string KindTitle, string Path, string SettingLabel,
    JsonNode? Value, string OldText, string NewText, bool Caution = false) : OrgChange(Item, Api, Url)
{
    public override string Key => Path;
    public override string Label => $"{KindTitle}: {SettingLabel}";
    public override string Before => OldText;
    public override string After => NewText;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => OldText == NewText;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetOrgSetting e ? this with { OldText = e.OldText } : this;
}

/// <summary>A new item, POSTed to its collection (or PUT to its own address, for a Teams policy).</summary>
public sealed record CreateOrgObject(ChangeTarget Item, OrgApi Api, string Url, string KindTitle, JsonObject Body, string BodyText, bool Put = false)
    : OrgChange(Item, Api, Url)
{
    public override string Key => "create";
    public override string Label => $"New {KindTitle} \"{Item.Name}\"";
    public override string Before => "None";
    public override string After => BodyText;
    public override bool IsNoChange => false;
}

/// <summary>Deletes an item. The admin types its name.</summary>
public sealed record DeleteOrgObject(ChangeTarget Item, OrgApi Api, string Url, string KindTitle, string Detail) : OrgChange(Item, Api, Url)
{
    public override string Key => "delete";
    public override string Label => $"{KindTitle} \"{Item.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Item.Name;
    public override bool IsNoChange => false;
}

/// <summary>An action on an item (verify, archive, restore), sent to a path under it.</summary>
public sealed record RunOrgAction(ChangeTarget Item, OrgApi Api, string Url, string Method, string Title, string Done, JsonObject? Body, bool Caution)
    : OrgChange(Item, Api, Url)
{
    public override string Key => "action:" + Title;
    public override string Label => $"{Title} \"{Item.Name}\"";
    public override string Before => "Not done";
    public override string After => Done;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => false;
}
