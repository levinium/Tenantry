using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tenantry.App;
using Tenantry.App.Services;
using Tenantry.Core.Changes;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Lists;

/// <summary>
/// tenantry-snapshots --crawl: opens the demo tenant (with P2, every page visible) and clicks every
/// enabled button it can find on every area, page tab, preview, record page and record tab, and
/// every item of every menu those buttons open. Reports unhandled exceptions, binding errors (a
/// view bound to something that doesn't exist), popups that don't close, and where each happened.
/// Demo data only; changes go to the demo's in-memory pipeline.
/// </summary>
static class Crawler
{
    sealed record Finding(string Kind, string Where, string What);

    static readonly List<Finding> Findings = [];
    static readonly HashSet<string> Seen = [];
    static string _where = "start";
    static int _clicks;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static readonly TimeSpan Budget = TimeSpan.FromMinutes(40);

    /// <summary>Buttons that leave the tenant, restart things or open the browser: not clicked.</summary>
    static readonly string[] Skip =
    [
        "Switch Tenant", "Sign Out", "Restart", "Try Again", "How to Install PowerShell", "Grant Permissions", "Update to",
        "Open in Purview", "Export in Purview", "Open in Browser", "Open Admin Center", "Copy Link", "Save as File…", "Export…",
    ];

    public static int Run()
    {
        MainViewModel.PreviewSettle = TimeSpan.Zero;
        Tenantry.ViewModels.Records.RecordFreshness.SettleAfter = TimeSpan.FromHours(1);
        Logger.Sink = new BindingSink();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Add("exception", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Add("unobserved task", e.Exception.InnerException ?? e.Exception);
            e.SetObserved();
        };
        Platform.SaveCsv = (_, _) => Task.FromResult(true);
        Platform.OpenCsv = () => Task.FromResult<(string, string)?>(null);
        Platform.PickFolder = () => Task.FromResult<string?>(null);

        var (window, main) = Open();
        foreach (var section in main.Sections.ToArray())
        {
            if (OutOfTime())
                break;
            // Security & Compliance's pages come in groups; every other area has one row of pages.
            IReadOnlyList<SecurityPageGroup?> groups = section.Key == "security" ? [.. SecurityAreaViewModel.Sections] : [null];
            foreach (var group in groups)
            {
                void Group()
                {
                    if (group is not null)
                        main.SecurityArea.SelectedSection = group;
                }
                main.SelectedSection = section;
                Group();
                Settle(20);
                var pages = PageSwitchers(window).ToArray();
                if (pages.Length == 0)
                    CrawlPlace(window, main, section.Title, () => Restore(main, section, null));
                for (var p = 0; p < pages.Length; p++)
                {
                    var index = p;
                    void Back()
                    {
                        Restore(main, section, null);
                        Group();
                        Restore(main, section, index);
                    }
                    Back();
                    CrawlPlace(window, main, $"{section.Title} › {Label(PageSwitchers(window).ElementAt(index))}", Back);
                }
            }
        }
        // The console and tray last: the tray has whatever the crawl staged.
        main.OpenPowerShellCommand.Execute(null);
        Settle(10);
        CrawlPlace(window, main, "PowerShell", () => main.OpenPowerShellCommand.Execute(null));

        Console.WriteLine($"crawled {_clicks} clicks in {Clock.Elapsed.TotalSeconds:0} s{(OutOfTime() ? " (stopped at the time budget)" : "")}");
        foreach (var group in Findings.GroupBy(f => (f.Kind, f.What)))
            Console.WriteLine($"{group.Key.Kind.ToUpperInvariant()}  {group.Key.What}\n    at: {string.Join(" | ", group.Select(f => f.Where).Distinct().Take(4))}");
        Console.WriteLine(Findings.Count == 0 ? "CRAWL CLEAN" : $"CRAWL FOUND {Findings.Select(f => (f.Kind, f.What)).Distinct().Count()} PROBLEM(S)");
        return Findings.Count == 0 ? 0 : 1;
    }

