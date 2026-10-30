using System.Globalization;
using Tenantry.Core.Lists;

namespace Tenantry.Core.Directory;

/// <summary>What a retention policy is for: keeping or deleting content, publishing labels, or applying one.</summary>
public enum RetentionPolicyKind
{
    Retention,
    PublishLabels,
    AutoApplyLabel,
}

/// <summary>Where a retention policy applies, as Purview's location parameters name it.</summary>
public static class RetentionPlaces
{
    public const string Exchange = "Exchange";
    public const string SharePoint = "SharePoint";
    public const string OneDrive = "OneDrive";
    public const string Groups = "ModernGroup";
    public const string TeamsChannels = "TeamsChannel";
    public const string TeamsChats = "TeamsChat";
    public const string PublicFolders = "PublicFolder";

    /// <summary>Every kind, in the order the admin center shows them; Teams kinds can't share a policy with the others.</summary>
    public static IReadOnlyList<string> All { get; } = [Exchange, SharePoint, OneDrive, Groups, PublicFolders, TeamsChannels, TeamsChats];

    public static bool IsTeams(string kind) => kind is TeamsChannels or TeamsChats;

    public static string Label(string kind) => kind switch
    {
        Exchange => "Mailboxes",
        SharePoint => "SharePoint Sites",
        OneDrive => "OneDrive Accounts",
        Groups => "Microsoft 365 Group Mailboxes and Sites",
        PublicFolders => "Public Folders",
        TeamsChannels => "Teams Channel Messages",
        TeamsChats => "Teams Chats",
        _ => kind,
    };

    /// <summary>How many of a kind, in words: "1 SharePoint site", "3 mailboxes".</summary>
    public static string Count(int count, string kind)
    {
        var noun = Noun(kind);
        if (count != 1)
            return $"{count:N0} {noun}";
        return $"1 {(noun.EndsWith("xes", StringComparison.Ordinal) ? noun[..^2] : noun.EndsWith('s') ? noun[..^1] : noun)}";
    }

    /// <summary>The kind in a sentence ("mailboxes", "SharePoint sites").</summary>
    public static string Noun(string kind) => kind switch
    {
        Exchange => "mailboxes",
        SharePoint => "SharePoint sites",
        OneDrive => "OneDrive accounts",
        Groups => "Microsoft 365 groups",
        PublicFolders => "public folders",
        TeamsChannels => "Teams channel messages",
        TeamsChats => "Teams chats",
        _ => kind,
    };

    /// <summary>What a chosen place is, for pickers: a person's mailbox, a site, a group.</summary>
    public static string PickKind(string kind) => kind switch
    {
        SharePoint or OneDrive => "site",
        Groups or TeamsChannels => "group",
        _ => "mailbox",
    };
}

/// <summary>How long content is kept and what happens after.</summary>
/// <param name="Action">"Keep" (for the period, or forever), "KeepAndDelete" (then deleted) or "Delete" (deleted after the period).</param>
/// <param name="Days">The period, or null for forever (only with "Keep").</param>
/// <param name="BasedOn">"CreationAgeInDays" or "ModificationAgeInDays" (labels also "TaggedAgeInDays" and "EventAgeInDays").</param>
public sealed record RetentionSetting(string Action, int? Days, string BasedOn)
{
    public static string Period(int? days)
    {
        if (days is not { } d)
            return "forever";
        if (d % 365 == 0)
            return d == 365 ? "1 year" : $"{d / 365} years";
        if (d % 30 == 0 && d < 365)
            return d == 30 ? "1 month" : $"{d / 30} months";
        return d == 1 ? "1 day" : $"{d.ToString("N0", CultureInfo.CurrentCulture)} days";
    }

    public static string BasedOnText(string basedOn) => basedOn switch
    {
        "ModificationAgeInDays" => "it was last changed",
        "TaggedAgeInDays" => "it was labeled",
        "EventAgeInDays" => "an event happens",
        _ => "it was created",
    };

    /// <summary>"Keep for 7 years from when it was created, then delete".</summary>
    public string Text => Action switch
    {
        "Delete" => $"Delete {Period(Days)} after {BasedOnText(BasedOn)}",
        "KeepAndDelete" => $"Keep for {Period(Days)} after {BasedOnText(BasedOn)}, then delete",
        _ => Days is null ? "Keep forever" : $"Keep for {Period(Days)} after {BasedOnText(BasedOn)}",
    };

