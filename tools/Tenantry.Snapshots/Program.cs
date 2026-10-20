using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Tenantry.App;
using Tenantry.App.Services;
using Tenantry.Core.Directory;
using Tenantry.Core.Tenancy;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Lists;

// tenantry-snapshots <output folder>
//
// Renders each screen in light and dark themes. Demo data and fictional profiles only.
//
// Everything runs synchronously on this (the UI) thread. The headless platform has no message
// loop: an await would resume on a pool thread, where every Avalonia call is illegal, and the
// first version of this tool hung exactly that way. Work is settled by pumping the dispatcher
// and ticking the render timer by hand instead.

var output = Path.GetFullPath(args.FirstOrDefault() ?? "snapshots");
Directory.CreateDirectory(output);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var connector = new SnapshotConnector(new TenantConnector().ConnectDemo());

foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
{
    Application.Current!.RequestedThemeVariant = theme;
    var suffix = theme == ThemeVariant.Light ? "light" : "dark";
    var full = theme == ThemeVariant.Light;

    Render(new ShellViewModel(connector).CurrentPage, $"welcome-{suffix}");
    if (full)
    {
        var welcome = (WelcomeViewModel)new ShellViewModel(connector).CurrentPage;
        welcome.AskRemoveCommand.Execute(welcome.Profiles[1]);
        Render(welcome, "welcome-confirm-remove-light");
    }

    var wizard = new SetupWizardViewModel(connector, () => { }, () => { });
    wizard.ChooseAutomaticCommand.Execute(null);
    Render(wizard, $"wizard-automatic-{suffix}");

    var main = Main();
    Render(main, $"users-{suffix}");

    // Filtered, two-level sort: pills, header arrows and priority numbers.
    var list = main.Users;
    Pick(list, UserFields.Status, hide: true, "Blocked");
    Pick(list, UserFields.Licenses, hide: false, "Microsoft 365 E5", "Microsoft 365 E3");
    list.SortByColumn(list.Columns.Single(c => c.Key == UserFields.Department));
    list.AddSortByColumn(list.Columns.Single(c => c.Key == UserFields.Status));
    if (args.Contains("--debug"))
        foreach (var c in list.Columns.Where(c => c.IsSorted || c.Key == UserFields.Status))
            Console.WriteLine($"    {c.Key}: sort={c.Sort} priority={c.SortPriority} show={c.ShowPriority} text='{c.PriorityText}' glyph={c.SortGlyph}");
    Render(main, $"users-filtered-{suffix}");

    if (full)
    {
        list.EditFilter(list.FilterFields.Single(f => f.Key == UserFields.Licenses));
        Render(main, "panel-choice-filter-light");

        list.OpenSortPanel();
        Render(main, "panel-sort-light");

        list.OpenBuilder();
        var b = list.Builder!;
        b.MatchAny = true;
        b.Rules[0].Field = b.Rules[0].Fields.Single(f => f.Key == UserFields.LastSignIn);
        b.Rules[0].Op = b.Rules[0].Ops.Single(o => o.Key == "older");
        b.Rules[0].Value = "90";
        b.AddRule();
        b.Rules[1].Field = b.Rules[1].Fields.Single(f => f.Key == UserFields.Licenses);
        b.Rules[1].Value = "Unlicensed";
        b.AddGroup();
        Render(main, "panel-builder-light");

        list.OpenColumnsPanel();
        Render(main, "panel-columns-light");
        list.ClosePanels();
    }

    var grouped = Main();
    grouped.Users.SelectedGroup = grouped.Users.GroupOptions.Single(g => g.Key == UserFields.Department);
    Render(grouped, $"users-grouped-{suffix}");

    var preview = Main();
    preview.Users.PreviewRow(preview.Users.Rows[1]);
    Render(preview, $"users-preview-{suffix}");
    if (full)
    {
        Render(preview, "users-preview-small-light", 1040, 760);
        var compact = Main();
        compact.Users.ToggleDensity();
        Render(compact, "users-compact-light");
    }

    if (full)
    {
        preview.OpenUser((UserSummary)preview.Users.Rows[0]);
        Render(preview, "user-detail-light");
    }
}

Console.WriteLine($"Snapshots written to {output}");
return 0;

MainViewModel Main()
{
    var main = new MainViewModel(connector.ConnectDemo() with { ListState = new Tenantry.Core.Lists.MemoryListStateStore() }, () => { });
    // The demo directory answers synchronously, so this completes without a message loop.
    main.InitializeAsync().GetAwaiter().GetResult();
    return main;
}

static void Pick(DataListViewModel list, string field, bool hide, params string[] values)
{
    list.EditFilter(list.FilterFields.Single(f => f.Key == field));
    var editor = (ChoiceFilterEditor)list.Editor!;
    foreach (var v in editor.Values)
        v.IsChecked = values.Contains(v.Value);
    editor.HideSelected = hide;
    list.ApplyFilter();
}

void Render(object page, string name, int width = 1440, int height = 900)
{
    var shell = new ShellViewModel(connector);
    shell.ShowPage(page);
    var window = new MainWindow { Width = width, Height = height, DataContext = shell };
    window.Show();
    for (var i = 0; i < 10; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
    using var frame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException($"No frame rendered for {name}");
#pragma warning disable CS0618 // the encoder-options overload adds nothing for a plain PNG
    frame.Save(Path.Combine(output, name + ".png"));
#pragma warning restore CS0618
    window.Close();
    Console.WriteLine($"  {name}.png");
}

/// <summary>Fictional saved tenants; the demo for everything else.</summary>
sealed class SnapshotConnector(ConnectedTenant demo) : ITenantConnector
{
    static readonly DateTimeOffset Now = DateTimeOffset.Now;

    public IReadOnlyList<TenantProfile> LoadProfiles() =>
    [
        new("a", "Northgate Partners", "00000000-0000-0000-0000-00000000000a", "northgate.example", "app", Now.AddMonths(-3), "admin@northgate.example", Now.AddHours(-2), IsDefault: true),
        new("b", "Fabrikam Foundation", "00000000-0000-0000-0000-00000000000b", "fabrikam.example", "app", Now.AddMonths(-1), "it@fabrikam.example", Now.AddDays(-6)),
        new("c", "Tailspin Toys", "00000000-0000-0000-0000-00000000000c", "tailspin.example", "app", Now.AddMonths(-2), "ops@tailspin.example", Now.AddDays(-20)),
        new("d", "Woodgrove Clinic", "00000000-0000-0000-0000-00000000000d", "woodgrove.example", "app", Now.AddMonths(-5), "admin@woodgrove.example", Now.AddDays(-40)),
        new("e", "Litware Academy", "00000000-0000-0000-0000-00000000000e", "litware.example", "app", Now.AddMonths(-6), "it@litware.example"),
    ];

    public void RemoveProfile(string profileId) { }

    public void SetDefaultProfile(string? profileId) { }

    public Task<ConnectedTenant> ConnectAsync(TenantProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(demo);

    public ConnectedTenant ConnectDemo() => demo;

    public Task<SetupOutcome> SetUpAutomaticallyAsync(IProgress<string> progress, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<TenantProfile> AddManualAsync(string tenantId, string clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
