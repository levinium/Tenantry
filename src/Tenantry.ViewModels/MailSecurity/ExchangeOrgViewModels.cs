using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>A choice for a choice setting, as its picker shows it.</summary>
public sealed record SettingChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Everything a setting row needs from the page it sits on.</summary>
public sealed record SettingContext(ChangePipeline? Pipeline, Availability CanEdit);

/// <summary>
/// One Exchange setting as a row: a box to tick, a number or a choice, with what it does. Each
/// change restages one <see cref="SetExchangeSetting"/> against the value read (setting it back
/// drops it).
/// </summary>
public sealed partial class SettingRow : ObservableObject
{
    readonly SettingContext _context;
    readonly ChangeTarget _owner;
    readonly string? _identity;
    readonly JsonNode? _read;
    bool _loading = true;

    public SettingRow(SettingDef def, JsonNode? read, SettingContext context, ChangeTarget owner, string? identity)
    {
        Def = def;
        _read = read;
        _context = context;
        _owner = owner;
        _identity = identity;
        // Unset shows what Microsoft does by default, so the switch never claims "off" for something that's on.
        IsOn = def.Kind == SettingKind.OnOff && (read is JsonValue on && on.TryGetValue<bool>(out var isOn) ? isOn : def.DefaultOn == true);
        Choices = [.. (def.Choices ?? []).Select(c => new SettingChoice(c.Value, c.Label))];
        if (def.Kind == SettingKind.Choice && read?.GetValue<string>() is { } value)
            Choice = Choices.FirstOrDefault(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        NumberText = def.Kind == SettingKind.Number && read is not null ? read.GetValue<int>().ToString(CultureInfo.CurrentCulture) : "";
        _loading = false;
    }

    public SettingDef Def { get; }

    public string Label => Def.Label;
    public string Help => Def.Help;
    public bool IsOnOff => Def.Kind == SettingKind.OnOff;
    public bool IsNumber => Def.Kind == SettingKind.Number;
    public bool IsChoice => Def.Kind == SettingKind.Choice;

    public bool CanEdit => _context.Pipeline is not null && _context.CanEdit.IsAvailable;

    /// <summary>Exchange leaves it unset: Microsoft's default applies until it's chosen here.</summary>
    public bool IsUnset => _read is null;

    public string UnsetText => Def.DefaultOn is { } d ? $"Not set: Microsoft's default ({(d ? "on" : "off")}) applies." : "Not set: Microsoft's default applies.";

    public IReadOnlyList<SettingChoice> Choices { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial SettingChoice? Choice { get; set; }

    [ObservableProperty]
    public partial string NumberText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    partial void OnIsOnChanged(bool value) => Restage(JsonValue.Create(value));

    partial void OnChoiceChanged(SettingChoice? value) => Restage(value is null ? null : JsonValue.Create(value.Value));

    /// <summary>A number takes effect on Enter or leaving the box.</summary>
    [RelayCommand]
    void CommitNumber()
    {
        if (!int.TryParse(NumberText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n < Def.Min || n > Def.Max)
        {
            Problem = $"A whole number from {Def.Min:N0} to {Def.Max:N0}.";
            return;
        }
        Problem = null;
        Restage(JsonValue.Create(n));
    }

    string Key => $"{Def.SetCmdlet}:{Def.Parameter}";

    void Restage(JsonNode? value)
    {
        if (_loading || !CanEdit)
            return;
        var pipeline = _context.Pipeline!;
        if (pipeline.PendingFor(_owner.Id, Key) is { } pending)
            pipeline.Unstage(pending);
        if (JsonNode.DeepEquals(value, _read))
            return;
        pipeline.Stage(new SetExchangeSetting(_owner, Def.SetCmdlet, Def.GetCmdlet, _identity, Def.Parameter, Def.Label,
            Def.ToExchange(value) switch { bool b => JsonValue.Create(b), int n => JsonValue.Create(n), string s => JsonValue.Create(s), _ => null },
            Def.Words(_read), Def.Words(value), Def.IsCaution(value)));
    }
}

/// <summary>A titled card of setting rows.</summary>
public sealed record SettingGroup(string Title, IReadOnlyList<SettingRow> Rows);

/// <summary>Builds setting cards from a catalog and the row Exchange returned.</summary>
static class SettingCards
{
    public static IReadOnlyList<SettingGroup> Build(IEnumerable<SettingDef> defs, Func<SettingDef, JsonNode?> read, SettingContext context, ChangeTarget owner, string? identity) =>
        [.. defs.GroupBy(d => d.Group).Select(g => new SettingGroup(g.Key, [.. g.Select(d => new SettingRow(d, read(d), context, owner, identity))]))];
}

/// <summary>A page read live when first shown, with Refresh and a reason when it can't be read.</summary>
public abstract partial class LivePage : ObservableObject
{
    protected LivePage(ChangePipeline? pipeline, Availability canEdit)
    {
        Context = new SettingContext(pipeline, canEdit);
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (IsLoaded && applied.Any(Concerns))
                    _ = Refresh();
            };
    }

    protected SettingContext Context { get; }

    public bool CanEdit => Context.Pipeline is not null && Context.CanEdit.IsAvailable;

    public string EditReason => Context.Pipeline is null ? "This tenant can't be edited" : Context.CanEdit.Reason ?? "";

    /// <summary>An applied change this page shows, so it reads again.</summary>
    protected abstract bool Concerns(ChangeOp change);

    protected abstract Task LoadCoreAsync(CancellationToken cancellationToken);

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    public async Task Refresh()
    {
        if (IsLoading)
            return;
        IsLoading = true;
        Error = null;
        try
        {
            await LoadCoreAsync(default);
            IsLoaded = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"Couldn't read them: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

// ---- Organization mail settings ------------------------------------------------------------------

/// <summary>The organization's mail settings (Set-OrganizationConfig, Set-TransportConfig, Set-ExternalInOutlook), in cards.</summary>
public sealed partial class OrgMailSettingsViewModel(IExchangeOrgRecords? records, ChangePipeline? pipeline, Availability canEdit) : LivePage(pipeline, canEdit)
{
    public ObservableCollection<SettingGroup> Groups { get; } = [];

    protected override bool Concerns(ChangeOp change) => change is SetExchangeSetting { Identity: null };

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (records is null)
            return;
        var rows = await records.GetOrgSettingsAsync(cancellationToken);
        Groups.Clear();
        foreach (var g in SettingCards.Build(OrgMailSettings.All, d => d.Read(rows.GetValueOrDefault(d.GetCmdlet)), Context, SetExchangeSetting.Organization("Mail Settings"), null))
            Groups.Add(g);
    }
}

// ---- Remote domains ----------------------------------------------------------------------------

/// <summary>A remote domain's card: its settings, and Remove for any but the default.</summary>
public sealed partial class RemoteDomainRow(RemoteDomain domain, IReadOnlyList<SettingGroup> groups, RemoteDomainsViewModel page) : ObservableObject
{
    public RemoteDomain Domain { get; } = domain;
    public string Title => Domain.IsDefault ? "Every Other Domain (Default)" : Domain.DomainName;
    public string Subtitle => Domain.IsDefault ? "The settings for mail to any domain without its own entry here." : $"Named \"{Domain.Name}\"";
    public IReadOnlyList<SettingGroup> Groups { get; } = groups;
    public bool CanRemove => !Domain.IsDefault && page.CanEdit;
    public string RemoveText => page.PendingRemove(this) ? "Undo Remove" : "Remove";

    [RelayCommand]
    void Remove() => page.ToggleRemove(this);

    internal void Refresh() => OnPropertyChanged(nameof(RemoveText));
}

/// <summary>
/// Remote domains: per-domain rules for mail leaving the organization (automatic replies and
/// forwarding, bounces, receipts, formats). "Default" covers every domain without its own.
/// </summary>
public sealed partial class RemoteDomainsViewModel : LivePage
{
    readonly IExchangeOrgRecords? _records;

    public RemoteDomainsViewModel(IExchangeOrgRecords? records, ChangePipeline? pipeline, Availability canEdit) : base(pipeline, canEdit)
    {
        _records = records;
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var d in Domains)
                    d.Refresh();
                OnPropertyChanged(nameof(AddText));
            };
    }

    public ObservableCollection<RemoteDomainRow> Domains { get; } = [];

    protected override bool Concerns(ChangeOp change) => change is CreateRemoteDomain or RemoveRemoteDomain or SetExchangeSetting { SetCmdlet: "Set-RemoteDomain" };

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_records is null)
            return;
        var domains = await _records.GetRemoteDomainsAsync(cancellationToken);
        Domains.Clear();
        foreach (var d in domains)
            Domains.Add(new RemoteDomainRow(d, SettingCards.Build(RemoteDomainSettings.All, s => d.Values.GetValueOrDefault(s.Key), Context, Target(d.Name), d.Name), this));
    }

