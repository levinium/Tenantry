using System.Text.Json;
using Tenantry.Core.Directory;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Organization;

namespace Tenantry.Tests;

/// <summary>Service health and Message Center (15a).</summary>
public sealed class ServiceHealthTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static DateTimeOffset Now() => DateTimeOffset.UtcNow;

    [Fact]
    public void Issues_and_posts_read_from_Graph_with_HTML_flattened_and_views_kept()
    {
        var issue = GraphUserRecords.ToIssue(J("""
            {"id":"EX1","title":"Mail delays","service":"Exchange Online","feature":"Mail flow","status":"serviceDegradation","classification":"incident",
             "isResolved":false,"highImpact":true,"startDateTime":"2026-11-12T10:00:00Z","lastModifiedDateTime":"2026-11-12T12:00:00Z",
             "posts":[{"createdDateTime":"2026-11-12T10:00:00Z","description":{"contentType":"html","content":"<p>Current status: <b>investigating</b></p><script>x()</script>"}},
                      {"createdDateTime":"2026-11-12T12:00:00Z","description":{"contentType":"text","content":"Fixed soon."}}]}
            """));
        Assert.True(issue.IsIncident);
        Assert.False(issue.IsResolved);
        Assert.Equal("Degraded", issue.StatusText);
        Assert.Equal("Current status: investigating", issue.Posts[0].Text);
        Assert.Equal("Fixed soon.", issue.Latest!.Text);

        var post = GraphUserRecords.ToPost(J("""
            {"id":"MC1","title":"Retiring something","services":["Exchange Online","Microsoft Teams"],"category":"planForChange","severity":"high",
             "tags":["Retirement"],"isMajorChange":true,"actionRequiredByDateTime":"2026-12-31T00:00:00Z","startDateTime":"2026-11-01T00:00:00Z",
             "body":{"contentType":"html","content":"<p>Do <a href=\"https://example.com/x\">this</a>.</p>"},
             "details":[{"name":"RoadmapIds","value":"123"},{"name":"Empty","value":""}],
             "viewPoint":{"isRead":true,"isArchived":false,"isFavorited":true}}
            """));
        Assert.Equal(["Exchange Online", "Microsoft Teams"], post.Services);
        Assert.Equal("Plan for Change", post.CategoryText);
        Assert.Equal("Do this [https://example.com/x].", post.Body);
        Assert.Single(post.Details);
        Assert.True(post.IsRead);
        Assert.True(post.IsFavorited);
        Assert.False(post.IsArchived);
    }

    [Fact]
    public void Statuses_read_in_words_and_problems_sort_first()
    {
        Assert.Equal("Healthy", ServiceHealth.StatusText("serviceOperational"));
        Assert.Equal("Interrupted", ServiceHealth.StatusText("serviceInterruption"));
        Assert.Equal("Some New Status", ServiceHealth.StatusText("someNewStatus"));
        Assert.True(ServiceHealth.Severity("serviceInterruption") < ServiceHealth.Severity("serviceDegradation"));
        Assert.True(ServiceHealth.Severity("investigating") < ServiceHealth.Severity("serviceOperational"));
    }

    [Fact]
    public async Task Service_health_shows_a_tile_per_service_with_its_open_issues()
    {
        var records = new DemoUserRecords(new DemoTenantDirectory(userCount: 5));
        var health = new ServiceHealthViewModel(records, null, Now);
        await health.LoadAsync();
        Assert.Null(health.Note);
        Assert.Equal("Exchange Online", health.Services[0].Name);
        Assert.Equal(1, health.Services[0].OpenIssues);
        Assert.Contains("have a problem", health.Summary, StringComparison.Ordinal);
        // The first view shows only what's still open.
        Assert.All(health.Issues.Rows.Cast<ServiceIssue>(), i => Assert.False(i.IsResolved));
    }

    [Fact]
    public void An_update_reads_as_sections_with_bullets_and_the_title_left_out()
    {
        var sections = IssuePostText.Parse("""
            Title: Users can't open mail

            User impact: Users can't open mail.

            Current status: We're rolling back a change.
            It should finish soon.

            More info: Affected services:
            - Exchange Online: Mail may be delayed.
            - A plain bullet with no lead. Really: none.

            Next update by: Friday, 3:00 PM UTC
            """);
        Assert.Equal(["User Impact", "Current Status", "More Info", "Next Update by"], sections.Select(s => s.Heading));
        Assert.Equal("We're rolling back a change. It should finish soon.", Assert.Single(sections[1].Blocks).Text);
        var bullets = sections[2].Blocks.Where(b => b.IsBullet).ToArray();
        Assert.Equal(("Exchange Online", "Mail may be delayed."), (bullets[0].Lead, bullets[0].Text));
        Assert.Null(bullets[1].Lead);
        // Text with no headings stays one paragraph.
        Assert.Equal("Fixed soon.", Assert.Single(Assert.Single(IssuePostText.Parse("Fixed soon.")).Blocks).Text);
    }

    [Fact]
    public void Degraded_is_red_everywhere_and_investigating_amber()
    {
        Assert.Equal(BadgeKind.Danger, new ServiceTile(new ServiceStatus("x", "Exchange Online", "serviceDegradation"), 1).Kind);
        Assert.Equal(BadgeKind.Danger, ServiceHealthLists.KindOf("serviceDegradation"));
        Assert.Equal(BadgeKind.Warning, ServiceHealthLists.KindOf("investigating"));
        Assert.Equal(BadgeKind.Success, ServiceHealthLists.KindOf("serviceDegradation", resolved: true));
    }

    sealed class FixedHealth(ServiceHealthRead read) : IServiceHealthRecords
    {
        public Task<ServiceHealthRead> GetServiceHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(read);
        public Task<MessageCenterRead> GetMessageCenterAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MessageCenterRead([]));
        public Task SetMessageViewAsync(IReadOnlyList<string> ids, MessageView view, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    static ServiceIssue Issue(string id, string service, bool resolved) =>
        new(id, "An issue", service, null, null, null, resolved ? "serviceRestored" : "serviceDegradation", true, resolved, false, null, null, null, []);

    [Fact]
    public async Task An_issue_posted_under_one_service_counts_for_every_service_it_affects_and_healthy_ones_hide()
    {
        // Microsoft lists a Microsoft 365 suite incident under Exchange and Teams too.
        var read = new ServiceHealthRead(
        [
            new ServiceStatus("Exchange", "Exchange Online", "serviceDegradation") { IssueIds = ["EX1", "MO1"] },
            new ServiceStatus("Teams", "Microsoft Teams", "serviceDegradation") { IssueIds = ["MO1"] },
            new ServiceStatus("Planner", "Planner", "serviceOperational") { IssueIds = ["PL1"] },
        ],
        [Issue("MO1", "Microsoft 365 suite", false), Issue("EX1", "Exchange Online", true), Issue("PL1", "Planner", true)]);
        var health = new ServiceHealthViewModel(new FixedHealth(read), null, Now);
        await health.LoadAsync();
        Assert.Equal([1, 1, 0], health.Services.Select(s => s.OpenIssues));
        Assert.Contains("1 open issue", health.Summary, StringComparison.Ordinal);
        Assert.Equal(["Exchange Online", "Microsoft Teams"], health.ShownServices.Select(s => s.Name));
        health.ToggleAllServicesCommand.Execute(null);
        Assert.Equal(3, health.ShownServices.Count);
        Assert.Equal("MO1", Assert.Single(health.Issues.Rows.Cast<ServiceIssue>()).Id);
    }

    [Fact]
    public async Task Opening_a_post_marks_it_read_and_archive_moves_it_out_of_the_inbox()
    {
        var records = new DemoUserRecords(new DemoTenantDirectory(userCount: 5));
        var center = new MessageCenterViewModel(records, null, Now);
        await center.Load();
        var list = center.Posts;
        Assert.All(list.Rows.Cast<MessageCenterPost>(), p => Assert.False(p.IsArchived));
        var unread = list.Rows.Cast<MessageCenterPost>().First(p => !p.IsRead);

        list.PreviewRow(unread);
        var preview = Assert.IsType<MessagePostPreviewViewModel>(list.PreviewModel);
        await Task.Yield();
        Assert.True((await records.GetMessageCenterAsync(TestContext.Current.CancellationToken)).Posts.Single(p => p.Id == unread.Id).IsRead);

        await preview.ToggleArchiveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(list.Rows.Cast<MessageCenterPost>(), p => p.Id == unread.Id);
        Assert.True((await records.GetMessageCenterAsync(TestContext.Current.CancellationToken)).Posts.Single(p => p.Id == unread.Id).IsArchived);
    }

    [Fact]
    public async Task Ticking_several_posts_offers_marking_them_all()
    {
        var records = new DemoUserRecords(new DemoTenantDirectory(userCount: 5));
        var center = new MessageCenterViewModel(records, null, Now);
        await center.Load();
        var list = center.Posts;
        var two = list.Rows.Take(2).ToArray();
        list.SelectedRows = two;
        var panel = Assert.IsType<MessageSelectionViewModel>(list.PreviewModel);
        await panel.SetCommand.ExecuteAsync("Favorite");
        var posts = (await records.GetMessageCenterAsync(TestContext.Current.CancellationToken)).Posts;
        Assert.All(two.Cast<MessageCenterPost>(), p => Assert.True(posts.Single(x => x.Id == p.Id).IsFavorited));
    }

    [Fact]
    public void The_area_has_every_page_in_a_section()
    {
        var pages = OrganizationAreaViewModel.Sections.SelectMany(s => s.Pages).ToArray();
        Assert.Equal(OrganizationAreaViewModel.Pages.Count, pages.Distinct().Count());
        Assert.All(OrganizationAreaViewModel.Pages, p => Assert.Contains(p, pages));
    }
}