    /// <summary>"Unlimited" or the number of days, as Purview reads it back.</summary>
    public static int? ParseDays(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null;
}

/// <summary>A retention policy: where it applies and what it does (its rule).</summary>
/// <param name="Locations">Places by kind (<see cref="RetentionPlaces"/>); "All" for every place of that kind.</param>
/// <param name="Exceptions">Places left out of an "All".</param>
/// <param name="Scopes">Adaptive scopes it uses instead of chosen places.</param>
/// <param name="IsLocked">Preservation Lock: Purview won't let it be turned off, deleted or shortened.</param>
/// <param name="Setting">What a retention policy does; null for label policies.</param>
/// <param name="Labels">Labels a publishing policy offers, or the one an auto-apply policy applies.</param>
/// <param name="Query">An auto-apply policy's keyword query.</param>
/// <param name="Distribution">"Success", "Pending" or "Error": whether it's in place everywhere.</param>
public sealed record RetentionPolicy(string Id, string Name, string? Comment, bool Enabled, RetentionPolicyKind Kind,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Locations, IReadOnlyDictionary<string, IReadOnlyList<string>> Exceptions,
    IReadOnlyList<string> Scopes, bool IsLocked, RetentionSetting? Setting, IReadOnlyList<string> Labels, string? Query,
    string? Distribution, bool IsPendingDeletion, DateTimeOffset? Created, DateTimeOffset? Modified, string? ModifiedBy)
{
    public bool IsTeams => Locations.Keys.Any(RetentionPlaces.IsTeams);

    public string PlacesText
    {
        get
        {
            if (Scopes.Count > 0)
                return Scopes.Count == 1 ? $"Adaptive scope {Scopes[0]}" : $"{Scopes.Count} adaptive scopes";
            var parts = RetentionPlaces.All.Where(Locations.ContainsKey).Select(k =>
            {
                var places = Locations[k];
                var noun = RetentionPlaces.Noun(k);
                return EDiscovery.IsAll(places)
                    ? $"All {noun}" + (Exceptions.TryGetValue(k, out var except) && except.Count > 0 ? $" but {except.Count}" : "")
                    : RetentionPlaces.Count(places.Count, k);
            }).ToArray();
            return parts.Length == 0 ? "Nowhere yet" : string.Join(", ", parts);
        }
    }

    public string WhatText => Kind switch
    {
        RetentionPolicyKind.PublishLabels => Labels.Count == 0 ? "Publishes no labels" : $"Publishes {string.Join(", ", Labels)}",
        RetentionPolicyKind.AutoApplyLabel => Labels.Count == 0 ? "Applies a label" : $"Applies {Labels[0]}" + (Query is { Length: > 0 } q ? $" to content matching {q}" : " to everything"),
        _ => Setting?.Text ?? "No setting yet",
    };
}

/// <summary>A retention label: what it does to content it's applied to.</summary>
/// <param name="Action">"Keep", "Delete", "KeepAndDelete", or null for a label that only classifies.</param>
/// <param name="IsRecord">Content it's applied to becomes a record: it can't be edited or deleted while labeled.</param>
/// <param name="Regulatory">A regulatory record: not even an admin can remove the label (set in PowerShell only).</param>
/// <param name="Reviewers">Who reviews content before it's deleted (disposition review), as addresses.</param>
public sealed record RetentionLabel(string Id, string Name, string? Comment, string? Notes, string? Action, int? Days, string BasedOn, bool IsRecord, bool Regulatory,
    string? EventType, IReadOnlyList<string> Reviewers, bool IsPendingDeletion, DateTimeOffset? Created, DateTimeOffset? Modified, string? ModifiedBy)
{
    public string WhatText => Action is null ? "Only classifies (keeps and deletes nothing)"
        : new RetentionSetting(Action, Days, BasedOn).Text;

    public string KindText => Regulatory ? "Regulatory Record" : IsRecord ? "Record" : "Label";
}

/// <summary>A query-based set of users, groups or sites that policies can use instead of chosen places.</summary>
/// <param name="LocationType">"User", "Group" or "Site".</param>
public sealed record AdaptiveScope(string Id, string Name, string LocationType, string? Query, string? Comment, DateTimeOffset? Created, DateTimeOffset? Modified);

/// <summary>A kind of event that can start a label's retention period (an employee leaving, a contract ending).</summary>
public sealed record RetentionEventType(string Id, string Name, string? Comment, bool BuiltIn);

/// <summary>An event that happened: it starts the retention period for content labeled for its type.</summary>
public sealed record RetentionEvent(string Id, string Name, string EventType, DateTimeOffset? When, string? ExchangeQuery, string? SharePointQuery, string? Comment,
    DateTimeOffset? Created, string? CreatedBy);

/// <summary>Everything retention, read together; each part says if it can't be read here.</summary>
public sealed record RetentionDirectory(IReadOnlyList<RetentionPolicy> Policies, IReadOnlyList<RetentionLabel> Labels, IReadOnlyList<AdaptiveScope> Scopes,
    IReadOnlyList<RetentionEventType> EventTypes, IReadOnlyList<RetentionEvent> Events, string? ScopesUnavailable = null, string? EventsUnavailable = null);

/// <summary>Retention (8f-3), read live through Purview's cmdlets.</summary>
public interface IRetentionRecords
{
    Task<RetentionDirectory> GetRetentionAsync(CancellationToken cancellationToken = default);
}

/// <summary>The retention lists' fields.</summary>
public static class RetentionFields
{
    public const string Name = "name";
    public const string Kind = "kind";
    public const string Status = "status";
    public const string What = "what";
    public const string Places = "places";
    public const string Modified = "modified";
    public const string Created = "created";
    public const string Type = "type";