    static ChangeTarget Target(string name) => new("remotedomain", name, name, null);

    internal bool PendingRemove(RemoteDomainRow row) => Context.Pipeline?.PendingFor(row.Domain.Name, "remotedomain") is RemoveRemoteDomain;

    internal void ToggleRemove(RemoteDomainRow row)
    {
        if (!CanEdit || row.Domain.IsDefault)
            return;
        if (Context.Pipeline!.PendingFor(row.Domain.Name, "remotedomain") is { } pending)
            Context.Pipeline.Unstage(pending);
        else
            Context.Pipeline.Stage(new RemoveRemoteDomain(Target(row.Domain.Name), row.Domain.DomainName));
    }

    /// <summary>The domain to add (contoso.com, or *.contoso.com for its subdomains too).</summary>
    [ObservableProperty]
    public partial string NewDomain { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddProblem))]
    public partial string? AddProblem { get; private set; }

    public bool HasAddProblem => AddProblem is not null;

    public string AddText => Context.Pipeline?.Pending.OfType<CreateRemoteDomain>().Any() == true ? "Add Another" : "Add";

    [RelayCommand]
    void Add()
    {
        var domain = NewDomain.Trim().ToLowerInvariant();
        AddProblem = domain.Length == 0 || !domain.Contains('.') || domain.Contains('@') || domain.Contains(' ') ? "Type a domain, such as partner.example (or *.partner.example)."
            : Domains.Any(d => d.Domain.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase)) ? "That domain has its own entry already."
            : null;
        if (AddProblem is not null || !CanEdit)
            return;
        Context.Pipeline!.Stage(new CreateRemoteDomain(Target(domain.TrimStart('*', '.')), domain));
        NewDomain = "";
    }
}

