using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>
/// The demo's eDiscovery: three cases (one closed), searches in them and one outside any case, a
/// hold, and members. A search's counts are made up from its name and places, so they stay the same
/// between runs; running one again marks it done at once.
/// </summary>
public sealed partial class DemoUserRecords : IEDiscoveryRecords, IEdExports
{
    readonly List<EdAction> _actions = [];
    readonly Dictionary<string, int> _exports = [];
    List<EdCase>? _cases;
    List<EdSearch>? _searches;
    readonly Dictionary<string, EdSearchDetail> _searchDetails = new(StringComparer.OrdinalIgnoreCase);
    List<EdHold>? _holds;
    readonly Dictionary<string, List<EdMember>> _members = new(StringComparer.OrdinalIgnoreCase);

    async Task EnsureEDiscoveryAsync(CancellationToken cancellationToken)
    {
        if (_cases is not null)
            return;
        var people = (await directory.GetUsersAsync(cancellationToken)).Where(u => u.Mailbox is not null).Take(12).ToArray();
        string Address(int i) => people[i % people.Length].Mailbox?.PrimaryAddress ?? people[i % people.Length].UserPrincipalName;
        var now = DateTimeOffset.UtcNow;
        _cases =
        [
            new("case-vendor", "Vendor Contract Review", "Messages about the cleaning contract renewal.", false, now.AddDays(-40), now.AddDays(-2), "Avery Admin", null, null),
            new("case-departure", "Former Employee Inquiry", "Files and mail of someone who left in spring.", false, now.AddDays(-12), now.AddDays(-1), "Avery Admin", null, null),
            new("case-records", "Records Request 2025", "A public records request, answered.", true, now.AddDays(-300), now.AddDays(-200), "Avery Admin", now.AddDays(-200), "Avery Admin"),
        ];
        _searches = [];
        void Search(string name, string? caseName, string query, string[] mailboxes, string[] sites, int ranDaysAgo, string? description = null)
        {
            var search = new EdSearch(name, caseName, description, ranDaysAgo < 0 ? "NotStarted" : "Completed", now.AddDays(-Math.Abs(ranDaysAgo) - 1), "Avery Admin",
                ranDaysAgo < 0 ? null : now.AddDays(-ranDaysAgo), ranDaysAgo < 0 ? null : "Avery Admin");
            _searches.Add(search);
            _searchDetails[name] = Counted(search, query, mailboxes, sites, false);
        }
        Search("Contract emails", "Vendor Contract Review", "subject:\"cleaning contract\" OR \"renewal terms\"", [Address(0), Address(1), Address(2)], [], 2);
        Search("Contract files", "Vendor Contract Review", "filetype:docx AND contract", [], ["https://northgate.sharepoint.example/sites/Facilities"], 3);
        Search("Departure mailbox", "Former Employee Inquiry", "", [Address(3)], [], 1, "Everything in their mailbox.");
        Search("Departure OneDrive", "Former Employee Inquiry", "sent>=2026-01-01", [], ["https://northgate-my.sharepoint.example/personal/" + Address(3).Replace('@', '_').Replace('.', '_')], -1);
        Search("Records request answer", "Records Request 2025", "\"budget meeting\" AND sent>=2025-01-01", [EDiscovery.All], [], 210);
        Search("Phishing message", null, "subject:\"Your mailbox is full\"", [EDiscovery.All], [], 6, "Finding a phishing message that reached many people.");
        _holds =
        [
            new("hold-vendor", "Contract hold", "Vendor Contract Review", true, [Address(0), Address(1)], ["https://northgate.sharepoint.example/sites/Facilities"], false, "",
                "Success", now.AddDays(-39), now.AddDays(-39)),
        ];
        _members["Vendor Contract Review"] = [new("Avery Admin", Account), new(people[4].DisplayName, people[4].UserPrincipalName)];
        _members["Former Employee Inquiry"] = [new("Avery Admin", Account)];
        _members["Records Request 2025"] = [new("Avery Admin", Account)];
    }

