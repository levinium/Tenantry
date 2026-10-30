using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>One email app (POP, Outlook on the web…) with a switch that stages the change.</summary>
public sealed partial class EmailAppRow(string name, string explanation, bool isOn, bool isPending, bool canEdit, Action<bool> set) : ObservableObject
{
    public string Name => name;
    public string Explanation => explanation;
    public bool CanEdit => canEdit;
    public bool HasPending => isPending;

    public bool IsOn
    {
        get => isOn;
        set
        {
            if (value != isOn)
                set(value);
        }
    }
}

/// <summary>Someone (or a group) on the list of who may send to a mailbox.</summary>
public sealed partial class SenderRow(string name, string? detail, string? pendingText, bool canEdit, Action remove) : ObservableObject
{
    public string Name => name;
    public string? Detail => detail;
    public bool HasDetail => detail is not null;
    public string? PendingText => pendingText;
    public bool HasPending => pendingText is not null;
    public bool CanEdit => canEdit;
    public string RemoveTip => HasPending ? "Undo this pending change" : "Take them off the list (added to Pending Changes)";

    [RelayCommand]
    void Remove() => remove();
}

/// <summary>
/// The Mail tab's settings (4b-2): limits, who can send to the mailbox, email apps, the online
/// archive, litigation hold and the retention policy. Every edit goes to Pending changes as it's made.
/// </summary>
public sealed partial class MailSection
{
    const string InternalOnly = "Only People in the Organization";
    const string Anyone = "Anyone";
    const long Mb = 1024 * 1024;

    MailboxSettings? Settings => _data?.Settings;

    public bool HasSettings => Settings is not null;

    ChangeOp? PendingFor(string key) => _pipeline?.PendingFor(_user.Id, key);

    /// <summary>"Pending: label → after" for whichever of these slots have a change waiting.</summary>
    string? PendingText(params string[] keys)
    {
        var parts = keys.Select(PendingFor).OfType<ChangeOp>().Select(p => $"{p.Label}: {p.After}").ToArray();
        return parts.Length == 0 ? null : "Pending: " + string.Join(" · ", parts);
    }

    static string MbText(long? bytes) => bytes is { } b ? $"{Math.Round((double)b / Mb):0} MB" : "No Limit";

    void RebuildSettings()
    {
        RebuildApps();
        RebuildSenders();
        foreach (var name in new[]
                 {
                     nameof(HasSettings), nameof(LimitsText), nameof(LimitsPendingText), nameof(HasLimitsPending),
                     nameof(OnlyInternal), nameof(SendersPendingText), nameof(HasSendersPending), nameof(HasAllowedSenders), nameof(SendersText),
                     nameof(ArchiveOn), nameof(ArchiveText), nameof(CanEnableAutoExpanding), nameof(ArchivePendingText), nameof(HasArchivePending),
                     nameof(HoldText), nameof(HoldNoteText), nameof(HasHoldNote), nameof(HoldPendingText), nameof(HasHoldPending), nameof(IsOnHold),
                     nameof(RetentionPolicies), nameof(RetentionPolicy), nameof(RetentionPendingText), nameof(HasRetentionPending),
                 })
            OnPropertyChanged(name);
    }

    void ResetSettingsDrafts()
    {
        if (Settings is not { } s)
            return;
        RecipientLimitDraft = (PendingFor("mbx:RecipientLimits") as SetMailboxValue)?.New ?? s.RecipientLimit;
        MaxSendDraft = Digits((PendingFor("mbx:MaxSendSize") as SetMailboxValue)?.New ?? MbText(s.MaxSendBytes));
        MaxReceiveDraft = Digits((PendingFor("mbx:MaxReceiveSize") as SetMailboxValue)?.New ?? MbText(s.MaxReceiveBytes));
        var hold = PendingFor("hold") as SetLitigationHold;
        HoldOnDraft = hold?.NewOn ?? s.HoldOn;
        HoldDaysDraft = (hold is null ? s.HoldDays : hold.NewDays)?.ToString(CultureInfo.CurrentCulture) ?? "";
        HoldNoteDraft = (hold is null ? s.HoldNote : hold.NewNote) ?? "";

        static string Digits(string text) => new(text.TakeWhile(char.IsDigit).ToArray());
    }

