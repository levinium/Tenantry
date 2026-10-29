using Tenantry.Core.Directory;

namespace Tenantry.Core.Changes;

/// <summary>
/// An eDiscovery change (8f), sent through Purview's cmdlets. The target is a case (kind
/// "ediscoverycase"), a search ("ediscoverysearch") or a hold ("casehold"), each judged by its name;
/// the mailboxes and sites a search or hold covers are judged too, so Test mode allows only
/// zz-tenantry-test- cases, searches and holds over test mailboxes and sites.
/// </summary>
public abstract record EdChange(ChangeTarget Item) : ChangeOp
{
    public const string CaseKind = "ediscoverycase";
    public const string SearchKind = "ediscoverysearch";
    public const string HoldKind = "casehold";

    public override ChangeTarget Target => Item;
    public override ChangeOp Since(ChangeOp earlier) => this;

    /// <summary>A place as a cmdlet takes it: a site by its URL (its ID here), a mailbox by its address.</summary>
    public static string Place(ChangeTarget place) =>
        place.Id == EDiscovery.All ? EDiscovery.All : place.Kind == "site" ? place.Id : place.Address ?? place.Name;

    /// <summary>"All mailboxes" (or sites): no test object, so Test mode refuses it.</summary>
    public static ChangeTarget Everywhere(string kind) => new(kind, EDiscovery.All, kind == "site" ? "All sites and OneDrives" : "All mailboxes", null);

    public static bool IsEverywhere(IReadOnlyList<ChangeTarget> places) => places.Count == 1 && places[0].Id == EDiscovery.All;

    protected static string Places(IReadOnlyList<ChangeTarget> mailboxes, IReadOnlyList<ChangeTarget> sites, bool publicFolders) =>
        EDiscovery.PlacesText([.. mailboxes.Select(m => m.Id == EDiscovery.All ? EDiscovery.All : m.Name)],
            [.. sites.Select(s => s.Id == EDiscovery.All ? EDiscovery.All : s.Name)], publicFolders);

    protected static string QueryText(string query) => query.Trim().Length == 0 ? "everything" : $"\"{query.Trim()}\"";
}

/// <summary>A new eDiscovery case.</summary>
public sealed record CreateCase(ChangeTarget Case, string? Description) : EdChange(Case)
{
    public override string Key => "create";
    public override string Label => $"New Case \"{Case.Name}\"";
    public override string Before => "None";
    public override string After => Description is { Length: > 0 } d ? $"Active; \"{d}\"" : "Active";
    public override bool IsNoChange => false;
}

/// <summary>Closes a case (its holds stop applying) or opens it again. Closing is a caution change.</summary>
public sealed record SetCaseClosed(ChangeTarget Case, bool Close, int Holds) : EdChange(Case)
{
    public override string Key => "status";
    public override string Label => $"Case \"{Case.Name}\"";
    public override string Before => Close ? "Active" : "Closed";
    public override string After => Close ? Holds == 0 ? "Closed" : $"Closed; its {Holds} {(Holds == 1 ? "hold stops" : "holds stop")} keeping content" : "Active";
    public override bool IsCaution => Close && Holds > 0;
    public override bool IsNoChange => false;
}

/// <summary>A case's description.</summary>
public sealed record SetCaseDescription(ChangeTarget Case, string? Old, string? New) : EdChange(Case)
{
    public override string Key => "description";
    public override string Label => $"Description of Case \"{Case.Name}\"";
    public override string Before => Show(Old);
    public override string After => Show(New);
    public override bool IsNoChange => (Old ?? "") == (New ?? "");
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetCaseDescription e ? this with { Old = e.Old } : this;
}

/// <summary>Deletes a case with its searches. Purview refuses while it still has holds. The admin types its name.</summary>
public sealed record DeleteCase(ChangeTarget Case, string Detail) : EdChange(Case)
{
    public override string Key => "delete";
    public override string Label => $"Case \"{Case.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted, with its searches";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Case.Name;
    public override bool IsNoChange => false;
}

/// <summary>Lets someone work on a case (see its searches and holds), or takes that away.</summary>
public sealed record SetCaseMember(ChangeTarget Case, ChangeTarget Member, bool Add) : EdChange(Case)
{
    public override string Key => "member:" + (Member.Address ?? Member.Name);
    public override string Label => $"{Member.Name} on Case \"{Case.Name}\"";
    public override string Before => Add ? "Not a member" : "Member";
    public override string After => Add ? "Member" : "Not a member";
    public override bool IsCaution => !Add;
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [Case, Member];
}