// ---- Connectors and accepted domains ------------------------------------------------------------

/// <summary>A connector's card: what it does in sentences, and on or off.</summary>
public sealed record ConnectorRow(MailConnector Connector, SettingRow Enabled)
{
    public string Name => Connector.Name;
    public string Direction => Connector.Direction;
    public string? Comment => Connector.Comment;
    public bool HasComment => Connector.Comment is not null;
    public IReadOnlyList<string> Facts => Connector.Facts;
    public string StateText => Connector.Enabled ? "On" : "Off";
    public BadgeKind StateKind => Connector.Enabled ? BadgeKind.Success : BadgeKind.Neutral;
}

/// <summary>Connectors: how mail moves between Microsoft 365 and your own servers, partners or filtering services.</summary>
public sealed partial class ConnectorsViewModel(IExchangeOrgRecords? records, ChangePipeline? pipeline, Availability canEdit) : LivePage(pipeline, canEdit)
{
    public ObservableCollection<ConnectorRow> Connectors { get; } = [];

    public bool IsEmpty => IsLoaded && Connectors.Count == 0;

    protected override bool Concerns(ChangeOp change) => change is SetExchangeSetting { SetCmdlet: "Set-InboundConnector" or "Set-OutboundConnector" };

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (records is null)
            return;
        var connectors = await records.GetConnectorsAsync(cancellationToken);
        Connectors.Clear();
        foreach (var c in connectors)
        {
            var noun = c.Inbound ? "InboundConnector" : "OutboundConnector";
            var def = new SettingDef("", "enabled", "Turned On", c.Inbound ? "Mail matching it is handled as it says." : "Mail matching it leaves through it.",
                $"Get-{noun}", $"Set-{noun}", "Enabled", CautionWhen: false);
            Connectors.Add(new ConnectorRow(c, new SettingRow(def, JsonValue.Create(c.Enabled), Context, new ChangeTarget("connector", c.Name, c.Name, null), c.Name)));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>An accepted domain's row: its kind (who Exchange delivers it to) as a choice.</summary>
public sealed record AcceptedDomainRow(AcceptedDomain Domain, SettingRow Type)
{
    public string Name => Domain.DomainName;
    public string Tags => string.Join(" · ", new[] { Domain.IsDefault ? "Default" : null, Domain.MatchSubDomains ? "Includes subdomains" : null,
        Domain.SendingDisabled ? "Sending from it is off" : null }.OfType<string>());
    public bool HasTags => Tags.Length > 0;
    public bool IsInitial => Domain.DomainName.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Accepted domains: the domains the organization takes mail for, and whether every address at one
/// lives here (authoritative) or some are elsewhere (internal relay, during a move). Adding and
/// verifying a domain is in Microsoft 365's domain setup.
/// </summary>
public sealed partial class AcceptedDomainsViewModel(IExchangeOrgRecords? records, ChangePipeline? pipeline, Availability canEdit) : LivePage(pipeline, canEdit)
{
    static readonly SettingDef TypeDef = new("", "domaintype", "Kind",
        "Authoritative: every address at it is in Microsoft 365, and mail to an unknown one bounces. Internal relay: some addresses live on another mail system, and mail for them is passed on.",
        "Get-AcceptedDomain", "Set-AcceptedDomain", "DomainType", SettingKind.Choice,
        Choices: [("Authoritative", "Authoritative (All Addresses Here)"), ("InternalRelay", "Internal Relay (Some Addresses Elsewhere)")]);

    public ObservableCollection<AcceptedDomainRow> Domains { get; } = [];

    protected override bool Concerns(ChangeOp change) => change is SetExchangeSetting { SetCmdlet: "Set-AcceptedDomain" };

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (records is null)
            return;
        var domains = await records.GetAcceptedDomainsAsync(cancellationToken);
        Domains.Clear();
        foreach (var d in domains)
            Domains.Add(new AcceptedDomainRow(d, new SettingRow(TypeDef, JsonValue.Create(d.Type), Context, new ChangeTarget("accepteddomain", d.Name, d.DomainName, null), d.Name)));
    }
}

// ---- Journal rules -------------------------------------------------------------------------------

/// <summary>A journal rule's row: what it copies where, on or off, Remove.</summary>
public sealed partial class JournalRuleRow(JournalRule rule, JournalRulesViewModel page) : ObservableObject
{
    public JournalRule Rule { get; } = rule;
    public string Name => Rule.Name;
    public string What => $"{(Rule.Scope switch { "Internal" => "Mail inside the organization", "External" => "Mail to or from outside", _ => "All mail" })}"
                          + $"{(Rule.Recipient is { } r ? $" for {r}" : "")}, copied to {Rule.JournalAddress}";
    public string StateText => Rule.Enabled ? "On" : "Off";
    public BadgeKind StateKind => Rule.Enabled ? BadgeKind.Success : BadgeKind.Neutral;
    public bool CanEdit => page.CanEdit;
    public string SwitchText => page.Pending(this) is SetJournalRuleEnabled ? "Undo" : Rule.Enabled ? "Turn Off" : "Turn On";
    public string RemoveText => page.Pending(this) is RemoveJournalRule ? "Undo Remove" : "Remove";

    [RelayCommand]
    void Switch() => page.Toggle(this, () => new SetJournalRuleEnabled(page.Target(Rule.Name), Rule.Enabled, !Rule.Enabled));

    [RelayCommand]
    void Remove() => page.Toggle(this, () => new RemoveJournalRule(page.Target(Rule.Name)));

    internal void Refresh()
    {
        OnPropertyChanged(nameof(SwitchText));
        OnPropertyChanged(nameof(RemoveText));
    }
}

/// <summary>Journal rules: copies of mail (all, inside, or outside; for everyone or one person or list) sent to an outside archive.</summary>
public sealed partial class JournalRulesViewModel : LivePage
{
    readonly IExchangeOrgRecords? _records;

    public JournalRulesViewModel(IExchangeOrgRecords? records, ChangePipeline? pipeline, Availability canEdit) : base(pipeline, canEdit)
    {
        _records = records;
        NewScope = Scopes[0];
        if (pipeline is not null)
            pipeline.Changed += (_, _) =>
            {
                foreach (var r in Rules)
                    r.Refresh();
            };
    }

    public ObservableCollection<JournalRuleRow> Rules { get; } = [];

    public bool IsEmpty => IsLoaded && Rules.Count == 0;

    protected override bool Concerns(ChangeOp change) => change is SetJournalRuleEnabled or CreateJournalRule or RemoveJournalRule;

    protected override async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_records is null)
            return;
        var rules = await _records.GetJournalRulesAsync(cancellationToken);
        Rules.Clear();
        foreach (var r in rules)
            Rules.Add(new JournalRuleRow(r, this));
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal ChangeTarget Target(string name) => new("journalrule", name, name, null);

    internal ChangeOp? Pending(JournalRuleRow row) => Context.Pipeline?.PendingFor(row.Rule.Name, "journal");

    internal void Toggle(JournalRuleRow row, Func<ChangeOp> make)
    {
        if (!CanEdit)
            return;
        if (Pending(row) is { } pending)
            Context.Pipeline!.Unstage(pending);
        else
            Context.Pipeline!.Stage(make());
    }

    public static IReadOnlyList<SettingChoice> Scopes { get; } =
        [new("Global", "All Mail"), new("External", "Mail to or from Outside"), new("Internal", "Mail inside the Organization")];

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial SettingChoice NewScope { get; set; }

    [ObservableProperty]
    public partial string NewRecipient { get; set; } = "";

    [ObservableProperty]
    public partial string NewAddress { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddProblem))]
    public partial string? AddProblem { get; private set; }

    public bool HasAddProblem => AddProblem is not null;

    [RelayCommand]
    void Add()
    {
        var name = NewName.Trim();
        var address = NewAddress.Trim();
        AddProblem = name.Length == 0 ? "Give the rule a name."
            : Rules.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? "A rule has that name already."
            : !address.Contains('@') ? "Give the archive's address (outside Exchange Online: journal reports can't go to a mailbox here)."
            : null;
        if (AddProblem is not null || !CanEdit)
            return;
        Context.Pipeline!.Stage(new CreateJournalRule(Target(name), NewScope.Value, NewRecipient.Trim().Length > 0 ? NewRecipient.Trim() : null, address));
        NewName = NewRecipient = NewAddress = "";
    }
}