    /// <summary>Made-up counts from the search's name and places, the same every time.</summary>
    static EdSearchDetail Counted(EdSearch search, string query, IReadOnlyList<string> mailboxes, IReadOnlyList<string> sites, bool publicFolders)
    {
        var seed = search.Name.Aggregate(17, (h, c) => h * 31 + c) & 0x7fffffff;
        var places = EDiscovery.IsAll(mailboxes) ? ["All mailboxes"] : mailboxes.Concat(sites).ToArray();
        var hits = search.Status == "Completed"
            ? places.Select((p, i) => new EdLocationHits(p, (seed >> i) % 400 + (query.Length == 0 ? 900 : 3), null)).ToArray()
            : [];
        var items = hits.Sum(h => h.Items);
        var bytes = items * 48_000;
        return new EdSearchDetail(search, query, mailboxes, sites, publicFolders, items, bytes, items / 40, bytes / 30,
            [.. hits.Select(h => h with { Size = EDiscovery.Size(h.Items * 48_000) })], null);
    }

    public async Task<EdDirectory> GetEDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        await EnsureEDiscoveryAsync(cancellationToken);
        return new EdDirectory([.. _cases!], [.. _searches!]);
    }

    public async Task<EdCaseDetail> GetCaseDetailAsync(EdCase eCase, CancellationToken cancellationToken = default)
    {
        await EnsureEDiscoveryAsync(cancellationToken);
        return new EdCaseDetail([.. _searches!.Where(s => s.CaseName == eCase.Name)], [.. _holds!.Where(h => h.CaseName == eCase.Name)],
            [.. _members.GetValueOrDefault(eCase.Name) ?? []]);
    }

    public async Task<EdSearchDetail> GetEdSearchAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureEDiscoveryAsync(cancellationToken);
        return _searchDetails.TryGetValue(name, out var detail) ? detail : throw new InvalidOperationException($"The search \"{name}\" wasn't found; it may have been deleted.");
    }

    public async Task<IReadOnlyList<EdAction>> GetSearchActionsAsync(string? caseName, string searchName, CancellationToken cancellationToken = default)
    {
        await EnsureEDiscoveryAsync(cancellationToken);
        return [.. _actions.Where(a => string.Equals(a.SearchName, searchName, StringComparison.OrdinalIgnoreCase)).OrderByDescending(a => a.Created)];
    }

    /// <summary>A pretend export: it moves on a third each time it's checked, then offers one small file.</summary>
    public Task<string> StartExportAsync(string caseName, string searchName, EdExportOptions options, CancellationToken cancellationToken = default)
    {
        var id = $"demo-export-{_exports.Count + 1}|{searchName}|{(options.Pst ? "pst" : "msg")}";
        _exports[id] = 0;
        return Task.FromResult(id);
    }

    public Task<EdExportProgress> GetExportAsync(string exportId, CancellationToken cancellationToken = default)
    {
        var step = _exports[exportId] = Math.Min(3, _exports.GetValueOrDefault(exportId) + 1);
        var parts = exportId.Split('|');
        return Task.FromResult(step < 3
            ? new EdExportProgress("running", step * 33, [], null)
            : new EdExportProgress("succeeded", 100, [new EdExportFile($"{parts[1]} (demo).txt", "demo://" + exportId, 120)], null));
    }

    public async Task DownloadAsync(EdExportFile file, Stream to, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        var text = System.Text.Encoding.UTF8.GetBytes("Demo export from Tenantry. A real export holds the mail and files the search found." + Environment.NewLine);
        await to.WriteAsync(text, cancellationToken);
        progress?.Report(text.Length);
    }

    public async Task StartEdSearchAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureEDiscoveryAsync(cancellationToken);
        Ran(name);
    }

    void Ran(string name)
    {
        var at = _searches!.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return;
        var ran = _searches[at] with { Status = "Completed", LastRun = DateTimeOffset.UtcNow, RunBy = "Avery Admin" };
        _searches[at] = ran;
        var d = _searchDetails[name];
        _searchDetails[name] = Counted(ran, d.Query, d.Mailboxes, d.Sites, d.PublicFolders);
    }

    static string[] Names(IEnumerable<ChangeTarget> places) => [.. places.Select(EdChange.Place)];

    /// <summary>Follows eDiscovery changes in the demo's cases, searches and holds; null for other changes.</summary>
    public ApplyResult? ApplyEDiscovery(ChangeOp change)
    {
        if (change is not EdChange || _cases is null)
            return change is EdChange ? new ApplyResult(ChangeOutcome.Applied) : null;
        var now = DateTimeOffset.UtcNow;
        int CaseAt(string name) => _cases.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        switch (change)
        {
            case CreateCase c:
                if (CaseAt(c.Case.Name) >= 0)
                    return new ApplyResult(ChangeOutcome.Failed, "A case with that name already exists.");
                _cases.Add(new EdCase($"case-{Guid.NewGuid():N}", c.Case.Name, c.Description, false, now, now, "Avery Admin", null, null));
                _members[c.Case.Name] = [new("Avery Admin", Account)];
                break;
            case SetCaseClosed s when CaseAt(s.Case.Name) is >= 0 and var at:
                _cases[at] = _cases[at] with { IsClosed = s.Close, Closed = s.Close ? now : null, ClosedBy = s.Close ? "Avery Admin" : null, Modified = now };
                var holds = _holds!;
                foreach (var i in Enumerable.Range(0, holds.Count).Where(i => holds[i].CaseName == s.Case.Name))
                    holds[i] = holds[i] with { Enabled = !s.Close };
                break;
            case SetCaseDescription d when CaseAt(d.Case.Name) is >= 0 and var at:
                _cases[at] = _cases[at] with { Description = d.New, Modified = now };
                break;
            case DeleteCase d:
                if (_holds!.Any(h => h.CaseName == d.Case.Name))
                    return new ApplyResult(ChangeOutcome.Failed, "Purview won't delete a case that still has holds: delete its holds first.");
                _cases.RemoveAll(c => c.Name == d.Case.Name);
                foreach (var gone in _searches!.Where(s => s.CaseName == d.Case.Name).ToArray())
                {
                    _searches!.Remove(gone);
                    _searchDetails.Remove(gone.Name);
                }
                break;
            case SetCaseMember m:
                var members = _members.TryGetValue(m.Case.Name, out var list) ? list : _members[m.Case.Name] = [];
                members.RemoveAll(x => string.Equals(x.Address, m.Member.Address, StringComparison.OrdinalIgnoreCase));
                if (m.Add)
                    members.Add(new EdMember(m.Member.Name, m.Member.Address));
                break;
            case CreateSearch s:
                if (_searchDetails.ContainsKey(s.Search.Name))
                    return new ApplyResult(ChangeOutcome.Failed, "A search with that name already exists.");
                var made = new EdSearch(s.Search.Name, s.CaseName, s.Description, "Completed", now, "Avery Admin", now, "Avery Admin");
                _searches!.Add(made);
                _searchDetails[made.Name] = Counted(made, s.Query, Names(s.Mailboxes), Names(s.Sites), s.PublicFolders);
                break;
            case UpdateSearch u when _searchDetails.TryGetValue(u.Search.Name, out var old):
                _searchDetails[u.Search.Name] = old with { Query = u.NewQuery.Trim(), Mailboxes = Names(u.NewMailboxes), Sites = Names(u.NewSites), PublicFolders = u.NewPublicFolders };
                Ran(u.Search.Name);
                break;
            case PurgeSearch p when _searchDetails.TryGetValue(p.Search.Name, out var found):
                var removed = Math.Min(found.Items, PurgeSearch.PerMailbox * Math.Max(1, found.Mailboxes.Count));
                _actions.Add(new EdAction(p.Search.Name + "_Purge", p.Search.Name, "Purge", "Completed", now, now, "Avery Admin",
                    $"Purge Type: {(p.Hard ? "HardDelete" : "SoftDelete")}; Item count: {removed}; Total size {removed * 48_000}; Details: {{}}", null));
                break;
            case DeleteSearch d:
                _searches!.RemoveAll(s => s.Name == d.Search.Name);
                _searchDetails.Remove(d.Search.Name);
                break;
            case CreateHold h:
                _holds!.Add(new EdHold($"hold-{Guid.NewGuid():N}", h.Hold.Name, h.CaseName, true, Names(h.Mailboxes), Names(h.Sites), h.PublicFolders, h.Query.Trim(),
                    "Success", now, now));
                break;
            case UpdateHold u when _holds!.FindIndex(x => x.Name == u.Hold.Name) is >= 0 and var at:
                _holds[at] = _holds[at] with { Mailboxes = Names(u.NewMailboxes), Sites = Names(u.NewSites), PublicFolders = u.NewPublicFolders, Query = u.NewQuery.Trim(), Modified = now };
                break;
            case SetHoldEnabled e when _holds!.FindIndex(x => x.Name == e.Hold.Name) is >= 0 and var at:
                _holds[at] = _holds[at] with { Enabled = e.New, Modified = now };
                break;
            case DeleteHold d:
                _holds!.RemoveAll(h => h.Name == d.Hold.Name);
                break;
            default:
                return new ApplyResult(ChangeOutcome.Failed, "That isn't in the demo any more.");
        }
        return new ApplyResult(ChangeOutcome.Applied);
    }
}
