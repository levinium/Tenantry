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

    public QuarantinePreviewViewModel(QuarantinedMessage message, IMailSecurity? records, ChangePipeline? pipeline, Availability canEdit)
    {
        Message = message;
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public QuarantinedMessage Message { get; }

    public string Subject => Message.Subject;
    public string Sender => Message.Sender;
    public string RecipientsText => string.Join(", ", Message.Recipients);
    public string ReasonText => Message.ReasonText;
    public BadgeKind ReasonKind => Message.IsDangerous ? BadgeKind.Danger : Message.Reason == "Phish" ? BadgeKind.Warning : BadgeKind.Neutral;
    public string StatusText => MailSecurityLists.StatusOf(Message);
    public string ReceivedText => Message.Received.ToLocalTime().ToString("d MMM yyyy, h:mm tt");
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

    public bool CanRelease => CanEdit && !Message.IsReleased;

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

    public string ReleaseText => Pending is ReleaseQuarantined ? "Undo release" : "Release";

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

    public string DeleteText => Pending is DeleteQuarantined ? "Undo delete" : "Delete";

    [RelayCommand]
    void Delete()
    {
        if (!CanEdit)
            return;
        if (Pending is DeleteQuarantined pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new DeleteQuarantined(Target, Message.Sender, Message.Recipients));
    }

    ChangeTarget SenderTarget => ListEntryChange.TargetFor(ListKind.Sender, Message.Sender);

    ChangeOp? PendingBlock => _pipeline?.PendingFor(SenderTarget.Id, "entry");

    public bool IsBlockPending => PendingBlock is not null;

    public string BlockText => IsBlockPending ? "Undo block" : "Block the sender";

    [RelayCommand]
    void BlockSender()
    {
        if (!CanEdit || Message.Sender.Length == 0)
            return;
        if (PendingBlock is { } pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new AddListEntry(SenderTarget, ListKind.Sender, true, ListExpiry.Never, $"Blocked from quarantine: \"{Message.Subject}\""));
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var name in new[] { nameof(HasPending), nameof(PendingText), nameof(ReleaseText), nameof(DeleteText), nameof(IsBlockPending), nameof(BlockText) })
            OnPropertyChanged(name);
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
