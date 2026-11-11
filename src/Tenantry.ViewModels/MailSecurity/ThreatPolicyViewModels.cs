using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>
/// One threat policy as a card: who it covers and in what order (its rule), on or off, every
/// setting with Microsoft's recommendation beside it, and Remove. The default and Microsoft's
/// preset and built-in policies show their settings; only the default's can change.
/// </summary>
public sealed partial class ThreatPolicyCard : ObservableObject
{
    readonly ThreatPoliciesViewModel _page;

    public ThreatPolicyCard(ThreatPolicy policy, IReadOnlyList<SettingDef> settings, SettingContext context, ThreatPoliciesViewModel page)
    {
        Policy = policy;
        _page = page;
        var owner = ThreatPolicyChange.TargetFor(policy.Kind.Key, policy.Name);
        // Preset and built-in policies are Microsoft's: their rows read, but don't change.
        var settingContext = policy.ReadOnly ? context with { CanEdit = Availability.No("Microsoft keeps this policy; change the preset in Microsoft's portal.") } : context;
        Groups = SettingCards.Build(settings, d => d.Read(policy.Policy), settingContext, owner, policy.Name);
        if (policy.Rule is { } rule && !policy.ReadOnly)
        {
            var kind = policy.Kind;
            var (people, groups, domains) = kind.Who;
            var who = kind.ScopesSenders ? "senders" : "recipients";
            SettingDef R(string key, string label, string parameter, string help, SettingKind type = SettingKind.List, int min = 0, int max = int.MaxValue) =>
                new("Who and When", key, label, help, kind.GetRule, kind.SetRule, parameter, type, Min: min, Max: max);
            Rule = SettingCards.Build(
            [
                R("people", "People", people, $"These {who}, by address."),
                R("groups", "Groups", groups, $"{char.ToUpperInvariant(who[0])}{who[1..]} in these groups."),
                R("domains", "Domains", domains, $"{char.ToUpperInvariant(who[0])}{who[1..]} at these domains."),
                R("xpeople", "Except These People", "ExceptIf" + people, ""),
                R("xgroups", "Except These Groups", "ExceptIf" + groups, ""),
                R("xdomains", "Except These Domains", "ExceptIf" + domains, ""),
                R("priority", "Order", "Priority", "0 runs first. Each person gets the first policy that covers them.", SettingKind.Number, max: 1000),
            ], d => d.Read(rule), context, owner, policy.Name);
        }
    }

    public ThreatPolicy Policy { get; }

    public string Name => Policy.IsDefault ? $"{Policy.Name} (Default)" : Policy.Name;

    public string AppliesTo => Policy.AppliesTo;

    public string OrderText => Policy.IsDefault ? "Runs last" : Policy.ReadOnly ? "Microsoft's" : Policy.Priority is { } p ? $"Order {p}" : "";

    public IReadOnlyList<SettingGroup> Groups { get; }

    public IReadOnlyList<SettingGroup> Rule { get; } = [];

    public bool HasRule => Rule.Count > 0;

    public bool IsReadOnly => Policy.ReadOnly;

    public string ReadOnlyText => "Microsoft keeps this policy (a preset or built-in protection); its settings can't be changed one by one.";

    /// <summary>Settings that differ from Microsoft's recommendation, for the card's line.</summary>
    public int DifferCount => Groups.SelectMany(g => g.Rows).Count(r => r.HasRecommendedNote);

    public string SummaryText => DifferCount == 0 ? "Matches Microsoft's recommendations" : $"{DifferCount} {(DifferCount == 1 ? "setting differs" : "settings differ")} from Microsoft's recommendations";

    public BadgeKind SummaryKind => DifferCount == 0 ? BadgeKind.Success : BadgeKind.Warning;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool CanSwitch => !Policy.IsDefault && !Policy.ReadOnly && Policy.Rule is not null && _page.CanEdit;

    public string StateText => Policy.Enabled ? "On" : "Off";

    public BadgeKind StateKind => Policy.Enabled ? BadgeKind.Success : BadgeKind.Neutral;

    ChangeOp? Pending(string key) => _page.Pipeline?.PendingFor(ThreatPolicyChange.TargetFor(Policy.Kind.Key, Policy.Name).Id, key);

