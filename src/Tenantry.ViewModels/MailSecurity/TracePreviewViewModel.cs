using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>One step of a traced message, as the preview lists it, with any delivery error explained.</summary>
public sealed record TraceStepRow(string When, string Event, string Detail, DeliveryProblem? Problem = null)
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasProblem => Problem is not null;
    public string ProblemText => Problem is null ? "" : $"{Problem.Code}: {Problem.Meaning}";
}

/// <summary>What a traced message's preview can do beyond showing it: follow its sender or recipient, find it in quarantine.</summary>
public sealed record TraceActions(Action<string> TraceSender, Action<string> TraceRecipient, Action<TracedMessage> FindInQuarantine);

/// <summary>
/// A message from the trace results in the preview pane, with every step it went through (read when
/// shown) and any failure explained in words. Moving on to another row cancels the read, so arrowing
/// through results doesn't pile them up. From here: trace everything from the sender or to the
/// recipient, find a quarantined message, block or allow the sender, or send it to Microsoft.
/// </summary>
public sealed partial class TracePreviewViewModel : ObservableObject, IDisposable
{
    readonly IMailSecurity? _records;
    readonly CancellationTokenSource _lifetime = new();
    readonly TraceActions? _actions;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    readonly Func<IEnumerable<ListEntry>>? _listed;
    readonly Func<string, bool>? _isOwnAddress;

    public TracePreviewViewModel(TracedMessage message, IMailSecurity? records, TraceActions? actions = null, ChangePipeline? pipeline = null,
        Availability? canEdit = null, Func<IEnumerable<ListEntry>>? listed = null, Func<string, bool>? isOwnAddress = null)
    {
        _isOwnAddress = isOwnAddress;
        Message = message;
        _records = records;
        _actions = actions;
        _pipeline = pipeline;
        _canEdit = canEdit ?? Availability.Yes;
        _listed = listed;
        SubmitAs = SubmitCategories.First(c => c.Value == (message.Status == "Quarantined" ? "notJunk" : "phishing"));
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
        _ = LoadAsync();
    }

    public TracedMessage Message { get; }

    public string Subject => Message.Subject;
    public string Sender => Message.Sender;
    public string Recipient => Message.Recipient;
    public string StatusText => MailSecurityLists.StatusText(Message.Status);
    public string ReceivedText => Message.Received.ToLocalTime().ToString("d MMM yyyy h:mm:ss tt");