// ---- Spoofed senders -----------------------------------------------------------------------------

/// <summary>
/// A spoofed sender in the preview: who sent as whom, how often, Microsoft's verdict, and Allow or
/// Block (a spoof entry on the Tenant Allow/Block List, staged).
/// </summary>
public sealed partial class SpoofPreviewViewModel(SpoofedSender sender, ChangePipeline? pipeline, Availability canEdit) : ObservableObject
{
    public SpoofedSender Sender { get; } = sender;
    public string Title => $"{Sender.Infrastructure} sending as {Sender.SpoofedUser}";
    public string Meaning => Sender.IsInternal
        ? $"Mail that says it's from your own domain {Sender.SpoofedUser} came from {Sender.Infrastructure}, which isn't one of your servers. That's normal for services sending for you (newsletters, ticketing); otherwise it's someone pretending to be you."
        : $"Mail that says it's from {Sender.SpoofedUser} came from {Sender.Infrastructure}, which that domain doesn't list as its own. That can be a mailing service working for them, or a forgery.";
    public string CountText => $"{Sender.Messages:N0} {(Sender.Messages == 1 ? "message" : "messages")}{(Sender.LastSeen is { } at ? $", last {Display.Relative(at, DateTimeOffset.Now)}" : "")}";
    public string VerdictText => Sender.IsAllowed ? "Microsoft lets it through." : "Microsoft treats it as spoofing (junk or quarantine).";
    public bool CanEdit => pipeline is not null && canEdit.IsAvailable;

    ChangeTarget Target => new("listentry", $"spoof:{Sender.SpoofedUser}|{Sender.Infrastructure}".ToLowerInvariant(), Sender.SpoofedUser, Sender.SpoofedUser);

    AddListEntry? Pending => pipeline?.PendingFor(Target.Id, "entry") as AddListEntry;

    public string AllowText => Pending is { Block: false } ? "Undo Allow" : "Always Allow";
    public string BlockText => Pending is { Block: true } ? "Undo Block" : "Always Block";

    [RelayCommand]
    void Allow() => Set(block: false);

    [RelayCommand]
    void Block() => Set(block: true);

    void Set(bool block)
    {
        if (!CanEdit)
            return;
        if (Pending is { } pending)
        {
            pipeline!.Unstage(pending);
            if (pending.Block == block)
            {
                Notify();
                return;
            }
        }
        pipeline!.Stage(new AddListEntry(Target, ListKind.Spoof, block, ListExpiry.Never, null, Sender.Infrastructure, Sender.SpoofType));
        Notify();
    }

    void Notify()
    {
        OnPropertyChanged(nameof(AllowText));
        OnPropertyChanged(nameof(BlockText));
    }
}