/// <summary>A new content search, in a case or not; it runs as soon as it's made, to count what it finds.</summary>
/// <param name="Mailboxes">People or shared mailboxes (by address), or <see cref="EdChange.Everywhere"/>.</param>
/// <param name="Sites">Sites and OneDrives (Id = URL), or <see cref="EdChange.Everywhere"/>.</param>
public sealed record CreateSearch(ChangeTarget Search, string? CaseName, string Query, IReadOnlyList<ChangeTarget> Mailboxes, IReadOnlyList<ChangeTarget> Sites,
    bool PublicFolders, string? Description) : EdChange(Search)
{
    public override string Key => "create";
    public override string Label => $"New Search \"{Search.Name}\"" + (CaseName is null ? "" : $" in \"{CaseName}\"");
    public override string Before => "None";
    public override string After => $"Looks for {QueryText(Query)} in {Places(Mailboxes, Sites, PublicFolders).ToLowerInvariant()}";
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [Search, .. Mailboxes, .. Sites];
}

/// <summary>Changes what a search looks for and where; it runs again afterwards.</summary>
public sealed record UpdateSearch(ChangeTarget Search, string OldQuery, string NewQuery, IReadOnlyList<ChangeTarget> OldMailboxes, IReadOnlyList<ChangeTarget> NewMailboxes,
    IReadOnlyList<ChangeTarget> OldSites, IReadOnlyList<ChangeTarget> NewSites, bool OldPublicFolders, bool NewPublicFolders) : EdChange(Search)
{
    public override string Key => "update";
    public override string Label => $"Search \"{Search.Name}\"";
    public override string Before => $"Looks for {QueryText(OldQuery)} in {Places(OldMailboxes, OldSites, OldPublicFolders).ToLowerInvariant()}";
    public override string After => $"Looks for {QueryText(NewQuery)} in {Places(NewMailboxes, NewSites, NewPublicFolders).ToLowerInvariant()}";

    public override bool IsNoChange => OldQuery.Trim() == NewQuery.Trim() && OldPublicFolders == NewPublicFolders && Same(OldMailboxes, NewMailboxes) && Same(OldSites, NewSites);

    static bool Same(IReadOnlyList<ChangeTarget> a, IReadOnlyList<ChangeTarget> b) =>
        a.Select(Place).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.Select(Place));

    public override IReadOnlyList<ChangeTarget> Guarded => [Search, .. NewMailboxes.Concat(OldMailboxes).Concat(NewSites).Concat(OldSites).DistinctBy(Place, StringComparer.OrdinalIgnoreCase)];

    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateSearch e
        ? this with { OldQuery = e.OldQuery, OldMailboxes = e.OldMailboxes, OldSites = e.OldSites, OldPublicFolders = e.OldPublicFolders }
        : this;
}

/// <summary>Deletes a search (only the search: nothing it found is touched).</summary>
public sealed record DeleteSearch(ChangeTarget Search, string Detail) : EdChange(Search)
{
    public override string Key => "delete";
    public override string Label => $"Search \"{Search.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted (what it found is untouched)";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

/// <summary>
/// Search and Delete: removes what a search found from the mailboxes it looked in, up to 10 items
/// per mailbox per run (Purview's limit; it's an incident tool). Soft delete leaves the items
/// recoverable until the deleted-items retention period ends; hard delete removes them for good
/// once the mailbox is next processed. Sites, OneDrives and unindexed items are never touched.
/// The admin types the search's name.
/// </summary>
public sealed record PurgeSearch(ChangeTarget Search, IReadOnlyList<ChangeTarget> Mailboxes, bool Hard, long Found) : EdChange(Search)
{
    public const int PerMailbox = 10;