    // ---- Limits -----------------------------------------------------------------------------

    public string LimitsText => Settings is { } s
        ? $"Up to {s.RecipientLimit} recipients per message · sends messages up to {MbText(s.MaxSendBytes)} · receives up to {MbText(s.MaxReceiveBytes)}"
        : "";

    public string? LimitsPendingText => PendingText("mbx:RecipientLimits", "mbx:MaxSendSize", "mbx:MaxReceiveSize");

    public bool HasLimitsPending => LimitsPendingText is not null;

    [ObservableProperty]
    public partial string RecipientLimitDraft { get; set; } = "";

    [ObservableProperty]
    public partial string MaxSendDraft { get; set; } = "";

    [ObservableProperty]
    public partial string MaxReceiveDraft { get; set; } = "";

    partial void OnRecipientLimitDraftChanged(string value) => AutoStage(StageLimits);
    partial void OnMaxSendDraftChanged(string value) => AutoStage(StageLimits);
    partial void OnMaxReceiveDraftChanged(string value) => AutoStage(StageLimits);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLimitsError))]
    public partial string? LimitsError { get; private set; }

    public bool HasLimitsError => LimitsError is not null;

    void StageLimits()
    {
        if (!CanEdit || Settings is not { } s)
            return;
        var recipients = RecipientLimitDraft.Trim();
        var unlimited = recipients.Equals("Unlimited", StringComparison.OrdinalIgnoreCase);
        var count = 0;
        if (!unlimited && (!int.TryParse(recipients, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out count) || count is < 1 or > 1000))
        {
            LimitsError = "Recipients per message: a number from 1 to 1,000.";
            return;
        }
        if (ParseMb(MaxSendDraft) is not { } send || ParseMb(MaxReceiveDraft) is not { } receive)
        {
            LimitsError = "Message sizes: a number of MB from 1 to 150 (Exchange Online's largest).";
            return;
        }
        LimitsError = null;
        var recipientValue = unlimited ? "Unlimited" : count.ToString(CultureInfo.InvariantCulture);
        _pipeline!.StageAll(
        [
            new SetMailboxValue(Target, "RecipientLimits", "Recipients per Message", s.RecipientLimit, recipientValue, JsonValue.Create(recipientValue)),
            new SetMailboxValue(Target, "MaxSendSize", "Largest Message They Can Send", MbText(s.MaxSendBytes), $"{send} MB", JsonValue.Create($"{send} MB")),
            new SetMailboxValue(Target, "MaxReceiveSize", "Largest Message They Can Receive", MbText(s.MaxReceiveBytes), $"{receive} MB", JsonValue.Create($"{receive} MB")),
        ]);

        static int? ParseMb(string text) =>
            int.TryParse(text.Trim().TrimEnd('B', 'b').TrimEnd('M', 'm').Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out var mb) && mb is >= 1 and <= 150 ? mb : null;
    }

    // ---- Who can send to them ---------------------------------------------------------------

    /// <summary>Only senders inside the organization (as it will be once pending changes apply). Switching stages it.</summary>
    public bool OnlyInternal
    {
        get => (PendingFor("mbx:RequireSenderAuthenticationEnabled") as SetMailboxValue)?.New is { } pending
            ? pending == InternalOnly
            : Settings?.OnlyInternalSenders ?? false;
        set
        {
            if (!CanEdit || Settings is not { } s || value == OnlyInternal)
                return;
            // Turning it on stops mail from outside arriving: a caution change.
            _pipeline!.Stage(new SetMailboxValue(Target, "RequireSenderAuthenticationEnabled", "Who Can Send to Them",
                s.OnlyInternalSenders ? InternalOnly : Anyone, value ? InternalOnly : Anyone, JsonValue.Create(value), Caution: value));
        }
    }

    public ObservableCollection<SenderRow> AllowedSenders { get; } = [];

    public bool HasAllowedSenders => AllowedSenders.Count > 0;

    public string SendersText => HasAllowedSenders
        ? "Only the people and groups below can send to this mailbox; mail from anyone else bounces."
        : "No list yet. Add people or groups below to accept mail from them only.";

    public string? SendersPendingText => PendingFor("mbx:RequireSenderAuthenticationEnabled") is { } p ? $"Pending: {p.After.ToLowerInvariant()}" : null;

    public bool HasSendersPending => SendersPendingText is not null;

    public PersonPicker SenderPicker { get; }

    /// <summary>People and mail-enabled groups not already on the list.</summary>
    IEnumerable<PickOption> SenderOptions()
    {
        var listed = AllowedSenders.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return GroupAccess.People(_index).Concat(_index.AllGroups.Where(g => g.Mail is not null && !g.IsMailbox).Select(PickOption.Of))
            .Where(o => o.Id != _user.Id && !listed.Contains(o.Name));
    }

    void AddSender(PickOption option)
    {
        if (!CanEdit || Settings is null)
            return;
        _pipeline!.Stage(new SetAllowedSender(Target, option.Target(option.Type), false, true));
    }

    /// <summary>
    /// Exchange lists senders by name; each is matched to the local copy (by name, address or
    /// alias) so it shows with its address and can be removed by identity.
    /// </summary>
    void RebuildSenders()
    {
        AllowedSenders.Clear();
        if (Settings is not { } s)
            return;
        var known = new Dictionary<string, PickOption>(StringComparer.OrdinalIgnoreCase);
        if (s.AllowedSenders.Count > 0)
            foreach (var option in GroupAccess.People(_index).Concat(_index.AllGroups.Where(g => g.Mail is not null).Select(PickOption.Of)))
                foreach (var name in new[] { option.Name, option.Address, option.Address?.Split('@')[0] })
                    if (name is { Length: > 0 })
                        known.TryAdd(name, option);
        var pending = _pipeline?.Pending.OfType<SetAllowedSender>().Where(p => p.User.Id == _user.Id).ToList() ?? [];
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in s.AllowedSenders)
        {
            var target = known.TryGetValue(name, out var option) ? option.Target(option.Type) : new ChangeTarget("recipient", name, name, null);
            if (!shown.Add(target.Id))
                continue;
            var change = pending.FirstOrDefault(p => p.Key == "accept:" + target.Id.ToLowerInvariant());
            AllowedSenders.Add(new SenderRow(target.Name, target.Address, change is { New: false } ? "Removing" : null, CanEdit, () => ToggleSender(target, true)));
        }
        foreach (var add in pending.Where(p => p.New && shown.Add(p.Sender.Id)))
            AllowedSenders.Add(new SenderRow(add.Sender.Name, add.Sender.Address, "Adding", CanEdit, () => ToggleSender(add.Sender, false)));
    }

    void ToggleSender(ChangeTarget sender, bool listed)
    {
        if (!CanEdit)
            return;
        if (PendingFor("accept:" + sender.Id.ToLowerInvariant()) is { } pending)
            _pipeline!.Unstage(pending);
        else if (listed)
            _pipeline!.Stage(new SetAllowedSender(Target, sender, true, false));
    }

    // ---- Email apps -------------------------------------------------------------------------

    public ObservableCollection<EmailAppRow> AppRows { get; } = [];

    void RebuildApps()
    {
        AppRows.Clear();
        if (Settings is not { } s)
            return;
        foreach (var (parameter, name, explanation) in EmailApps.All)
        {
            var actual = s.Apps.IsOn(parameter);
            var pending = PendingFor("mailapp:" + parameter) as SetEmailApp;
            var detail = parameter == "SmtpClientAuthenticationDisabled" && s.Apps.SmtpSend is null
                ? $"{explanation}. Follows the organization's setting."
                : explanation;
            AppRows.Add(new EmailAppRow(name, detail, pending?.New ?? actual, pending is not null, CanEdit,
                on => _pipeline?.Stage(new SetEmailApp(Target, parameter, name, actual, on))));
        }
    }

    // ---- Archive ----------------------------------------------------------------------------

    /// <summary>The online archive is on (as it will be once pending changes apply). Switching stages it.</summary>
    public bool ArchiveOn
    {
        get => (PendingFor("archive") as SetArchive)?.New ?? Settings?.ArchiveOn ?? false;
        set
        {
            if (!CanEdit || Settings is not { } s || value == ArchiveOn)
                return;
            _pipeline!.Stage(new SetArchive(Target, s.ArchiveOn, value));
        }
    }

    public string ArchiveText => Settings is not { } s ? ""
        : !s.ArchiveOn ? "Off. Turning it on gives them a second mailbox, for older mail, that doesn't count against their quota."
        : $"On{(s.ArchiveUsedBytes is { } used ? $", holding {MailDetails.Size(used)}" : "")}{(s.AutoExpandingArchive ? " · grows automatically as it fills" : "")}";

    /// <summary>The archive is on and not yet auto-expanding (Microsoft offers no way back).</summary>
    public bool CanEnableAutoExpanding => CanEdit && Settings is { ArchiveOn: true, AutoExpandingArchive: false } && PendingFor("autoexpand") is null;

    public string? ArchivePendingText => PendingText("archive", "autoexpand");

    public bool HasArchivePending => ArchivePendingText is not null;

    [RelayCommand]
    void EnableAutoExpanding()
    {
        if (CanEnableAutoExpanding)
            _pipeline!.Stage(new EnableAutoExpandingArchive(Target));
    }

    // ---- Litigation hold --------------------------------------------------------------------

    public bool IsOnHold => Settings?.HoldOn ?? false;

    public string HoldText => Settings is not { } s ? ""
        : !s.HoldOn ? "Off"
        : $"On{(s.HoldSince is { } since ? $" since {since.ToLocalTime():d MMM yyyy}" : "")}{(s.HoldBy is { } by ? $", set by {by}" : "")}. Keeps every item {(s.HoldDays is { } d ? $"for {d:N0} days" : "indefinitely")}, even ones they delete.";

    public string? HoldNoteText => Settings?.HoldNote is { } note ? $"Note: {note}" : null;

    public bool HasHoldNote => HoldNoteText is not null;

    public string? HoldPendingText => PendingFor("hold") is { } p ? $"Pending: {p.After}" : null;

    public bool HasHoldPending => HoldPendingText is not null;

    [ObservableProperty]
    public partial bool HoldOnDraft { get; set; }

    [ObservableProperty]
    public partial string HoldDaysDraft { get; set; } = "";

    [ObservableProperty]
    public partial string HoldNoteDraft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHoldError))]
    public partial string? HoldError { get; private set; }

    public bool HasHoldError => HoldError is not null;

    partial void OnHoldOnDraftChanged(bool value) => AutoStage(StageHold);
    partial void OnHoldDaysDraftChanged(string value) => AutoStage(StageHold);
    partial void OnHoldNoteDraftChanged(string value) => AutoStage(StageHold);

    void StageHold()
    {
        if (!CanEdit || Settings is not { } s)
            return;
        int? days = null;
        if (HoldOnDraft && HoldDaysDraft.Trim() is { Length: > 0 } text)
        {
            if (!int.TryParse(text, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var parsed) || parsed is < 1 or > 36500)
            {
                HoldError = "Days to keep items: a number from 1 to 36,500, or leave it empty to keep them indefinitely.";
                return;
            }
            days = parsed;
        }
        HoldError = null;
        var note = HoldNoteDraft.Trim() is { Length: > 0 } n ? n : null;
        _pipeline!.Stage(HoldOnDraft
            ? new SetLitigationHold(Target, s.HoldOn, s.HoldDays, s.HoldNote, true, days, note)
            : new SetLitigationHold(Target, s.HoldOn, s.HoldDays, s.HoldNote, false, s.HoldDays, s.HoldNote));
    }

    // ---- Retention policy -------------------------------------------------------------------

    public IReadOnlyList<string> RetentionPolicies => Settings?.RetentionPolicies ?? [];

    /// <summary>The retention policy (as it will be once pending changes apply). Choosing one stages it.</summary>
    public string? RetentionPolicy
    {
        get => (PendingFor("mbx:RetentionPolicy") as SetMailboxValue)?.New ?? Settings?.RetentionPolicy;
        set
        {
            if (!CanEdit || Settings is not { } s || value is null || value == RetentionPolicy)
                return;
            _pipeline!.Stage(new SetMailboxValue(Target, "RetentionPolicy", "Retention Policy", s.RetentionPolicy ?? "None", value, JsonValue.Create(value)));
        }
    }

    public string? RetentionPendingText => PendingFor("mbx:RetentionPolicy") is { } p ? $"Pending: {p.After}" : null;

    public bool HasRetentionPending => RetentionPendingText is not null;
}