    public string SwitchText => Pending("state") is not null ? "Undo" : Policy.Enabled ? "Turn Off" : "Turn On";

    public string RemoveText => Pending("policy") is RemoveThreatPolicy ? "Undo Remove" : "Remove";

    public bool CanRemove => !Policy.IsDefault && !Policy.ReadOnly && _page.CanEdit;

    [RelayCommand]
    void Switch() => _page.Toggle(this, "state", () => new SetThreatRuleEnabled(ThreatPolicyChange.TargetFor(Policy.Kind.Key, Policy.Name), Policy.Kind.PolicyNoun,
        Policy.Kind.RuleNoun, Policy.Kind.Title, Policy.Enabled, !Policy.Enabled));

    [RelayCommand]
    void Remove() => _page.Toggle(this, "policy", () => new RemoveThreatPolicy(ThreatPolicyChange.TargetFor(Policy.Kind.Key, Policy.Name), Policy.Kind.PolicyNoun,
        Policy.Kind.RuleNoun, Policy.Kind.Title, Policy.Rule is not null));

    internal void Refresh()
    {
        OnPropertyChanged(nameof(SwitchText));
        OnPropertyChanged(nameof(RemoveText));
    }
}

/// <summary>One kind of threat policy (anti-spam, anti-phishing…): its policies as cards, and a new one.</summary>
public sealed partial class ThreatPoliciesViewModel : LivePage
{
    readonly IThreatPolicyRecords? _records;

