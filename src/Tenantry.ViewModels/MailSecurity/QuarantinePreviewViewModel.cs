using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>A recipient a quarantined message can be released to, ticked or not.</summary>
public sealed partial class ReleaseRecipient(string address, bool released, Action changed) : ObservableObject
{
    public string Address { get; } = address;

    /// <summary>Already released to them: shown, not offered again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTick))]
    public partial bool IsReleased { get; set; } = released;

    public bool CanTick => !IsReleased;

    partial void OnIsReleasedChanged(bool value)
    {
        if (value)
            IsTicked = false;
    }

    [ObservableProperty]
    public partial bool IsTicked { get; set; } = !released;

    partial void OnIsTickedChanged(bool value) => changed();
}

/// <summary>One sender or spam check on the message, as the preview shows it.</summary>
public sealed record VerdictRow(string Check, string Result, string Meaning, BadgeKind Kind);

/// <summary>
/// A quarantined message in the preview pane: who, why, and its content on request (as plain text;
/// it may be malicious). Release it (to everyone or chosen recipients; approving a release
/// request is the same), deny a request, delete it or block the sender: each goes to Pending
/// changes. Its headers explain the sender checks and Microsoft's verdict, and it can be
/// downloaded as an .eml file.
/// </summary>
public sealed partial class QuarantinePreviewViewModel : ObservableObject, IDisposable
{
    readonly IMailSecurity? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;

    readonly Func<IEnumerable<ListEntry>>? _listed;

    /// <param name="listed">The block and allow entries read so far (none until that page has been shown).</param>
    public QuarantinePreviewViewModel(QuarantinedMessage message, IMailSecurity? records, ChangePipeline? pipeline, Availability canEdit,
        Func<IEnumerable<ListEntry>>? listed = null)
    {
        Message = message;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        _listed = listed;
        var done = message.ReleasedTo ?? [];
        ReleasedText = done.Count > 0 ? $"Already released to {string.Join(", ", done)}." : null;
        foreach (var to in message.Recipients)
            Recipients.Add(new ReleaseRecipient(to, done.Contains(to, StringComparer.OrdinalIgnoreCase), RestageRelease));
        if (_pipeline is not null)
        {
            _pipeline.Changed += OnPendingChanged;
            _pipeline.Applied += OnApplied;
            // A release staged earlier (from the list) shows its choices here.
            if (Pending is ReleaseQuarantined staged)
            {
                AllowSender = staged.AllowSender;
                ReportFalsePositive = staged.ReportFalsePositive;
                if (staged.To is { } to)
                    foreach (var r in Recipients)
                        r.IsTicked = !r.IsReleased && to.Contains(r.Address, StringComparer.OrdinalIgnoreCase);
            }
        }
        // Who it was released to only comes with a read of the message itself.
        if (records is not null && message.Recipients.Count > 1)
            _ = ReadReleasedAsync(records);
    }

    async Task ReadReleasedAsync(IMailSecurity records)
    {
        try
        {
            if (await records.GetQuarantinedAsync(Message.Identity) is not { ReleasedTo: { Count: > 0 } done })
                return;
            foreach (var r in Recipients)
                r.IsReleased = done.Contains(r.Address, StringComparer.OrdinalIgnoreCase);
            ReleasedText = $"Already released to {string.Join(", ", done)}.";
            OnPropertyChanged(nameof(CanChooseRecipients));
            OnPropertyChanged(nameof(NoneChosen));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Only a detail: the release itself skips anyone who has it already.
        }
    }

    /// <summary>
    /// A release or delete of this message went through: the page (which can stay open as a tab)
    /// says so and stops offering it again.
    /// </summary>
    void OnApplied(object? sender, IReadOnlyList<ChangeOp> applied)
    {
        foreach (var change in applied.Where(c => c.Target.Id == Message.Identity))
            Outcome = change switch
            {
                ReleaseQuarantined { To: { } to } => $"Released to {string.Join(", ", to)}.",
                ReleaseQuarantined => "Released to its recipients.",
                DenyQuarantineRelease => "Release request denied; it stays in quarantine.",
                DeleteQuarantined => "Deleted from quarantine.",
                _ => Outcome,
            };
    }

    /// <summary>What happened to it here (released, deleted), once applied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome), nameof(CanAct), nameof(CanRelease), nameof(CanDeny))]
    public partial string? Outcome { get; private set; }

    public bool HasOutcome => Outcome is not null;

