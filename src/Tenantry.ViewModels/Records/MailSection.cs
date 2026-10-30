using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>One of a mailbox's addresses, with what can be done to it.</summary>
public sealed partial class AddressRow(string address, bool isPrimary, string? pendingText, bool canEdit, bool canRemove, bool canMakePrimary,
    Action remove, Action makePrimary) : ObservableObject
{
    public string Address => address;
    public bool IsPrimary => isPrimary;
    public string? PendingText => pendingText;
    public bool HasPending => pendingText is not null;
    public bool CanRemove => canEdit && canRemove;
    public bool CanMakePrimary => canEdit && canMakePrimary;
    public string RemoveTip => isPrimary ? "The primary address can't be removed: make another one primary first"
        : HasPending ? "Undo this pending change" : "Remove this address (added to Pending Changes)";

    [RelayCommand]
    void Remove() => remove();

    [RelayCommand]
    void MakePrimary() => makePrimary();
}

/// <summary>
/// The Mail tab: storage against the quota, email addresses (aliases and the primary),
/// forwarding, automatic replies and address-list visibility. Read live from Exchange when first
/// shown. Every edit goes to Pending changes as it's made: switches and choices at once, text
/// when the box is left. Changes made elsewhere (the tray's Discard) reset the forms.
/// </summary>
public sealed partial class MailSection : RecordSection, IDisposable
{
    UserSummary _user;
    readonly IUserRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly IDirectoryIndex _index;
    readonly Action? _convert;
    MailDetails? _data;
    bool _loading;

    public MailSection(UserSummary user, IUserRecords? records, ChangePipeline? pipeline, Availability canEdit, IDirectoryIndex? index = null,
        Action? convert = null)
    {
        _user = user;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _index = index ?? EmptyIndex.Instance;
        _convert = convert;
        SenderPicker = new PersonPicker("Accept mail only from: type a name or group", SenderOptions, AddSender);
        CreateSharingPicker();
        Summary = SummaryFor(user.Mailbox, null);
        if (_pipeline is not null)
        {
            _pipeline.Changed += OnPendingChanged;
            _pipeline.Applied += OnApplied;
        }
    }

    public override string Key => "mail";
    public override string Title => "Mail";
    public override string Icon => "Mail";
    public override string FreshKeys => "mailbox,addresses,forwarding,forwardto,addresslist,autoreply,storage,storagestate,hold,archive,emailapps";

    public bool HasMailbox => _user.Mailbox is not null;

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    ChangeTarget Target => new("user", _user.Id, _user.DisplayName, _user.UserPrincipalName);

    static string SummaryFor(MailboxInfo? mailbox, MailDetails? data)
    {
        if (mailbox is null)
            return "No mailbox";
        var parts = new List<string>
        {
            mailbox.Forwarding == ForwardingKind.None ? "Not forwarding" : mailbox.Forwarding == ForwardingKind.External ? "Forwarding outside" : "Forwarding",
            mailbox.Aliases.Count switch { 0 => "no aliases", 1 => "1 alias", var n => $"{n} aliases" },
        };
        if (data?.AutoReply.State is AutoReplyState.On or AutoReplyState.Scheduled)
            parts.Add("automatic reply on");
        if (data?.UsedFraction is { } used)
            parts.Add($"{used:P0} used");
        return string.Join(" · ", parts);
    }

    // ---- Loading ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    public bool IsReady => !IsLoading && _data is not null;

