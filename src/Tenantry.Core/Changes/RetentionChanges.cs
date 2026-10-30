using Tenantry.Core.Directory;

namespace Tenantry.Core.Changes;

/// <summary>
/// A retention change (8f-3), sent through Purview's cmdlets with the search-only sign-in. The
/// target is a policy (kind "retentionpolicy"), label ("retentionlabel"), adaptive scope
/// ("adaptivescope"), event type ("retentioneventtype") or event ("retentionevent"), each judged by
/// its name; a policy's places are judged too.
/// </summary>
public abstract record RetentionChange(ChangeTarget Item) : ChangeOp
{
    public const string PolicyKind = "retentionpolicy";
    public const string LabelKind = "retentionlabel";
    public const string ScopeKind = "adaptivescope";
    public const string EventTypeKind = "retentioneventtype";
    public const string EventKind = "retentionevent";

    public override ChangeTarget Target => Item;
    public override ChangeOp Since(ChangeOp earlier) => this;
}

/// <summary>Everything editable about a retention policy, compared whole between before and after.</summary>
/// <param name="Places">Chosen places by kind (<see cref="RetentionPlaces"/>); a kind's list may be <see cref="EdChange.Everywhere"/>.</param>
/// <param name="Scopes">Adaptive scope names, used instead of chosen places.</param>
/// <param name="Setting">For retention policies.</param>
/// <param name="Labels">Labels a publishing policy offers, or the one an auto-apply policy applies.</param>
/// <param name="Query">An auto-apply policy's keyword query.</param>
public sealed record RetentionPolicyDraft(RetentionPolicyKind Kind, string? Comment, IReadOnlyDictionary<string, IReadOnlyList<ChangeTarget>> Places,
    IReadOnlyList<string> Scopes, RetentionSetting? Setting, IReadOnlyList<string> Labels, string? Query)
{
    public IEnumerable<ChangeTarget> AllPlaces => Places.Values.SelectMany(p => p);

    static HashSet<string> Set(IEnumerable<string> values) => values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool SamePlaces(RetentionPolicyDraft other) =>
        RetentionPlaces.All.All(k => Set((Places.GetValueOrDefault(k) ?? []).Select(EdChange.Place)).SetEquals((other.Places.GetValueOrDefault(k) ?? []).Select(EdChange.Place)))
        && Set(Scopes).SetEquals(other.Scopes);

    public string PlacesText => Scopes.Count > 0
        ? $"adaptive scopes {string.Join(", ", Scopes)}"
        : string.Join(", ", RetentionPlaces.All.Where(k => Places.GetValueOrDefault(k) is { Count: > 0 }).Select(k =>
            EdChange.IsEverywhere(Places[k]) ? $"all {RetentionPlaces.Noun(k)}" : RetentionPlaces.Count(Places[k].Count, k))) is { Length: > 0 } text
            ? text : "nowhere";

    public string Summary => Kind switch
    {
        RetentionPolicyKind.PublishLabels => $"Publishes {(Labels.Count == 0 ? "no labels" : string.Join(", ", Labels))} to {PlacesText}",
        RetentionPolicyKind.AutoApplyLabel => $"Applies {Labels.FirstOrDefault() ?? "a label"}{(Query is { Length: > 0 } q ? $" to content matching {q}" : "")} in {PlacesText}",
        _ => $"{Setting?.Text ?? "No setting"} in {PlacesText}",
    } + (Comment is { Length: > 0 } c ? $" (\"{c}\")" : "");
}

/// <summary>A new retention policy, with its rule.</summary>
public sealed record CreateRetentionPolicy(ChangeTarget Policy, RetentionPolicyDraft Draft) : RetentionChange(Policy)
{
    public override string Key => "create";
    public override string Label => $"New Retention Policy \"{Policy.Name}\"";
    public override string Before => "None";
    public override string After => Draft.Summary;
    public override bool IsCaution => Draft.Setting?.Action is "Delete" or "KeepAndDelete";
    public override bool IsNoChange => false;
    public override IReadOnlyList<ChangeTarget> Guarded => [Policy, .. Draft.AllPlaces];
}