    public QuarantinedMessage Message { get; }

    public string Subject => Message.Subject;
    public string Sender => Message.Sender;
    public string RecipientsText => string.Join(", ", Message.Recipients);
    public string ReasonText => Message.ReasonText;
    public BadgeKind ReasonKind => Message.IsDangerous ? BadgeKind.Danger : Message.Reason == "Phish" ? BadgeKind.Warning : BadgeKind.Neutral;
    public string StatusText => MailSecurityLists.StatusOf(Message);
    public BadgeKind StatusKind => Message.IsRequested ? BadgeKind.Info : Message.IsReleased ? BadgeKind.Success : BadgeKind.Neutral;
    public string ReceivedText => Message.Received.ToLocalTime().ToString("d MMM yyyy h:mm tt");
    public string ExpiresText => Message.Expires is { } at ? $"Deleted automatically on {at.ToLocalTime():d MMM yyyy}" : "";
    public string? Policy => Message.Policy;
    public bool HasPolicy => !string.IsNullOrEmpty(Message.Policy);

    /// <summary>"Released to amy@…" when some recipients already have it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReleased))]
    public partial string? ReleasedText { get; private set; }

    public bool HasReleased => ReleasedText is not null;

    public bool IsDangerous => Message.IsDangerous;

    public string DangerText => Message.Reason == "Malware" || Message.Reason == "SPOMalware"
        ? "Microsoft found malware in this message. Release it only if you're sure it's safe: the recipients can open the attachment."
        : "Microsoft is highly confident this is phishing. Release it only if you've confirmed with the sender that it's genuine.";

    /// <summary>A recipient asked for it: the request is waiting for an admin.</summary>
    public bool IsRequested => Message.IsRequested;

    public string RequestText => "A recipient asked for this message to be released. Approve releases it to the recipients you choose; "
                                 + "Deny keeps it in quarantine (Exchange denies it for every recipient).";

    // ---- The message itself -----------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsLoadingBody { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody))]
    public partial string? Body { get; private set; }