    public override async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_data is not null || _loading || _records is null || !HasMailbox)
            return;
        _loading = true;
        IsLoading = true;
        LoadError = null;
        try
        {
            _data = await _records.GetMailAsync(_user.Id, _user.Mailbox!.PrimaryAddress ?? _user.Mail ?? _user.UserPrincipalName, cancellationToken);
            Summary = SummaryFor(_user.Mailbox, _data);
            ResetDrafts();
            Rebuild();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read their mailbox: {Friendly.Message(e)}";
        }
        finally
        {
            _loading = false;
            IsLoading = false;
        }
    }

    public override void RecordChanged(UserSummary user)
    {
        _user = user;
        Summary = SummaryFor(user.Mailbox, _data);
        OnPropertyChanged(nameof(HasMailbox));
        if (_data is null)
            return;
        _data = null;
        _ = LoadAsync();
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        RebuildContents();
        OnPropertyChanged(nameof(MailboxTypePendingText));
        OnPropertyChanged(nameof(HasMailboxTypePending));
        if (_data is null)
            return;
        if (!_staging)
            ResetDrafts();
        Rebuild();
    }

    bool _resetting;
    bool _staging;

    /// <summary>Stages a form after one of its fields changed (not while the forms are being reset).</summary>
    void AutoStage(Action stage)
    {
        if (_resetting || _data is null || !CanEdit)
            return;
        _staging = true;
        try
        {
            stage();
        }
        finally
        {
            _staging = false;
        }
    }

    partial void OnForwardOnChanged(bool value) => AutoStage(StageForwarding);
    partial void OnForwardToChanged(string value) => AutoStage(StageForwarding);
    partial void OnKeepCopyChanged(bool value) => AutoStage(StageForwarding);
    partial void OnReplyStateChanged(AutoReplyState value) => AutoStage(StageAutoReply);
    partial void OnStartDateChanged(DateTime? value) => AutoStage(StageAutoReply);
    partial void OnStartTimeChanged(TimeSpan? value) => AutoStage(StageAutoReply);
    partial void OnEndDateChanged(DateTime? value) => AutoStage(StageAutoReply);
    partial void OnEndTimeChanged(TimeSpan? value) => AutoStage(StageAutoReply);
    partial void OnInternalMessageChanged(string value) => AutoStage(StageAutoReply);
    partial void OnSameOutsideChanged(bool value) => AutoStage(StageAutoReply);
    partial void OnExternalMessageChanged(string value) => AutoStage(StageAutoReply);
    partial void OnAudienceChanged(AutoReplyAudience value) => AutoStage(StageAutoReply);

    void Rebuild()
    {
        RebuildAddresses();
        RebuildSettings();
        foreach (var name in new[]
                 {
                     nameof(IsReady), nameof(UsedText), nameof(UsedPercent), nameof(ItemsText), nameof(IsNearlyFull), nameof(HasStorage),
                     nameof(ForwardingText), nameof(ForwardingPendingText), nameof(HasForwardingPending), nameof(IsForwardingOutside),
                     nameof(AutoReplyText), nameof(AutoReplyPendingText), nameof(HasAutoReplyPending),
                     nameof(ShowInAddressList), nameof(AddressListPendingText), nameof(HasAddressListPending), nameof(Domains),
                     nameof(IsSharedMailbox), nameof(MailboxTypeText), nameof(ConvertText), nameof(MailboxTypePendingText), nameof(HasMailboxTypePending),
                 })
            OnPropertyChanged(name);
    }

    // ---- Storage ----------------------------------------------------------------------------

    public bool HasStorage => _data?.UsedBytes is not null;

    public string UsedText => _data is { UsedBytes: { } used } d
        ? d.QuotaBytes is { } quota ? $"{MailDetails.Size(used)} of {MailDetails.Size(quota)}" : $"{MailDetails.Size(used)} (no limit)"
        : "Not known yet";

    public double UsedPercent => Math.Clamp((_data?.UsedFraction ?? 0) * 100, 0, 100);

    public bool IsNearlyFull => _data?.UsedFraction >= 0.9;

    public string ItemsText => _data?.Items is { } items ? $"{items:N0} items · {_data.UsedFraction ?? 0:P0} used" : "";

    // ---- Addresses --------------------------------------------------------------------------

    public ObservableCollection<AddressRow> Addresses { get; } = [];

    public IReadOnlyList<string> Domains => _data?.Domains ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAliasError))]
    public partial string? AliasError { get; private set; }

    public bool HasAliasError => AliasError is not null;

    [ObservableProperty]
    public partial string NewAliasName { get; set; } = "";

    [ObservableProperty]
    public partial string? NewAliasDomain { get; set; }

    SetPrimaryAddress? PendingPrimary => _pipeline?.PendingFor(_user.Id, "primary") as SetPrimaryAddress;

    void RebuildAddresses()
    {
        Addresses.Clear();
        if (_data is not { } data)
            return;
        var pendingPrimary = PendingPrimary;
        var all = new[] { data.PrimaryAddress }.Concat(data.Aliases).ToList();
        foreach (var add in _pipeline?.Pending.OfType<SetMailAlias>().Where(p => p.User.Id == _user.Id && p.New) ?? [])
            if (!all.Contains(add.Address, StringComparer.OrdinalIgnoreCase))
                all.Add(add.Address);
        var primary = pendingPrimary?.New ?? data.PrimaryAddress;
        foreach (var address in all.OrderByDescending(a => string.Equals(a, primary, StringComparison.OrdinalIgnoreCase))
                     .ThenBy(a => a, StringComparer.OrdinalIgnoreCase))
        {
            var isPrimary = string.Equals(address, primary, StringComparison.OrdinalIgnoreCase);
            var pending = _pipeline?.PendingFor(_user.Id, "alias:" + address.ToLowerInvariant()) as SetMailAlias;
            var text = pending is { New: true } ? "Adding"
                : pending is { New: false } ? "Removing"
                : isPrimary && pendingPrimary is not null ? "Becomes Primary"
                : string.Equals(address, pendingPrimary?.Old, StringComparison.OrdinalIgnoreCase) ? "Stops Being Primary"
                : null;
            var captured = address;
            Addresses.Add(new AddressRow(address, isPrimary, text, CanEdit,
                canRemove: !isPrimary,
                canMakePrimary: !isPrimary && pending is not { New: false },
                remove: () => ToggleAlias(captured),
                makePrimary: () => MakePrimary(captured)));
        }
        NewAliasDomain ??= data.Domains.FirstOrDefault();
    }

    void ToggleAlias(string address)
    {
        if (!CanEdit || _data is null)
            return;
        if (_pipeline!.PendingFor(_user.Id, "alias:" + address.ToLowerInvariant()) is { } pending)
            _pipeline.Unstage(pending);
        else
            _pipeline.Stage(new SetMailAlias(Target, address, true, false));
    }

    void MakePrimary(string address)
    {
        if (!CanEdit || _data is null)
            return;
        // A primary chosen from a pending new alias still works: Exchange adds it first.
        _pipeline!.Stage(new SetPrimaryAddress(Target, _data.PrimaryAddress, address));
    }

    [RelayCommand]
    void AddAlias()
    {
        if (!CanEdit || _data is null)
            return;
        var name = NewAliasName.Trim();
        var domain = NewAliasDomain ?? "";
        if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c is '@' or ',' or ';' or '<' or '>' or '"') || name.StartsWith('.') || name.EndsWith('.'))
        {
            AliasError = "Type the part before the @: letters, numbers, dots, dashes or underscores.";
            return;
        }
        var address = $"{name}@{domain}";
        if (Addresses.Any(a => string.Equals(a.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            AliasError = $"{address} is already one of their addresses.";
            return;
        }
        AliasError = null;
        NewAliasName = "";
        _pipeline!.Stage(new SetMailAlias(Target, address, false, true));
    }

    // ---- Forwarding -------------------------------------------------------------------------

    SetForwarding? PendingForwarding => _pipeline?.PendingFor(_user.Id, "forwarding") as SetForwarding;

    public string ForwardingText => _data?.ForwardingTo is { } to
        ? $"Forwarding to {to}{(_data.KeepCopy ? ", keeping a copy" : ", no copy kept")}"
        : "Not Forwarding";

    public bool IsForwardingOutside => _data?.ForwardingTo is { } to && !IsInternal(to);

    public string? ForwardingPendingText => PendingForwarding is { } p ? $"Pending: {p.After}" : null;

    public bool HasForwardingPending => PendingForwarding is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForwardDraftOutside))]
    public partial bool ForwardOn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ForwardDraftOutside))]
    public partial string ForwardTo { get; set; } = "";

    [ObservableProperty]
    public partial bool KeepCopy { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasForwardError))]
    public partial string? ForwardError { get; private set; }

    public bool HasForwardError => ForwardError is not null;

    /// <summary>The address typed is outside the organization: shown as a warning before staging.</summary>
    public bool ForwardDraftOutside => ForwardOn && ForwardTo.Contains('@') && !IsInternal(ForwardTo.Trim());

    bool IsInternal(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 0)
            return true;
        var domain = address[(at + 1)..];
        return Domains.Any(d => string.Equals(domain, d, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    void StageForwarding()
    {
        if (!CanEdit || _data is null)
            return;
        string? to = null;
        if (ForwardOn)
        {
            to = ForwardTo.Trim();
            if (to.Length == 0)
            {
                // Waiting for the address: nothing to stage yet.
                ForwardError = null;
                if (PendingForwarding is { } waiting)
                    _pipeline!.Unstage(waiting);
                return;
            }
            var at = to.IndexOf('@');
            if (at <= 0 || at != to.LastIndexOf('@') || at == to.Length - 1 || to.Contains(' '))
            {
                ForwardError = "Type a full email address, like name@example.com.";
                return;
            }
        }
        ForwardError = null;
        _pipeline!.Stage(new SetForwarding(Target, _data.ForwardingTo, _data.KeepCopy, to, KeepCopy, to is not null && !IsInternal(to)));
    }

    // ---- Automatic replies ------------------------------------------------------------------

    SetAutoReply? PendingAutoReply => _pipeline?.PendingFor(_user.Id, "autoreply") as SetAutoReply;

    public string AutoReplyText => _data?.AutoReply.Describe() ?? "";

    /// <summary>Replies are going out now or on a schedule (a health chip on the record).</summary>
    public bool IsAutoReplying => _data?.AutoReply.State is AutoReplyState.On or AutoReplyState.Scheduled;

    public string? AutoReplyPendingText => PendingAutoReply is { } p ? $"Pending: {p.After}" : null;

    public bool HasAutoReplyPending => PendingAutoReply is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReplyOff), nameof(ReplyOn), nameof(ReplyScheduled), nameof(ShowReplyMessages))]
    public partial AutoReplyState ReplyState { get; set; }

    public bool ReplyOff { get => ReplyState == AutoReplyState.Off; set { if (value) ReplyState = AutoReplyState.Off; } }
    public bool ReplyOn { get => ReplyState == AutoReplyState.On; set { if (value) ReplyState = AutoReplyState.On; } }
    public bool ReplyScheduled { get => ReplyState == AutoReplyState.Scheduled; set { if (value) ReplyState = AutoReplyState.Scheduled; } }
    public bool ShowReplyMessages => ReplyState != AutoReplyState.Off;

    [ObservableProperty]
    public partial DateTime? StartDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? StartTime { get; set; }

    [ObservableProperty]
    public partial DateTime? EndDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? EndTime { get; set; }

    [ObservableProperty]
    public partial string InternalMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExternalMessage))]
    public partial bool SameOutside { get; set; } = true;

    [ObservableProperty]
    public partial string ExternalMessage { get; set; } = "";

    public static IReadOnlyList<AutoReplyAudience> Audiences { get; } = [AutoReplyAudience.All, AutoReplyAudience.Known, AutoReplyAudience.None];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExternalMessage))]
    public partial AutoReplyAudience Audience { get; set; } = AutoReplyAudience.All;

    public bool ShowExternalMessage => !SameOutside && Audience != AutoReplyAudience.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplyError))]
    public partial string? ReplyError { get; private set; }

    public bool HasReplyError => ReplyError is not null;

    /// <summary>Replies are being turned on but have no message yet: shown as a prompt, not an error.</summary>
    [ObservableProperty]
    public partial bool NeedsReplyText { get; private set; }

    void StageAutoReply()
    {
        if (!CanEdit || _data is null)
            return;
        DateTimeOffset? start = null, end = null;
        if (ReplyState == AutoReplyState.Scheduled)
        {
            if (StartDate is not { } sd || EndDate is not { } ed)
            {
                ReplyError = "Choose when the replies start and stop.";
                return;
            }
            start = new DateTimeOffset(sd.Date + (StartTime ?? TimeSpan.FromHours(9)), TimeZoneInfo.Local.GetUtcOffset(sd.Date));
            end = new DateTimeOffset(ed.Date + (EndTime ?? TimeSpan.FromHours(17)), TimeZoneInfo.Local.GetUtcOffset(ed.Date));
            if (end <= start)
            {
                ReplyError = "The replies must stop after they start.";
                return;
            }
        }
        NeedsReplyText = ReplyState != AutoReplyState.Off && string.IsNullOrWhiteSpace(InternalMessage);
        if (NeedsReplyText)
        {
            ReplyError = null;
            if (PendingAutoReply is { } waiting)
                _pipeline!.Unstage(waiting);
            return;
        }
        ReplyError = null;
        var reply = ReplyState == AutoReplyState.Off
            // Turning replies off keeps the messages, as Outlook does, ready for next time.
            ? _data.AutoReply with { State = AutoReplyState.Off, Start = null, End = null }
            : new AutoReply(ReplyState, start, end, InternalMessage.Trim(), (SameOutside ? InternalMessage : ExternalMessage).Trim(), Audience);
        _pipeline!.Stage(new SetAutoReply(Target, _data.AutoReply, reply));
    }

    // ---- Address list -----------------------------------------------------------------------

    SetHiddenFromAddressList? PendingAddressList => _pipeline?.PendingFor(_user.Id, "gal") as SetHiddenFromAddressList;

    /// <summary>Shown in the address list, as it will be once pending changes apply. Switching it stages the change.</summary>
    public bool ShowInAddressList
    {
        get => !(PendingAddressList?.New ?? _data?.HiddenFromAddressList ?? false);
        set
        {
            if (!CanEdit || _data is null || value == ShowInAddressList)
                return;
            _pipeline!.Stage(new SetHiddenFromAddressList(Target, _data.HiddenFromAddressList, !value));
        }
    }

    public string? AddressListPendingText => PendingAddressList is { } p ? $"Pending: {p.After.ToLowerInvariant()}" : null;

    public bool HasAddressListPending => PendingAddressList is not null;

    // ---- Drafts -----------------------------------------------------------------------------

    /// <summary>Forms start from what's pending, else from the mailbox as it is.</summary>
    void ResetDrafts()
    {
        if (_data is not { } data)
            return;
        _resetting = true;
        try
        {
            ResetFormDrafts(data);
        }
        finally
        {
            _resetting = false;
        }
        ForwardError = ReplyError = LimitsError = HoldError = null;
        NeedsReplyText = false;
    }

    void ResetFormDrafts(MailDetails data)
    {
        var forwarding = PendingForwarding;
        ForwardOn = (forwarding is null ? data.ForwardingTo : forwarding.NewTo) is not null;
        ForwardTo = (forwarding is null ? data.ForwardingTo : forwarding.NewTo) ?? "";
        KeepCopy = forwarding?.NewKeepCopy ?? (data.ForwardingTo is null || data.KeepCopy);

        var reply = PendingAutoReply?.New ?? data.AutoReply;
        ReplyState = reply.State;
        var start = reply.Start?.ToLocalTime() ?? DateTimeOffset.Now.Date.AddDays(1).AddHours(9);
        var end = reply.End?.ToLocalTime() ?? start.AddDays(7).AddHours(8);
        StartDate = start.Date;
        StartTime = start.TimeOfDay;
        EndDate = end.Date;
        EndTime = end.TimeOfDay;
        InternalMessage = reply.Internal;
        ExternalMessage = reply.External;
        SameOutside = reply.External.Length == 0 || reply.External == reply.Internal;
        Audience = reply.Audience;
        ResetSettingsDrafts();
    }

    public void Dispose()
    {
        if (_pipeline is not null)
        {
            _pipeline.Changed -= OnPendingChanged;
            _pipeline.Applied -= OnApplied;
        }
    }
}
