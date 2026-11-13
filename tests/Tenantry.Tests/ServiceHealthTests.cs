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
