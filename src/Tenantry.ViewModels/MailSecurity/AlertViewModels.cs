using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>An alert that fired, as a row: what, how bad, when, about whom; and Start, Resolve as…, or open it in Microsoft's portal.</summary>
public sealed partial class AlertRow(SecurityAlert alert, AlertsViewModel page) : ObservableObject
{
    public SecurityAlert Alert { get; } = alert;
    public string Title => Alert.Title;
    public string? Description => Alert.Description;
    public bool HasDescription => Alert.Description is { Length: > 0 };
    public string Meta => string.Join(" · ", new[]
    {
        SecurityAlert.SourceText(Alert.Source),
        Alert.Created is { } at ? Display.Relative(at, DateTimeOffset.Now) : null,
        Alert.Involved.Count > 0 ? string.Join(", ", Alert.Involved.Take(3)) + (Alert.Involved.Count > 3 ? $" and {Alert.Involved.Count - 3} more" : "") : null,
        Alert.AssignedTo is { } who ? $"assigned to {who}" : null,
    }.OfType<string>());
    public string SeverityText => Alert.Severity.Length == 0 ? "" : char.ToUpperInvariant(Alert.Severity[0]) + Alert.Severity[1..];
    public BadgeKind SeverityKind => Alert.Severity.ToLowerInvariant() switch { "high" => BadgeKind.Danger, "medium" => BadgeKind.Warning, "low" => BadgeKind.Info, _ => BadgeKind.Neutral };
    public string StatusText => Alert.IsResolved ? $"Resolved: {SecurityAlert.ClassificationText(Alert.Classification)}" : SecurityAlert.StatusText(Alert.Status);
    public BadgeKind StatusKind => Alert.IsResolved ? BadgeKind.Success : Alert.Status == "new" ? BadgeKind.Info : BadgeKind.Neutral;
    public bool CanWork => !Alert.IsResolved && page.CanEdit;
    public bool CanStart => CanWork && Alert.Status == "new";
    public bool HasLink => Alert.PortalUrl is not null;
    public string PendingText => page.Pending(this) is UpdateSecurityAlert u ? $"Pending: {char.ToLowerInvariant(u.After[0])}{u.After[1..]}" : "";
    public bool HasPending => page.Pending(this) is not null;

    [RelayCommand]
    void Open()
    {
        if (Alert.PortalUrl is { } url)
            Platform.OpenUrl(url);
    }

    [RelayCommand]
    void Start() => page.Stage(this, "inProgress", null, null);

    /// <summary>Resolve as a real threat, a false alarm, or expected activity.</summary>
    [RelayCommand]
    void Resolve(string classification) => page.Stage(this, "resolved", classification, classification switch
    {
        "falsePositive" => "notMalicious",
        "informationalExpectedActivity" => "confirmedActivity",
        _ => null,
    });

    internal void Refresh()
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
    }
}

/// <summary>An alert policy as a row: what it watches, on or off, and whether (and to whom) it emails.</summary>
public sealed partial class AlertPolicyRow : ObservableObject
{
    readonly AlertsViewModel _page;
    bool _loading = true;

    public AlertPolicyRow(AlertPolicy policy, AlertsViewModel page)
    {
        Policy = policy;
        _page = page;
        IsOn = !policy.Disabled;
        Emails = policy.Emails;
        RecipientsText = string.Join(Environment.NewLine, policy.NotifyUser);
        _loading = false;
    }

    public AlertPolicy Policy { get; }
    public string Name => Policy.Name;
    public string? Explanation => Policy.Explanation;
    public bool HasExplanation => Policy.Explanation is { Length: > 0 };
    public string SeverityText => Policy.Severity;
    public BadgeKind SeverityKind => Policy.Severity switch { "High" => BadgeKind.Danger, "Medium" => BadgeKind.Warning, "Low" => BadgeKind.Info, _ => BadgeKind.Neutral };
    public string NotifyText => $"Emails: {Policy.NotifyText}";
    public string Owner => Policy.BeingRemoved ? "Being removed" : Policy.IsSystem ? "Microsoft's" : "Yours";
    public bool CanEdit => _page.CanEdit && !Policy.BeingRemoved;

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool Emails { get; set; }

    /// <summary>The recipients editor is open.</summary>
    [ObservableProperty]
    public partial bool ShowRecipients { get; set; }

    /// <summary>Who's emailed, one per line ("TenantAdmins" means every global admin); takes effect on leaving the box.</summary>
    [ObservableProperty]
    public partial string RecipientsText { get; set; }

    partial void OnIsOnChanged(bool value) => Restage("Disabled", "Alert Policy", JsonValue.Create(!value), !Policy.Disabled ? "On" : "Off", value ? "On" : "Off", caution: !value,
        unchanged: value == !Policy.Disabled);