    public ThreatPoliciesViewModel(ThreatPolicyKind kind, IThreatPolicyRecords? records, ChangePipeline? pipeline, Availability canEdit) : base(pipeline, canEdit)
    {
        Kind = kind;
        _records = records;
        Pipeline = pipeline;
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var c in Cards)
                    c.Refresh();
            };
    }

    public ThreatPolicyKind Kind { get; }

    internal ChangePipeline? Pipeline { get; }

    public string Title => Kind.Title;

    public string About => Kind.About;

    public ObservableCollection<ThreatPolicyCard> Cards { get; } = [];

    /// <summary>The tenant hasn't Defender for Office 365, which this kind needs.</summary>
    [ObservableProperty]
    public partial bool NotLicensed { get; private set; }

    public string NotLicensedText => $"{Kind.Title} needs Microsoft Defender for Office 365 Plan 1 (included in Microsoft 365 Business Premium and E5). This tenant doesn't have it.";

    protected override bool Concerns(ChangeOp change) => change is ThreatPolicyChange tp && tp.PolicyNoun == Kind.PolicyNoun
        || change is SetExchangeSetting s && (s.SetCmdlet == Kind.SetPolicy || s.SetCmdlet == Kind.SetRule);

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_records is null)
            return;
        var policies = await _records.GetThreatPoliciesAsync(Kind, cancellationToken);
        NotLicensed = policies is null;
        var quarantine = (await _records.GetQuarantinePoliciesAsync(cancellationToken)).Select(q => q.Name).ToArray();
        var settings = ThreatPolicyCatalog.Settings(Kind, quarantine);
        var open = Cards.Where(c => c.IsExpanded).Select(c => c.Policy.Name).ToHashSet();
        Cards.Clear();
        foreach (var p in policies ?? [])
            Cards.Add(new ThreatPolicyCard(p, settings, Context, this) { IsExpanded = open.Contains(p.Name) });
        if (Cards.Count == 1)
            Cards[0].IsExpanded = true;
    }

    internal void Toggle(ThreatPolicyCard card, string key, Func<ChangeOp> make)
    {
        if (!CanEdit)
            return;
        if (Pipeline!.PendingFor(ThreatPolicyChange.TargetFor(Kind.Key, card.Policy.Name).Id, key) is { } pending)
            Pipeline.Unstage(pending);
        else
            Pipeline.Stage(make());
    }

    // ---- A new policy ------------------------------------------------------------------------

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial string NewPeople { get; set; } = "";

    [ObservableProperty]
    public partial string NewGroups { get; set; } = "";

    [ObservableProperty]
    public partial string NewDomains { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddProblem))]
    public partial string? AddProblem { get; private set; }

    public bool HasAddProblem => AddProblem is not null;

    public string WhoLabel => Kind.ScopesSenders ? "Senders it covers" : "People it covers";

    static IReadOnlyList<string> Split(string text) =>
        [.. text.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    [RelayCommand]
    void Add()
    {
        var name = NewName.Trim();
        var (people, groups, domains) = (Split(NewPeople), Split(NewGroups), Split(NewDomains));
        AddProblem = name.Length == 0 ? "Give the policy a name."
            : Cards.Any(c => c.Policy.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "A policy has that name already."
            : people.Count + groups.Count + domains.Count == 0 ? $"Say who it covers: people, groups or domains. (Everyone else keeps the policies they have.)"
            : null;
        if (AddProblem is not null || !CanEdit)
            return;
        var (p, g, d) = Kind.Who;
        Pipeline!.Stage(new CreateThreatPolicy(ThreatPolicyChange.TargetFor(Kind.Key, name), Kind.PolicyNoun, Kind.RuleNoun, Kind.Title, [p, g, d], people, groups, domains));
        NewName = NewPeople = NewGroups = NewDomains = "";
    }
}

// ---- Quarantine policies ---------------------------------------------------------------------

/// <summary>A quarantine policy's row: what people can do, notifications, and (for a custom one) a preset to choose.</summary>
public sealed partial class QuarantinePolicyRow : ObservableObject
{
    readonly QuarantinePoliciesViewModel _page;
    readonly int _value;
    bool _loading = true;

    public QuarantinePolicyRow(QuarantinePolicy policy, QuarantinePoliciesViewModel page, SettingContext context)
    {
        Policy = policy;
        _page = page;
        _value = QuarantinePermissions.Value(policy.Permissions);
        var owner = new ChangeTarget("quarantinepolicy", policy.Name, policy.Name, null);
        Notify = new SettingRow(new SettingDef("", "esn", "Email People about Their Quarantined Mail",
            "A regular message listing what's held, from which they can act as the policy allows.", "Get-QuarantinePolicy", "Set-QuarantinePolicy", "ESNEnabled"),
            JsonValue.Create(policy.Notifies), policy.BuiltIn ? context with { CanEdit = Availability.No("Microsoft's built-in policy") } : context, owner, policy.Name);
        Preset = Presets.FirstOrDefault(p => p.Value == _value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _loading = false;
    }

    public QuarantinePolicy Policy { get; }

    public string Name => Policy.BuiltIn ? Policy.Name switch
    {
        "AdminOnlyAccessPolicy" => "Admins Only (AdminOnlyAccessPolicy)",
        "DefaultFullAccessPolicy" => "People Can Release (DefaultFullAccessPolicy)",
        "DefaultFullAccessWithNotificationPolicy" => "People Can Release, and Are Told (DefaultFullAccessWithNotificationPolicy)",
        _ => Policy.Name,
    } : Policy.Name;

    public string What => Policy.Describe() + ".";

    public string Tag => Policy.BuiltIn ? "Microsoft's" : "Yours";

    public SettingRow Notify { get; }

    public bool CanChangePermissions => !Policy.BuiltIn && _page.CanEdit;

    public static IReadOnlyList<SettingChoice> Presets { get; } = [.. QuarantinePermissions.Presets.Select(p => new SettingChoice(p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), p.Label))];

    [ObservableProperty]
    public partial SettingChoice? Preset { get; set; }

    partial void OnPresetChanged(SettingChoice? value)
    {
        if (_loading || value is null || !CanChangePermissions)
            return;
        var pipeline = _page.Pipeline!;
        if (pipeline.PendingFor(Policy.Name, "permissions") is { } pending)
            pipeline.Unstage(pending);
        var next = int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture);
        if (next != _value)
            pipeline.Stage(new SetQuarantinePermissions(new ChangeTarget("quarantinepolicy", Policy.Name, Policy.Name, null), _value, next, Policy.Describe(),
                new QuarantinePolicy(Policy.Name, false, QuarantinePermissions.All.ToDictionary(p => p.Key, p => (next & p.Bit) != 0), Policy.Notifies).Describe()));
    }
}