/// <summary>Changes a retention policy's places, setting, labels or description.</summary>
public sealed record UpdateRetentionPolicy(ChangeTarget Policy, RetentionPolicyDraft Old, RetentionPolicyDraft New) : RetentionChange(Policy)
{
    public override string Key => "update";
    public override string Label => $"Retention Policy \"{Policy.Name}\"";
    public override string Before => Old.Summary;
    public override string After => New.Summary;

    /// <summary>Anything that could let content be deleted sooner, or be kept less.</summary>
    public override bool IsCaution => !New.SamePlaces(Old) || New.Setting != Old.Setting || New.Query != Old.Query
                                      || !New.Labels.ToHashSet(StringComparer.OrdinalIgnoreCase).IsSupersetOf(Old.Labels);

    public override bool IsNoChange => Old.Summary == New.Summary && New.SamePlaces(Old) && Old.Labels.SequenceEqual(New.Labels, StringComparer.OrdinalIgnoreCase);

    public override IReadOnlyList<ChangeTarget> Guarded => [Policy, .. Old.AllPlaces.Concat(New.AllPlaces).DistinctBy(EdChange.Place, StringComparer.OrdinalIgnoreCase)];

    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateRetentionPolicy e ? this with { Old = e.Old } : this;
}

/// <summary>Turns a policy off (its content is no longer kept or deleted by it: a caution change) or on.</summary>
public sealed record SetRetentionPolicyEnabled(ChangeTarget Policy, bool Old, bool New) : RetentionChange(Policy)
{
    public override string Key => "enabled";
    public override string Label => $"Retention Policy \"{Policy.Name}\"";
    public override string Before => Old ? "On" : "Off";
    public override string After => New ? "On" : "Off";
    public override bool IsCaution => Old && !New;
    public override bool IsNoChange => Old == New;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetRetentionPolicyEnabled e ? this with { Old = e.Old } : this;
}

/// <summary>Deletes a retention policy. The admin types its name.</summary>
public sealed record DeleteRetentionPolicy(ChangeTarget Policy, string Detail) : RetentionChange(Policy)
{
    public override string Key => "delete";
    public override string Label => $"Retention Policy \"{Policy.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Policy.Name;
    public override bool IsNoChange => false;
}

/// <summary>Everything a retention label holds.</summary>
/// <param name="Action">"Keep", "Delete", "KeepAndDelete", or null for a label that only classifies.</param>
/// <param name="Reviewers">Disposition reviewers' addresses.</param>
public sealed record RetentionLabelDraft(string? Comment, string? Notes, string? Action, int? Days, string BasedOn, bool IsRecord, string? EventType,
    IReadOnlyList<string> Reviewers)
{
    public string Summary => (Action is null ? "Only classifies" : new RetentionSetting(Action, Days, BasedOn).Text)
                             + (IsRecord ? "; marks content as a record" : "")
                             + (EventType is { } e ? $"; starts on {e} events" : "")
                             + (Reviewers.Count > 0 ? $"; reviewed by {string.Join(", ", Reviewers)} before deletion" : "");
}

/// <summary>A new retention label.</summary>
public sealed record CreateRetentionLabel(ChangeTarget Tag, RetentionLabelDraft Draft) : RetentionChange(Tag)
{
    public override string Key => "create";
    public override string Label => $"New Retention Label \"{Tag.Name}\"";
    public override string Before => "None";
    public override string After => Draft.Summary;
    public override bool IsNoChange => false;
}

/// <summary>
/// Changes a label. Purview lets a label's description, notes, period and reviewers change, but
/// not what it does or whether it marks records once made.
/// </summary>
public sealed record UpdateRetentionLabel(ChangeTarget Tag, RetentionLabelDraft Old, RetentionLabelDraft New) : RetentionChange(Tag)
{
    public override string Key => "update";
    public override string Label => $"Retention Label \"{Tag.Name}\"";
    public override string Before => Old.Summary + (Old.Comment is { Length: > 0 } c ? $" (\"{c}\")" : "");
    public override string After => New.Summary + (New.Comment is { Length: > 0 } c ? $" (\"{c}\")" : "");
    public override bool IsCaution => New.Days < Old.Days || (Old.Reviewers.Count > 0 && New.Reviewers.Count == 0);
    public override bool IsNoChange => Before == After && (Old.Notes ?? "") == (New.Notes ?? "");
    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateRetentionLabel e ? this with { Old = e.Old } : this;
}

