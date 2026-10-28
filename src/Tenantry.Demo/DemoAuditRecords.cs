using System.Text.Json.Nodes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's audit log: a search waits queued, then runs, then is done, one step each time it's
/// checked (Purview takes minutes; the demo takes moments). Records are made up from the demo's
/// people over the searched dates, on documentation-range addresses, and filtered as Purview would.
/// </summary>
public sealed partial class DemoUserRecords : IAuditRecords
{
    readonly List<AuditSearch> _auditSearches = [];

    static readonly (string Operation, string Service, string RecordType, string Object)[] AuditEvents =
    [
        ("UserLoggedIn", "AzureActiveDirectory", "azureActiveDirectoryStsLogon", "Office 365"),
        ("FileAccessed", "OneDrive", "sharePointFileOperation", "/Documents/Budget.xlsx"),
        ("FileModified", "SharePoint", "sharePointFileOperation", "/sites/projects/Shared Documents/Plan.docx"),
        ("SharingSet", "SharePoint", "sharePointSharingOperation", "/sites/projects/Shared Documents/Plan.docx"),
        ("MailItemsAccessed", "Exchange", "exchangeItemGroup", "Inbox"),
        ("New-InboxRule", "Exchange", "exchangeAdmin", "Move to Archive"),
        ("Set-Mailbox", "Exchange", "exchangeAdmin", "Front Desk"),
        ("Add member to group.", "AzureActiveDirectory", "azureActiveDirectory", "Project Hub"),
        ("Update user.", "AzureActiveDirectory", "azureActiveDirectory", "A person"),
        ("Reset user password.", "AzureActiveDirectory", "azureActiveDirectory", "A person"),
        ("MemberAdded", "MicrosoftTeams", "microsoftTeams", "Project Hub"),
    ];

    public Task<IReadOnlyList<AuditSearch>> GetSearchesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuditSearch>>([.. Enumerable.Reverse(_auditSearches)]);

    public Task<AuditSearch> StartSearchAsync(AuditSearchRequest request, CancellationToken cancellationToken = default)
    {
        var search = new AuditSearch($"aq-{Guid.NewGuid():N}", request.Name, request.Start, request.End, "notStarted", null, false, request);
        _auditSearches.Add(search);
        return Task.FromResult(search);
    }

    public async Task<AuditSearch> GetSearchAsync(string id, CancellationToken cancellationToken = default)
    {
        var at = _auditSearches.FindIndex(s => s.Id == id);
        var search = _auditSearches[at];
        search = search.Status switch
        {
            "notStarted" => search with { Status = "running" },
            "running" => search with { Status = "succeeded", ApproximateCount = (await AuditRecordsFor(search.Filters, cancellationToken)).Count },
            _ => search,
        };
        _auditSearches[at] = search;
        return search;
    }

    public async Task<IReadOnlyList<AuditRecord>> GetRecordsAsync(string id, int limit, CancellationToken cancellationToken = default)
    {
        var search = _auditSearches.Single(s => s.Id == id);
        return [.. (await AuditRecordsFor(search.Filters, cancellationToken)).Take(limit)];
    }

    async Task<IReadOnlyList<AuditRecord>> AuditRecordsFor(AuditSearchRequest request, CancellationToken cancellationToken)
    {
        var people = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Kind == UserKind.Member).Take(12).ToArray();
        var span = request.End - request.Start;
        var count = (int)Math.Clamp(span.TotalHours * 2, 10, 400);
        var records = new List<AuditRecord>();
        for (var i = 0; i < count; i++)
        {
            var person = people[i * 7 % people.Length];
            var e = AuditEvents[i * 5 % AuditEvents.Length];
            var when = request.End - TimeSpan.FromTicks(span.Ticks / count * i);
            var ip = $"198.51.100.{i * 13 % 250 + 1}";
            var json = new JsonObject
            {
                ["Id"] = $"rec-{i}",
                ["CreationTime"] = when.UtcDateTime.ToString("s"),
                ["Operation"] = e.Operation,
                ["Workload"] = e.Service,
                ["UserId"] = person.UserPrincipalName,
                ["ClientIP"] = ip,
                ["ObjectId"] = e.Object,
                ["ResultStatus"] = "Succeeded",
            }.ToJsonString();
            records.Add(new AuditRecord($"rec-{i}", when, person.UserPrincipalName, "Regular", e.Operation, e.Service, e.RecordType, e.Object, ip, json));
        }
        bool Any(IReadOnlyList<string>? filter, string? value) => filter is not { Count: > 0 } || filter.Any(f => string.Equals(f, value, StringComparison.OrdinalIgnoreCase));
        return [.. records.Where(r => Any(request.Users, r.User) && Any(request.Operations, r.Operation) && Any(request.RecordTypes, r.RecordType)
            && Any(request.IpAddresses, r.ClientIp) && Any(request.ObjectIds, r.ObjectId)
            && (string.IsNullOrWhiteSpace(request.Keyword) || r.Json.Contains(request.Keyword.Trim(), StringComparison.OrdinalIgnoreCase)))];
    }
}
