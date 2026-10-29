using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's retention: a few policies of each kind, labels (one a record, one waiting for an
/// event), event types and one event. Adaptive scopes need E5-level licensing, so the plain demo
/// says so and the premium one has a scope.
/// </summary>
public sealed partial class DemoUserRecords : IRetentionRecords
{
    List<RetentionPolicy>? _retentionPolicies;
    List<RetentionLabel>? _labels;
    List<AdaptiveScope>? _scopes;
    List<RetentionEventType>? _eventTypes;
    List<RetentionEvent>? _events;

    const int Year = 365;

    void EnsureRetention()
    {
        if (_retentionPolicies is not null)
            return;
        var now = DateTimeOffset.UtcNow;
        IReadOnlyDictionary<string, IReadOnlyList<string>> Where(string kind, params string[] places) =>
            new Dictionary<string, IReadOnlyList<string>> { [kind] = places };
        IReadOnlyDictionary<string, IReadOnlyList<string>> None() => new Dictionary<string, IReadOnlyList<string>>();
        _retentionPolicies =
        [
            new("pol-mail", "Keep mail seven years", "Everyone's mail, for audits.", true, RetentionPolicyKind.Retention, Where(RetentionPlaces.Exchange, EDiscovery.All), None(), [],
                false, new RetentionSetting("Keep", 7 * Year, "CreationAgeInDays"), [], null, "Success", false, now.AddDays(-400), now.AddDays(-90), "Avery Admin"),
            new("pol-chats", "Delete Teams chats after a year", null, true, RetentionPolicyKind.Retention, Where(RetentionPlaces.TeamsChats, EDiscovery.All), None(), [],
                false, new RetentionSetting("Delete", Year, "CreationAgeInDays"), [], null, "Success", false, now.AddDays(-200), now.AddDays(-200), "Avery Admin"),
            new("pol-publish", "Finance labels", "Labels the finance team can choose.", true, RetentionPolicyKind.PublishLabels,
                Where(RetentionPlaces.SharePoint, "https://northgate.sharepoint.example/sites/Finance"), None(), [], false, null, ["Financial record", "Invoice"], null,
                "Success", false, now.AddDays(-150), now.AddDays(-30), "Avery Admin"),
            new("pol-auto", "Label contracts", null, false, RetentionPolicyKind.AutoApplyLabel, Where(RetentionPlaces.SharePoint, EDiscovery.All), None(), [], false, null,
                ["Contract"], "contract AND (signed OR executed)", "Pending", false, now.AddDays(-3), now.AddDays(-3), "Avery Admin"),
        ];
        _eventTypes =
        [
            new("et-employee", "Employee activity", "Processes related to hiring, performance and leaving.", true),
            new("et-product", "Product lifetime", "Processes related to the last manufacturing date of a product.", true),
            new("et-contract", "Expiration or termination of contracts and agreements", "Processes related to contracts and agreements.", true),
        ];
        _labels =
        [
            new("lab-fin", "Financial record", "Ledgers, statements and audit papers.", null, "Keep", 7 * Year, "CreationAgeInDays", true, false, null, [], false,
                now.AddDays(-160), now.AddDays(-160), "Avery Admin"),
            new("lab-inv", "Invoice", null, null, "KeepAndDelete", 7 * Year, "CreationAgeInDays", false, false, null, ["records@northgate.example"], false,
                now.AddDays(-160), now.AddDays(-20), "Avery Admin"),
            new("lab-contract", "Contract", "Kept until the contract ends, then ten years.", null, "KeepAndDelete", 10 * Year, "EventAgeInDays", false, false,
                "Expiration or termination of contracts and agreements", [], false, now.AddDays(-5), now.AddDays(-5), "Avery Admin"),
            new("lab-ref", "Reference", "Marks reference material; keeps and deletes nothing.", null, null, null, "CreationAgeInDays", false, false, null, [], false,
                now.AddDays(-60), now.AddDays(-60), "Avery Admin"),
        ];
        _scopes = pim
            ? [new AdaptiveScope("scope-fin", "Finance department", "User", "Department -eq 'Finance'", "Everyone in finance, as they join and leave.", now.AddDays(-40), now.AddDays(-40))]
            : [];
        _events =
        [
            new RetentionEvent("ev-1", "Cleaning contract ended", "Expiration or termination of contracts and agreements", now.AddDays(-30), null, "ContractId:4471", null,
                now.AddDays(-29), "Avery Admin"),
        ];
    }

    public Task<RetentionDirectory> GetRetentionAsync(CancellationToken cancellationToken = default)
    {
        EnsureRetention();
        return Task.FromResult(new RetentionDirectory([.. _retentionPolicies!], [.. _labels!], [.. _scopes!], [.. _eventTypes!], [.. _events!],
            pim ? null : "This needs Microsoft 365 E5 (or E5 Compliance) licensing."));
    }

