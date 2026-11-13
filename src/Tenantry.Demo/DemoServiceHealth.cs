using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's service health and Message Center (15a): a degraded service, a few issues, and a range of posts.</summary>
public sealed partial class DemoUserRecords : IServiceHealthRecords
{
    List<MessageCenterPost>? _posts;

    public Task<ServiceHealthRead> GetServiceHealthAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        ServiceStatus S(string id, string name, string status = "serviceOperational") => new(id, name, status);
        var services = new[]
        {
            S("Exchange", "Exchange Online", "serviceDegradation"), S("microsoftteams", "Microsoft Teams", "investigating"),
            S("SharePoint", "SharePoint Online"), S("OneDriveForBusiness", "OneDrive for Business"), S("Intune", "Microsoft Intune"),
            S("OrgLiveID", "Microsoft Entra"), S("OSDPPlatform", "Microsoft 365 suite"), S("Forms", "Microsoft Forms"), S("Planner", "Planner"),
            S("PowerBIcom", "Power BI", "serviceRestored"), S("O365Client", "Microsoft 365 Apps"), S("MicrosoftDefenderforEndpoint", "Microsoft Defender for Endpoint"),
        };
        IssuePost P(int hoursAgo, string text) => new(now.AddHours(-hoursAgo), text);
        var issues = new[]
        {
            new ServiceIssue("EX812345", "Some users may see delays receiving external email", "Exchange Online", "E-Mail and calendar access", "E-Mail and calendar access",
                "Users may notice delays of up to an hour receiving email from outside the organization.", "serviceDegradation", true, false, true,
                now.AddHours(-5), null, now.AddMinutes(-40),
                [P(5, "Title: Some users may see delays receiving external email\n\nUser impact: Users may notice delays of up to an hour receiving email from outside the organization.\n\nCurrent status: We're investigating reports of delayed inbound mail and reviewing service telemetry."),
                 P(1, "Current status: We've identified a configuration change that slowed part of the mail flow infrastructure and are rolling it back. Mail queued during the delay is being delivered.\n\nNext update by: within two hours")]),
            new ServiceIssue("TM812400", "Users may be unable to join meetings from the calendar", "Microsoft Teams", "Teams Components", "Meetings",
                "Users may get an error when joining a meeting from the Teams calendar. Joining from the meeting link works.", "investigating", true, false, false,
                now.AddHours(-2), null, now.AddMinutes(-15),
                [P(2, "Current status: We're reviewing logs to understand why joining from the calendar fails for some users. Joining from the link in the invitation is a workaround.")]),
            new ServiceIssue("SP811020", "Search results may take longer to show new files", "SharePoint Online", "SharePoint Features", "Search",
                "Newly uploaded files may take up to a day to appear in search.", "serviceDegradation", false, false, false,
                now.AddDays(-2), null, now.AddHours(-6),
                [P(48, "Current status: Indexing is running slower than usual for some sites. Files can still be opened and shared."), P(6, "Current status: Indexing has caught up for most sites; we're monitoring.")]),
            new ServiceIssue("PB809870", "Some reports didn't refresh on schedule", "Power BI", "Data refresh", "Power BI",
                "Scheduled refreshes failed for some datasets.", "serviceRestored", true, true, false,
                now.AddDays(-4), now.AddDays(-3), now.AddDays(-3),
                [P(96, "Current status: We're investigating failed scheduled refreshes."), P(72, "Final status: We fixed a capacity problem and refreshes have succeeded since. Failed refreshes run at their next scheduled time.")]),
            new ServiceIssue("MO808100", "Admins may see outdated counts on the usage dashboard", "Microsoft 365 suite", "Administration", "Admin center",
                "Usage counts may lag by a few days.", "resolved", false, true, false,
                now.AddDays(-9), now.AddDays(-7), now.AddDays(-7),
                [P(216, "Current status: Usage data is delayed."), P(168, "Final status: Usage data is current again.")]),
        };
        return Task.FromResult(new ServiceHealthRead(
            [.. services.OrderBy(s => ServiceHealth.Severity(s.Status)).ThenBy(s => s.Service, StringComparer.CurrentCultureIgnoreCase)],
            [.. issues.OrderByDescending(i => i.Modified ?? i.Start)]));
    }

    List<MessageCenterPost> Posts()
    {
        if (_posts is not null)
            return _posts;
        var now = DateTimeOffset.UtcNow;
        MessageCenterPost M(string id, string title, string[] services, string category, int daysAgo, string body, string severity = "normal", string[]? tags = null,
            bool major = false, int? actInDays = null, bool read = false, bool archived = false, bool favorite = false, int? updatedDaysAgo = null) =>
            new(id, title, services, category, severity, tags ?? [], major, actInDays is { } a ? now.AddDays(a) : null, now.AddDays(-daysAgo), now.AddDays(90),
                now.AddDays(-(updatedDaysAgo ?? daysAgo)), body,
                [new("Roadmap ID", $"4{id[^5..]}"), new("Platforms", "Web, Desktop")], read, archived, favorite);
        _posts =
        [
            M("MC900101", "Retirement of basic authentication for SMTP sending", ["Exchange Online"], "planForChange", 2,
                "We're retiring basic authentication for SMTP AUTH client submission. Apps and devices that send mail with a user name and password must move to OAuth.\n\nHow this will affect your organization: Devices such as printers and scanners that send mail with basic authentication will stop sending.\n\nWhat you need to do to prepare: Review the SMTP AUTH clients report and update or replace the devices listed.",
                "high", ["Retirement", "Admin impact", "User impact"], major: true, actInDays: 60),
            M("MC900102", "New meeting recap in the calendar", ["Microsoft Teams"], "stayInformed", 4,
                "Meeting recaps will show in the Teams calendar for meetings that were recorded or transcribed.\n\nHow this will affect your organization: People will see a Recap tab on past meetings. No admin action is needed.",
                tags: ["New feature", "User impact"]),
            M("MC900103", "Update to the default sharing link for new sites", ["SharePoint Online", "OneDrive for Business"], "planForChange", 6,
                "New sites will default to the organization's sharing link setting instead of 'specific people'.\n\nWhat you need to do to prepare: Check the default sharing link setting in SharePoint settings.",
                tags: ["Updated message", "Admin impact"], updatedDaysAgo: 1, actInDays: 30),
            M("MC900104", "Action required: renew the Apple push certificate", ["Microsoft Intune"], "preventOrFixIssue", 9,
                "The Apple MDM push certificate expires soon. When it expires, Intune can't manage Apple devices until they're enrolled again.\n\nWhat you need to do: Renew the certificate in the Intune admin center with the same Apple ID.",
                "critical", ["Admin impact"], actInDays: 14, favorite: true),
            M("MC900105", "Microsoft Forms: collect files in responses for everyone", ["Microsoft Forms"], "stayInformed", 12,
                "File upload questions are now available to everyone in the organization.", tags: ["Feature update"], read: true),
            M("MC900106", "Planner: new plans start in the new Planner", ["Planner"], "stayInformed", 15,
                "New plans open in the new Planner experience. Existing plans aren't affected.", tags: ["User impact"], read: true),
            M("MC900107", "Entra: passkeys in Microsoft Authenticator available everywhere", ["Microsoft Entra"], "planForChange", 18,
                "Passkeys stored in Microsoft Authenticator can be used to sign in on any device. Turn them on in the authentication methods policy.",
                tags: ["New feature", "Admin impact"], major: true, read: true),
            M("MC900108", "Microsoft 365 Apps: monthly channel update schedule", ["Microsoft 365 Apps"], "stayInformed", 25,
                "The monthly enterprise channel moves its release day. Update rings set to a channel follow the new day automatically.", read: true, archived: true),
            M("MC900109", "Defender for Endpoint: new device timeline filters", ["Microsoft Defender for Endpoint"], "stayInformed", 30,
                "The device timeline has new filters for network and file events.", tags: ["Feature update"], read: true, archived: true),
            M("MC900110", "Teams: retirement of the classic wiki", ["Microsoft Teams"], "planForChange", 40,
                "Classic wiki tabs will stop working. Content can be exported to Loop or OneNote.\n\nWhat you need to do to prepare: Tell owners of teams that use wiki tabs.",
                "high", ["Retirement", "User impact"], major: true, actInDays: -5, read: true),
        ];
        return _posts;
    }

    public Task<MessageCenterRead> GetMessageCenterAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MessageCenterRead([.. Posts().OrderByDescending(m => m.Start)]));

    public Task SetMessageViewAsync(IReadOnlyList<string> ids, MessageView view, CancellationToken cancellationToken = default)
    {
        var posts = Posts();
        for (var i = 0; i < posts.Count; i++)
            if (ids.Contains(posts[i].Id))
                posts[i] = view switch
                {
                    MessageView.Read => posts[i] with { IsRead = true },
                    MessageView.Unread => posts[i] with { IsRead = false },
                    MessageView.Archive => posts[i] with { IsArchived = true },
                    MessageView.Unarchive => posts[i] with { IsArchived = false },
                    MessageView.Favorite => posts[i] with { IsFavorited = true },
                    _ => posts[i] with { IsFavorited = false },
                };
        return Task.CompletedTask;
    }
}
