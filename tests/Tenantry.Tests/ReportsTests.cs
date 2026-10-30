using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Lists;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>Reports and the Change Log (9).</summary>
public sealed class ReportsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("user", "Person")]
    [InlineData(AppChange.Registration, "App Registration")]
    [InlineData(RetentionChange.PolicyKind, "Retention Policy")]
    [InlineData("somethingnew", "Somethingnew")]
    public void Changed_things_read_in_words(string kind, string words) => Assert.Equal(words, ChangeLogList.KindText(kind));

    sealed class Answers(ChangeOutcome outcome) : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ApplyResult(outcome, outcome == ChangeOutcome.Failed ? "Refused by Microsoft 365" : null));
    }

    [Fact]
    public async Task The_change_log_lists_what_was_applied_and_follows_new_changes()
    {
        var pipeline = new ChangePipeline(new Answers(ChangeOutcome.Failed), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin@contoso.example");
        var opened = new List<ChangeTarget>();
        var area = new ReportsAreaViewModel([], pipeline, (_, _) => { }, opened.Add, null, () => DateTimeOffset.UtcNow);
        var ada = new ChangeTarget("user", "u1", "Ada Example", "ada@contoso.example");

        pipeline.Stage(new SetSignInAllowed(ada, true, false));
        await pipeline.ApplyAsync(cancellationToken: Ct);
        // One read, awaited (choosing the page would start a second one alongside it).
        await area.ChangeLog.LoadAsync(Ct);

        var entry = Assert.IsType<ChangeLogEntry>(Assert.Single(area.ChangeLog.Rows));
        Assert.Equal(ChangeOutcome.Failed, entry.Outcome);
        area.ChangeLog.IsPreviewOpen = true;
        area.ChangeLog.SelectedRow = entry;
        await Task.Delay(50, Ct);
        var preview = Assert.IsType<AppPreviewViewModel>(area.ChangeLog.PreviewModel);
        Assert.Contains(preview.Badges, b => b.Text == "Failed");
        Assert.Contains(preview.Facts, f => f.Label == "Detail" && f.Value == "Refused by Microsoft 365");
        preview.OpenCommand.Execute(null);
        Assert.Equal([ada], opened);
    }
}
