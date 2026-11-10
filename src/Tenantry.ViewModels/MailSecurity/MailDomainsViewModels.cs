using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

// ---- Blocked senders --------------------------------------------------------------------------

/// <summary>A blocked sender in the list: why and since when, Unblock (staged) and their account.</summary>
public sealed partial class BlockedSenderRow(BlockedSender sender, BlockedSendersViewModel page, Action? openPerson) : ObservableObject
{
    public BlockedSender Sender { get; } = sender;

    public string Address => Sender.Address;

    public string Reason => Sender.Reason ?? "Sent too much mail that looked like spam";

    public string WhenText
    {
        get
        {
            var since = Sender.BlockedAt is { } at ? $"Blocked {Display.Relative(at, DateTimeOffset.Now)}" : "Blocked";
            return Sender.Temporary && Sender.Expires is { } ends ? $"{since}; Microsoft lifts it by itself on {ends.ToLocalTime():d MMM 'at' h:mm tt}"
                : Sender.Temporary ? $"{since}; Microsoft lifts it by itself within a day" : $"{since}; stays blocked until unblocked";
        }
    }

    public bool CanOpenPerson => openPerson is not null;

    [RelayCommand]
    void OpenPerson() => openPerson?.Invoke();

    public bool IsPending => page.PendingFor(this) is not null;

    public string UnblockText => IsPending ? "Undo Unblock" : "Unblock";

    [RelayCommand]
    void Unblock() => page.Toggle(this);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(UnblockText));
    }
}

/// <summary>
/// People Microsoft stopped from sending because their account sent spam (Exchange's restricted
/// entities; the sender gets bounce 5.1.8). Usually the account was taken over: secure it first,
/// then Unblock (staged in Pending Changes). Read live when shown.
/// </summary>
public sealed partial class BlockedSendersViewModel : ObservableObject
{
    readonly IMailDomainRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly Func<string, Action?>? _openPerson;

