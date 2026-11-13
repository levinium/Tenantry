using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Service health and Message Center (15a), from Graph's service announcements.</summary>
public sealed partial class GraphUserRecords : IServiceHealthRecords
{
    const string Announcements = "admin/serviceAnnouncement/";

    /// <summary>Message Center keeps about a year of posts; this many is several years' worth for any tenant.</summary>
    const int MaxPosts = 3000;

    static string? Refused(GraphException e, string what, string permission, string roles) => e.Status switch
    {
        HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
            $"Reading {what} needs the {permission} permission (grant it from Settings) and a role that can see it, such as {roles}.",
        _ => $"Couldn't read {what}: {e.Message}",
    };

    public async Task<ServiceHealthRead> GetServiceHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // The issues collection answers only its first 100 (oldest-first, with no next link), so
            // today's issues never arrive. Each service's overview with its issues expanded has them
            // all, and lists an issue under every service it affects.
            var services = new List<ServiceStatus>();
            var issues = new Dictionary<string, ServiceIssue>(StringComparer.OrdinalIgnoreCase);
            await foreach (var s in graph.GetAllAsync($"{Announcements}healthOverviews?$expand=issues", cancellationToken))
            {
                var affecting = new List<string>();
                if (s.TryGetProperty("issues", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var i in list.EnumerateArray())
                    {
                        var issue = ToIssue(i);
                        issues.TryAdd(issue.Id, issue);
                        affecting.Add(issue.Id);
                    }
                services.Add(new ServiceStatus(Str(s, "id") ?? "", Str(s, "service") ?? Str(s, "id") ?? "A service", Str(s, "status") ?? "") { IssueIds = affecting });
            }
            return new ServiceHealthRead([.. services.OrderBy(s => ServiceHealth.Severity(s.Status)).ThenBy(s => s.Service, StringComparer.CurrentCultureIgnoreCase)],
                [.. issues.Values.OrderBy(i => i.IsResolved).ThenByDescending(i => i.Modified ?? i.Start)]);
        }
        catch (GraphException e)
        {
            return new ServiceHealthRead([], [], Refused(e, "service health", "ServiceHealth.Read.All", "Service Support Administrator or Global Reader"));
        }
    }

    internal static ServiceIssue ToIssue(JsonElement i)
    {
        var posts = i.TryGetProperty("posts", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(post => new IssuePost(Date(post, "createdDateTime"),
                post.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.Object ? BodyText(d) : "")).Where(x => x.Text.Length > 0).ToArray()
            : [];
        return new ServiceIssue(Str(i, "id") ?? "", Str(i, "title") ?? "An issue", Str(i, "service") ?? "", Str(i, "feature"), Str(i, "featureGroup"),
            Str(i, "impactDescription"), Str(i, "status") ?? "", string.Equals(Str(i, "classification"), "incident", StringComparison.OrdinalIgnoreCase),
            i.TryGetProperty("isResolved", out var r) && r.ValueKind == JsonValueKind.True,
            i.TryGetProperty("highImpact", out var h) && h.ValueKind == JsonValueKind.True,
            Date(i, "startDateTime"), Date(i, "endDateTime"), Date(i, "lastModifiedDateTime"), posts);
    }

    /// <summary>An itemBody's text: HTML is flattened, never rendered.</summary>
    static string BodyText(JsonElement body)
    {
        var content = Str(body, "content") ?? "";
        return string.Equals(Str(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase) || content.Contains('<')
            ? PlainText.FromHtml(content) : content.Trim();
    }

    public async Task<MessageCenterRead> GetMessageCenterAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var posts = new List<MessageCenterPost>();
            // No $top: with one, Microsoft answers that many posts and no next link, so the rest never
            // arrive (seen live: 100 of several hundred). Without it, the pages link on as usual.
            await foreach (var m in graph.GetAllAsync($"{Announcements}messages", cancellationToken))
            {
                posts.Add(ToPost(m));
                if (posts.Count >= MaxPosts)
                    break;
            }
            return new MessageCenterRead([.. posts.OrderByDescending(m => m.Start ?? m.Modified)]);
        }
        catch (GraphException e)
        {
            return new MessageCenterRead([], Refused(e, "Message Center", "ServiceMessage.Read.All", "Message Center Reader or Global Reader"));
        }
    }

    internal static MessageCenterPost ToPost(JsonElement m)
    {
        static IReadOnlyList<string> Strings(JsonElement e, string name) => e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).OfType<string>()]
            : [];
        var view = m.TryGetProperty("viewPoint", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        bool Flag(string name) => view.ValueKind == JsonValueKind.Object && view.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.True;
        var details = m.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Array
            ? d.EnumerateArray().Select(x => new KeyValuePair<string, string>(Str(x, "name") ?? "", Str(x, "value") ?? "")).Where(x => x.Key.Length > 0 && x.Value.Length > 0).ToArray()
            : [];
        return new MessageCenterPost(Str(m, "id") ?? "", Str(m, "title") ?? "A post", Strings(m, "services"), Str(m, "category") ?? "", Str(m, "severity") ?? "normal",
            Strings(m, "tags"), m.TryGetProperty("isMajorChange", out var major) && major.ValueKind == JsonValueKind.True,
            Date(m, "actionRequiredByDateTime"), Date(m, "startDateTime"), Date(m, "endDateTime"), Date(m, "lastModifiedDateTime"),
            m.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? BodyText(b) : "",
            details, Flag("isRead"), Flag("isArchived"), Flag("isFavorited"));
    }

    public async Task SetMessageViewAsync(IReadOnlyList<string> ids, MessageView view, CancellationToken cancellationToken = default)
    {
        var action = view switch
        {
            MessageView.Read => "markRead",
            MessageView.Unread => "markUnread",
            MessageView.Archive => "archive",
            MessageView.Unarchive => "unarchive",
            MessageView.Favorite => "favorite",
            _ => "unfavorite",
        };
        // Graph takes up to 100 posts at a time.
        foreach (var chunk in ids.Chunk(100))
            await graph.PostAsync($"{Announcements}messages/{action}", new JsonObject { ["messageIds"] = new JsonArray([.. chunk.Select(id => (JsonNode)id)]) }, cancellationToken);
    }
}
