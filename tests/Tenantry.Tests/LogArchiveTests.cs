using System.Net;
using System.Text.Json;
using Tenantry.Cache;
using Tenantry.Core.Cache;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Lists;

namespace Tenantry.Tests;

/// <summary>The sign-in and audit history kept on this computer (10).</summary>
public sealed class LogArchiveTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string _folder = Path.Combine(Path.GetTempPath(), "tenantry-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public LogArchiveTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    public static TheoryData<string> Kinds => ["memory", "sqlite"];

    static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Events_are_added_once_read_by_range_newest_first_and_pruned(string kind)
    {
        using var store = kind == "memory" ? (IEntityStore)new MemoryEntityStore() : SqliteEntityStore.Open(Path.Combine(_folder, "cache.db"), _key);
        var archive = (ILogArchive)store;

        Assert.Equal(3, archive.AddEvents("log.x", [new("a", T0, "{}"), new("b", T0.AddHours(1), "{}"), new("c", T0.AddDays(2), "{}")]));
        Assert.Equal(1, archive.AddEvents("log.x", [new("a", T0, "{}"), new("d", T0.AddDays(3), "{}")]));

        Assert.Equal(["b", "a"], archive.ReadEvents("log.x", T0, T0.AddDays(1), 10).Select(e => e.Id));
        Assert.Equal(["d"], archive.ReadEvents("log.x", T0, T0.AddDays(10), 1).Select(e => e.Id));
        var span = archive.Span("log.x");
        Assert.Equal((4, T0, T0.AddDays(3)), (span.Count, span.Oldest!.Value, span.Newest!.Value));
        Assert.Equal(2, archive.PruneEvents("log.x", T0.AddDays(1)));
        Assert.Equal(0, archive.Span("log.other").Count);
    }

    static string SignIn(string id, DateTimeOffset at, int code = 0) => $$$"""
        {"id":"{{{id}}}","createdDateTime":"{{{at:o}}}","userDisplayName":"Ada Example","userPrincipalName":"ada@contoso.example","userId":"u1","appDisplayName":"Microsoft Teams",
         "ipAddress":"192.0.2.1","clientAppUsed":"Browser","isInteractive":true,"authenticationRequirement":"multiFactorAuthentication",
         "status":{"errorCode":{{{code}}},"failureReason":"{{{(code == 0 ? "" : "Invalid password")}}}"},"location":{"city":"Toronto","countryOrRegion":"CA"},
         "deviceDetail":{"operatingSystem":"Windows 11","browser":"Edge"}}
        """;

    [Fact]
    public async Task A_first_run_reads_30_days_and_the_next_only_since_the_newest_held()
    {
        var now = T0;
        var http = new FakeHttp()
            .Once(HttpMethod.Get, "auditLogs/signIns", $$"""{"value":[{{SignIn("s1", now.AddDays(-1))}},{{SignIn("s2", now.AddDays(-2), 50126)}}]}""")
            .On(HttpMethod.Get, "auditLogs/signIns", """{"value":[]}""")
            .On(HttpMethod.Get, "directoryAudits", """{"value":[{"id":"a1","activityDateTime":"2026-09-30T10:00:00Z","activityDisplayName":"Update user","category":"UserManagement","result":"success","initiatedBy":{"user":{"userPrincipalName":"admin@contoso.example"}},"targetResources":[{"userPrincipalName":"ada@contoso.example"}]}]}""");
        var store = new MemoryEntityStore();
        var archiver = new LogArchiver(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, store, null, () => SyncSettings.Default, () => now);

        var (added, notes) = await archiver.ArchiveAsync(null, Ct);

        Assert.Equal(3, added);
        Assert.Empty(notes);
        var first = http.Requests.First(r => r.PathAndQuery.Contains("signIns", StringComparison.Ordinal)).PathAndQuery;
        Assert.Contains("createdDateTime ge 2026-09-01T12:00:00Z", first, StringComparison.Ordinal);
        var failed = LogKinds.Read<SignInEntry>(store.ReadEvents(LogKinds.SignIns, now.AddDays(-3), now, 10).Single(e => e.Id == "s2"))!;
        Assert.Equal("Invalid password", failed.Failure);
        Assert.Equal("Toronto, CA", failed.Location);
        Assert.Equal("Multifactor", failed.Auth);

        await archiver.ArchiveAsync(null, Ct);
        var second = http.Requests.Where(r => r.PathAndQuery.Contains("signIns", StringComparison.Ordinal)).Last().PathAndQuery;
        // The newest held (a day ago), less half an hour for events that arrive late.
        Assert.Contains("createdDateTime ge 2026-09-30T11:30:00Z", second, StringComparison.Ordinal);
        Assert.DoesNotContain(http.Requests, r => r.PathAndQuery.Contains("nonInteractiveUser", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_P1_is_said_and_old_events_are_pruned_by_the_setting()
    {
        var http = new FakeHttp()
            .On(HttpMethod.Get, "auditLogs/signIns", """{"error":{"code":"Authentication_RequestFromNonPremiumTenantOrB2CTenant","message":"Neither tenant is B2C or tenant doesn't have premium license"}}""", HttpStatusCode.Forbidden)
            .On(HttpMethod.Get, "directoryAudits", """{"value":[]}""");
        var store = new MemoryEntityStore();
        store.AddEvents(LogKinds.Audits, [new("old", T0.AddDays(-100), "{}"), new("new", T0.AddDays(-10), "{}")]);
        var archiver = new LogArchiver(new GraphClient(http.Client(), new FakeTokens(), delay: NoDelay.Instance), store, store, null,
            () => SyncSettings.Default with { KeepLogsDays = 90 }, () => T0);

        var (_, notes) = await archiver.ArchiveAsync(null, Ct);

        Assert.Equal("The sign-ins history needs Microsoft Entra ID P1.", Assert.Single(notes));
        Assert.Equal(["new"], store.ReadEvents(LogKinds.Audits, DateTimeOffset.MinValue.AddDays(1), T0, 10).Select(e => e.Id));
    }

    [Fact]
    public void An_Exchange_admin_action_comes_from_the_unified_audit_record()
    {
        var audit = JsonSerializer.Serialize(new
        {
            Id = "x1", CreationTime = "2026-10-01T09:00:00", Operation = "Set-Mailbox", UserId = "admin@contoso.example", ObjectId = "ada@contoso.example",
            ResultStatus = "True", Parameters = new[] { new { Name = "Identity", Value = "ada" }, new { Name = "HiddenFromAddressListsEnabled", Value = "True" } },
        });
        var row = JsonDocument.Parse(JsonSerializer.Serialize(new { RecordType = "ExchangeAdmin", AuditData = audit })).RootElement;

        var e = LogArchiver.ToExchangeAdmin(row)!;
        var entry = LogKinds.Read<ExchangeAdminEntry>(e)!;

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), e.At);
        Assert.Equal("Set-Mailbox", entry.Operation);
        Assert.Equal("-Identity ada; -HiddenFromAddressListsEnabled True", entry.Parameters);
        Assert.True(entry.Succeeded);
    }

    [Fact]
    public void Settings_saved_before_the_history_existed_read_with_its_defaults()
    {
        File.WriteAllText(Path.Combine(_folder, "sync.json"), """{"ChangesMinutes":5,"DetailsMinutes":60,"MailboxDetailsMinutes":60,"MailboxSizesMinutes":240}""");
        var s = new JsonSyncSettingsStore(_folder).Load();
        Assert.Equal((5, 60, 365, false), (s.ChangesMinutes, s.LogsMinutes, s.KeepLogsDays, s.BackgroundSignIns));
    }

    [Fact]
    public void Saving_a_sync_setting_keeps_the_history_settings()
    {
        var store = new MemorySyncSettingsStore(SyncSettings.Default with { KeepLogsDays = 730, BackgroundSignIns = true });
        var settings = new SettingsViewModel("t", store, null, _ => Task.CompletedTask, () => { });

        settings.Changes = SettingsViewModel.ChangesOptions[0];

        Assert.Equal((730, true), (store.Load().KeepLogsDays, store.Load().BackgroundSignIns));
        settings.KeepLogs = SettingsViewModel.KeepLogsOptions.Single(o => o.Minutes == 0);
        Assert.Equal(0, store.Load().KeepLogsDays);
    }

    [Fact]
    public async Task The_history_page_reads_the_chosen_range_and_says_what_the_archive_holds()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new MemoryEntityStore();
        store.AddEvents(LogKinds.SignIns, [.. new[] { 1, 3, 40 }.Select(d =>
            new ArchivedEvent($"s{d}", now.AddDays(-d), LogKinds.Write(new SignInEntry($"s{d}", now.AddDays(-d), "Ada", null, null, "Teams", null, null, null, null, null, null, null, null, null, null, null, true))))]);
        var page = LogHistoryViewModel.Create(store, LogKinds.SignIns, "d", LogLists.SignIns(_ => new object()), null, () => now, null);

        await page.ShownAsync();
        Assert.Equal(2, page.List.Total);
        Assert.StartsWith("This computer holds 3 sign-ins from", page.Summary, StringComparison.Ordinal);
        Assert.False(page.CanReadNew);

        page.Range = LogHistoryViewModel.Ranges.Single(r => r.Value == "0");
        await page.List.ReloadAsync(cancellationToken: Ct);
        Assert.Equal(3, page.List.Total);
    }
}