/// <summary>
/// Quarantine policies: what people can do with their own quarantined mail (see, release or ask,
/// delete), and how often they're told. Each threat policy says which one applies to what it holds.
/// </summary>
public sealed partial class QuarantinePoliciesViewModel(IThreatPolicyRecords? records, ChangePipeline? pipeline, Availability canEdit) : LivePage(pipeline, canEdit)
{
    internal ChangePipeline? Pipeline { get; } = pipeline;

    public ObservableCollection<QuarantinePolicyRow> Policies { get; } = [];

    public ObservableCollection<SettingGroup> Global { get; } = [];

    static readonly IReadOnlyList<SettingDef> GlobalDefs =
    [
        new("How Often People Are Told", "frequency", "Quarantine Notices Every", "For policies that email people about their quarantined mail.",
            "Get-QuarantinePolicy", "Set-QuarantinePolicy", "EndUserSpamNotificationFrequency", SettingKind.Choice,
            Choices: [("PT4H", "4 Hours"), ("P1D", "Day"), ("P7D", "Week")]),
        new("How Often People Are Told", "branding", "Use the Organization's Logo", "Brand the notices with the logo set up in Microsoft 365.",
            "Get-QuarantinePolicy", "Set-QuarantinePolicy", "OrganizationBrandingEnabled"),
    ];

    protected override bool Concerns(ChangeOp change) => change is SetQuarantinePermissions or SetExchangeSetting { SetCmdlet: "Set-QuarantinePolicy" };

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (records is null)
            return;
        var policies = await records.GetQuarantinePoliciesAsync(cancellationToken);
        var global = await records.GetQuarantineGlobalAsync(cancellationToken);
        Policies.Clear();
        foreach (var p in policies)
            Policies.Add(new QuarantinePolicyRow(p, this, Context));
        Global.Clear();
        var name = global.ValueKind == JsonValueKind.Object && global.TryGetProperty("Name", out var n) ? n.GetString() ?? "DefaultGlobalTag" : "DefaultGlobalTag";
        foreach (var g in SettingCards.Build(GlobalDefs, d => d.Read(global), Context, SetExchangeSetting.Organization("Quarantine Notices"), name))
            Global.Add(g);
    }
}

// ---- Connection filter -----------------------------------------------------------------------

/// <summary>The connection filter: IP addresses always let in or always refused, before any other check.</summary>
public sealed partial class ConnectionFilterViewModel(IThreatPolicyRecords? records, ChangePipeline? pipeline, Availability canEdit) : LivePage(pipeline, canEdit)
{
    public ObservableCollection<SettingGroup> Groups { get; } = [];

    static readonly IReadOnlyList<SettingDef> Defs =
    [
        new("IP Addresses", "allow", "Always Let In",
            "Mail from these addresses skips spam filtering (it's still checked for malware and high-confidence phishing). IPv4 only: an address, a range, or /24 to /32. Up to 1,273.",
            "Get-HostedConnectionFilterPolicy", "Set-HostedConnectionFilterPolicy", "IPAllowList", SettingKind.List),
        new("IP Addresses", "block", "Always Refuse",
            "Mail from these addresses is refused before anything else (and doesn't appear in message trace).", "Get-HostedConnectionFilterPolicy", "Set-HostedConnectionFilterPolicy",
            "IPBlockList", SettingKind.List),
        new("Microsoft's List", "safelist", "Microsoft's Safe List",
            "Let in mail from senders Microsoft's own list trusts. Microsoft recommends off: it skips checks that would otherwise catch problems.",
            "Get-HostedConnectionFilterPolicy", "Set-HostedConnectionFilterPolicy", "EnableSafeList", Recommended: JsonValue.Create(false)),
    ];

    protected override bool Concerns(ChangeOp change) => change is SetExchangeSetting { SetCmdlet: "Set-HostedConnectionFilterPolicy" };

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (records is null)
            return;
        var row = await records.GetConnectionFilterAsync(cancellationToken);
        Groups.Clear();
        foreach (var g in SettingCards.Build(Defs, d => d.Read(row), Context, SetExchangeSetting.Organization("Connection Filter"), "Default"))
            Groups.Add(g);
    }
}
