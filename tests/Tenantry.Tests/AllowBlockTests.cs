using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Block and allow lists (5b): format checks, the Add panel, what each change sends, and the guard.</summary>
public sealed class AllowBlockTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    static GraphChangeExecutor Executor(FakeCmdlets exchange) =>
        new(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);

    [Theory]
    [InlineData(ListKind.Sender, "pat@example.com", true)]
    [InlineData(ListKind.Sender, "example.com", true)]
    [InlineData(ListKind.Sender, "not an address", false)]
    [InlineData(ListKind.Url, "*.example.com/path", true)]
    [InlineData(ListKind.Url, "nodot", false)]
    [InlineData(ListKind.File, "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", true)]
    [InlineData(ListKind.File, "abc123", false)]
    [InlineData(ListKind.IP, "2001:db8::/32", true)]
    [InlineData(ListKind.IP, "192.0.2.1", false)]
    public void Each_kind_checks_its_format(ListKind kind, string value, bool ok) =>
        Assert.Equal(ok, AllowBlock.Problem(kind, value) is null);

    [Fact]
    public void Pasted_values_split_on_lines_commas_and_semicolons_without_repeats() =>
        Assert.Equal(["a@x.example", "b.example", "c@y.example"], AllowBlock.Split("a@x.example\r\nb.example, c@y.example;A@X.EXAMPLE\n\n"));

    static AddListEntriesViewModel Panel(ChangePipeline pipeline, params ListEntry[] existing) =>
        new(pipeline, Availability.Yes, () => existing, () => Now);

    static ChangePipeline Pipeline(IChangeExecutor? executor = null, bool testMode = true) =>
        new(executor ?? new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(testMode), "admin");

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }

    [Fact]
    public void Adding_stages_one_change_per_good_line_and_leaves_the_rest_to_fix()
    {
        var pipeline = Pipeline();
        var existing = new ListEntry("id1", ListKind.Sender, "old.example", true, null, null, null, null, null, null, null, "Tenant");
        var panel = Panel(pipeline, existing);
        panel.Values = "spam@bad.example\nnot valid\nold.example\nphish.example";
        panel.Notes = "INC-42";

        panel.StageCommand.Execute(null);

        var staged = pipeline.Pending.Cast<AddListEntry>().ToArray();
        Assert.Equal(["spam@bad.example", "phish.example"], staged.Select(s => s.Entry.Name));
        Assert.All(staged, s => Assert.Equal((true, ListExpiry.Never, "INC-42"), (s.Block, s.Expiry, s.Notes)));
        Assert.Equal(2, panel.Problems.Count);
        Assert.Equal("not valid\nold.example", panel.Values.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Allows_default_to_45_days_after_last_use_and_dates_must_be_in_the_future()
    {
        var pipeline = Pipeline();
        var panel = Panel(pipeline);
        panel.Action = AddListEntriesViewModel.Actions.Single(a => !a.Value);
        panel.Values = "partner.example";

        Assert.Equal(ListExpiry.AllowDefault, Assert.Single(panel.Plan().Changes).Expiry);

        panel.Expiry = panel.ExpiryOptions.Single(o => o.Choice == ExpiryChoice.OnDate);
        panel.ExpiryDate = Now.Date.AddDays(-1);
        panel.StageCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
        Assert.Contains(panel.Problems, p => p.StartsWith("Choose an expiry date", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blocks_never_expire_allows_go_45_days_after_use_and_dated_entries_send_their_date()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet.StartsWith("Get-", StringComparison.Ordinal) ? """[{"Action":"Block","Notes":"n"},{"Action":"Allow","Notes":"n"}]""" : "[]");
        var executor = Executor(exchange);
        var target = ListEntryChange.TargetFor(ListKind.Url, "zz-tenantry-test-x.example/*");

        await executor.ApplyAsync(new AddListEntry(target, ListKind.Url, true, ListExpiry.Never, null), Ct);
        await executor.ApplyAsync(new AddListEntry(target, ListKind.Url, false, ListExpiry.AllowDefault, null), Ct);
        await executor.ApplyAsync(new AddListEntry(target, ListKind.Url, true, new ListExpiry(On: Now.AddDays(10)), "n"), Ct);

        var calls = exchange.Calls.Where(c => c.Cmdlet == "New-TenantAllowBlockListItems").Select(c => c.Parameters).ToArray();
        Assert.Contains("\"NoExpiration\":true", calls[0]);
        Assert.Contains("\"ListType\":\"Url\"", calls[0]);
        Assert.Contains("\"RemoveAfter\":45", calls[1]);
        Assert.Contains("\"ExpirationDate\":\"2026-11-05", calls[2]);
        Assert.Contains("\"Notes\":\"n\"", calls[2]);
    }

    [Fact]
    public async Task Spoofed_senders_and_removals_use_their_own_cmdlets()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-TenantAllowBlockListSpoofItems" ? """[{"SpoofedUser":"zz-tenantry-test.example","Action":"Allow"}]""" : "[]");
        var executor = Executor(exchange);
        var spoof = ListEntryChange.TargetFor(ListKind.Spoof, "zz-tenantry-test.example");

        var added = await executor.ApplyAsync(new AddListEntry(spoof, ListKind.Spoof, false, ListExpiry.Never, null, "mailer.example", "External"), Ct);
        var removed = await executor.ApplyAsync(new RemoveListEntry(ListEntryChange.TargetFor(ListKind.File, "zz"), ListKind.File, "id-9", true), Ct);

        Assert.Equal((ChangeOutcome.Applied, ChangeOutcome.Applied), (added.Outcome, removed.Outcome));
        var spoofCall = exchange.Calls.Single(c => c.Cmdlet == "New-TenantAllowBlockListSpoofItems").Parameters;
        Assert.Contains("\"SendingInfrastructure\":\"mailer.example\"", spoofCall);
        Assert.Contains("\"Action\":\"Allow\"", spoofCall);
        Assert.Contains("\"ListType\":\"FileHash\"", exchange.Calls.Single(c => c.Cmdlet == "Remove-TenantAllowBlockListItems").Parameters);
    }

    [Fact]
    public async Task Test_mode_only_lets_test_marked_values_change()
    {
        var exchange = new FakeCmdlets();
        var pipeline = Pipeline(Executor(exchange));
        pipeline.Stage(new AddListEntry(ListEntryChange.TargetFor(ListKind.Sender, "real@partner.example"), ListKind.Sender, true, ListExpiry.Never, null));

        Assert.Equal(ChangeOutcome.Refused, Assert.Single(await pipeline.ApplyAsync(cancellationToken: Ct)).Result.Outcome);
        Assert.Empty(exchange.Calls);
    }

    [Fact]
    public void The_preview_stages_only_what_was_changed()
    {
        var pipeline = Pipeline();
        var entry = new ListEntry("id1", ListKind.Sender, "zz-tenantry-test.example", true, "old note", Now.AddDays(5), null, null, null, null, null, "Tenant");
        var preview = new ListEntryPreviewViewModel(entry, pipeline, Availability.Yes);

        preview.SaveDetailsCommand.Execute(null);
        Assert.Empty(pipeline.Pending);
        Assert.Equal("Nothing changed.", preview.Message);

        preview.NotesDraft = "new note";
        preview.SaveDetailsCommand.Execute(null);
        var update = Assert.IsType<UpdateListEntry>(Assert.Single(pipeline.Pending));
        Assert.Equal((update.OldExpiry, "new note"), (update.NewExpiry, update.NewNotes));

        preview.RemoveCommand.Execute(null);
        Assert.IsType<RemoveListEntry>(Assert.Single(pipeline.Pending));
    }

    [Fact]
    public void An_allowed_entry_with_no_expiry_opens_as_never_expiring()
    {
        // Allowed spoofed senders have no expiry at all; the preview once failed to open them.
        var spoof = new ListEntry("id2", ListKind.Spoof, "news.example", false, null, null, null, null, null, null, null, null, "198.51.100.0/24", "External");

        var preview = new ListEntryPreviewViewModel(spoof, Pipeline(), Availability.Yes);

        Assert.Equal(ExpiryChoice.Never, preview.Expiry.Choice);
    }

    [Fact]
    public void Block_the_sender_says_so_when_they_are_listed_already()
    {
        var message = new QuarantinedMessage("a\\1", Now, "x@bad.example", ["zz-tenantry-test-bo@contoso.example"], "Verify now", 1, "Phish", null, null, "NOTRELEASED", null);
        ListEntry Listed(bool block) => new("id1", ListKind.Sender, "X@bad.example", block, null, null, null, null, null, null, null, "Tenant");
        var pipeline = Pipeline();

        var blocked = new QuarantinePreviewViewModel(message, null, pipeline, Availability.Yes, () => [Listed(true)]);
        var allowed = new QuarantinePreviewViewModel(message, null, pipeline, Availability.Yes, () => [Listed(false)]);
        blocked.BlockSenderCommand.Execute(null);
        allowed.BlockSenderCommand.Execute(null);

        Assert.Empty(pipeline.Pending);
        Assert.False(blocked.CanBlock);
        Assert.Equal("This sender is already blocked.", blocked.BlockNote);
        Assert.StartsWith("This sender is on the allow list.", allowed.BlockNote);
        Assert.True(new QuarantinePreviewViewModel(message, null, pipeline, Availability.Yes, () => []).CanBlock);
    }
}