    static bool OutOfTime() => Clock.Elapsed > Budget;

    /// <summary>Clicks every not-yet-seen button in view, then the first row's preview and record page.</summary>
    static void CrawlPlace(MainWindow window, MainViewModel main, string where, Action back, int depth = 0)
    {
        _where = where;
        Settle(15);
        CrawlButtons(window, main, where, back, depth);
        if (depth > 0 || OutOfTime())
            return;
        // A list here: preview its first row, then open it.
        back();
        Settle(10);
        if (CurrentList(main) is { Rows.Count: > 0 } list && list.HasPreview)
        {
            list.IsPreviewOpen = true;
            list.SelectedRow = list.Rows[0];
            Settle(15);
            CrawlButtons(window, main, where + " › preview", () => { back(); list.IsPreviewOpen = true; list.SelectedRow = list.Rows.Count > 0 ? list.Rows[0] : null; Settle(10); }, depth);
            back();
            Settle(5);
            if (list.HasRecordPages && list.Rows.Count > 0)
            {
                var tabs = main.Tabs.Count;
                list.OpenRow(list.Rows[0]);
                Settle(20);
                if (main.Tabs.Count > tabs && main.SelectedTab is { } page)
                    CrawlRecord(window, main, $"{where} › {page.Title}", page);
            }
        }
    }

    /// <summary>A record page: each of its tabs, and every button on each.</summary>
    static void CrawlRecord(MainWindow window, MainViewModel main, string where, TabViewModel page)
    {
        void Show()
        {
            if (!main.Tabs.Contains(page))
                return;
            main.SelectedTab = page;
            Settle(10);
        }
        Show();
        var tabItems = RecordTabs(window).Count();
        if (tabItems == 0)
            CrawlButtons(window, main, where, Show, 1);
        for (var t = 0; t < tabItems && !OutOfTime(); t++)
        {
            var index = t;
            void ShowTab()
            {
                Show();
                if (RecordTabs(window).ElementAtOrDefault(index) is { } item)
                {
                    item.IsSelected = true;
                    Settle(15);
                }
            }
            ShowTab();
            var name = RecordTabs(window).ElementAtOrDefault(index) is { } i ? Label(i) : index.ToString();
            CrawlButtons(window, main, $"{where} › {name}", ShowTab, 1);
        }
        if (main.Tabs.Contains(page))
            main.CloseTab(page);
        Settle(5);
    }

    static void CrawlButtons(MainWindow window, MainViewModel main, string where, Action back, int depth)
    {
        for (var guard = 0; guard < 120 && !OutOfTime(); guard++)
        {
            back();
            var next = Clickable(window, main).FirstOrDefault(b => !Seen.Contains(where + "|" + Label(b)));
            if (next is null)
                return;
            var label = Label(next);
            Seen.Add(where + "|" + label);
            _where = $"{where} › [{label}]";
            var tabsBefore = main.Tabs.ToArray();
            try
            {
                if (!next.IsEffectivelyVisible || !next.IsEnabled)
                    continue;
                next.BringIntoView();
                Settle(2);
                Click(window, next);
            }
            catch (Exception e)
            {
                Add("exception", e);
                continue;
            }
            _clicks++;
            Settle(10);
            CrawlMenu(window, main, where, label, back);
            CloseDialog(main, window);
            // A new tab (a record or editor): crawl it once, then close it.
            var opened = main.Tabs.Except(tabsBefore).ToArray();
            foreach (var tab in opened)
            {
                if (depth == 0 && !Seen.Contains("tab|" + tab.GetType().Name))
                {
                    Seen.Add("tab|" + tab.GetType().Name);
                    CrawlRecord(window, main, $"{where} › {tab.Title}", tab);
                }
                if (main.Tabs.Contains(tab) && tab.CanClose)
                    main.CloseTab(tab);
            }
            Settle(3);
        }
    }

