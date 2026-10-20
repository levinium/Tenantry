using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Tenantry.App;
using Tenantry.App.Services;
using Tenantry.Core.Tenancy;
using Tenantry.ViewModels;

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

    Render(new ShellViewModel(connector).CurrentPage, $"welcome-{suffix}");

    var wizard = new SetupWizardViewModel(connector, () => { }, () => { });
    Render(wizard, $"wizard-choose-{suffix}");
    wizard.ChooseAutomaticCommand.Execute(null);
    Render(wizard, $"wizard-automatic-{suffix}");
    wizard.BackCommand.Execute(null);
    wizard.ChooseManualCommand.Execute(null);
    Render(wizard, $"wizard-manual-{suffix}");

    var main = new MainViewModel(connector.ConnectDemo(), () => { });
    // The demo directory answers synchronously, so this completes without a message loop.
    main.InitializeAsync().GetAwaiter().GetResult();
    Render(main, $"users-{suffix}");

    main.Users.QuickFilter = UserQuickFilter.Guests;
    Render(main, $"users-guests-{suffix}");
    main.Users.QuickFilter = UserQuickFilter.All;

    main.OpenUser(main.Users.VisibleUsers[0].User);
    Render(main, $"user-detail-{suffix}");
}

Console.WriteLine($"Snapshots written to {output}");
return 0;

void Render(object page, string name)
{
    var shell = new ShellViewModel(connector);
    shell.ShowPage(page);
    var window = new MainWindow { Width = 1280, Height = 820, DataContext = shell };
    window.Show();
    for (var i = 0; i < 4; i++)
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
        new("a", "Northgate Partners", "00000000-0000-0000-0000-00000000000a", "northgate.example", "app", Now.AddMonths(-3), "admin@northgate.example", Now.AddHours(-2)),
        new("b", "Fabrikam Foundation", "00000000-0000-0000-0000-00000000000b", "fabrikam.example", "app", Now.AddMonths(-1), "it@fabrikam.example", Now.AddDays(-6)),
    ];

    public void RemoveProfile(string profileId) { }

    public Task<ConnectedTenant> ConnectAsync(TenantProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(demo);

    public ConnectedTenant ConnectDemo() => demo;

    public Task<SetupOutcome> SetUpAutomaticallyAsync(IProgress<string> progress, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<TenantProfile> AddManualAsync(string tenantId, string clientId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