    public const string On = "On";
    public const string Off = "Off";

    public static string KindText(RetentionPolicyKind kind) => kind switch
    {
        RetentionPolicyKind.PublishLabels => "Publishes Labels",
        RetentionPolicyKind.AutoApplyLabel => "Applies a Label",
        _ => "Retention",
    };

    public static IReadOnlyList<Field<RetentionPolicy>> Policies { get; } =
    [
        new() { Key = Name, Label = "Policy", Kind = FieldKind.Text, Searchable = true, Text = p => p.Name },
        new() { Key = Kind, Label = "Kind", Kind = FieldKind.Choice, ChoiceOrder = ["Retention", "Publishes Labels", "Applies a Label"], Text = p => KindText(p.Kind) },
        new() { Key = Status, Label = "Status", Kind = FieldKind.Choice, ChoiceOrder = [On, Off], Text = p => p.Enabled ? On : Off },
        new() { Key = What, Label = "What It Does", Kind = FieldKind.Text, Searchable = true, Text = p => p.WhatText },
        new() { Key = Places, Label = "Where", Kind = FieldKind.Text, Searchable = true, Text = p => p.PlacesText },
        new() { Key = Modified, Label = "Last Changed", Kind = FieldKind.Date, Date = p => p.Modified },
    ];

    public static IReadOnlyList<Field<RetentionLabel>> Labels { get; } =
    [
        new() { Key = Name, Label = "Label", Kind = FieldKind.Text, Searchable = true, Text = l => l.Name },
        new() { Key = Kind, Label = "Kind", Kind = FieldKind.Choice, ChoiceOrder = ["Label", "Record", "Regulatory Record"], Text = l => l.KindText },
        new() { Key = What, Label = "What It Does", Kind = FieldKind.Text, Searchable = true, Text = l => l.WhatText },
        new() { Key = Modified, Label = "Last Changed", Kind = FieldKind.Date, Date = l => l.Modified },
    ];

    public static IReadOnlyList<Field<AdaptiveScope>> Scopes { get; } =
    [
        new() { Key = Name, Label = "Scope", Kind = FieldKind.Text, Searchable = true, Text = s => s.Name },
        new() { Key = Type, Label = "Of", Kind = FieldKind.Choice, ChoiceOrder = ["Users", "Groups", "Sites"], Text = s => ScopeType(s.LocationType) },
        new() { Key = What, Label = "Query", Kind = FieldKind.Text, Searchable = true, Text = s => s.Query },
        new() { Key = Created, Label = "Created", Kind = FieldKind.Date, Date = s => s.Created },
    ];

    public static string ScopeType(string locationType) => locationType switch
    {
        "Group" => "Groups",
        "Site" => "Sites",
        _ => "Users",
    };
}