    public override string Key => "purge";
    public override string Label => $"What Search \"{Search.Name}\" Found";
    public override string Before => $"{Found:N0} items in {Places(Mailboxes, [], false).ToLowerInvariant()}";
    public override string After => Hard
        ? $"Deleted for good (up to {PerMailbox} per mailbox)"
        : $"Deleted, recoverable for a while (up to {PerMailbox} per mailbox)";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Search.Name;
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [Search, .. Mailboxes];
}

/// <summary>
/// A record in the change log that a search's results were exported to this computer (nothing in
/// Microsoft 365 changes). Written as it happens, never staged.
/// </summary>
public sealed record SearchExported(ChangeTarget Search, string What) : ChangeOp
{
    public override ChangeTarget Target => Search;
    public override string Key => "exported";
    public override string Label => $"What Search \"{Search.Name}\" Found";
    public override string Before => "In Microsoft 365";
    public override string After => What;
    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>
/// A new hold in a case: content in the chosen places (all of it, or what matches the query) can't
/// be deleted for good while the hold is on. Made as a hold policy plus its rule.
/// </summary>
public sealed record CreateHold(ChangeTarget Hold, string CaseName, string Query, IReadOnlyList<ChangeTarget> Mailboxes, IReadOnlyList<ChangeTarget> Sites,
    bool PublicFolders) : EdChange(Hold)
{
    public override string Key => "create";
    public override string Label => $"New Hold \"{Hold.Name}\" in \"{CaseName}\"";
    public override string Before => "None";
    public override string After => $"Keeps {QueryText(Query)} in {Places(Mailboxes, Sites, PublicFolders).ToLowerInvariant()}";
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [Hold, .. Mailboxes, .. Sites];
}

/// <summary>Changes where a hold applies and what it keeps. Taking places off a hold is a caution change.</summary>
public sealed record UpdateHold(ChangeTarget Hold, string OldQuery, string NewQuery, IReadOnlyList<ChangeTarget> OldMailboxes, IReadOnlyList<ChangeTarget> NewMailboxes,
    IReadOnlyList<ChangeTarget> OldSites, IReadOnlyList<ChangeTarget> NewSites, bool OldPublicFolders, bool NewPublicFolders) : EdChange(Hold)
{
    public override string Key => "update";
    public override string Label => $"Hold \"{Hold.Name}\"";
    public override string Before => $"Keeps {QueryText(OldQuery)} in {Places(OldMailboxes, OldSites, OldPublicFolders).ToLowerInvariant()}";
    public override string After => $"Keeps {QueryText(NewQuery)} in {Places(NewMailboxes, NewSites, NewPublicFolders).ToLowerInvariant()}";

    static HashSet<string> Set(IEnumerable<ChangeTarget> places) => places.Select(Place).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Anything released: a place taken off, or a narrower query.</summary>
    public override bool IsCaution => !Set(NewMailboxes).IsSupersetOf(Set(OldMailboxes)) || !Set(NewSites).IsSupersetOf(Set(OldSites))
                                      || (OldPublicFolders && !NewPublicFolders) || OldQuery.Trim() != NewQuery.Trim();

    public override bool IsNoChange => OldQuery.Trim() == NewQuery.Trim() && OldPublicFolders == NewPublicFolders
                                       && Set(OldMailboxes).SetEquals(Set(NewMailboxes)) && Set(OldSites).SetEquals(Set(NewSites));

    public override IReadOnlyList<ChangeTarget> Guarded => [Hold, .. NewMailboxes.Concat(OldMailboxes).Concat(NewSites).Concat(OldSites).DistinctBy(Place, StringComparer.OrdinalIgnoreCase)];

    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateHold e
        ? this with { OldQuery = e.OldQuery, OldMailboxes = e.OldMailboxes, OldSites = e.OldSites, OldPublicFolders = e.OldPublicFolders }
        : this;
}

/// <summary>Turns a hold off (content it kept can be deleted again: a caution change) or back on.</summary>
public sealed record SetHoldEnabled(ChangeTarget Hold, bool Old, bool New) : EdChange(Hold)
{
    public override string Key => "enabled";
    public override string Label => $"Hold \"{Hold.Name}\"";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off: content it kept can be deleted again";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetHoldEnabled e ? this with { Old = e.Old } : this;
}

/// <summary>Deletes a hold: what it kept can be deleted again. The admin types its name.</summary>
public sealed record DeleteHold(ChangeTarget Hold, string Detail) : EdChange(Hold)
{
    public override string Key => "delete";
    public override string Label => $"Hold \"{Hold.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted: content it kept can be deleted again";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Hold.Name;
    public override bool IsNoChange => false;
}
