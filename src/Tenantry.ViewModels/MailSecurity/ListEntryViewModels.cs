using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.MailSecurity;

/// <summary>When an entry stops applying, as a choice in a list.</summary>
public enum ExpiryChoice
{
    Never,
    AfterLastUse,
    OnDate,
}

public sealed record ExpiryOption(ExpiryChoice Choice, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Add entries" on the Block and allow lists page: a kind, block or allow, one or many values
/// pasted at once (each checked for the right format), when they expire, and a note. Each valid
/// value becomes one change in Pending changes.
/// </summary>
public sealed partial class AddListEntriesViewModel : ObservableObject
{
    readonly ChangePipeline? _pipeline;
    readonly Func<IEnumerable<ListEntry>> _existing;
    readonly Func<DateTimeOffset> _now;

    public AddListEntriesViewModel(ChangePipeline? pipeline, Availability canEdit, Func<IEnumerable<ListEntry>> existing, Func<DateTimeOffset>? now = null)
    {
        _pipeline = pipeline;
        _existing = existing;
        _now = now ?? (() => DateTimeOffset.Now);
        CanEdit = pipeline is not null && canEdit.IsAvailable;
        EditReason = pipeline is null ? "This tenant can't be edited" : canEdit.Reason ?? "";
        ExpiryDate = _now().Date.AddDays(30);
        Kind = Kinds[0];
        Expiry = ExpiryOptions[0];
    }

    public bool CanEdit { get; }

    public string EditReason { get; }

    public static IReadOnlyList<Choice<ListKind>> Kinds { get; } =
        Enum.GetValues<ListKind>().Select(k => new Choice<ListKind>(k, AllowBlock.KindName(k))).ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpoof), nameof(HasExpiry), nameof(Placeholder), nameof(KindHelp))]
    public partial Choice<ListKind> Kind { get; set; }

    public bool IsSpoof => Kind.Value == ListKind.Spoof;

    /// <summary>Spoofed senders have no expiry or note in Exchange.</summary>
    public bool HasExpiry => !IsSpoof;

    public string Placeholder => Kind.Value switch
    {
        ListKind.Sender => "name@example.com or example.com, one per line",
        ListKind.Url => "example.com/path, *.example.com or example.com/*, one per line",
        ListKind.File => "SHA256 hashes, one per line",
        ListKind.IP => "2001:db8::1 or 2001:db8::/32, one per line",
        _ => "The address or domain mail claims to be from, one per line",
    };

    public string KindHelp => Kind.Value switch
    {
        ListKind.Sender => "Blocked senders' mail goes to quarantine. A domain covers everyone at it.",
        ListKind.Url => "Blocked links are stopped when someone clicks them in mail and Office apps. * matches any part of a name or path.",
        ListKind.File => "Blocked files are stopped in mail. Get a file's SHA256 hash with Get-FileHash in PowerShell.",
        ListKind.IP => "Microsoft's list takes IPv6 addresses only; IPv4 senders are blocked with connection filtering.",
        _ => "For mail that legitimately sends as your domain from elsewhere (a newsletter service), or spoofing to block.",
    };

    public static IReadOnlyList<Choice<bool>> Actions { get; } = [new(true, "Block"), new(false, "Allow")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpiryOptions))]
    public partial Choice<bool> Action { get; set; } = Actions[0];

    partial void OnActionChanged(Choice<bool> value) => Expiry = ExpiryOptions[0];

    /// <summary>Blocks can last for ever; Microsoft removes allows after a time (45 days after they last match, by default).</summary>
    public IReadOnlyList<ExpiryOption> ExpiryOptions => Action.Value
        ? [new(ExpiryChoice.Never, "Never expires"), new(ExpiryChoice.OnDate, "Until a date")]
        : [new(ExpiryChoice.AfterLastUse, "45 days after it last matches (Microsoft's default)"), new(ExpiryChoice.OnDate, "Until a date")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnDate))]
    public partial ExpiryOption Expiry { get; set; }

    public bool IsOnDate => Expiry?.Choice == ExpiryChoice.OnDate;

    [ObservableProperty]
    public partial DateTime? ExpiryDate { get; set; }

    [ObservableProperty]
    public partial string Values { get; set; } = "";

    [ObservableProperty]
    public partial string Notes { get; set; } = "";

    [ObservableProperty]
    public partial string Infrastructure { get; set; } = "";

    public static IReadOnlyList<string> SpoofTypes { get; } = ["External", "Internal"];

    [ObservableProperty]
    public partial string SpoofType { get; set; } = SpoofTypes[0];

    /// <summary>Lines that can't be added, and why.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    public bool HasResult => Result is not null;

    ListExpiry ChosenExpiry => Expiry.Choice switch
    {
        ExpiryChoice.OnDate when ExpiryDate is { } d => new ListExpiry(On: new DateTimeOffset(d.Date.AddDays(1).AddSeconds(-1))),
        ExpiryChoice.AfterLastUse => ListExpiry.AllowDefault,
        _ => ListExpiry.Never,
    };

    /// <summary>The changes the typed values would stage, and the lines that can't be added.</summary>
    public (IReadOnlyList<AddListEntry> Changes, IReadOnlyList<string> Problems) Plan()
    {
        var problems = new List<string>();
        var changes = new List<AddListEntry>();
        var kind = Kind.Value;
        if (IsOnDate && (ExpiryDate is not { } date || date.Date <= _now().Date))
            problems.Add("Choose an expiry date after today.");
        if (IsSpoof && AllowBlock.Problem(ListKind.Sender, Infrastructure) is { } infraProblem && !System.Net.IPAddress.TryParse(Infrastructure.Split('/')[0], out _))
            problems.Add($"Sending infrastructure: {infraProblem}");
        var listed = _existing().Where(e => e.Kind == kind).Select(e => e.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in AllowBlock.Split(Values))
        {
            if (AllowBlock.Problem(kind, value) is { } problem)
                problems.Add($"{value}: {problem}");
            else if (listed.Contains(value))
                problems.Add($"{value}: already on the list (change it from its row).");
            else
                changes.Add(new AddListEntry(ListEntryChange.TargetFor(kind, value), kind, Action.Value, IsSpoof ? ListExpiry.Never : ChosenExpiry,
                    IsSpoof || Notes.Trim().Length == 0 ? null : Notes.Trim(), IsSpoof ? Infrastructure.Trim() : null, IsSpoof ? SpoofType : null));
        }
        if (changes.Count == 0 && problems.Count == 0)
            problems.Add("Type or paste at least one entry.");
        return (changes, problems);
    }

    [RelayCommand]
    void Stage()
    {
        if (!CanEdit)
            return;
        var (changes, problems) = Plan();
        // A setting problem (the date, the infrastructure) stops everything; bad lines only skip themselves.
        if (problems.Any(p => p.StartsWith("Choose", StringComparison.Ordinal) || p.StartsWith("Sending", StringComparison.Ordinal)) || changes.Count == 0)
        {
            Problems = problems;
            Result = null;
            return;
        }
        _pipeline!.StageAll(changes);
        Problems = problems;
        Result = changes.Count == 1 ? "1 entry added to Pending changes." : $"{changes.Count} entries added to Pending changes.";
        Values = string.Join(Environment.NewLine, problems.Select(p => p.Split(": ")[0]).Where(v => v.Length > 0 && !v.StartsWith("Type", StringComparison.Ordinal)));
    }
}

