using System.Text.Json;

namespace Tenantry.Core.Directory;

/// <summary>One of Microsoft 365's usage reports: its Graph function, and whether it takes a period.</summary>
public sealed record UsageReport(string Key, string Title, string Function, string About)
{
    public override string ToString() => Title;
}

/// <summary>A report period, as the picker shows it.</summary>
public sealed record UsagePeriod(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A report as read: one row per person, site or group, or why it couldn't be read.</summary>
public sealed record UsageReportRead(IReadOnlyList<JsonElement> Rows, string? Note = null);

/// <summary>Usage reports (15b), from Graph's reports (beta, as JSON).</summary>
public interface IUsageReportRecords
{
    Task<UsageReportRead> GetUsageReportAsync(UsageReport report, UsagePeriod period, CancellationToken cancellationToken = default);
}

/// <summary>The usage reports the admin center has, in its order.</summary>
public static class UsageReports
{
    public static IReadOnlyList<UsagePeriod> Periods { get; } =
        [new("D7", "Last 7 Days"), new("D30", "Last 30 Days"), new("D90", "Last 90 Days"), new("D180", "Last 180 Days")];

    public static IReadOnlyList<UsageReport> All { get; } =
    [
        new("active", "Active Users", "getOffice365ActiveUserDetail", "Who used each Microsoft 365 service, and when they last did."),
        new("apps", "Microsoft 365 Apps", "getM365AppUserDetail", "Who used Outlook, Word, Excel, PowerPoint, OneNote and Teams, and on which platforms."),
        new("email", "Email Activity", "getEmailActivityUserDetail", "Mail sent, received and read per person."),
        new("emailapps", "Email Apps", "getEmailAppUsageUserDetail", "Which mail apps each person used."),
        new("mailbox", "Mailbox Usage", "getMailboxUsageDetail", "Each mailbox's size against its limits."),
        new("teams", "Teams Activity", "getTeamsUserActivityUserDetail", "Messages, calls and meetings per person."),
        new("teamsdevices", "Teams Devices", "getTeamsDeviceUsageUserDetail", "Which devices each person used Teams on."),
        new("onedriveactivity", "OneDrive Activity", "getOneDriveActivityUserDetail", "Files viewed, edited, synced and shared per person."),
        new("onedriveusage", "OneDrive Usage", "getOneDriveUsageAccountDetail", "Each OneDrive's storage and files."),
        new("spactivity", "SharePoint Activity", "getSharePointActivityUserDetail", "Files viewed, edited, synced and shared per person."),
        new("spsites", "SharePoint Site Usage", "getSharePointSiteUsageDetail", "Each site's storage, files and page views."),
        new("groups", "Microsoft 365 Groups Activity", "getOffice365GroupsActivityDetail", "Each group's mail, files and storage."),
        new("forms", "Forms Activity", "getFormsUserActivityUserDetail", "Forms made and responded to per person."),
    ];
}
