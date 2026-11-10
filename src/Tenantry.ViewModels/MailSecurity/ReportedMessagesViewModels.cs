using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>Where reports go, as the picker shows it.</summary>
public sealed record ReportModeChoice(ReportMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A choice of report category or review verdict, as a picker shows it.</summary>
public sealed record SubmitChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A reported message in the preview pane: what was reported and by whom, Microsoft's verdict in
/// words, and (for a person's report) the admin's answer: phishing, spam or no threat, staged in
/// Pending Changes; Microsoft emails the person when the report settings say to.
/// </summary>
public sealed partial class SubmissionPreviewViewModel : ObservableObject, IDisposable
{
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;

    public SubmissionPreviewViewModel(ThreatSubmission submission, ChangePipeline? pipeline, Availability canEdit)
    {
        Submission = submission;
        _pipeline = pipeline;
        _canEdit = canEdit;
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public ThreatSubmission Submission { get; }

    public string Subject => Submission.Subject;
    public string KindText => Submission.Kind switch { "url" => "Link", "file" => "File", _ => "Email" };
    public string? Sender => Submission.Sender;
    public bool HasSender => Submission.Sender is not null;
    public string? Recipient => Submission.Recipient;
    public bool HasRecipient => Submission.Recipient is not null;
    public string ReportedBy => $"{(Submission.ByUser ? "Reported by" : "Sent by")} {Submission.CreatedBy ?? "someone"} {Display.Relative(Submission.Created, DateTimeOffset.Now)}";
    public string ReportedAs => ThreatSubmission.CategoryText(Submission.Category);
    public string Verdict => MailSecurityLists.ResultOf(Submission);
    public string VerdictDetail => ThreatSubmission.DetailText(Submission.ResultDetail);
    public bool HasVerdictDetail => VerdictDetail.Length > 0;

    /// <summary>A person said phishing and Microsoft said clean, or the other way round: worth a look.</summary>
    public bool Disagrees => Submission.Status == "succeeded" && Submission.ResultCategory is { } r
        && ThreatSubmission.CategoryText(r) is var verdict && verdict != ReportedAs && verdict is "Not Junk" or "No Threats Found" or "Phishing" or "Malware" or "Spam";

    public string? ReviewedText => Submission.IsReviewed
        ? $"Reviewed as {ThreatSubmission.CategoryText(Submission.ReviewedAs)} by {Submission.ReviewedBy ?? "an admin"}{(Submission.ReviewedAt is { } at ? " " + Display.Relative(at, DateTimeOffset.Now) : "")}."
        : null;

    public bool HasReviewed => ReviewedText is not null;

    public bool CanReview => Submission.CanReview && !Submission.IsReviewed && _pipeline is not null && _canEdit.IsAvailable
                             && Submission.CreatedBy is { Length: > 0 } by && by.Contains('@');

    public string ReviewNote => "Tell them what it was: they get an email with your answer when Where Reports Go says to notify reporters.";

    ChangeOp? Pending => _pipeline?.PendingFor(Submission.CreatedBy ?? "", "review:" + Submission.Id);

    public bool HasPending => Pending is not null;

    public string PendingText => Pending is ReviewSubmission r ? $"Pending: {char.ToLowerInvariant(r.After[0])}{r.After[1..]}" : "";

    [RelayCommand]
    void Review(string verdict)
    {
        if (!CanReview)
            return;
        if (Pending is ReviewSubmission pending)
        {
            _pipeline!.Unstage(pending);
            if (pending.Verdict == verdict)
                return;
        }
        var by = Submission.CreatedBy!;
        _pipeline!.Stage(new ReviewSubmission(new ChangeTarget("mailbox", by, by, by), Submission.Id, Submission.Subject, verdict));
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}

/// <summary>
/// Where Outlook's Report button sends reports: Microsoft, the organization's reporting mailbox,
/// both, or nowhere, plus the reporter's pop-ups and review emails. Read from Exchange; every
/// choice restages one change against what was read.
/// </summary>
public sealed partial class ReportRoutingEditor : ObservableObject
{
    readonly ISubmissionRecords? _records;
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;
    ReportRouting? _read;
    bool _loading;

    public ReportRoutingEditor(ISubmissionRecords? records, ChangePipeline? pipeline, Availability canEdit)
    {
        _records = records;
        _pipeline = pipeline;
        _canEdit = canEdit;
        if (_pipeline is not null)
            _pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is SetReportRouting))
                    _ = LoadAsync();
            };
    }

    public static IReadOnlyList<ReportModeChoice> Modes { get; } =
        [.. new[] { ReportMode.MicrosoftAndMailbox, ReportMode.MailboxOnly, ReportMode.MicrosoftOnly, ReportMode.Off }.Select(m => new ReportModeChoice(m, ReportRouting.ModeText(m)))];

    public ReportModeChoice SelectedMode
    {
        get => Modes.First(m => m.Mode == Mode);
        set
        {
            if (value is not null)
                Mode = value.Mode;
        }
    }

    /// <summary>The mailbox as typed; it becomes the setting on Enter or leaving the box.</summary>
    [ObservableProperty]
    public partial string MailboxDraft { get; set; } = "";

    [RelayCommand]
    void CommitMailbox() => Mailbox = MailboxDraft.Trim();

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>The settings in a line, for the collapsed card: "Microsoft and reports@…".</summary>
    public string Summary => Error is not null ? "Couldn't read them" : IsLoaded ? Current().Describe() : "Reading…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesMailbox), nameof(ModeDescription), nameof(IsOn), nameof(SelectedMode))]
    public partial ReportMode Mode { get; set; }

    public bool UsesMailbox => Mode is ReportMode.MicrosoftAndMailbox or ReportMode.MailboxOnly;

    public bool IsOn => Mode != ReportMode.Off;

    public string ModeDescription => Mode switch
    {
        ReportMode.MicrosoftAndMailbox => "Microsoft analyzes each report, and a copy goes to your reporting mailbox so you can see what people report. (Microsoft's default.)",
        ReportMode.MailboxOnly => "Reports go only to your mailbox; Microsoft doesn't see them unless an admin sends them on. Reporters are always told when you've reviewed them.",
        ReportMode.MicrosoftOnly => "Microsoft analyzes each report; nothing comes to a mailbox of yours. You still see them here.",
        _ => "People can't report messages from Outlook.",
    };

    [ObservableProperty]
    public partial string Mailbox { get; set; } = "";

    [ObservableProperty]
    public partial bool NotifyReporters { get; set; }

    [ObservableProperty]
    public partial bool AskFirst { get; set; }

    [ObservableProperty]
    public partial bool ThankAfter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    public async Task LoadAsync()
    {
        if (_records is null)
            return;
        try
        {
            var routing = await _records.GetReportRoutingAsync();
            _read = routing;
            _loading = true;
            Mode = routing.Mode;
            Mailbox = routing.Mailbox ?? "";
            MailboxDraft = Mailbox;
            NotifyReporters = routing.NotifyReporters;
            AskFirst = routing.AskFirst;
            ThankAfter = routing.ThankAfter;
            _loading = false;
            Error = null;
            IsLoaded = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Error = $"Couldn't read the report settings: {Friendly.Message(e)}";
        }
    }

    partial void OnModeChanged(ReportMode value) => Restage();
    partial void OnMailboxChanged(string value) => Restage();
    partial void OnNotifyReportersChanged(bool value) => Restage();
    partial void OnAskFirstChanged(bool value) => Restage();
    partial void OnThankAfterChanged(bool value) => Restage();

    public ReportRouting Current() => _read is null ? new(Mode, null, false, true, true, false, false)
        : _read with { Mode = Mode, Mailbox = UsesMailbox && Mailbox.Trim().Length > 0 ? Mailbox.Trim() : null, NotifyReporters = NotifyReporters, AskFirst = AskFirst, ThankAfter = ThankAfter };

    /// <summary>One change for the whole card: what was read becomes what's shown (nothing staged when they match).</summary>
    void Restage()
    {
        OnPropertyChanged(nameof(Summary));
        if (_loading || _read is null || !CanEdit)
            return;
        var next = Current();
        Problem = next.Problem;
        var target = SetReportRouting.TargetFor();
        if (_pipeline!.PendingFor(target.Id, "routing") is { } pending)
            _pipeline.Unstage(pending);
        if (Problem is null)
            _pipeline.Stage(new SetReportRouting(target, _read, next));
    }
}

