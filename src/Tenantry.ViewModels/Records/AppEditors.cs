using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>
/// New App Registration (8c-2): a name, who can sign in, and optionally where sign-ins return.
/// Add puts it in Pending changes (with its enterprise app) and closes the popup.
/// </summary>
public sealed partial class NewAppViewModel(ChangePipeline pipeline, Action close) : ObservableObject
{
    public static IReadOnlyList<EditorChoice> Audiences { get; } =
    [
        new("AzureADMyOrg", "This Organization Only", "People with accounts here. Right for most internal tools."),
        new("AzureADMultipleOrgs", "Any Organization", "People with work or school accounts anywhere."),
        new("AzureADandPersonalMicrosoftAccount", "Any Organization and Personal Accounts", "Also personal Microsoft accounts (Outlook.com, Xbox)."),
    ];

    public static IReadOnlyList<EditorChoice> Platforms { get; } =
    [
        new("", "None Yet", "Add one later, when you know where it runs."),
        new("Web", "Web", "A website or web app with a server."),
        new("Single-Page App", "Single-Page App", "A browser app with no server of its own."),
        new("Mobile and Desktop", "Mobile and Desktop", "An app installed on phones or computers."),
    ];

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial EditorChoice Audience { get; set; } = Audiences[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlatform))]
    public partial EditorChoice Platform { get; set; } = Platforms[0];

    public bool HasPlatform => Platform.Value.Length > 0;

    [ObservableProperty]
    public partial string RedirectUri { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>What Entra would refuse: no name, or a redirect that isn't a full address (https, or http only for localhost).</summary>
    public static IReadOnlyList<string> Check(string name, string platform, string uri)
    {
        var problems = new List<string>();
        if (name.Trim().Length == 0)
            problems.Add("Give it a name.");
        if (platform.Length > 0 && uri.Trim().Length > 0)
        {
            var ok = Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var u)
                     && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) || platform == "Mobile and Desktop");
            if (!ok)
                problems.Add("The redirect URI must be a full https:// address (http:// only for localhost).");
        }
        return problems;
    }

    [RelayCommand]
    void Add()
    {
        Problems = Check(Name, Platform.Value, RedirectUri);
        if (Problems.Count > 0)
            return;
        var uri = HasPlatform && RedirectUri.Trim().Length > 0 ? RedirectUri.Trim() : null;
        pipeline.Stage(new CreateAppRegistration(new ChangeTarget(AppChange.Registration, Name.Trim(), Name.Trim(), null), Audience.Value, uri is null ? null : Platform.Value, uri));
        close();
    }

    [RelayCommand]
    void Cancel() => close();
}

/// <summary>Deleted app registrations (8c-2): each with Restore, and Delete Permanently (typed confirmation).</summary>
public static class DeletedApps
{
    public static ChangeTarget Target(DeletedApp d) => new(AppChange.Registration, d.Id, d.Name, null);

    static string Day(DateTimeOffset? when) => when is { } w ? w.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "Unknown";

    public static AppSection Section(IAppRecords? records, ChangePipeline? pipeline, bool canEdit) => new("deletedapps", "Deleted Apps", "Delete", async ct =>
    {
        if (records is null)
            return [new AppCard("Deleted Apps", EmptyText: "Deleted apps can't be read for this tenant.")];
        var deleted = await records.GetDeletedAppsAsync(ct);
        var pending = pipeline?.Pending.OfType<AppChange>().Where(c => c is RestoreApp or PurgeApp).ToArray() ?? [];
        return
        [
            new AppCard("Deleted App Registrations", "Entra keeps a deleted app registration for 30 days; restore it in that time and it comes back as it was.",
                Rows: [.. deleted.OrderByDescending(d => d.Deleted).Select(d =>
                {
                    var detail = $"Deleted {Day(d.Deleted)} · gone for good {Day(d.Gone)}";
                    var row = new AppRow { Title = d.Name, Detail = detail, Code = $"Application (client) ID {d.AppId}" };
                    var waiting = pending.FirstOrDefault(p => p.App.Id == d.Id);
                    if (!canEdit || pipeline is null)
                        return row;
                    if (waiting is not null)
                        return row.With(waiting is RestoreApp ? "Restoring" : "Deleting", "Undo", "Take the change back out of Pending Changes", () => pipeline.Unstage(waiting));
                    var restorable = row.With(null, "Delete Permanently", "Delete it for good now (you type its name to confirm)",
                        () => pipeline.Stage(new PurgeApp(Target(d), detail)));
                    return new AppRow
                    {
                        Title = restorable.Title,
                        Detail = restorable.Detail,
                        Code = restorable.Code,
                        ActionText = restorable.ActionText,
                        ActionTip = restorable.ActionTip,
                        Act = restorable.Act,
                        AltText = "Restore",
                        AltTip = "Bring it back as it was (added to Pending Changes)",
                        AltAct = () => pipeline.Stage(new RestoreApp(Target(d), detail)),
                    };
                })],
                EmptyText: "No app registrations were deleted in the last 30 days."),
        ];
    }, "Reading deleted apps…");
}