    static IReadOnlyDictionary<string, IReadOnlyList<string>> PlacesOf(RetentionPolicyDraft draft) =>
        draft.Places.Where(p => p.Value.Count > 0).ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[.. p.Value.Select(EdChange.Place)]);

    /// <summary>Follows retention changes in the demo's lists; null for other changes.</summary>
    public ApplyResult? ApplyRetention(ChangeOp change)
    {
        if (change is not RetentionChange)
            return null;
        EnsureRetention();
        var now = DateTimeOffset.UtcNow;
        int PolicyAt(string name) => _retentionPolicies!.FindIndex(p => p.Name == name);
        int LabelAt(string name) => _labels!.FindIndex(l => l.Name == name);
        switch (change)
        {
            case CreateRetentionPolicy c:
                if (PolicyAt(c.Policy.Name) >= 0)
                    return new ApplyResult(ChangeOutcome.Failed, "A policy with that name already exists.");
                _retentionPolicies!.Add(new RetentionPolicy($"pol-{Guid.NewGuid():N}", c.Policy.Name, c.Draft.Comment, true, c.Draft.Kind, PlacesOf(c.Draft),
                    new Dictionary<string, IReadOnlyList<string>>(), c.Draft.Scopes, false, c.Draft.Setting, c.Draft.Labels, c.Draft.Query, "Pending", false, now, now, "Avery Admin"));
                break;
            case UpdateRetentionPolicy u when PolicyAt(u.Policy.Name) is >= 0 and var at:
                _retentionPolicies![at] = _retentionPolicies[at] with
                {
                    Comment = u.New.Comment, Locations = PlacesOf(u.New), Scopes = u.New.Scopes, Setting = u.New.Setting, Labels = u.New.Labels, Query = u.New.Query,
                    Modified = now, Distribution = "Pending",
                };
                break;
            case SetRetentionPolicyEnabled e when PolicyAt(e.Policy.Name) is >= 0 and var at:
                _retentionPolicies![at] = _retentionPolicies[at] with { Enabled = e.New, Modified = now };
                break;
            case DeleteRetentionPolicy d:
                _retentionPolicies!.RemoveAll(p => p.Name == d.Policy.Name);
                break;
            case CreateRetentionLabel c:
                if (LabelAt(c.Tag.Name) >= 0)
                    return new ApplyResult(ChangeOutcome.Failed, "A label with that name already exists.");
                _labels!.Add(new RetentionLabel($"lab-{Guid.NewGuid():N}", c.Tag.Name, c.Draft.Comment, c.Draft.Notes, c.Draft.Action, c.Draft.Days, c.Draft.BasedOn,
                    c.Draft.IsRecord, false, c.Draft.EventType, c.Draft.Reviewers, false, now, now, "Avery Admin"));
                break;
            case UpdateRetentionLabel u when LabelAt(u.Tag.Name) is >= 0 and var at:
                _labels![at] = _labels[at] with { Comment = u.New.Comment, Notes = u.New.Notes, Days = u.New.Days, Reviewers = u.New.Reviewers, Modified = now };
                break;
            case DeleteRetentionLabel d:
                if (_retentionPolicies!.Any(p => p.Labels.Contains(d.Tag.Name)))
                    return new ApplyResult(ChangeOutcome.Failed, "A policy still publishes or applies this label; take it out of the policy first.");
                _labels!.RemoveAll(l => l.Name == d.Tag.Name);
                break;
            case CreateAdaptiveScope c:
                if (!pim)
                    return new ApplyResult(ChangeOutcome.Failed, "This organization isn't licensed for this (adaptive scopes need Microsoft 365 E5 or E5 Compliance).");
                _scopes!.Add(new AdaptiveScope($"scope-{Guid.NewGuid():N}", c.Scope.Name, c.LocationType, c.Query, c.Comment, now, now));
                break;
            case UpdateAdaptiveScope u when _scopes!.FindIndex(s => s.Name == u.Scope.Name) is >= 0 and var at:
                _scopes[at] = _scopes[at] with { Query = u.NewQuery, Comment = u.NewComment, Modified = now };
                break;
            case DeleteAdaptiveScope d:
                _scopes!.RemoveAll(s => s.Name == d.Scope.Name);
                break;
            case CreateRetentionEventType c:
                _eventTypes!.Add(new RetentionEventType($"et-{Guid.NewGuid():N}", c.EventType.Name, c.Comment, false));
                break;
            case DeleteRetentionEventType d:
                _eventTypes!.RemoveAll(t => t.Name == d.EventType.Name);
                break;
            case CreateRetentionEvent c:
                _events!.Add(new RetentionEvent($"ev-{Guid.NewGuid():N}", c.Event.Name, c.EventType, c.When, c.ExchangeQuery, c.SharePointQuery, c.Comment, now, "Avery Admin"));
                break;
            default:
                return new ApplyResult(ChangeOutcome.Failed, "That isn't in the demo any more.");
        }
        return new ApplyResult(ChangeOutcome.Applied);
    }
}
