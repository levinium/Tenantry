using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>
/// A quarantined message in the preview pane: who, why, and its content on request (as plain text;
/// it may be malicious). Release (optionally allowing the sender from now on), delete, or block the
/// sender: each goes to Pending changes.
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
        if (_pipeline is not null)
        {
            _pipeline.Changed += OnPendingChanged;
            _pipeline.Applied += OnApplied;
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
                ReleaseQuarantined => "Released to its recipients.",
                DeleteQuarantined => "Deleted from quarantine.",
                _ => Outcome,
            };
    }

    /// <summary>What happened to it here (released, deleted), once applied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome), nameof(CanAct), nameof(CanRelease))]
    public partial string? Outcome { get; private set; }

    public bool HasOutcome => Outcome is not null;

    public QuarantinedMessage Message { get; }

    public string Subject => Message.Subject;
    public string Sender => Message.Sender;
    public string RecipientsText => string.Join(", ", Message.Recipients);
    public string ReasonText => Message.ReasonText;
    public BadgeKind ReasonKind => Message.IsDangerous ? BadgeKind.Danger : Message.Reason == "Phish" ? BadgeKind.Warning : BadgeKind.Neutral;
    public string StatusText => MailSecurityLists.StatusOf(Message);
    public string ReceivedText => Message.Received.ToLocalTime().ToString("d MMM yyyy h:mm tt");
    public string ExpiresText => Message.Expires is { } at ? $"Deleted automatically on {at.ToLocalTime():d MMM yyyy}" : "";
    public string? Policy => Message.Policy;
    public bool HasPolicy => !string.IsNullOrEmpty(Message.Policy);

    public bool IsDangerous => Message.IsDangerous;

    public string DangerText => Message.Reason == "Malware" || Message.Reason == "SPOMalware"
        ? "Microsoft found malware in this message. Release it only if you're sure it's safe: the recipients can open the attachment."
        : "Microsoft is highly confident this is phishing. Release it only if you've confirmed with the sender that it's genuine.";

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

    // ---- Actions ------------------------------------------------------------------------------

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    /// <summary>The actions show while it can still be changed here.</summary>
    public bool CanAct => CanEdit && Outcome is null;

    public bool CanRelease => CanAct && !Message.IsReleased;

    ChangeTarget Target => new("quarantine", Message.Identity, Message.Subject, null);

    ChangeOp? Pending => _pipeline?.PendingFor(Message.Identity, "quarantine");

    public bool HasPending => Pending is not null;

    public string PendingText => Pending switch
    {
        ReleaseQuarantined { AllowSender: true } => "Pending: release, and allow the sender",
        ReleaseQuarantined => "Pending: release",
        DeleteQuarantined => "Pending: delete",
        _ => "",
    };

    /// <summary>Also allow the sender from now on (the Tenant Allow/Block List).</summary>
    [ObservableProperty]
    public partial bool AllowSender { get; set; }

    partial void OnAllowSenderChanged(bool value)
    {
        // Already releasing: restage with the new choice.
        if (Pending is ReleaseQuarantined)
            _pipeline!.Stage(new ReleaseQuarantined(Target, Message.Sender, Message.Recipients, value));
    }

    public string ReleaseText => Pending is ReleaseQuarantined ? "Undo Release" : "Release";

    [RelayCommand]
    void Release()
    {
        if (!CanRelease)
            return;
        if (Pending is ReleaseQuarantined pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new ReleaseQuarantined(Target, Message.Sender, Message.Recipients, AllowSender));
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
        foreach (var name in new[] { nameof(HasPending), nameof(PendingText), nameof(ReleaseText), nameof(DeleteText), nameof(IsBlockPending), nameof(BlockText),
                     nameof(CanBlock), nameof(BlockNote), nameof(HasBlockNote) })
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