    public ObservableCollection<TraceStepRow> Steps { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>Read, and Exchange had no steps for it (yet).</summary>
    [ObservableProperty]
    public partial bool HasNoSteps { get; private set; }

    /// <summary>Why it didn't arrive (the first failing or delayed step), in words.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(ProblemHeading), nameof(IsOnPurpose), nameof(IsRealProblem))]
    public partial DeliveryProblem? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    public string ProblemHeading => Problem switch
    {
        null => "",
        { Code: "Automatic reply" or "Automatic forwarding" } => $"Not Sent on Purpose ({Problem.Code})",
        { Temporary: true } => $"Delayed ({Problem.Code})",
        _ => $"Why It Failed ({Problem.Code})",
    };

    /// <summary>Exchange dropped it on purpose (an automatic reply): an information note, not an error.</summary>
    public bool IsOnPurpose => Problem is { Code: "Automatic reply" or "Automatic forwarding" };

    public bool IsRealProblem => HasProblem && !IsOnPurpose;

    async Task LoadAsync()
    {
        if (_records is null)
            return;
        IsLoading = true;
        try
        {
            foreach (var e in await _records.TraceDetailAsync(Message, _lifetime.Token))
            {
                var detail = e.Detail.Trim();
                // Only failures and delays carry codes worth explaining (a 250 is fine).
                var problem = e.Event is "Fail" or "FAIL" or "Defer" or "DEFER" or "Drop" || detail.StartsWith('4') || detail.StartsWith('5')
                    ? DeliveryErrors.Explain(detail) : null;
                Steps.Add(new TraceStepRow(e.Date.ToLocalTime().ToString("h:mm:ss tt"), e.Event, detail, problem));
                Problem ??= problem is { Temporary: false } ? problem : null;
            }
            Problem ??= Steps.Select(s => s.Problem).FirstOrDefault(p => p is not null);
            HasNoSteps = Steps.Count == 0;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Moved on to another row.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"Couldn't read its steps: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---- Following it up ---------------------------------------------------------------------

    public bool HasActions => _actions is not null;

    public bool CanFindInQuarantine => _actions is not null && Message.Status is "Quarantined";

    [RelayCommand]
    void TraceSender() => _actions?.TraceSender(Message.Sender);

    [RelayCommand]
    void TraceRecipient() => _actions?.TraceRecipient(Message.Recipient);

    [RelayCommand]
    void FindInQuarantine() => _actions?.FindInQuarantine(Message);

    // ---- The sender on the block and allow lists ----------------------------------------------

    /// <summary>Outside senders only: one of the organization's own people isn't blocked or allowed here.</summary>
    public bool CanEditLists => _pipeline is not null && _canEdit.IsAvailable && Message.Sender.Length > 0 && _isOwnAddress?.Invoke(Message.Sender) != true;

    ChangeTarget SenderTarget => ListEntryChange.TargetFor(ListKind.Sender, Message.Sender);

    ChangeOp? PendingEntry => _pipeline?.PendingFor(SenderTarget.Id, "entry");

    ListEntry? Listed => _listed?.Invoke().FirstOrDefault(e => e.Kind == ListKind.Sender && string.Equals(e.Value, Message.Sender, StringComparison.OrdinalIgnoreCase));

    /// <summary>The sender is listed already (Exchange refuses a second entry), unless the change is ours to undo.</summary>
    public bool CanList => CanEditLists && (PendingEntry is not null || Listed is null);

    public string? ListedNote => PendingEntry is not null ? null : Listed switch
    {
        { Block: true } => "This sender is on the block list.",
        { } => "This sender is on the allow list.",
        _ => null,
    };

    public bool HasListedNote => ListedNote is not null;

    public string BlockText => PendingEntry is AddListEntry { Block: true } ? "Undo Block" : "Block the Sender";

    public string AllowText => PendingEntry is AddListEntry { Block: false } ? "Undo Allow" : "Allow the Sender";

    [RelayCommand]
    void BlockSender() => List(block: true);

    [RelayCommand]
    void AllowSender() => List(block: false);

    void List(bool block)
    {
        if (!CanList)
            return;
        if (PendingEntry is AddListEntry pending)
        {
            _pipeline!.Unstage(pending);
            if (pending.Block == block)
                return;
        }
        _pipeline!.Stage(new AddListEntry(SenderTarget, ListKind.Sender, block, block ? ListExpiry.Never : ListExpiry.AllowDefault,
            $"{(block ? "Blocked" : "Allowed")} from message trace: \"{Message.Subject}\""));
    }

    // ---- Send to Microsoft -------------------------------------------------------------------

    /// <summary>Defender's Submissions page, for a message Tenantry can't reach.</summary>
    public const string DefenderSubmissions = "https://security.microsoft.com/reportsubmission?viewid=email";

    /// <summary>Delivered, sent to junk or quarantined, and Graph's submissions are there.</summary>
    public bool CanSubmit => _records is ISubmissionRecords && ThreatSubmission.CanSendTraced(Message);

    public static IReadOnlyList<SubmitChoice> SubmitCategories => QuarantinePreviewViewModel.SubmitCategories;

    /// <summary>Quarantined: most likely held wrongly ("not junk"). Delivered: most likely phishing that got through.</summary>
    [ObservableProperty]
    public partial SubmitChoice SubmitAs { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendNow))]
    public partial bool IsSubmitting { get; private set; }

    /// <summary>Sent once already: a second send would only make a duplicate submission.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSendNow))]
    public partial bool IsSubmitted { get; private set; }

    public bool CanSendNow => !IsSubmitting && !IsSubmitted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubmitNote))]
    public partial string? SubmitNote { get; private set; }

    public bool HasSubmitNote => SubmitNote is not null;

    /// <summary>The mailbox couldn't be opened: offer Defender's Submissions page and the ID it asks for.</summary>
    [ObservableProperty]
    public partial bool ShowDefender { get; private set; }

    public string SubmitHint => Message.Status == "Quarantined"
        ? "Sent from quarantine, so nothing in the mailbox is touched."
        : "Microsoft reads it from the recipient's mailbox, which you need access to (Full Access).";

    [RelayCommand]
    async Task Submit()
    {
        if (_records is not ISubmissionRecords submissions || !CanSubmit || !CanSendNow)
            return;
        IsSubmitting = true;
        SubmitNote = null;
        ShowDefender = false;
        try
        {
            await submissions.SubmitTracedAsync(Message, SubmitAs.Value, _lifetime.Token);
            IsSubmitted = true;
            SubmitNote = $"Sent to Microsoft as {SubmitAs.Label.Split(' ')[0].ToLowerInvariant()}. Its verdict shows in Reported Messages when it's ready.";
        }
        catch (MailboxNotOpenException e)
        {
            SubmitNote = e.Message;
            ShowDefender = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Moved on to another row.
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

    [RelayCommand]
    void OpenDefender() => Platform.OpenUrl(DefenderSubmissions);

    /// <summary>Defender's form asks for the Network Message ID, which is the trace ID.</summary>
    [RelayCommand]
    void CopyNetworkMessageId() => Platform.CopyText(Message.TraceId);

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var name in new[] { nameof(BlockText), nameof(AllowText), nameof(CanList), nameof(ListedNote), nameof(HasListedNote) })
            OnPropertyChanged(name);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