/// <summary>An entry in the preview pane: its details, its expiry and note to change, and Remove.</summary>
public sealed partial class ListEntryPreviewViewModel : ObservableObject, IDisposable
{
    readonly ChangePipeline? _pipeline;
    readonly Availability _canEdit;

    public ListEntryPreviewViewModel(ListEntry entry, ChangePipeline? pipeline, Availability canEdit)
    {
        Entry = entry;
        _pipeline = pipeline;
        _canEdit = canEdit;
        NotesDraft = entry.Notes ?? "";
        Expiry = ExpiryOptions.First(o => o.Choice == (entry.Expires is not null ? ExpiryChoice.OnDate : entry.RemoveAfterDays is not null ? ExpiryChoice.AfterLastUse : ExpiryChoice.Never));
        ExpiryDate = entry.Expires?.LocalDateTime.Date;
        if (_pipeline is not null)
            _pipeline.Changed += OnPendingChanged;
    }

    public ListEntry Entry { get; }

    public string Value => Entry.Value;
    public string KindText => Entry.KindText;
    public string ActionText => Entry.Block ? "Blocked" : "Allowed";
    public BadgeKind ActionKind => Entry.Block ? BadgeKind.Danger : BadgeKind.Success;
    public string ExpiryText => Entry.ExpiryText;
    public string LastUsedText => Entry.LastUsed is { } at ? $"Last matched {at.ToLocalTime():d MMM yyyy}" : "Hasn't matched anything yet";
    public string AddedText => Entry.Created is { } at ? $"Added {at.ToLocalTime():d MMM yyyy}{(Entry.ModifiedBy is { } by ? $"; last changed by {by}" : "")}" : "";
    public bool IsSpoof => Entry.Kind == ListKind.Spoof;
    public string InfrastructureText => $"Sent by {Entry.Infrastructure} ({Entry.SpoofType?.ToLowerInvariant()} spoofing)";
    public bool CanChangeDetails => CanEdit && !IsSpoof;