    public bool HasBody => Body is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachments))]
    public partial string? AttachmentsText { get; private set; }

    public bool HasAttachments => AttachmentsText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBodyError))]
    public partial string? BodyError { get; private set; }

    public bool HasBodyError => BodyError is not null;

    /// <summary>Reads the message (Microsoft logs each preview). Shown as plain text: nothing in it loads or runs.</summary>
    [RelayCommand]
    async Task ShowMessage()
    {
        if (_records is null || IsLoadingBody)
            return;
        IsLoadingBody = true;
        BodyError = null;
        try
        {
            var preview = await _records.PreviewQuarantinedAsync(Message.Identity);
            Body = preview.IsHtml ? PlainText.FromHtml(preview.Body) : preview.Body.Trim();
            if (Body.Length == 0)
                Body = "(The message has no text.)";
            AttachmentsText = preview.Attachments.Count == 0 ? null : "Attachments: " + string.Join(", ", preview.Attachments);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            BodyError = $"Couldn't read the message: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoadingBody = false;
        }
    }

    // ---- Headers: the sender checks and Microsoft's verdict, in words --------------------------

    public ObservableCollection<VerdictRow> Verdicts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeaders))]
    public partial string? Headers { get; private set; }

    public bool HasHeaders => Headers is not null;

    [ObservableProperty]
    public partial bool IsLoadingHeaders { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHeadersError))]
    public partial string? HeadersError { get; private set; }

    public bool HasHeadersError => HeadersError is not null;

    /// <summary>The full headers, shown on request under the checks.</summary>
    [ObservableProperty]
    public partial bool ShowRawHeaders { get; set; }

    public bool HasNoVerdicts => HasHeaders && Verdicts.Count == 0;

    [RelayCommand]
    async Task ShowHeaders()
    {
        if (_records is null || IsLoadingHeaders)
            return;
        IsLoadingHeaders = true;
        HeadersError = null;
        try
        {
            var text = await _records.GetQuarantineHeadersAsync(Message.Identity);
            Verdicts.Clear();
            foreach (var v in MessageHeaders.Verdicts(text))
                Verdicts.Add(new VerdictRow(v.Check, v.Result, v.Meaning, v.Good switch { true => BadgeKind.Success, false => BadgeKind.Danger, _ => BadgeKind.Neutral }));
            Headers = text;
            OnPropertyChanged(nameof(HasNoVerdicts));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            HeadersError = $"Couldn't read the headers: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoadingHeaders = false;
        }
    }

    [RelayCommand]
    void CopyHeaders()
    {
        if (Headers is not null)
            Platform.CopyText(Headers);
    }

    // ---- Send to Microsoft -----------------------------------------------------------------

    /// <summary>Sending it to Microsoft needs Graph's submissions (and a recipient to say it was for).</summary>
    public bool CanSubmit => _records is ISubmissionRecords && Message.Recipients.Count > 0;

    public static IReadOnlyList<SubmitChoice> SubmitCategories { get; } = [.. ThreatSubmission.SubmitAs.Select(c => new SubmitChoice(c.Value, c.Label))];

    /// <summary>Held wrongly: "not junk" is the likely report; otherwise what Microsoft thought it was.</summary>
    [ObservableProperty]
    public partial SubmitChoice SubmitAs { get; set; } = SubmitCategories[0];

    [ObservableProperty]
    public partial bool IsSubmitting { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubmitNote))]
    public partial string? SubmitNote { get; private set; }

    public bool HasSubmitNote => SubmitNote is not null;

    /// <summary>Sends the message to Microsoft for a verdict (it reads the message; nothing in the tenant changes).</summary>
    [RelayCommand]
    async Task Submit()
    {
        if (_records is not ISubmissionRecords submissions || IsSubmitting || !CanSubmit)
            return;
        IsSubmitting = true;
        SubmitNote = null;
        try
        {
            await submissions.SubmitQuarantinedAsync(Message.Identity, Message.Recipients[0], SubmitAs.Value);
            SubmitNote = $"Sent to Microsoft as {SubmitAs.Label.Split(' ')[0].ToLowerInvariant()}. Its verdict shows in Reported Messages when it's ready.";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SubmitNote = $"Microsoft didn't take it: {Friendly.Message(e)}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    // ---- Download --------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadNote))]
    public partial string? DownloadNote { get; private set; }

    public bool HasDownloadNote => DownloadNote is not null;

    /// <summary>Saves it as an .eml file (Microsoft records each download). It may be malicious: open it with care.</summary>
    [RelayCommand]
    async Task Download()
    {
        if (_records is null || IsDownloading)
            return;
        IsDownloading = true;
        DownloadNote = null;
        try
        {
            var bytes = await _records.ExportQuarantinedAsync(Message.Identity);
            var saved = await Platform.SaveFile(FileName(Message.Subject) + ".eml", "Email Message", "eml", bytes);
            DownloadNote = saved ? "Saved. It may be harmful: open it only on a computer you can afford to lose, or inspect it as text." : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DownloadNote = $"Couldn't download it: {Friendly.Message(e)}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public static string FileName(string subject)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var name = new string([.. subject.Select(c => bad.Contains(c) ? '-' : c)]).Trim(' ', '.', '-');
        return name.Length == 0 ? "Quarantined message" : name.Length > 80 ? name[..80].Trim() : name;
    }

    // ---- Actions ------------------------------------------------------------------------------

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    /// <summary>The actions show while it can still be changed here.</summary>
    public bool CanAct => CanEdit && Outcome is null;

    public bool CanRelease => CanAct && !Message.IsReleased && Message.NotReleasedTo.Count > 0;

    public bool CanDeny => CanAct && Message.IsRequested;

    /// <summary>More than one recipient: choose who gets it.</summary>
    public bool CanChooseRecipients => Recipients.Count(r => !r.IsReleased) > 1 || Recipients.Any(r => r.IsReleased);

    public ObservableCollection<ReleaseRecipient> Recipients { get; } = [];

    /// <summary>Null for everyone it hasn't reached yet; otherwise the ticked ones.</summary>
    IReadOnlyList<string>? ChosenRecipients
    {
        get
        {
            // Everyone still waiting is "release to all" (Exchange skips those who have it already).
            var ticked = Recipients.Where(r => r.IsTicked && !r.IsReleased).Select(r => r.Address).ToArray();
            return ticked.Length == Recipients.Count(r => !r.IsReleased) ? null : ticked;
        }
    }

    public bool NoneChosen => Recipients.All(r => !r.IsTicked || r.IsReleased);

    public bool CanReportFalsePositive => Message.CanReportFalsePositive;

    ChangeTarget Target => new("quarantine", Message.Identity, Message.Subject, null);

    ChangeOp? Pending => _pipeline?.PendingFor(Message.Identity, "quarantine");

    public bool HasPending => Pending is not null;

    public string PendingText => Pending switch
    {
        ReleaseQuarantined r => $"Pending: {char.ToLowerInvariant(r.After[0])}{r.After[1..]}",
        DenyQuarantineRelease => "Pending: deny the release request",
        DeleteQuarantined => "Pending: delete",
        _ => "",
    };

    /// <summary>Also allow the sender from now on (the Tenant Allow/Block List).</summary>
    [ObservableProperty]
    public partial bool AllowSender { get; set; }

    partial void OnAllowSenderChanged(bool value) => RestageRelease();

    /// <summary>Tell Microsoft it was wrongly caught (spam only), to improve its filtering.</summary>
    [ObservableProperty]
    public partial bool ReportFalsePositive { get; set; }

    partial void OnReportFalsePositiveChanged(bool value) => RestageRelease();

    ReleaseQuarantined NewRelease() => new(Target, Message.Sender, Message.Recipients, AllowSender, ChosenRecipients,
        ReportFalsePositive && Message.CanReportFalsePositive, Message.IsRequested);

    /// <summary>Already releasing: restage with the new choices (nobody ticked takes it back out).</summary>
    void RestageRelease()
    {
        OnPropertyChanged(nameof(NoneChosen));
        if (_pipeline is null || Pending is not ReleaseQuarantined pending)
            return;
        if (NoneChosen)
            _pipeline.Unstage(pending);
        else
            _pipeline.Stage(NewRelease());
    }

    public string ReleaseText => Pending is ReleaseQuarantined ? "Undo Release" : Message.IsRequested ? "Approve Release" : "Release";

    [RelayCommand]
    void Release()
    {
        if (!CanRelease)
            return;
        if (Pending is ReleaseQuarantined pending)
            _pipeline!.Unstage(pending);
        else if (!NoneChosen)
            _pipeline!.Stage(NewRelease());
    }

    public string DenyText => Pending is DenyQuarantineRelease ? "Undo Deny" : "Deny the Request";

    [RelayCommand]
    void Deny()
    {
        if (!CanDeny)
            return;
        if (Pending is DenyQuarantineRelease pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new DenyQuarantineRelease(Target, Message.Sender, Message.Recipients));
    }

    public string DeleteText => Pending is DeleteQuarantined ? "Undo Delete" : "Delete";

    [RelayCommand]
    void Delete()
    {
        if (!CanAct)
            return;
        if (Pending is DeleteQuarantined pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new DeleteQuarantined(Target, Message.Sender, Message.Recipients));
    }

    ChangeTarget SenderTarget => ListEntryChange.TargetFor(ListKind.Sender, Message.Sender);

    ChangeOp? PendingBlock => _pipeline?.PendingFor(SenderTarget.Id, "entry");

    public bool IsBlockPending => PendingBlock is not null;

    public string BlockText => IsBlockPending ? "Undo Block" : "Block the Sender";

    /// <summary>The sender's own entry on the block and allow lists, if Tenantry has read one: Exchange refuses a second.</summary>
    ListEntry? Listed => Message.Sender.Length == 0 ? null
        : _listed?.Invoke().FirstOrDefault(e => e.Kind == ListKind.Sender && string.Equals(e.Value, Message.Sender, StringComparison.OrdinalIgnoreCase));

    public bool CanBlock => Message.Sender.Length > 0 && (IsBlockPending || Listed is null);

    /// <summary>Why the sender can't be blocked from here, when they're listed already.</summary>
    public string? BlockNote => IsBlockPending ? null : Listed switch
    {
        { Block: true } => "This sender is already blocked.",
        { } => "This sender is on the allow list. Remove that entry in Block and Allow Lists first, then block them.",
        _ => null,
    };

    public bool HasBlockNote => BlockNote is not null;

    [RelayCommand]
    void BlockSender()
    {
        if (!CanEdit || !CanBlock)
            return;
        if (PendingBlock is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new AddListEntry(SenderTarget, ListKind.Sender, true, ListExpiry.Never, $"Blocked from quarantine: \"{Message.Subject}\""));
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var name in new[] { nameof(HasPending), nameof(PendingText), nameof(ReleaseText), nameof(DenyText), nameof(DeleteText), nameof(IsBlockPending),
                     nameof(BlockText), nameof(CanBlock), nameof(BlockNote), nameof(HasBlockNote) })
            OnPropertyChanged(name);
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