    /// <summary>A menu the click opened: each item once, reopening the menu each time.</summary>
    static void CrawlMenu(MainWindow window, MainViewModel main, string where, string button, Action back)
    {
        var items = OpenMenuItems(window).Select(Label).ToArray();
        if (items.Length == 0)
            return;
        Escape(window);
        foreach (var name in items)
        {
            if (Seen.Contains(where + "|menu|" + button + "|" + name) || OutOfTime())
                continue;
            Seen.Add(where + "|menu|" + button + "|" + name);
            back();
            var opener = Clickable(window, main).FirstOrDefault(b => Label(b) == button);
            if (opener is null)
                return;
            Click(window, opener);
            Settle(5);
            var item = OpenMenuItems(window).FirstOrDefault(i => Label(i) == name && i.IsEnabled);
            if (item is null)
            {
                Escape(window);
                continue;
            }
            _where = $"{where} › [{button}] › [{name}]";
            try
            {
                Click(window, item);
            }
            catch (Exception e)
            {
                Add("exception", e);
            }
            _clicks++;
            Settle(10);
            if (OpenMenuItems(window).Any())
            {
                Escape(window);
                if (OpenMenuItems(window).Any())
                    Findings.Add(new Finding("menu stays open", _where, "the menu didn't close after its item was clicked"));
            }
            CloseDialog(main, window);
        }
    }

    static void CloseDialog(MainViewModel main, MainWindow window)
    {
        if (!main.HasDialog)
            return;
        Escape(window);
        if (main.HasDialog)
            main.CloseDialog();
        Settle(3);
    }

    static void Restore(MainViewModel main, Tenantry.ViewModels.NavSection section, int? page)
    {
        if (main.HasDialog)
            main.CloseDialog();
        if (main.SelectedSection != section)
            main.SelectedSection = section;
        foreach (var tab in main.Tabs.Where(t => t.CanClose && t is not PowerShellConsoleViewModel).ToArray())
            main.CloseTab(tab);
        Settle(5);
        if (page is { } index && PageSwitchers(Window!).ElementAtOrDefault(index) is { } switcher && !IsSelected(switcher))
        {
            Click(Window!, switcher);
            Settle(15);
        }
    }

    static MainWindow? Window;

    static (MainWindow, MainViewModel) Open()
    {
        var demo = new TenantConnector().ConnectDemo(premium: true);
        if (demo.Directory is Tenantry.Demo.DemoTenantDirectory directory)
            directory.CheckTakes = TimeSpan.Zero;
        var main = new MainViewModel(demo with
        {
            IsDemo = false,
            ListState = new Tenantry.Core.Lists.MemoryListStateStore(),
            SyncSettings = new Tenantry.Core.Directory.MemorySyncSettingsStore(),
            PasswordStyle = demo.PasswordStyle ?? new MemoryPasswordStyleStore(),
            Changes = new ChangePipeline(new Tenantry.Demo.DemoChangeExecutor((Tenantry.Demo.DemoTenantDirectory)demo.Directory, demo.Records as Tenantry.Demo.DemoUserRecords),
                new ChangeLog(new Tenantry.Core.Cache.MemoryEntityStore()), new MemoryTestModeStore(true), demo.Account),
        }, () => { });
        main.Users.LoadAsync().GetAwaiter().GetResult();
        var shell = new ShellViewModel(new SnapshotConnector(demo));
        shell.ShowPage(main);
        var window = new MainWindow { Width = 1440, Height = 1000, DataContext = shell };
        window.Show();
        Window = window;
        Settle(20);
        return (window, main);
    }

    // ---- Finding things on screen ----------------------------------------------------------

    static IEnumerable<Control> PageSwitchers(MainWindow window) =>
        window.GetVisualDescendants().OfType<Control>()
            .Where(c => c is TabStripItem or ListBoxItem && c.IsEffectivelyVisible && c.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage);

