using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

public static partial class BulkActions
{
    // ---- Quarantine (12a) ---------------------------------------------------------------------

    public static ChangeTarget QuarantineTarget(QuarantinedMessage m) => new("quarantine", m.Identity, m.Subject, null);

    static readonly BulkChoice[] ReleaseChoices =
    [
        new("Release Only", "release"),
        new("Release, and Allow Each Sender from Now On", "allow"),
        new("Release, and Tell Microsoft the Spam Isn't Spam", "report"),
    ];

    /// <param name="selected">The quarantined messages checked right now (read each time).</param>
    public static IReadOnlyList<BulkAction> ForQuarantine(Func<IReadOnlyList<QuarantinedMessage>> selected) =>
    [
        new("Release", "Deliver each to everyone it hasn't reached yet. Release requests among them are approved.", "MailArrowForward",
            (action, _) => Task.FromResult(Plan(selected(), m =>
                m.IsReleased || m.NotReleasedTo.Count == 0 ? (null, $"{m.Subject}: released already")
                : (new ReleaseQuarantined(QuarantineTarget(m), m.Sender, m.Recipients, (string?)action.Choice?.Value == "allow", null,
                    (string?)action.Choice?.Value == "report" && m.CanReportFalsePositive, m.IsRequested), null))),
            choiceLabel: "How", choices: ReleaseChoices),
        new("Deny Release Requests", "Turn down the requests to release them: they stay in quarantine. Messages nobody asked about are skipped.", "MailDismiss",
            (_, _) => Task.FromResult(Plan(selected(), m =>
                m.IsRequested ? (new DenyQuarantineRelease(QuarantineTarget(m), m.Sender, m.Recipients), null) : (null, $"{m.Subject}: no release request")))),
        new("Delete", "Remove them from quarantine for good.", "Delete",
            (_, _) => Task.FromResult(Plan(selected(), m => (new DeleteQuarantined(QuarantineTarget(m), m.Sender, m.Recipients), null))),
            isCaution: true),
        new("Block the Senders", "Add each sender to the organization's block list: their mail goes to quarantine from now on.", "PersonProhibited",
            (_, _) => Task.FromResult(Plan(selected().Where(m => m.Sender.Length > 0).DistinctBy(m => m.Sender.ToLowerInvariant()), m =>
                (new AddListEntry(ListEntryChange.TargetFor(ListKind.Sender, m.Sender), ListKind.Sender, true, ListExpiry.Never,
                    $"Blocked from quarantine: \"{m.Subject}\""), null))),
            isCaution: true),
    ];
}