    partial void OnEmailsChanged(bool value) => Restage("NotificationEnabled", "Alert Emails", JsonValue.Create(value), Policy.Emails ? "On" : "Off", value ? "On" : "Off",
        caution: !value, unchanged: value == Policy.Emails);

    [RelayCommand]
    void CommitRecipients()
    {
        var who = RecipientsText.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Restage("NotifyUser", "Alert Recipients", new JsonArray([.. who.Select(w => (JsonNode?)JsonValue.Create(w))]), Policy.NotifyText,
            new AlertPolicy("", "", "", "", null, false, true, who, false).NotifyText, caution: false, unchanged: who.SequenceEqual(Policy.NotifyUser, StringComparer.OrdinalIgnoreCase));
    }

    void Restage(string parameter, string label, JsonNode value, string oldText, string newText, bool caution, bool unchanged)
    {
        if (_loading || !_page.CanEdit)
            return;
        var pipeline = _page.Pipeline!;
        var target = SetAlertPolicy.TargetFor(Policy.Name);
        if (pipeline.PendingFor(target.Id, "alert:" + parameter) is { } pending)
            pipeline.Unstage(pending);
        if (!unchanged)
            pipeline.Stage(new SetAlertPolicy(target, Policy.Identity, parameter, label, value, oldText, newText, caution));
    }
}

/// <summary>A category of alert policies, as a card.</summary>
public sealed record AlertPolicyGroup(string Title, IReadOnlyList<AlertPolicyRow> Policies);

/// <summary>
/// Alerts: what fired in the last 30 days (Graph alerts_v2), to start, resolve or open in Microsoft's
/// portal; and the alert policies that raise them (Security &amp; Compliance), on or off and who's emailed.
/// </summary>
public sealed partial class AlertsViewModel : LivePage
{
    readonly IAlertRecords? _records;

    public AlertsViewModel(IAlertRecords? records, ChangePipeline? pipeline, Availability canEdit) : base(pipeline, canEdit)
    {
        _records = records;
        Pipeline = pipeline;
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var a in Alerts)
                    a.Refresh();
            };
    }

    internal ChangePipeline? Pipeline { get; }

    public ObservableCollection<AlertRow> Alerts { get; } = [];

    public ObservableCollection<AlertPolicyGroup> Policies { get; } = [];

    /// <summary>Show resolved alerts too.</summary>
    [ObservableProperty]
    public partial bool ShowResolved { get; set; }

    partial void OnShowResolvedChanged(bool value) => Fill();

    IReadOnlyList<SecurityAlert> _alerts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlertsNote))]
    public partial string? AlertsNote { get; private set; }

    public bool HasAlertsNote => AlertsNote is not null;

    [ObservableProperty]
    public partial string AlertsHeading { get; private set; } = "Alerts";

    protected override bool Concerns(ChangeOp change) => change is SetAlertPolicy or UpdateSecurityAlert;

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_records is null)
            return;
        var policies = _records.GetAlertPoliciesAsync(cancellationToken);
        try
        {
            _alerts = await _records.GetAlertsAsync(cancellationToken);
            AlertsNote = null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _alerts = [];
            AlertsNote = $"Couldn't read the alerts: {Friendly.Message(e)}";
        }
        Fill();
        Policies.Clear();
        foreach (var g in (await policies).GroupBy(p => AlertPolicy.CategoryText(p.Category)).OrderBy(g => g.Key == "Threats" ? 0 : 1).ThenBy(g => g.Key))
            Policies.Add(new AlertPolicyGroup(g.Key, [.. g.Select(p => new AlertPolicyRow(p, this))]));
    }

    void Fill()
    {
        Alerts.Clear();
        foreach (var a in _alerts.Where(a => ShowResolved || !a.IsResolved))
            Alerts.Add(new AlertRow(a, this));
        var open = _alerts.Count(a => !a.IsResolved);
        AlertsHeading = open == 0 ? "Alerts: Nothing Open" : $"Alerts: {open} Open";
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Why the list is empty, when it is (and could be read).</summary>
    public string? EmptyText => Alerts.Count > 0 || HasAlertsNote ? null
        : _alerts.Count == 0 ? "No alerts in the last 30 days." : "Nothing open. Tick Show Resolved to see the rest.";

    public bool IsEmpty => EmptyText is not null;

    internal ChangeOp? Pending(AlertRow row) => Pipeline?.PendingFor(row.Alert.Id, "alert");

    internal void Stage(AlertRow row, string status, string? classification, string? determination)
    {
        if (!CanEdit)
            return;
        if (Pending(row) is { } pending)
        {
            Pipeline!.Unstage(pending);
            if (pending is UpdateSecurityAlert u && u.Status == status && u.Classification == classification)
                return;
        }
        Pipeline!.Stage(new UpdateSecurityAlert(UpdateSecurityAlert.TargetFor(row.Alert.Id, row.Alert.Title), row.Alert.Status, status, classification, determination, null));
    }
}