    public bool CanEdit => _pipeline is not null && _canEdit.IsAvailable;
    public string EditReason => _pipeline is null ? "This tenant can't be edited" : _canEdit.Reason ?? "";

    ChangeTarget Target => ListEntryChange.TargetFor(Entry.Kind, Entry.Value);

    ChangeOp? Pending => _pipeline?.PendingFor(Target.Id, "entry");

    public string? PendingText => Pending switch
    {
        RemoveListEntry => "Pending: remove",
        UpdateListEntry u => $"Pending: {u.After}",
        _ => null,
    };

    public bool HasPending => PendingText is not null;

    public string RemoveText => Pending is RemoveListEntry ? "Undo remove" : "Remove from the list";

    [RelayCommand]
    void Remove()
    {
        if (!CanEdit)
            return;
        if (Pending is RemoveListEntry pending)
            _pipeline!.Unstage(pending);
        else
            _pipeline!.Stage(new RemoveListEntry(Target, Entry.Kind, Entry.Identity, Entry.Block));
    }

    public IReadOnlyList<ExpiryOption> ExpiryOptions => Entry.Block
        ? [new(ExpiryChoice.Never, "Never expires"), new(ExpiryChoice.OnDate, "Until a date")]
        : [new(ExpiryChoice.AfterLastUse, "45 days after it last matches"), new(ExpiryChoice.OnDate, "Until a date")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnDate))]
    public partial ExpiryOption Expiry { get; set; }

    public bool IsOnDate => Expiry?.Choice == ExpiryChoice.OnDate;

    [ObservableProperty]
    public partial DateTime? ExpiryDate { get; set; }

    [ObservableProperty]
    public partial string NotesDraft { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    ListExpiry CurrentExpiry => new(Entry.Expires, Entry.Expires is null ? Entry.RemoveAfterDays : null);

    ListExpiry ChosenExpiry => Expiry.Choice switch
    {
        ExpiryChoice.OnDate when ExpiryDate is { } d => new ListExpiry(On: new DateTimeOffset(d.Date.AddDays(1).AddSeconds(-1))),
        ExpiryChoice.AfterLastUse => ListExpiry.AllowDefault,
        _ => ListExpiry.Never,
    };

    /// <summary>Stages the expiry and note as edited (or takes the change back out when they match again).</summary>
    [RelayCommand]
    void SaveDetails()
    {
        if (!CanChangeDetails)
            return;
        if (IsOnDate && (ExpiryDate is not { } d || d.Date <= DateTime.Today))
        {
            Message = "Choose an expiry date after today.";
            return;
        }
        Message = null;
        // The same choice (and, for a date, the same day) keeps the entry's expiry exactly as Exchange has it.
        var unchanged = Expiry.Choice switch
        {
            ExpiryChoice.OnDate => Entry.Expires?.LocalDateTime.Date == ExpiryDate?.Date,
            ExpiryChoice.AfterLastUse => Entry.Expires is null && Entry.RemoveAfterDays is not null,
            _ => Entry.Expires is null && Entry.RemoveAfterDays is null,
        };
        var expiry = unchanged ? CurrentExpiry : ChosenExpiry;
        var notes = NotesDraft.Trim().Length == 0 ? null : NotesDraft.Trim();
        _pipeline!.Stage(new UpdateListEntry(Target, Entry.Kind, Entry.Identity, CurrentExpiry, expiry, Entry.Notes, notes));
        if (Pending is null)
            Message = "Nothing changed.";
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(RemoveText));
    }

    public void Dispose()
    {
        if (_pipeline is not null)
            _pipeline.Changed -= OnPendingChanged;
    }
}