    static bool IsSelected(Control c) => c is TabStripItem t ? t.IsSelected : c is ListBoxItem l && l.IsSelected;

    static IEnumerable<TabItem> RecordTabs(MainWindow window) =>
        window.GetVisualDescendants().OfType<TabControl>().Where(t => t.Classes.Contains("recordtabs") && t.IsEffectivelyVisible)
            .Take(1).SelectMany(t => t.GetVisualDescendants().OfType<TabItem>().Where(i => i.IsEffectivelyVisible));

    static DataListViewModel? CurrentList(MainViewModel main) => main.SelectedTab switch
    {
        DataListViewModel list => list,
        _ => main.SelectedTab?.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(DataListViewModel))
            .Select(p => p.GetValue(main.SelectedTab) as DataListViewModel)
            .FirstOrDefault(l => l is not null && l.Rows.Count > 0 && Window!.GetVisualDescendants().OfType<Control>().Any(c => c.DataContext == l && c.IsEffectivelyVisible)),
    };

    static IEnumerable<Button> Clickable(MainWindow window, MainViewModel main) =>
        window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.IsEnabled && b.Command != main.SwitchTenantCommand && !InSidebar(b))
            .Where(b => Label(b) is { Length: > 0 } l && !Skip.Any(s => l.StartsWith(s, StringComparison.Ordinal)))
            .ToArray();

    static bool InSidebar(Visual v) => v.GetVisualAncestors().OfType<Control>().Any(a => a.Classes.Contains("sidebar"));

    static IEnumerable<MenuItem> OpenMenuItems(MainWindow window) =>
        TopLevel.GetTopLevel(window) is { } top
            ? top.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen).SelectMany(p => p.Child?.GetVisualDescendants().OfType<MenuItem>() ?? [])
                .Concat(window.GetVisualDescendants().OfType<MenuItem>().Where(m => m.IsEffectivelyVisible && m.GetVisualAncestors().OfType<Popup>().Any()))
                .Distinct()
            : [];

    static string Label(Control c)
    {
        var text = c switch
        {
            ContentControl { Content: string s } => s,
            HeaderedSelectingItemsControl { Header: string h } => h,
            _ => null,
        };
        text ??= string.Join(" ", c.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Take(3));
        if (string.IsNullOrWhiteSpace(text))
            text = ToolTip.GetTip(c) as string ?? (c is Button { Command: { } command } ? command.GetType().Name : "");
        return text.Length > 60 ? text[..60] : text;
    }

    // ---- Input and settling ----------------------------------------------------------------

    static void Click(TopLevel top, Control control)
    {
        var middle = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
            ?? throw new InvalidOperationException("control isn't on screen");
        top.MouseDown(middle, MouseButton.Left);
        top.MouseUp(middle, MouseButton.Left);
        Settle(2);
    }

    static void Escape(MainWindow window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(3);
    }

    static void Settle(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    static void Add(string kind, Exception e)
    {
        var frame = e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Tenantry", StringComparison.Ordinal)) ?? "";
        Findings.Add(new Finding(kind, _where, $"{e.GetType().Name}: {e.Message} {frame}"));
    }

    /// <summary>Avalonia's binding warnings: a view bound to a property its view model doesn't have.</summary>
    sealed class BindingSink : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Record(messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) => Record(messageTemplate, propertyValues);

        static void Record(string template, object?[] values)
        {
            var text = template;
            foreach (var value in values)
            {
                var start = text.IndexOf('{');
                var end = start < 0 ? -1 : text.IndexOf('}', start);
                if (end < 0)
                    break;
                text = text[..start] + value + text[(end + 1)..];
            }
            // Bindings to a null source while a view is torn down aren't bugs.
            if (text.Contains("Value is null", StringComparison.Ordinal))
                return;
            Findings.Add(new Finding("binding", _where, text.Length > 300 ? text[..300] : text));
        }
    }
}
