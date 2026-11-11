using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>One page of Mail &amp; security, as its chip reads.</summary>
/// <param name="Planned">Not built yet: what the page will hold.</param>
public sealed record MailSecurityPage(string Key, string Title, string Icon, string? Planned = null)
{
    public bool IsPlanned => Planned is not null;
}

/// <summary>A choice of how far back message trace looks.</summary>
public sealed record TraceRange(int Days, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Mail &amp; security area (Phase 5): one tab, with a page per job. Quarantine and message
/// trace are live lists (read from Exchange when shown, not kept in the local copy) with the usual
/// filters, views, columns and export; their preview pane holds the actions.
/// </summary>
public sealed partial class MailSecurityViewModel : TabViewModel
{
    readonly IMailSecurity? _records;
    readonly Func<DateTimeOffset> _now;

    public static IReadOnlyList<MailSecurityPage> Pages { get; } =
    [
        new("quarantine", "Quarantine", "ShieldError"),
        new("trace", "Message Trace", "MailSearch"),
        new("rules", "Mail Flow Rules", "ArrowRouting"),
        new("lists", "Block and Allow Lists", "ShieldProhibited"),
        new("reports", "Trace Reports", "DocumentTable"),
        new("blocked", "Blocked Senders", "PersonProhibited"),
        new("domains", "Email Authentication", "ShieldCheckmark"),
        new("reported", "Reported Messages", "Flag"),
        new("accepted", "Accepted Domains", "GlobeShield"),
        new("remote", "Remote Domains", "GlobeArrowForward"),
        new("connectors", "Connectors", "PlugConnected"),
        new("spoofed", "Spoofed Senders", "PersonWarning"),
        new("mailsettings", "Mail Settings", "Settings"),
        new("journal", "Journal Rules", "Archive"),
        new("tp-antispam", "Anti-Spam", "MailWarning"),
        new("tp-outbound", "Outbound Spam", "MailArrowUp"),
        new("tp-antiphish", "Anti-Phishing", "PersonAlert"),
        new("tp-antimalware", "Anti-Malware", "Bug"),
        new("tp-safelinks", "Safe Links", "LinkSquare"),
        new("tp-safeattach", "Safe Attachments", "Attach"),
        new("quarantinepolicies", "Quarantine Policies", "ShieldKeyhole"),
        new("connectionfilter", "Connection Filter", "PlugDisconnected"),
    ];

    /// <summary>The pages in sections: the sections on one row, the chosen section's pages under it.</summary>
    public static IReadOnlyList<SecurityPageGroup> Sections { get; } =
    [
        new("messages", "Messages", "Mail", Of("quarantine", "trace", "reports", "reported")),
        new("rules", "Rules & Lists", "ArrowRouting", Of("rules", "lists", "blocked", "spoofed")),
        new("domains", "Domains", "Globe", Of("domains", "accepted", "remote", "connectors")),
        new("policies", "Threat Policies", "ShieldTask", Of("tp-antispam", "tp-outbound", "tp-antiphish", "tp-antimalware", "tp-safelinks", "tp-safeattach",
            "quarantinepolicies", "connectionfilter")),
        new("organization", "Organization", "Building", Of("mailsettings", "journal")),
    ];

    static IReadOnlyList<MailSecurityPage> Of(params string[] keys) => [.. keys.Select(k => Pages.Single(p => p.Key == k))];

    public static SecurityPageGroup SectionOf(MailSecurityPage page) => Sections.First(s => s.Pages.Contains(page));

    [ObservableProperty]
    public partial SecurityPageGroup SelectedSection { get; set; } = Sections[0];

    partial void OnSelectedSectionChanged(SecurityPageGroup value)
    {
        if (!value.Pages.Contains(SelectedPage))
            SelectedPage = value.Pages[0];
    }

    bool _movingSection;

    /// <summary>A page chosen from anywhere (a follow-up link, a check) brings its section along.</summary>
    partial void OnSelectedPageChanged(MailSecurityPage oldValue, MailSecurityPage newValue)
    {
        // The page row clears its choice while it swaps to another section's pages; keep the page asked for.
        if (newValue is null)
        {
            SelectedPage = oldValue ?? SelectedSection.Pages[0];
            return;
        }
        if (_movingSection || SectionOf(newValue) == SelectedSection)
            return;
        _movingSection = true;
        try
        {
            SelectedSection = SectionOf(newValue);
        }
        finally
        {
            _movingSection = false;
        }
        if (SelectedPage != newValue)
            SelectedPage = newValue;
    }

    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly ITabHost? _tabs;

    public MailSecurityViewModel(IMailSecurity? records, ChangePipeline? pipeline, Availability canEdit, IListStateStore? listState, Func<DateTimeOffset>? now = null,
        ITabHost? tabs = null, string? account = null, Func<string, Action?>? openPerson = null)
    {
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _tabs = tabs;
        _now = now ?? (() => DateTimeOffset.Now);
        Quarantine = DataListViewModel.Create(
            MailSecurityLists.Quarantine(m => new QuarantinePreviewViewModel(m, records, pipeline, canEdit, () => _entries)),
            LoadQuarantineAsync, row => OpenQuarantined((QuarantinedMessage)row), listState, _now);
        var follow = new TraceActions(TraceFrom, TraceTo, FindInQuarantine);
        Trace = DataListViewModel.Create(
            MailSecurityLists.Trace(m => new TracePreviewViewModel(m, records, follow, pipeline, canEdit, () => _entries, IsOwnAddress)),
            LoadTraceAsync, _ => { }, listState, _now);
        Reports = new TraceReportsViewModel(records, account, now);
        Blocked = new BlockedSendersViewModel(records as IMailDomainRecords, pipeline, canEdit, openPerson);
        Domains = new MailDomainsViewModel(records as IMailDomainRecords, pipeline, canEdit);
        Reported = new ReportedMessagesViewModel(records as ISubmissionRecords, pipeline, canEdit, listState, _now);
        var org = records as IExchangeOrgRecords;
        MailSettings = new OrgMailSettingsViewModel(org, pipeline, canEdit);
        RemoteDomains = new RemoteDomainsViewModel(org, pipeline, canEdit);
        Connectors = new ConnectorsViewModel(org, pipeline, canEdit);
        AcceptedDomains = new AcceptedDomainsViewModel(org, pipeline, canEdit);
        Journal = new JournalRulesViewModel(org, pipeline, canEdit);
        var threats = records as IThreatPolicyRecords;
        ThreatPages = ThreatPolicyCatalog.Kinds.ToDictionary(k => "tp-" + k.Key, k => new ThreatPoliciesViewModel(k, threats, pipeline, canEdit));
        QuarantinePolicies = new QuarantinePoliciesViewModel(threats, pipeline, canEdit);
        ConnectionFilter = new ConnectionFilterViewModel(threats, pipeline, canEdit);
        Spoofed = DataListViewModel.Create(MailSecurityLists.Spoofed(s => new SpoofPreviewViewModel(s, pipeline, canEdit)),
            async ct => org is null ? [] : await org.GetSpoofedSendersAsync(ct), _ => { }, listState, _now);
        Spoofed.PrimaryAction = null;
        Spoofed.HasRecordPages = false;
        Spoofed.EmptyText = "Microsoft hasn't seen anyone sending as another domain.";
        Entries = DataListViewModel.Create(
            MailSecurityLists.Entries(e => new ListEntryPreviewViewModel(e, pipeline, canEdit)),
            LoadEntriesAsync, row => OpenEntry((ListEntry)row), listState, _now);
        AddEntries = new AddListEntriesViewModel(pipeline, canEdit, () => _entries, _now);
        Rules = DataListViewModel.Create(
            MailSecurityLists.Rules(r => new RulePreviewViewModel(r, _rules.Count, pipeline, canEdit, CanOpenEditor ? EditRule : null, () => _rules)),
            LoadRulesAsync, row => EditRule((MailFlowRule)row), listState, _now);
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is ListEntryChange) && _entriesRead)
                    _ = Entries.ReloadAsync(busyText: "Reading the lists again…");
                if (applied.Any(c => c is RuleChange or Tenantry.Core.Changes.ReorderRules) && _rulesRead)
                    _ = Rules.ReloadAsync(busyText: "Reading the rules again…");
                if (applied.Any(c => c is QuarantineChange) && _quarantineRead)
                    _ = Quarantine.ReloadAsync();
            };
        foreach (var list in new[] { Quarantine, Trace, Entries, Rules })
        {
            list.PrimaryAction = null;
            list.HasRecordPages = false;
        }
        // A rule opens as its own page: the editor, read-only for an admin who can't change rules.
        // Quarantined messages and block/allow entries open as pages too; a trace result has nothing more than its preview.
        Rules.HasRecordPages = CanOpenRulePage;
        Quarantine.HasRecordPages = _tabs is not null;
        Entries.HasRecordPages = _tabs is not null;
        Trace.EmptyText = "Choose what to look for above, then press Search.";
        Quarantine.EmptyText = "Nothing in quarantine matches these filters.";
        // Several ticked: release, approve or deny requests, delete or block their senders together.
        Quarantine.SelectionPanel = (rows, uncheck) => Records.SelectionPanelViewModel.ForQuarantine(rows, uncheck, pipeline, canEdit);
        TraceStart = _now().Date.AddDays(-1);
        SelectedPage = Pages[0];
    }

    public override string Title => "Mail & Security";

    public override bool CanClose => false;

    public DataListViewModel Quarantine { get; }

    public DataListViewModel Trace { get; }

    public DataListViewModel Entries { get; }

    public DataListViewModel Rules { get; }

    /// <summary>Every list in the area, for preferences shared across lists.</summary>
    public IEnumerable<DataListViewModel> Lists => [Quarantine, Trace, Entries, Rules, Reported.Submissions, Spoofed];

    public AddListEntriesViewModel AddEntries { get; }

    public TraceReportsViewModel Reports { get; }

    public BlockedSendersViewModel Blocked { get; }

    public MailDomainsViewModel Domains { get; }

    public ReportedMessagesViewModel Reported { get; }

    public OrgMailSettingsViewModel MailSettings { get; }
    public RemoteDomainsViewModel RemoteDomains { get; }
    public ConnectorsViewModel Connectors { get; }
    public AcceptedDomainsViewModel AcceptedDomains { get; }
    public JournalRulesViewModel Journal { get; }

    /// <summary>A page per kind of threat policy, by page key ("tp-antispam"…).</summary>
    public IReadOnlyDictionary<string, ThreatPoliciesViewModel> ThreatPages { get; }

    /// <summary>The threat policy page showing, if one is.</summary>
    public ThreatPoliciesViewModel? CurrentThreat => ThreatPages.GetValueOrDefault(SelectedPage.Key);

    public bool ShowThreat => CurrentThreat is not null;

    public QuarantinePoliciesViewModel QuarantinePolicies { get; }

    public ConnectionFilterViewModel ConnectionFilter { get; }

    public bool ShowQuarantinePolicies => SelectedPage.Key == "quarantinepolicies";

    public bool ShowConnectionFilter => SelectedPage.Key == "connectionfilter";
    public DataListViewModel Spoofed { get; }

    public bool ShowMailSettings => SelectedPage.Key == "mailsettings";
    public bool ShowRemote => SelectedPage.Key == "remote";
    public bool ShowConnectors => SelectedPage.Key == "connectors";
    public bool ShowAccepted => SelectedPage.Key == "accepted";
    public bool ShowJournal => SelectedPage.Key == "journal";
    public bool ShowSpoofed => SelectedPage.Key == "spoofed";

    bool _spoofedRead;

    public bool ShowReported => SelectedPage.Key == "reported";

    public bool ShowBlocked => SelectedPage.Key == "blocked";
    public bool ShowDomains => SelectedPage.Key == "domains";

    /// <summary>The organization's own domains, read once (to keep block and allow for outside senders).</summary>
    HashSet<string> _ownDomains = new(StringComparer.OrdinalIgnoreCase);

    bool _ownDomainsRead;

    async Task ReadOwnDomainsAsync()
    {
        if (_ownDomainsRead || _records is not IMailDomainRecords domains)
            return;
        _ownDomainsRead = true;
        try
        {
            _ownDomains = new HashSet<string>(await domains.GetAcceptedDomainNamesAsync(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _ownDomainsRead = false;
        }
    }

    bool IsOwnAddress(string address) => address.LastIndexOf('@') is var at and >= 0 && _ownDomains.Contains(address[(at + 1)..]);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQuarantine), nameof(ShowTrace), nameof(ShowLists), nameof(ShowRules), nameof(ShowReports), nameof(ShowBlocked), nameof(ShowDomains), nameof(ShowReported), nameof(ShowMailSettings), nameof(ShowRemote), nameof(ShowConnectors),
        nameof(ShowAccepted), nameof(ShowJournal), nameof(ShowSpoofed), nameof(CurrentThreat), nameof(ShowThreat), nameof(ShowQuarantinePolicies), nameof(ShowConnectionFilter),
        nameof(IsPlannedPage))]
    public partial MailSecurityPage SelectedPage { get; set; }

    public bool ShowReports => SelectedPage.Key == "reports";
    public bool ShowQuarantine => SelectedPage.Key == "quarantine";
    public bool ShowTrace => SelectedPage.Key == "trace";
    public bool ShowLists => SelectedPage.Key == "lists";
    public bool ShowRules => SelectedPage.Key == "rules";
    public bool IsPlannedPage => SelectedPage.IsPlanned;

    bool _quarantineRead;
    bool _entriesRead;
    bool _rulesRead;

    partial void OnSelectedPageChanged(MailSecurityPage value)
    {
        if (value is null)
            return;
        // Quarantine and the lists read when first shown; trace waits for a search.
        if (value.Key == "quarantine" && !_quarantineRead)
        {
            _quarantineRead = true;
            _ = Quarantine.LoadAsync();
        }
        if (value.Key == "rules" && !_rulesRead)
        {
            _rulesRead = true;
            _ = Rules.LoadAsync();
        }
        if (value.Key == "lists" && !_entriesRead)
        {
            _entriesRead = true;
            _ = Entries.LoadAsync();
        }
        if (value.Key == "reports" && !Reports.IsLoaded)
            _ = Reports.Refresh();
        if (value.Key == "trace")
            _ = ReadOwnDomainsAsync();
        if (value.Key == "blocked" && !Blocked.IsLoaded)
            _ = Blocked.Refresh();
        if (value.Key == "domains" && !Domains.IsLoaded)
            _ = Domains.Refresh();
        if (value.Key == "reported")
            _ = Reported.LoadAsync();
        LivePage? live = value.Key switch
        {
            "mailsettings" => MailSettings, "remote" => RemoteDomains, "connectors" => Connectors, "accepted" => AcceptedDomains, "journal" => Journal,
            "quarantinepolicies" => QuarantinePolicies, "connectionfilter" => ConnectionFilter, _ => ThreatPages.GetValueOrDefault(value.Key),
        };
        if (live is { IsLoaded: false })
            _ = live.Refresh();
        if (value.Key == "spoofed" && !_spoofedRead)
        {
            _spoofedRead = true;
            _ = Spoofed.LoadAsync();
        }
    }

    // ---- Mail flow rules ----------------------------------------------------------------------

    IReadOnlyList<MailFlowRule> _rules = [];

    bool _rulesLoaded;

    async Task<IReadOnlyList<MailFlowRule>> LoadRulesAsync(CancellationToken cancellationToken)
    {
        _rules = _records is null ? [] : await _records.GetRulesAsync(cancellationToken);
        if (!_rulesLoaded)
        {
            _rulesLoaded = true;
            OnPropertyChanged(nameof(CanStartRule));
            OnPropertyChanged(nameof(NewRuleTip));
        }
        OnPropertyChanged(nameof(CanReorder));
        ReorderRulesCommand.NotifyCanExecuteChanged();
        return _rules;
    }

    /// <summary>Reorder Rules needs the rules read, two or more of them, and an admin who can change rules.</summary>
    public bool CanReorder => _tabs is not null && _pipeline is not null && _canEdit.IsAvailable && _rulesLoaded && _rules.Count > 1;

    public string ReorderTip => !(_pipeline is not null && _canEdit.IsAvailable) ? _canEdit.Reason ?? "This tenant can't be edited"
        : !_rulesLoaded ? "Reading the rules first…"
        : _rules.Count < 2 ? "There's only one rule."
        : "Arrange the rules' order: drag them, or move several at once. The new order goes to Pending Changes as one change.";

    /// <summary>Opens Reorder Rules as its own page (one per tenant), on the rules as last read.</summary>
    [RelayCommand(CanExecute = nameof(CanReorder))]
    void ReorderRules() =>
        _tabs!.OpenTab(t => t is MailRecordPageViewModel p && p.Key == "ruleorder",
            () => new MailRecordPageViewModel("ruleorder", "Reorder Mail Flow Rules", new RuleOrderViewModel(_rules, _pipeline, _canEdit)));

    [RelayCommand]
    Task RefreshRules() => Rules.ReloadAsync(busyText: "Reading the mail flow rules…");

    /// <summary>The editor opens in a tab; it needs somewhere to open and an admin who can change rules.</summary>
    public bool CanOpenEditor => _tabs is not null && _records is not null && _pipeline is not null && _canEdit.IsAvailable;

    /// <summary>
    /// A new rule waits for the rules to be read: where it goes ("after every other rule") and
    /// whether its name is taken both need them.
    /// </summary>
    public bool CanStartRule => CanOpenEditor && _rulesLoaded;

    public string NewRuleTip => !CanOpenEditor
        ? _canEdit.Reason ?? "This tenant can't be edited"
        : _rulesLoaded ? "Start a new rule from a template or from scratch. New rules start in Test mode."
        : "Reading the rules first, so the new one knows where it goes…";

    [RelayCommand]
    void NewRule()
    {
        if (!CanStartRule)
            return;
        _tabs!.OpenTab(_ => false, () => RuleEditorViewModel.New(_pipeline, _canEdit, _rules, e => _tabs.CloseTab(e)));
    }

    /// <summary>Opens a quarantined message as its own page, or switches to it.</summary>
    public void OpenQuarantined(QuarantinedMessage message)
    {
        var key = "quarantine:" + message.Identity;
        _tabs?.OpenTab(t => t is MailRecordPageViewModel p && p.Key == key,
            () => new MailRecordPageViewModel(key, $"Quarantine: {message.Subject}", new QuarantinePreviewViewModel(message, _records, _pipeline, _canEdit, () => _entries)));
    }

    /// <summary>Opens a block or allow entry as its own page, or switches to it.</summary>
    public void OpenEntry(ListEntry entry)
    {
        var key = "entry:" + entry.Identity;
        _tabs?.OpenTab(t => t is MailRecordPageViewModel p && p.Key == key,
            () => new MailRecordPageViewModel(key, $"{(entry.Block ? "Blocked" : "Allowed")}: {entry.Value}", new ListEntryPreviewViewModel(entry, _pipeline, _canEdit)));
    }

    /// <summary>A rule's page needs somewhere to open and somewhere to read it from.</summary>
    public bool CanOpenRulePage => _tabs is not null && _records is not null;

    /// <summary>Opens the rule as its own page (the editor), or switches to its tab if it's already open.</summary>
    public void EditRule(MailFlowRule rule)
    {
        if (!CanOpenRulePage || _tabs is null)
            return;
        _tabs.OpenTab(t => t is RuleEditorViewModel e && e.RuleIdentity == rule.Identity,
            () => RuleEditorViewModel.Edit(rule, ct => _records!.GetRuleDraftAsync(rule.Identity, ct), _pipeline, _canEdit, _rules, e => _tabs.CloseTab(e)));
    }

    // ---- Block and allow lists ----------------------------------------------------------------

    IReadOnlyList<ListEntry> _entries = [];

    async Task<IReadOnlyList<ListEntry>> LoadEntriesAsync(CancellationToken cancellationToken) =>
        _entries = _records is null ? [] : await _records.GetListEntriesAsync(cancellationToken);

    [RelayCommand]
    Task RefreshEntries() => Entries.ReloadAsync(busyText: "Reading the block and allow lists…");

    [ObservableProperty]
    public partial bool IsAddOpen { get; set; }

    [RelayCommand]
    void ToggleAdd() => IsAddOpen = !IsAddOpen;

    // ---- Quarantine ---------------------------------------------------------------------------

    public static IReadOnlyList<TraceRange> QuarantineRanges { get; } =
        [new(1, "Last 24 Hours"), new(7, "Last 7 Days"), new(14, "Last 14 Days"), new(30, "Last 30 Days")];

    [ObservableProperty]
    public partial TraceRange QuarantineRange { get; set; } = QuarantineRanges[^1];

    partial void OnQuarantineRangeChanged(TraceRange value) => _ = Quarantine.ReloadAsync(busyText: QuarantineBusy);

    string QuarantineBusy => $"Reading quarantine ({QuarantineRange.Label.ToLowerInvariant()})…";

    async Task<IReadOnlyList<QuarantinedMessage>> LoadQuarantineAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QuarantinedMessage> messages = _records is null ? [] : await _records.GetQuarantineAsync(QuarantineRange.Days, cancellationToken);
        QuarantineNote = messages.Count >= QuarantinedMessage.MaxRead
            ? $"Showing the newest {QuarantinedMessage.MaxRead:N0} messages: choose a shorter range to see the rest."
            : null;
        return messages;
    }

    /// <summary>Set when quarantine held more than one read returns, so the list isn't taken as everything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuarantineNote))]
    public partial string? QuarantineNote { get; private set; }

    public bool HasQuarantineNote => QuarantineNote is not null;

    [RelayCommand]
    Task RefreshQuarantine() => Quarantine.ReloadAsync(busyText: QuarantineBusy);

    // ---- Message trace ------------------------------------------------------------------------

    public static IReadOnlyList<TraceRange> TraceRanges { get; } =
        [new(1, "1 Day"), new(2, "2 Days"), new(7, "7 Days"), new(TraceSearch.MaxDays, "10 Days"), new(30, "30 Days"), new(TraceSearch.HistoryDays, "90 Days (Everything)")];

    /// <summary>A range longer than 10 days starts that many days back, so it ends today.</summary>
    partial void OnTraceLengthChanged(TraceRange value)
    {
        if (value.Days > TraceSearch.MaxDays)
            TraceStart = _now().Date.AddDays(-value.Days);
    }

    [ObservableProperty]
    public partial DateTime TraceStart { get; set; }

    [ObservableProperty]
    public partial TraceRange TraceLength { get; set; } = TraceRanges[0];

    [ObservableProperty]
    public partial string TraceSender { get; set; } = "";

    [ObservableProperty]
    public partial string TraceRecipient { get; set; } = "";

    [ObservableProperty]
    public partial string TraceSubject { get; set; } = "";

    public static IReadOnlyList<string> TraceStatusChoices { get; } = ["Any Status", .. MailSecurityLists.TraceStatuses.Select(MailSecurityLists.StatusText)];

    [ObservableProperty]
    public partial string TraceStatus { get; set; } = "Any Status";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTraceNote))]
    public partial string? TraceNote { get; private set; }

    public bool HasTraceNote => TraceNote is not null;

    /// <summary>The oldest day message trace can search.</summary>
    public DateTime EarliestTraceDay => _now().Date.AddDays(-TraceSearch.HistoryDays);

    public DateTime LatestTraceDay => _now().Date;

    TraceSearch? _search;

    /// <summary>The search as entered, kept to the limits Exchange allows (10 days at a time, up to 90 days back).</summary>
    public TraceSearch CurrentSearch()
    {
        var start = new DateTimeOffset(TraceStart < EarliestTraceDay ? EarliestTraceDay : TraceStart);
        var end = start.AddDays(TraceLength.Days);
        // The longer ranges are "the last N days": up to now, today included.
        if (TraceLength.Days > TraceSearch.MaxDays)
        {
            end = _now();
            start = end.AddDays(-TraceLength.Days);
        }
        if (end > _now())
            end = _now();
        var status = TraceStatus == TraceStatusChoices[0] ? null
            : MailSecurityLists.TraceStatuses.FirstOrDefault(s => MailSecurityLists.StatusText(s) == TraceStatus);
        return new TraceSearch(start, end, Blank(TraceSender), Blank(TraceRecipient), Blank(TraceSubject), status);
    }

    static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [RelayCommand]
    async Task SearchTrace()
    {
        _search = CurrentSearch();
        TraceNote = null;
        Trace.EmptyText = "No messages match this search.";
        var windows = TraceSearch.Windows(_search.Start, _search.End).Count();
        await Trace.ReloadAsync(busyText: windows > 1
            ? $"Searching message trace, {windows} stretches of 10 days (Exchange's most per search)… This can take a few minutes."
            : "Searching message trace… This can take up to a minute for a busy day.");
        if (Trace.Total >= TraceSearch.MaxResults)
            TraceNote = $"Showing the first {TraceSearch.MaxResults:N0} messages: narrow the search (a sender, recipient or shorter range) to see the rest.";
    }

    /// <summary>From a traced message: everything from its sender over the same range.</summary>
    public void TraceFrom(string sender) => Retrace(sender, "");

    /// <summary>From a traced message: everything to its recipient over the same range.</summary>
    public void TraceTo(string recipient) => Retrace("", recipient);

    void Retrace(string sender, string recipient)
    {
        TraceSender = sender;
        TraceRecipient = recipient;
        TraceSubject = "";
        TraceStatus = TraceStatusChoices[0];
        SelectedPage = Pages.Single(p => p.Key == "trace");
        _ = SearchTraceCommand.ExecuteAsync(null);
    }

    /// <summary>From a quarantined trace result: Quarantine, showing everything held with that subject.</summary>
    public void FindInQuarantine(TracedMessage message)
    {
        SelectedPage = Pages[0];
        if (Quarantine.Views.FirstOrDefault(v => v.View.Id == "all") is { } all)
            Quarantine.ApplyView(all);
        Quarantine.SearchText = message.Subject;
    }

    async Task<IReadOnlyList<TracedMessage>> LoadTraceAsync(CancellationToken cancellationToken) =>
        _records is null || _search is null ? [] : await _records.TraceAsync(_search, cancellationToken);
}