    /// <param name="openPerson">An address's person in Tenantry, to open their page; null when not one of ours.</param>
    public BlockedSendersViewModel(IMailDomainRecords? records, ChangePipeline? pipeline, Availability canEdit, Func<string, Action?>? openPerson = null)
    {
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _openPerson = openPerson;
        if (_pipeline is not null)
        {
            _pipeline.Changed += (_, _) =>
            {
                foreach (var row in Senders)
                    row.Refresh();
            };
            _pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is UnblockSender))
                    _ = Refresh();
            };
        }
    }

    public ObservableCollection<BlockedSenderRow> Senders { get; } = [];

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool IsEmpty => IsLoaded && Senders.Count == 0;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    public async Task Refresh()
    {
        if (_records is null || IsLoading)
            return;
        IsLoading = true;
        Error = null;
        try
        {
            var blocked = await _records.GetBlockedSendersAsync();
            Senders.Clear();
            foreach (var b in blocked)
                Senders.Add(new BlockedSenderRow(b, this, _openPerson?.Invoke(b.Address)));
            IsLoaded = true;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"Couldn't read the blocked senders: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal ChangeOp? PendingFor(BlockedSenderRow row) => _pipeline?.PendingFor(row.Address, "blockedsender");

    internal void Toggle(BlockedSenderRow row)
    {
        if (!CanEdit)
            return;
        if (PendingFor(row) is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new UnblockSender(UnblockSender.TargetFor(row.Address), "Unblocked in Tenantry after securing the account"));
    }
}

// ---- Email authentication ---------------------------------------------------------------------

/// <summary>One check on a domain as the page shows it.</summary>
public sealed partial class DomainCheckRow(DomainCheck check) : ObservableObject
{
    public DomainCheck Check { get; } = check;

    public string Name => Check.Check;
    public string Summary => Check.Summary;
    public string Detail => Check.Detail;
    public BadgeKind Kind => MailDomainRow.KindOf(Check.State);
    public bool HasRecord => Check.Record is not null;
    public string RecordHeading => Check.RecordType is "CNAME" ? "Publish these CNAME records" : $"Publish this {Check.RecordType} record at {Check.RecordName}";
    public string Record => Check.Record ?? "";

    [RelayCommand]
    void Copy() => Platform.CopyText(Record);
}

/// <summary>A domain on the Email Authentication page: its checks and the DKIM actions it allows.</summary>
public sealed partial class MailDomainRow : ObservableObject
{
    readonly MailDomainsViewModel _page;

    public MailDomainRow(MailDomain domain, MailDomainsViewModel page)
    {
        Domain = domain;
        _page = page;
        Checks = [.. domain.Checks.Select(c => new DomainCheckRow(c))];
    }

    public MailDomain Domain { get; }

    public string Name => Domain.Name;

    public IReadOnlyList<DomainCheckRow> Checks { get; }

    public string Tags => string.Join(" · ", new[] { Domain.IsDefault ? "Default domain" : null, Domain.IsInitial ? "Microsoft's initial domain" : null }.OfType<string>());

    public bool HasTags => Tags.Length > 0;

    public string StateText => Domain.Worst switch
    {
        CheckState.Problem => Checks.Count(c => c.Check.State == CheckState.Problem) is var n && n == 1 ? "1 problem" : $"{Checks.Count(c => c.Check.State == CheckState.Problem)} problems",
        CheckState.Warning => "Could be better",
        _ => "All good",
    };

    public BadgeKind StateKind => KindOf(Domain.Worst);

    internal static BadgeKind KindOf(CheckState state) => state switch
    {
        CheckState.Good => BadgeKind.Success,
        CheckState.Warning => BadgeKind.Warning,
        CheckState.Problem => BadgeKind.Danger,
        _ => BadgeKind.Neutral,
    };

    ChangeTarget Target => DkimChange.TargetFor(Domain.Name);

    ChangeOp? Pending(string key) => _page.Pipeline?.PendingFor(Domain.Name, key);

    bool Editable => _page.CanEdit && !Domain.IsInitial;

    /// <summary>No DKIM setup yet, and it sends mail: create the keys.</summary>
    public bool CanCreateKeys => Editable && Domain.Dkim is null && !Domain.Parked;

    public bool ShowTurnOn => Editable && Domain.Dkim is { Enabled: false };

    /// <summary>Exchange refuses signing until both CNAMEs are in DNS, so the button waits for them.</summary>
    public bool CanTurnOn => ShowTurnOn && (MailDomainChecks.CanEnableDkim(Domain) || Pending("dkim") is SetDkimSigning);

    public string TurnOnTip => CanTurnOn ? "Start signing mail from this domain (added to Pending Changes)"
        : "Publish the two CNAME records first; this turns on once DNS shows them (Refresh to check)";

    public bool ShowTurnOff => Editable && Domain.Dkim is { Enabled: true };

    public bool CanRotate => Editable && Domain.Dkim is { Enabled: true };

    public string CreateText => Pending("dkim") is CreateDkimKeys ? "Undo Set Up" : "Set Up DKIM";

    public string TurnOnText => Pending("dkim") is SetDkimSigning ? "Undo Turn On" : "Turn Signing On";

    public string TurnOffText => Pending("dkim") is SetDkimSigning ? "Undo Turn Off" : "Turn Signing Off";

    public string RotateText => Pending("dkimrotate") is not null ? "Undo Rotate" : Domain.Dkim?.KeySize is < 2048 ? "Rotate to a 2048-Bit Key" : "Rotate Keys";

    public string? RotateNote => Domain.Dkim?.RotateOnDate is { } on && on > DateTimeOffset.Now ? $"A new key takes over on {on.ToLocalTime():d MMM yyyy}." : null;

    public bool HasRotateNote => RotateNote is not null;

    [RelayCommand]
    void CreateKeys() => Toggle("dkim", () => new CreateDkimKeys(Target));

    [RelayCommand]
    void TurnOn() => Toggle("dkim", () => new SetDkimSigning(Target, false, true));

    [RelayCommand]
    void TurnOff() => Toggle("dkim", () => new SetDkimSigning(Target, true, false));

    [RelayCommand]
    void Rotate() => Toggle("dkimrotate", () => new RotateDkimKeys(Target));

    void Toggle(string key, Func<ChangeOp> make)
    {
        if (!Editable || _page.Pipeline is not { } pipeline)
            return;
        if (Pending(key) is { } pending)
            pipeline.Unstage(pending);
        else
            pipeline.Stage(make());
    }

    internal void Refresh()
    {
        foreach (var name in new[] { nameof(CanTurnOn), nameof(TurnOnTip), nameof(CreateText), nameof(TurnOnText), nameof(TurnOffText), nameof(RotateText) })
            OnPropertyChanged(name);
    }
}

/// <summary>
/// Email authentication for every domain the organization sends from: SPF, DKIM, DMARC and MX, read
/// from Exchange and DNS and explained, with the records to publish. DKIM is set up and switched
/// here (staged in Pending Changes); SPF, DMARC and MX live at the domain's DNS host, so Tenantry
/// shows what to publish there.
/// </summary>
public sealed partial class MailDomainsViewModel : ObservableObject
{
    readonly IMailDomainRecords? _records;
    readonly Availability _canEdit;

    public MailDomainsViewModel(IMailDomainRecords? records, ChangePipeline? pipeline, Availability canEdit)
    {
        _records = records;
        Pipeline = pipeline;
        _canEdit = canEdit;
        if (pipeline is not null)
        {
            pipeline.Changed += (_, _) =>
            {
                foreach (var d in Domains)
                    d.Refresh();
            };
            pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is DkimChange))
                    _ = Refresh();
            };
        }
    }

    internal ChangePipeline? Pipeline { get; }

    public bool CanEdit => Pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => Pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    public ObservableCollection<MailDomainRow> Domains { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>"3 of 10 domains need attention."</summary>
    public string SummaryText
    {
        get
        {
            var own = Domains.Where(d => !d.Domain.IsInitial).ToArray();
            var problems = own.Count(d => d.Domain.Worst == CheckState.Problem);
            return !IsLoaded ? "" : problems == 0 ? $"All {own.Length} domains are set up well."
                : $"{problems} of {own.Length} domains need attention. Each problem says what to publish or switch on.";
        }
    }

    [RelayCommand]
    public async Task Refresh()
    {
        if (_records is null || IsLoading)
            return;
        IsLoading = true;
        Error = null;
        try
        {
            var domains = await _records.GetMailDomainsAsync();
            Domains.Clear();
            // Domains that need attention first.
            foreach (var d in domains.OrderBy(d => d.IsInitial).ThenBy(d => d.Worst switch { CheckState.Problem => 0, CheckState.Warning => 1, _ => 2 }))
                Domains.Add(new MailDomainRow(d, this));
            IsLoaded = true;
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"Couldn't read the domains: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