/// <summary>Deletes a label (Purview refuses while a policy publishes or applies it). The admin types its name.</summary>
public sealed record DeleteRetentionLabel(ChangeTarget Tag, string Detail) : RetentionChange(Tag)
{
    public override string Key => "delete";
    public override string Label => $"Retention Label \"{Tag.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override string? ConfirmPhrase => Tag.Name;
    public override bool IsNoChange => false;
}

/// <summary>A new adaptive scope (Microsoft 365 E5-level licensing).</summary>
/// <param name="LocationType">"User", "Group" or "Site".</param>
/// <param name="Query">An OPATH filter for users and groups, a KQL query for sites.</param>
public sealed record CreateAdaptiveScope(ChangeTarget Scope, string LocationType, string Query, string? Comment) : RetentionChange(Scope)
{
    public override string Key => "create";
    public override string Label => $"New Adaptive Scope \"{Scope.Name}\"";
    public override string Before => "None";
    public override string After => $"{RetentionFields.ScopeType(LocationType)} where {Query}";
    public override bool IsNoChange => false;
}

/// <summary>Changes which users, groups or sites a scope covers; policies using it follow.</summary>
public sealed record UpdateAdaptiveScope(ChangeTarget Scope, string OldQuery, string NewQuery, string? OldComment, string? NewComment) : RetentionChange(Scope)
{
    public override string Key => "update";
    public override string Label => $"Adaptive Scope \"{Scope.Name}\"";
    public override string Before => OldQuery + (OldComment is { Length: > 0 } c ? $" (\"{c}\")" : "");
    public override string After => NewQuery + (NewComment is { Length: > 0 } c ? $" (\"{c}\")" : "");
    public override bool IsCaution => OldQuery != NewQuery;
    public override bool IsNoChange => Before == After;
    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateAdaptiveScope e ? this with { OldQuery = e.OldQuery, OldComment = e.OldComment } : this;
}

/// <summary>Deletes an adaptive scope (Purview refuses while a policy uses it).</summary>
public sealed record DeleteAdaptiveScope(ChangeTarget Scope, string Detail) : RetentionChange(Scope)
{
    public override string Key => "delete";
    public override string Label => $"Adaptive Scope \"{Scope.Name}\"";
    public override string Before => Detail;
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

/// <summary>A new kind of event labels can wait for.</summary>
public sealed record CreateRetentionEventType(ChangeTarget EventType, string? Comment) : RetentionChange(EventType)
{
    public override string Key => "create";
    public override string Label => $"New Event Type \"{EventType.Name}\"";
    public override string Before => "None";
    public override string After => Comment is { Length: > 0 } c ? c : "Added";
    public override bool IsNoChange => false;
}

/// <summary>Deletes an event type (Purview refuses while a label uses it).</summary>
public sealed record DeleteRetentionEventType(ChangeTarget EventType) : RetentionChange(EventType)
{
    public override string Key => "delete";
    public override string Label => $"Event Type \"{EventType.Name}\"";
    public override string Before => "Exists";
    public override string After => "Deleted";
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}

/// <summary>
/// Records that an event happened: content labeled for its type (and matching its asset IDs)
/// starts its retention period, after which it may be deleted. Can't be undone, so a caution change.
/// </summary>
public sealed record CreateRetentionEvent(ChangeTarget Event, string EventType, DateTimeOffset When, string? ExchangeQuery, string? SharePointQuery, string? Comment)
    : RetentionChange(Event)
{
    public override string Key => "create";
    public override string Label => $"New Event \"{Event.Name}\"";
    public override string Before => "None";
    public override string After => $"{EventType} on {When.ToLocalTime():d MMM yyyy}" +
                                    (ExchangeQuery is { Length: > 0 } || SharePointQuery is { Length: > 0 } ? " for the matching content" : " for all its labeled content");
    public override bool IsCaution => true;
    public override bool IsNoChange => false;
}
