using Tenantry.Core.Cache;
using Tenantry.Core.Changes;
using Tenantry.Core.PowerShell;
using Tenantry.Demo;
using Tenantry.Graph;
using Tenantry.PowerShell;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Records;

namespace Tenantry.Tests;

/// <summary>The PowerShell console: its page, the change log entries it writes, and the real pwsh session.</summary>
public sealed class PowerShellConsoleTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A session that answers from a script of lines, as if PowerShell had written them.</summary>
    sealed class FakeSession(Func<string, IReadOnlyList<PsLine>>? answer = null) : IPowerShellSession
    {
        public List<string> Ran { get; } = [];
        public PsSessionStatus Status { get; private set; }
        public string? StatusDetail => "PowerShell 7.9 (test)";
        public IReadOnlyList<PsConnection> Connections { get; private set; } = [new(PsService.Graph, PsConnectionStatus.Waiting)];
        public event EventHandler? Changed;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Status = PsSessionStatus.Ready;
            Connections = [new(PsService.Graph, PsConnectionStatus.Connected), new(PsService.SharePoint, PsConnectionStatus.ModuleMissing, "PnP.PowerShell isn't installed.")];
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<PsRunResult> RunAsync(string script, Action<PsLine> output, CancellationToken cancellationToken = default)
        {
            Ran.Add(script);
            var lines = answer?.Invoke(script) ?? [];
            foreach (var line in lines)
                output(line);
            var errors = lines.Count(l => l.Stream == PsStream.Error);
            return Task.FromResult(new PsRunResult(errors == 0, errors, TimeSpan.FromSeconds(1.5)));
        }

        public Task<PsCompletion?> CompleteAsync(string input, int cursor, CancellationToken cancellationToken = default) =>
            Task.FromResult<PsCompletion?>(new PsCompletion(0, cursor, ["Get-Mailbox", "Get-MailboxPermission"]));

        public Task InstallModulesAsync(Action<PsLine> output, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SignInToPurviewAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    static ChangePipeline Pipeline() =>
        new(new DemoChangeExecutor(new DemoTenantDirectory(), new DemoUserRecords(new DemoTenantDirectory())), new ChangeLog(new MemoryEntityStore()),
            new MemoryTestModeStore(true), "admin@contoso.example");

    [Fact]
    public async Task A_command_shows_its_output_by_kind_and_is_logged()
    {
        var session = new FakeSession(_ =>
        [
            new(PsStream.Output, ""),
            new(PsStream.Output, "Name  Alias"),
            new(PsStream.Output, "----  -----"),
            new(PsStream.Output, "Ada   ada"),
            new(PsStream.Output, ""),
            new(PsStream.Warning, "WARNING: There are more results available."),
            new(PsStream.Output, ""),
        ]);
        var pipeline = Pipeline();
        var console = new PowerShellConsoleViewModel(session, pipeline, "Contoso", isDemo: false);

        Assert.Equal(PsSessionStatus.Ready, console.Status);
        Assert.Contains("Test Mode doesn't limit them", console.Banner);
        console.Input = "Get-Mailbox -ResultSize 1";
        await console.RunCommand.ExecuteAsync(null);

        var block = Assert.Single(console.Blocks);
        Assert.Equal("Get-Mailbox -ResultSize 1", block.Command);
        Assert.False(block.IsRunning);
        Assert.Equal("Done in 1.5 s", block.Outcome);
        // Leading blank lines dropped, the one between table and warning kept, trailing ones held back.
        Assert.Collection(block.Chunks,
            c => Assert.Equal("Name  Alias\n----  -----\nAda   ada\n", c.Text),
            c => Assert.True(c.IsWarning));
        Assert.Equal("", console.Input);

        var logged = Assert.Single(pipeline.Log.All());
        var command = Assert.IsType<PowerShellCommand>(logged.Change);
        Assert.Equal("Get-Mailbox -ResultSize 1", command.Script);
        Assert.Equal(ChangeOutcome.Applied, logged.Outcome);
        Assert.Equal("Contoso", logged.Change.Target.Name);
    }

    [Fact]
    public async Task A_command_with_errors_is_logged_as_failed()
    {
        var session = new FakeSession(_ => [new(PsStream.Error, "Set-Mailbox: The operation couldn't be performed.")]);
        var pipeline = Pipeline();
        var console = new PowerShellConsoleViewModel(session, pipeline, "Contoso", isDemo: false);

        console.Input = "Set-Mailbox -Identity x -HiddenFromAddressListsEnabled $true";
        await console.RunCommand.ExecuteAsync(null);

        var block = Assert.Single(console.Blocks);
        Assert.True(block.Failed);
        Assert.Equal("1 error · 1.5 s", block.Outcome);
        Assert.True(Assert.Single(block.Chunks).IsError);
        var logged = Assert.Single(pipeline.Log.All());
        Assert.Equal(ChangeOutcome.Failed, logged.Outcome);
        Assert.Equal("It wrote 1 error.", logged.Detail);
    }

    [Fact]
    public async Task Earlier_commands_come_back_and_Tab_cycles_through_matches()
    {
        var console = new PowerShellConsoleViewModel(new FakeSession(), null, "Contoso", isDemo: false);
        foreach (var command in new[] { "Get-MgContext", "Get-Mailbox" })
        {
            console.Input = command;
            await console.RunCommand.ExecuteAsync(null);
        }

        Assert.Equal("Get-Mailbox", console.HistoryBack());
        Assert.Equal("Get-MgContext", console.HistoryBack());
        Assert.Null(console.HistoryBack());
        Assert.Equal("Get-Mailbox", console.HistoryForward());
        Assert.Equal("", console.HistoryForward());

        var first = await console.CompleteAsync("Get-Mai", 7);
        Assert.Equal(("Get-Mailbox", 11), first);
        var second = await console.CompleteAsync("Get-Mailbox", 11);
        Assert.Equal(("Get-MailboxPermission", 21), second);
        var third = await console.CompleteAsync("Get-MailboxPermission", 21);
        Assert.Equal(("Get-Mailbox", 11), third);
    }

    [Fact]
    public void Missing_modules_and_sign_ins_offer_a_button()
    {
        var console = new PowerShellConsoleViewModel(new FakeSession(), null, "Contoso", isDemo: false);

        var sharePoint = console.Connections.Single(c => c.Name == "SharePoint");
        Assert.Equal("Module Missing", sharePoint.State);
        Assert.Equal("Install Modules", sharePoint.ActionText);
        Assert.True(console.InstallModulesCommand.CanExecute(null));
        Assert.Equal("Connected", console.Connections.Single(c => c.Name == "Microsoft Graph").State);
    }

    [Fact]
    public void The_demo_opens_a_console_and_Show_PowerShell_offers_it()
    {
        // As the app's connector builds the demo, with its pretend console.
        var directory = new DemoTenantDirectory();
        using var main = new MainViewModel(new ConnectedTenant("Northgate (demo)", "admin@northgate.example", directory,
            new Tenantry.Core.Capabilities.TenantContext(new HashSet<string>([Tenantry.Core.Capabilities.DirectoryRoles.UserAdministrator]), new HashSet<string>(), true),
            IsDemo: true,
            Changes: new ChangePipeline(new DemoChangeExecutor(directory), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(false), "admin@northgate.example"),
            Scripts: new GraphChangeScripts(),
            PowerShell: () => new DemoPowerShellSession()));

        Assert.True(main.HasPowerShell);
        main.OpenPowerShellCommand.Execute(null);
        var console = Assert.IsType<PowerShellConsoleViewModel>(main.SelectedTab);
        Assert.True(console.IsDemo);
        Assert.Equal("PowerShell", console.Title);

        var ada = new ChangeTarget("user", "u1", "Ada Example", "ada@contoso.example");
        main.ShowPowerShell([new SetHiddenFromAddressList(ada, false, true)]);
        var script = Assert.IsType<PowerShellScriptViewModel>(main.Dialog!.Content);
        Assert.Contains("Set-Mailbox", script.Text);
        Assert.Equal("Runs with Exchange Online.", script.Runs);
        script.OpenInConsoleCommand.Execute(null);

        Assert.Null(main.Dialog);
        Assert.Same(console, main.SelectedTab);
        Assert.Contains("Set-Mailbox", console.Input);
        Assert.Single(main.Tabs.OfType<PowerShellConsoleViewModel>());
    }

    [Fact]
    public void Pending_changes_show_their_PowerShell_one_or_all()
    {
        var pipeline = Pipeline();
        var shown = new List<IReadOnlyList<ChangeOp>>();
        var tray = new PendingChangesViewModel(pipeline, "Contoso", showTestMode: false, () => Task.CompletedTask, shown.Add);
        var ada = new ChangeTarget("user", "u1", "Ada Example", "ada@contoso.example");
        pipeline.Stage(new SetUserText(ada, "department", "Department", "Sales", "Finance"));
        pipeline.Stage(new SetHiddenFromAddressList(ada, false, true));

        tray.Rows[1].ShowPowerShellCommand.Execute(null);
        tray.ShowAllPowerShellCommand.Execute(null);

        Assert.Equal(2, shown.Count);
        Assert.IsType<SetHiddenFromAddressList>(Assert.Single(shown[0]));
        Assert.Equal(2, shown[1].Count);
        Assert.True(tray.CanShowPowerShell);
        Assert.False(new PendingChangesViewModel(pipeline, "Contoso", false, () => Task.CompletedTask).CanShowPowerShell);
    }

    [Fact]
    public async Task The_change_log_shows_PowerShell_and_puts_console_commands_back()
    {
        var pipeline = Pipeline();
        var shown = new List<ChangeOp>();
        var reopened = new List<string>();
        var area = new ReportsAreaViewModel([], pipeline, (_, _) => { }, null, null, () => DateTimeOffset.UtcNow,
            scripts: new GraphChangeScripts(), showPowerShell: shown.Add, openInConsole: reopened.Add);
        var ada = new ChangeTarget("user", "u1", "Ada Example", "ada@contoso.example");
        pipeline.Record(new SetHiddenFromAddressList(ada, false, true));
        pipeline.Record(new PowerShellCommand(ChangePipeline.TenantTarget("Contoso"), "Get-Mailbox\n| Select Name"), "It wrote 1 error.", ChangeOutcome.Failed);
        area.SelectedPage = ReportsAreaViewModel.Pages.Single(p => p.Key == "changelog");
        await area.ChangeLog.LoadAsync(Ct);

        var entries = area.ChangeLog.Rows.Cast<ChangeLogEntry>().ToArray();
        area.ChangeLog.IsPreviewOpen = true;
        area.ChangeLog.SelectedRow = entries.Single(e => e.Change is PowerShellCommand);
        var preview = Assert.IsType<AppPreviewViewModel>(area.ChangeLog.PreviewModel);
        Assert.Equal("Get-Mailbox\n| Select Name", preview.Code);
        Assert.Equal("Get-Mailbox | Select Name", entries.Single(e => e.Change is PowerShellCommand).Change.After);
        preview.Buttons.Single(b => b.Text == "Open in Console").ActCommand.Execute(null);
        Assert.Equal(["Get-Mailbox\n| Select Name"], reopened);

        area.ChangeLog.SelectedRow = entries.Single(e => e.Change is SetHiddenFromAddressList);
        var change = Assert.IsType<AppPreviewViewModel>(area.ChangeLog.PreviewModel);
        change.Buttons.Single(b => b.Text == "Show PowerShell").ActCommand.Execute(null);
        Assert.IsType<SetHiddenFromAddressList>(Assert.Single(shown));
    }

    // ---- The real session, against this computer's PowerShell 7 ---------------------------

    /// <summary>A session that never signs in to anything (the sign-in fails at once), for local commands.</summary>
    static PwshSession LocalSession() => new((_, _) => throw new InvalidOperationException("no sign-in in tests"));

    [Fact]
    public async Task Real_PowerShell_runs_commands_keeps_variables_and_sorts_output()
    {
        if (PwshLocator.Find() is null)
        {
            Assert.Skip("PowerShell 7 isn't installed here.");
            return;
        }
        await using var session = LocalSession();
        await session.StartAsync(Ct);

        Assert.Equal(PsSessionStatus.Ready, session.Status);
        Assert.StartsWith("PowerShell 7", session.StatusDetail);
        Assert.All(session.Connections, c => Assert.Equal(PsConnectionStatus.Failed, c.Status));

        var lines = new List<PsLine>();
        var first = await session.RunAsync("$tenantryTest = 40 + 2; $tenantryTest", lines.Add, Ct);
        Assert.True(first.Succeeded);
        Assert.Contains(lines, l => l.Stream == PsStream.Output && l.Text.Trim() == "42");

        lines.Clear();
        var second = await session.RunAsync("Write-Warning 'careful'; Write-Error 'nope'; Write-Host 'hello'; $tenantryTest + 1", lines.Add, Ct);
        Assert.False(second.Succeeded);
        Assert.Equal(1, second.ErrorCount);
        Assert.Contains(lines, l => l.Stream == PsStream.Warning && l.Text == "WARNING: careful");
        Assert.Contains(lines, l => l.Stream == PsStream.Error && l.Text.Contains("nope", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Stream == PsStream.Information && l.Text == "hello");
        Assert.Contains(lines, l => l.Stream == PsStream.Output && l.Text.Trim() == "43");

        var completion = await session.CompleteAsync("Get-ChildIt", 11, Ct);
        Assert.NotNull(completion);
        Assert.Contains("Get-ChildItem", completion.Matches);
        Assert.Equal(0, completion.Start);
    }

    [Fact]
    public async Task Stopping_a_command_starts_a_fresh_PowerShell()
    {
        if (PwshLocator.Find() is null)
        {
            Assert.Skip("PowerShell 7 isn't installed here.");
            return;
        }
        await using var session = LocalSession();
        await session.StartAsync(Ct);
        await session.RunAsync("$kept = 'yes'", _ => { }, Ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(TimeSpan.FromSeconds(1));
        var stopped = await session.RunAsync("Start-Sleep -Seconds 60", _ => { }, stop.Token);
        Assert.True(stopped.Stopped);

        // The next command waits for the restart, and the old variables are gone.
        var lines = new List<PsLine>();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        PsRunResult after;
        do
        {
            lines.Clear();
            after = await session.RunAsync("\"kept=$kept\"", lines.Add, Ct);
        }
        while (!after.Succeeded && DateTime.UtcNow < deadline);
        Assert.True(after.Succeeded);
        Assert.Contains(lines, l => l.Text.Trim() == "kept=");
    }

    [Fact]
    public async Task Without_PowerShell_the_console_says_so()
    {
        await using var session = new PwshSession((_, _) => throw new InvalidOperationException(), locate: () => null);
        await session.StartAsync(Ct);

        Assert.Equal(PsSessionStatus.Unavailable, session.Status);
        Assert.Equal("PowerShell 7 isn't installed on this computer.", session.StatusDetail);
        var console = new PowerShellConsoleViewModel(session, null, "Contoso", isDemo: false);
        Assert.True(console.IsUnavailable);
        Assert.False(console.RunCommand.CanExecute(null));
    }
}
