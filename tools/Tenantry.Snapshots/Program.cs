using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    var opening = new ShellViewModel(connector);
    opening.PrepareStartup();
    Render(opening.CurrentPage, $"starting-{suffix}");
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
        Render(main, "panel-columns-short-light", 1200, 620);
        // Mid-drag: the third column lifted and pulled down two rows, the others making room.
        Render(main, "panel-columns-drag-light", during: w => DragHandle(w, "ColumnsList", 2, 70));
        list.ClosePanels();
        list.AddSortByColumn(list.Columns.Single(c => c.Key == UserFields.LastSignIn));
        list.OpenSortPanel();
        Render(main, "panel-sort-drag-light", during: w => DragHandle(w, "SortList", 0, 60));
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

    // Switching between list tabs in one window (each list must keep its own view).
    var switching = Main();
    // Grouped rows add a hidden spacer column to the grid; rebuilding columns then crashed.
    switching.Users.SelectedGroup = switching.Users.GroupOptions.Single(g => g.Key == UserFields.Department);
    switching.SelectedSection = switching.Sections.Single(s => s.Key == "groups");
    switching.SelectedSection = switching.Sections.Single(s => s.Key == "users");
    Render(switching, $"tabs-switch-{suffix}", during: _ =>
    {
        NavSection Area(string key) => switching.Sections.Single(s => s.Key == key);
        switching.OpenUser(switching.Users.Rows.OfType<UserSummary>().First());
        Settle();
        switching.SelectedSection = Area("groups");
        Settle();
        switching.SelectedSection = Area("users");
        Settle();
        switching.Users.ResetColumnsCommand.Execute(null);
        Settle();
        switching.SelectedSection = Area("groups");
    });

    // --spacer-check: a canary for the DataGrid bug behind the 2026-10-21 crash. The first run
    // shows Columns.Clear() + Add still throws on a grouped grid (if it stops throwing, the
    // workaround in DataListView.RebuildColumns can go); the second shows our rebuild doesn't.
    if (args.Contains("--spacer-check"))
    {
        var grouped2 = Main();
        grouped2.Users.SelectedGroup = grouped2.Users.GroupOptions.Single(g => g.Key == UserFields.Department);
        Render(grouped2, "spacer-check", during: w =>
        {
            var grid = w.GetVisualDescendants().OfType<DataGrid>().First();
            var internals = typeof(DataGrid).GetProperty("ColumnsInternal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(grid)!;
            var spacer = internals.GetType().GetProperty("RowGroupSpacerColumn", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(internals)!;
            bool Represented() => (bool)spacer.GetType().GetProperty("IsRepresented", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.GetValue(spacer)!;
            Console.WriteLine($"    spacer represented while grouped: {Represented()}");
            var saved = grid.Columns.ToList();
            try
            {
                grid.Columns.Clear();
                grid.Columns.Add(new DataGridTextColumn { Header = "x" });
                Console.WriteLine("    Clear + Add: no error");
            }
            catch (ArgumentOutOfRangeException)
            {
                Console.WriteLine("    Clear + Add: ArgumentOutOfRangeException (the crash)");
            }
        });

        var grouped3 = Main();
        grouped3.Users.SelectedGroup = grouped3.Users.GroupOptions.Single(g => g.Key == UserFields.Department);
        Render(grouped3, "spacer-check-fixed", during: w =>
        {
            // The view's own rebuild path, while grouped: must not throw.
            foreach (var c in grouped3.Users.Columns.Where(c => c.Key == UserFields.Created))
                c.IsVisible = true;
            grouped3.Users.ResetColumnsCommand.Execute(null);
            Console.WriteLine("    view rebuild while grouped: no error");
        });
        return 0;
    }

    // Groups: the list with its preview, and a group's record page.
    var groupsPage = Main();
    groupsPage.SelectedSection = groupsPage.Sections.Single(s => s.Key == "groups");
    groupsPage.Groups.PreviewRow(groupsPage.Groups.Rows.Cast<GroupSummary>().First(g => g.DisplayName == "Finance"));
    Render(groupsPage, $"groups-{suffix}");
    if (full)
    {
        groupsPage.OpenGroup(groupsPage.Groups.Rows.Cast<GroupSummary>().First(g => g.DisplayName == "Announcements"));
        Render(groupsPage, "group-detail-light");

        // Shared mailboxes beside the groups, and one mailbox's page (people with access).
        var mailboxes = Main();
        mailboxes.SelectedSection = mailboxes.Sections.Single(s => s.Key == "groups");
        mailboxes.Groups.ApplyView(mailboxes.Groups.Views.Single(v => v.View.Id == "shared"));
        Render(mailboxes, "groups-shared-light");
        mailboxes.OpenGroup(mailboxes.Groups.Rows.Cast<GroupSummary>().OrderByDescending(g => g.Members.Count).First());
        Render(mailboxes, "mailbox-detail-light");

        // Mailbox columns and the external-forwarding view.
        var mail = Main();
        foreach (var c in mail.Users.Columns.Where(c => c.Key is UserFields.Mailbox or UserFields.Forwarding or UserFields.ForwardingTo))
            c.IsVisible = true;
        mail.Users.ApplyView(mail.Users.Views.Single(v => v.View.Id == "forwarding-external"));
        Render(mail, "users-forwarding-light");

        // Ctrl+K with a query typed.
        var search = Main();
        search.OpenPaletteCommand.Execute(null);
        search.Palette.Query = "fin";
        Render(search, "palette-light");
    }
    else
    {
        var search = Main();
        search.OpenPaletteCommand.Execute(null);
        search.Palette.Query = "fin";
        Render(search, "palette-dark");
    }

    // Editing: Test mode on, two changes staged from the preview (one still being edited), tray open.
    if (full)
    {
        var demo = connector.ConnectDemo();
        var editing = new MainViewModel(demo with
        {
            IsDemo = false,
            ListState = new Tenantry.Core.Lists.MemoryListStateStore(),
            Changes = new Tenantry.Core.Changes.ChangePipeline(new NoopExecutor(), new Tenantry.Core.Changes.ChangeLog(new Tenantry.Core.Cache.MemoryEntityStore()),
                new Tenantry.Core.Changes.MemoryTestModeStore(true), demo.Account),
        }, () => { });
        editing.Users.LoadAsync().GetAwaiter().GetResult();
        editing.Users.PreviewRow(editing.Users.Rows[1]);
        var person = (UserDetailViewModel)editing.Users.PreviewModel!;
        // The preview: summary, health, quick actions and a Manage row per area.
        Render(editing, "preview-manage-light");

        // Manage → Account opens the tab in a popup; one change staged, another being typed.
        person.ManageCommand.Execute(person.Account);
        person.Account.LoadAsync().GetAwaiter().GetResult();
        var fields = person.Account.Groups.SelectMany(g => g.Fields).ToDictionary(f => f.Property);
        fields["department"].EditCommand.Execute(null);
        fields["department"].Draft = "Operations";
        fields["department"].StageCommand.Execute(null);
        person.ToggleSignInCommand.Execute(null);
        fields["jobTitle"].EditCommand.Execute(null);
        fields["jobTitle"].Draft = "Senior Planner";
        Render(editing, "editing-light");

        // The record page on its Account tab, and a temporary password shown once after a reset.
        editing.CloseDialog();
        editing.OpenUser((UserSummary)editing.Users.Rows[2]);
        var page = (UserDetailViewModel)editing.SelectedTab!;
        page.SelectedSection = page.Account;
        page.Account.LoadAsync().GetAwaiter().GetResult();
        editing.Pending!.DiscardAllCommand.Execute(null);
        Render(editing, "user-account-light");

        // Licenses & apps: a license opened to its apps, one switched off, another being added.
        page.SelectedSection = page.LicensesTab;
        page.LicensesTab.LoadAsync().GetAwaiter().GetResult();
        page.LicensesTab.Licenses[0].IsExpanded = true;
        page.LicensesTab.Licenses[0].Apps.Last().IsOn = false;
        page.LicensesTab.Licenses[0].IsExpanded = true;
        page.LicensesTab.SelectedToAdd = page.LicensesTab.AddOptions.First(o => o.IsAvailable);
        page.LicensesTab.AddLicenseCommand.Execute(null);
        page.LicensesTab.Licenses[0].IsExpanded = true;
        Render(editing, "user-licenses-light");
        editing.Pending.DiscardAllCommand.Execute(null);

        // Mail: storage, addresses (one being added), forwarding outside typed, a scheduled reply.
        page.SelectedSection = page.MailTab;
        page.MailTab.LoadAsync().GetAwaiter().GetResult();
        page.MailTab.NewAliasName = "abigayle.a";
        page.MailTab.AddAliasCommand.Execute(null);
        page.MailTab.ForwardOn = true;
        page.MailTab.ForwardTo = "abigayle@gmail.example";
        page.MailTab.ReplyScheduled = true;
        page.MailTab.InternalMessage = "I'm away until Monday. For anything urgent, contact the front desk.";
        Render(editing, "user-mail-light", height: 1400);
        editing.Pending.DiscardAllCommand.Execute(null);

        // Groups & access: their groups (one membership being removed), a group being searched for.
        editing.Groups.LoadAsync().GetAwaiter().GetResult();
        page.Update(page.User);
        page.SelectedSection = page.GroupsTab;
        page.GroupsTab.LoadAsync().GetAwaiter().GetResult();
        if (page.GroupsTab.Groups.FirstOrDefault() is { } firstGroup)
            firstGroup.Toggles[0].IsOn = false;
        page.GroupsTab.GroupPicker.Query = "sg";
        Render(editing, "user-groups-light");
        editing.Pending.DiscardAllCommand.Execute(null);

        // A group's preview → Manage: owners and members in a popup, one owner being removed, a member being searched for.
        editing.SelectedSection = editing.Sections.Single(s => s.Key == "groups");
        editing.Groups.PreviewRow(editing.Groups.Rows.Cast<GroupSummary>().First(g => g.DisplayName == "Finance"));
        Render(editing, "group-preview-manage-light");
        var finance = (GroupDetailViewModel)editing.Groups.PreviewModel!;
        finance.ManagePeopleCommand.Execute(null);
        finance.People.Owners[0].RemoveCommand.Execute(null);
        finance.People.MemberPicker.Query = "a";
        Render(editing, "group-people-light");
        editing.CloseDialog();
        editing.Pending.DiscardAllCommand.Execute(null);

        // A shared mailbox's people with access, one right being added.
        editing.Groups.ApplyView(editing.Groups.Views.Single(v => v.View.Id == "shared"));
        editing.Groups.PreviewRow(editing.Groups.Rows.Cast<GroupSummary>().OrderByDescending(g => g.Members.Count).First());
        var mailbox = (GroupDetailViewModel)editing.Groups.PreviewModel!;
        mailbox.ManagePeopleCommand.Execute(null);
        mailbox.People.Access[0].Toggles[2].IsOn = !mailbox.People.Access[0].Toggles[2].IsOn;
        Render(editing, "mailbox-people-light");
        editing.CloseDialog();
        editing.Pending.DiscardAllCommand.Execute(null);
        // Shift-click range on the checkboxes: click row 2, Shift-click row 6 → rows 2 to 6.
        editing.SelectedSection = editing.Sections.Single(s => s.Key == "users");
        editing.Users.ClearSelectionCommand.Execute(null);
        Render(editing, "users-range-light", during: w =>
        {
            ClickCheck(w, 1, shift: false);
            ClickCheck(w, 5, shift: true);
        });
        var shownRows = editing.Users.Rows.ToList();
        var range = editing.Users.SelectedRows.Select(r => shownRows.IndexOf(r)).Order().ToArray();
        Console.WriteLine($"    shift-click range checked rows: {string.Join(",", range)}");
        if (!range.SequenceEqual([1, 2, 3, 4, 5]))
            throw new InvalidOperationException("Shift-click range didn't check rows 1 to 5");
        editing.Users.SelectedRows = [];

        // Bulk edit: three users checked turn the preview into the selection panel.
        editing.Users.SelectedRows = editing.Users.Rows.Skip(3).Take(3).ToArray();
        Render(editing, "users-selected-light");
        // A detail staged for all of them, and an action opened in place.
        var panel = (Tenantry.ViewModels.Records.SelectionPanelViewModel)editing.Users.PreviewModel!;
        var department = panel.Details!.Fields.Single(f => f.Property == "department");
        department.EditCommand.Execute(null);
        department.Draft = "Operations";
        department.StageCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        panel.Bulk!.ToggleCommand.Execute(panel.Bulk.Actions.Single(a => a.Title == "Add to a group"));
        panel.Bulk.Actions.Single(a => a.Title == "Add to a group").Picker!.Query = "sg";
        Render(editing, "bulk-edit-light");
        editing.Pending.DiscardAllCommand.Execute(null);
        editing.Users.ClearSelectionCommand.Execute(null);

        // The same panel for groups and mailboxes.
        editing.SelectedSection = editing.Sections.Single(s => s.Key == "groups");
        editing.Groups.ApplyView(editing.Groups.Views.First());
        editing.Groups.SelectedRows = editing.Groups.Rows.Take(4).ToArray();
        Render(editing, "groups-selected-light");
        editing.Groups.ClearSelectionCommand.Execute(null);
        editing.SelectedSection = editing.Sections.Single(s => s.Key == "users");
        editing.SelectedTab = page;
        // Demo people aren't test objects, so Test mode would (rightly) refuse the reset.
        editing.Pending.TurnOffTestModeCommand.Execute(null);
        editing.Pending.StartTurnOffCommand.Execute(null);
        editing.Pending.TurnOffText = editing.TenantName;
        editing.Pending.TurnOffTestModeCommand.Execute(null);
        page.ResetPasswordCommand.Execute(null);
        ((Tenantry.ViewModels.Records.ResetPasswordViewModel)editing.Dialog!.Content).StageCommand.Execute(null);
        editing.Pending.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        editing.Pending.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Render(editing, "password-reveal-light");
    }

    // A synced tenant: the sidebar shows when the local copy was last brought up to date.
    var synced = new MainViewModel(connector.ConnectDemo() with
    {
        ListState = new Tenantry.Core.Lists.MemoryListStateStore(), Sync = new SnapshotSync(), SyncSettings = new MemorySyncSettingsStore(),
    }, () => { });
    synced.Users.LoadAsync().GetAwaiter().GetResult();
    Render(synced, $"users-synced-{suffix}");
    // Settings: how often each part of the local copy is read, and when it last was.
    synced.OpenSettingsCommand.Execute(null);
    Render(synced, $"settings-{suffix}");
    synced.CloseDialog();
    if (full)
    {
        // The new mailbox views: nearly full, with the storage and reply columns shown.
        foreach (var c in synced.Users.Columns.Where(c => c.Key is UserFields.StorageUsed or UserFields.AutoReply))
            c.IsVisible = true;
        synced.Users.ApplyView(synced.Users.Views.Single(v => v.View.Id == "nearly-full"));
        Render(synced, "users-nearly-full-light");
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

// Presses the drag handle of row `index` in the named list and moves the pointer `dy` pixels
// without releasing, leaving the drag in progress for the capture.
/// <summary>Clicks a row's checkbox (the index-th one shown), with Shift held when asked.</summary>
static void ClickCheck(MainWindow window, int index, bool shift)
{
    var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.IsVisible);
    var check = grid.GetVisualDescendants().OfType<DataGridRow>()
        .OrderBy(r => r.TranslatePoint(new Point(0, 0), grid)!.Value.Y)
        .ElementAt(index)
        .GetVisualDescendants().OfType<CheckBox>().First();
    var at = check.TranslatePoint(new Point(check.Bounds.Width / 2, check.Bounds.Height / 2), window)!.Value;
    var modifiers = shift ? RawInputModifiers.Shift : RawInputModifiers.None;
    window.MouseMove(at, modifiers);
    window.MouseDown(at, MouseButton.Left, modifiers);
    window.MouseUp(at, MouseButton.Left, modifiers);
    Dispatcher.UIThread.RunJobs();
}

static void DragHandle(MainWindow window, string listName, int index, double dy)
{
    var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == listName);
    var handle = list.ContainerFromIndex(index)!.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("draghandle"));
    var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
    window.MouseMove(start);
    window.MouseDown(start, MouseButton.Left);
    for (var step = 1; step <= 6; step++)
    {
        window.MouseMove(start + new Point(0, dy * step / 6), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
    }
}

void Render(object page, string name, int width = 1440, int height = 900, Action<MainWindow>? during = null)
{
    var shell = new ShellViewModel(connector);
    shell.ShowPage(page);
    var window = new MainWindow { Width = width, Height = height, DataContext = shell };
    window.Show();
    Settle();
    if (during is not null)
    {
        during(window);
        // Let the transitions (under 200 ms) finish on the wall clock before capturing.
        for (var i = 0; i < 12; i++)
        {
            Thread.Sleep(25);
            Settle();
        }
    }
    using var frame = window.CaptureRenderedFrame()
        ?? throw new InvalidOperationException($"No frame rendered for {name}");
#pragma warning disable CS0618 // the encoder-options overload adds nothing for a plain PNG
    frame.Save(Path.Combine(output, name + ".png"));
#pragma warning restore CS0618
    window.Close();
    Console.WriteLine($"  {name}.png");
}

static void Settle()
{
    for (var i = 0; i < 10; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}

/// <summary>Accepts every change and does nothing (snapshots never apply).</summary>
sealed class NoopExecutor : Tenantry.Core.Changes.IChangeExecutor
{
    public Task<Tenantry.Core.Changes.ApplyResult> ApplyAsync(Tenantry.Core.Changes.ChangeOp change, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Tenantry.Core.Changes.ApplyResult(Tenantry.Core.Changes.ChangeOutcome.Applied,
            Reveal: change is Tenantry.Core.Changes.ResetPassword ? "Kp7r-Xw3n-Bq9t-Hm4s" : null));
}

/// <summary>A sync that last ran a few minutes ago and never runs again (nothing to fetch in a snapshot).</summary>
sealed class SnapshotSync : ITenantSync
{
    public DateTimeOffset? LastSyncedAt { get; } = DateTimeOffset.Now.AddMinutes(-4);

    public bool IsPersistent => true;

    public IReadOnlyList<string> Notes => [];

    public Task<int> SyncAsync(IProgress<string>? progress = null, SyncScope scope = SyncScope.Changes, CancellationToken cancellationToken = default) => Task.FromResult(0);

    public IReadOnlyList<SyncArea> Areas =>
    [
        new("Users", "Everyone in the directory, their licenses and status (changes only)", LastSyncedAt, "Every 10 minutes"),
        new("Groups", "Groups with their members and owners (changes only)", LastSyncedAt, "Every 10 minutes"),
        new("Licenses", "The tenant's licenses and how many are left", LastSyncedAt, "Every 10 minutes"),
        new("Last sign-in times", "When each person last signed in", DateTimeOffset.Now.AddMinutes(-34), "Every hour"),
        new("Domains and Teams", "The tenant's domains, and which groups are Teams", DateTimeOffset.Now.AddMinutes(-34), "Every hour"),
        new("Mailboxes", "Mailbox types, addresses, forwarding, address list; who can use shared mailboxes", DateTimeOffset.Now.AddMinutes(-34), "Every hour"),
        new("Mailbox size and automatic replies", "Two reads per mailbox, in the background", DateTimeOffset.Now.AddMinutes(-31), "Every hour"),
    ];
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