/// <summary>Send a link or a file to Microsoft as harmless or harmful (a submission, read back in the list).</summary>
public sealed partial class SendToMicrosoftViewModel : ObservableObject
{
    readonly ISubmissionRecords? _records;
    readonly Func<Task> _sent;

    public SendToMicrosoftViewModel(ISubmissionRecords? records, Func<Task> sent)
    {
        _records = records;
        _sent = sent;
        Category = Categories[2];
    }

    public static IReadOnlyList<SubmitChoice> Categories { get; } = [.. ThreatSubmission.SubmitAs.Select(c => new SubmitChoice(c.Value, c.Label))];

    [ObservableProperty]
    public partial SubmitChoice Category { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    public partial bool IsSending { get; private set; }

    [RelayCommand]
    async Task SendUrl()
    {
        var url = Url.Trim();
        if (!Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0)
        {
            Note = "Type a whole link (for example https://example.com/page).";
            return;
        }
        await Send(() => _records!.SubmitUrlAsync(parsed.ToString(), Category.Value), $"the link");
        if (Note?.StartsWith("Sent", StringComparison.Ordinal) == true)
            Url = "";
    }

    [RelayCommand]
    async Task SendFile()
    {
        if (await Platform.OpenFile("Choose a File to Send to Microsoft") is not { } file)
            return;
        await Send(() => _records!.SubmitFileAsync(file.Name, file.Bytes, Category.Value), file.Name);
    }

    async Task Send(Func<Task<ThreatSubmission>> send, string what)
    {
        if (_records is null || IsSending)
            return;
        IsSending = true;
        Note = null;
        try
        {
            await send();
            Note = $"Sent {what} to Microsoft as {Category.Label.Split(' ')[0].ToLowerInvariant()}. Its verdict shows in the list when it's ready.";
            await _sent();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Microsoft didn't take it: {Friendly.Message(e)}";
        }
        finally
        {
            IsSending = false;
        }
    }
}

/// <summary>
/// Reported Messages (12d): what people reported with Outlook's Report button and what admins
/// sent to Microsoft, with Microsoft's verdicts; where reports go; and sending a link or file.
/// Read live (Graph, beta) when shown; needs the ThreatSubmission.ReadWrite.All permission.
/// </summary>
public sealed partial class ReportedMessagesViewModel : ObservableObject
{
    public ReportedMessagesViewModel(ISubmissionRecords? records, ChangePipeline? pipeline, Availability canEdit, IListStateStore? listState, Func<DateTimeOffset> now)
    {
        Routing = new ReportRoutingEditor(records, pipeline, canEdit);
        Submissions = DataListViewModel.Create(MailSecurityLists.Submissions(s => new SubmissionPreviewViewModel(s, pipeline, canEdit)),
            async ct => records is null ? [] : await records.GetSubmissionsAsync(ct), _ => { }, listState, now);
        Submissions.PrimaryAction = null;
        Submissions.HasRecordPages = false;
        Submissions.EmptyText = "Nobody has reported anything in the last 30 days.";
        Send = new SendToMicrosoftViewModel(records, () => Submissions.ReloadAsync());
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is ReviewSubmission) && IsLoaded)
                    _ = Submissions.ReloadAsync();
            };
    }

    public ReportRoutingEditor Routing { get; }

    public DataListViewModel Submissions { get; }

    public SendToMicrosoftViewModel Send { get; }

    public bool IsLoaded { get; private set; }

    public async Task LoadAsync()
    {
        if (IsLoaded)
            return;
        IsLoaded = true;
        await Task.WhenAll(Routing.LoadAsync(), Submissions.LoadAsync());
    }
}
