using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tenantry.App;
using Tenantry.App.Services;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels;
using Tenantry.ViewModels.Records;

/// <summary>
/// tenantry-snapshots --behavior-check: drives the real window with simulated mouse clicks and
/// checks how pop-ups behave, so "a menu stays open over the dialog it opened" is caught here, not
/// by a person. Prints PASS/FAIL per check; any failure fails the run.
/// </summary>
static class InteractionChecks
{
    static int _failed;

    public static int Run()
    {
        Check("Record Actions menu: Convert closes the menu and opens its popup", () =>
        {
            var (window, main, page) = OpenRecord();
            var actions = FindButton(window, b => ToolTip.GetTip(b) as string == "More actions" && b.FindAncestorOfType<UserControl>()?.DataContext == page);
            Click(window, actions);
            var menu = (MenuFlyout)actions.Flyout!;
            Expect(menu.IsOpen, "the Actions menu opened");
            Click(MenuItem(menu, m => m.Command == page.OpenConvertMailboxCommand));
            Expect(!menu.IsOpen, "the Actions menu closed");
            Expect(main.Dialog?.Content is ConvertMailboxViewModel, "the Convert mailbox popup is open");
            window.Close();
        });

        Check("Record Actions menu: Sign out everywhere closes the menu and adds the change", () =>
        {
            var (window, main, page) = OpenRecord();
            var actions = FindButton(window, b => ToolTip.GetTip(b) as string == "More actions" && b.FindAncestorOfType<UserControl>()?.DataContext == page);
            Click(window, actions);
            var menu = (MenuFlyout)actions.Flyout!;
            Click(MenuItem(menu, m => m.Command == page.SignOutEverywhereCommand));
            Expect(!menu.IsOpen, "the Actions menu closed");
            Expect(main.Pending!.Count == 1, "Sign out everywhere is in Pending changes");
            window.Close();
        });

        Check("Mail tab, mobile devices: a wipe choice closes its menu and adds the change", () =>
        {
            var (window, main, page) = OpenRecord();
            page.SelectedSection = page.MailTab;
            Settle();
            page.MailTab.SelectedPage = MailPage.Devices;
            Settle();
            var wipe = FindButton(window, b => ToolTip.GetTip(b) as string == "Wipe options");
            wipe.BringIntoView();
            Settle();
            Click(window, wipe);
            var menu = (MenuFlyout)wipe.Flyout!;
            Expect(menu.IsOpen, "the wipe menu opened");
            Click(MenuItem(menu, m => (m.Header as string ?? "").StartsWith("Erase the Whole Device", StringComparison.Ordinal)));
            Expect(!menu.IsOpen, "the wipe menu closed");
            Expect(main.Pending!.Count == 1, "the wipe is in Pending changes");
            window.Close();
        });

        Check("Your access: turning Test mode back on closes the panel", () =>
        {
            var (window, main, _) = OpenRecord();
            main.Pending!.StartTurnOffCommand.Execute(null);
            main.Pending.TurnOffText = main.TenantName;
            main.Pending.TurnOffTestModeCommand.Execute(null);
            Settle();
            var access = FindButton(window, b => b.Flyout is Flyout && Labelled(b, "Your Access"));
            Click(window, access);
            var panel = (Flyout)access.Flyout!;
            Expect(panel.IsOpen, "the Your access panel opened");
            Click(Buttons(panel).First(b => b.Command == main.Pending.TurnOnTestModeCommand));
            Expect(!panel.IsOpen, "the panel closed");
            Expect(main.Pending.IsTestMode, "Test mode is on again");
            window.Close();
        });

        Check("Select from a list: Check matching rows keeps the panel open to show the result", () =>
        {
            var (window, main, _) = OpenList();
            var paste = FindButton(window, b => b.Flyout is Flyout && Labelled(b, "Select from List"));
            Click(window, paste);
            var panel = (Flyout)paste.Flyout!;
            main.Users.PasteText = ((UserSummary)main.Users.Rows[0]).UserPrincipalName + "\nnobody@nowhere.example";
            Settle();
            Click(Buttons(panel).First(b => b.Command == main.Users.SelectFromPasteCommand));
            Expect(panel.IsOpen, "the panel stayed open");
            Expect(main.Users.PasteResult is { Length: > 0 }, "the result shows in it");
            window.Close();
        });

        Check("Save as a view: Save with no name stays open; with a name it saves and closes", () =>
        {
            var (window, main, _) = OpenList();
            var save = FindButton(window, b => b.Flyout is Flyout f && Buttons(f).Any(x => x.Content as string == "Save"));
            Click(window, save);
            var panel = (Flyout)save.Flyout!;
            main.Users.NewViewName = "";
            Click(Buttons(panel).First(b => b.Content as string == "Save"));
            Expect(panel.IsOpen, "still open, asking for a name");
            main.Users.NewViewName = "Checked by the behavior check";
            Click(Buttons(panel).First(b => b.Content as string == "Save"));
            Expect(!panel.IsOpen, "closed after saving");
            window.Close();
        });

        Check("Record popup (Manage): Cancel and the close button close it", () =>
        {
            var (window, main, page) = OpenRecord();
            page.ResetPasswordCommand.Execute(null);
            Settle();
            Expect(main.Dialog is not null, "Reset password is open");
            Click(window, FindButton(window, b => b.Command is { } c && b.DataContext is ResetPasswordViewModel r && c == r.CancelCommand));
            Expect(main.Dialog is null, "Cancel closed it");
            window.Close();
        });

        Check("OneDrive tab: picking a person adds an Adding row, and its undo button takes it back out", () =>
        {
            var (window, main, page) = OpenRecord(p =>
            {
                var tab = p.Sections.OfType<OneDriveSection>().Single();
                tab.LoadAsync().GetAwaiter().GetResult();
                return tab.HasDrive;
            });
            var tab = page.Sections.OfType<OneDriveSection>().Single();
            page.SelectedSection = tab;
            Settle();
            tab.AccessPicker.Query = "a";
            Settle();
            var pick = FindButton(window, b => b.DataContext is Tenantry.ViewModels.Editing.PickOption && b.FindAncestorOfType<UserControl>()?.DataContext == tab.AccessPicker);
            var name = ((Tenantry.ViewModels.Editing.PickOption)pick.DataContext!).Name;
            Click(window, pick);
            Expect(main.Pending!.Count == 1, "the access change is in Pending changes");
            Expect(tab.Access.Any(r => r.Name == name && r.PendingText == "Adding"), "the person shows as Adding");
            Click(window, FindButton(window, b => b.DataContext is OneDriveAccessRow r && r.Name == name && b.Command == r.RemoveCommand));
            Expect(main.Pending.Count == 0, "undo took it back out");
            window.Close();
        });

        Check("Activity tab: choosing Changes they made reads the audit log the other way", () =>
        {
            var (window, _, page) = OpenRecord();
            var tab = page.Sections.OfType<ActivitySection>().Single();
            page.SelectedSection = tab;
            tab.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var radio = window.GetVisualDescendants().OfType<RadioButton>().First(r => r.Content as string == "Changes They Made");
            Click(window, radio);
            Expect(tab.AuditByThem, "the view switched to changes they made");
            Expect(tab.Audit.All(a => a.Party.Length == 0 || a.Party.StartsWith("To ", StringComparison.Ordinal)), "rows name what they changed");
            window.Close();
        });

        Check("Record Actions menu: Offboard closes the menu, and Add to Pending changes stages the checklist", () =>
        {
            var (window, main, page) = OpenRecord();
            var actions = FindButton(window, b => ToolTip.GetTip(b) as string == "More actions" && b.FindAncestorOfType<UserControl>()?.DataContext == page);
            Click(window, actions);
            var menu = (MenuFlyout)actions.Flyout!;
            Click(MenuItem(menu, m => m.Command == page.OffboardCommand));
            Expect(!menu.IsOpen, "the Actions menu closed");
            var workflow = main.Dialog?.Content as Tenantry.ViewModels.Workflows.OffboardingViewModel;
            Expect(workflow is not null, "the Offboard popup is open");
            Settle();
            var stage = FindButton(window, b => b.Content as string == "Add to Pending Changes");
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            for (var i = 0; i < 50 && workflow!.IsPlanning; i++)
                Settle();
            Expect(main.Pending!.Count > 3, $"the checklist's changes are in Pending changes (planning {workflow!.IsPlanning}, pending {main.Pending.Count}, " +
                $"result '{workflow.Result}', notes '{string.Join(" | ", workflow.Notes)}', dialog {main.Dialog?.Content?.GetType().Name})");
            Expect(main.Pending.Rows.Any(r => r.Change is RevokeSessions), "sign out everywhere is among them");
            window.Close();
        });

        Check("Record Actions menu: Respond to a compromised account checks, then Add to Pending changes stages and closes", () =>
        {
            var (window, main, page) = OpenRecord();
            var actions = FindButton(window, b => ToolTip.GetTip(b) as string == "More actions" && b.FindAncestorOfType<UserControl>()?.DataContext == page);
            Click(window, actions);
            var menu = (MenuFlyout)actions.Flyout!;
            Click(MenuItem(menu, m => m.Command == page.RespondToCompromiseCommand));
            Expect(!menu.IsOpen, "the Actions menu closed");
            var workflow = main.Dialog?.Content as Tenantry.ViewModels.Workflows.CompromiseViewModel;
            Expect(workflow is not null, "the response popup is open");
            for (var i = 0; i < 50 && workflow!.IsChecking; i++)
                Settle();
            Expect(!workflow!.IsChecking, "the check finished");
            var stage = FindButton(window, b => b.Content as string == "Add to Pending Changes");
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            Expect(main.Dialog is null, "the popup closed");
            Expect(main.Pending!.Rows.Any(r => r.Change is ResetPassword) && main.Pending.Rows.Any(r => r.Change is RevokeSessions), "the response is in Pending changes");
            window.Close();
        });

        Check("Mail & security: the sidebar opens it, the Message trace chip switches page, Search fills the list", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            Expect(main.SelectedTab == area, "the Mail & security tab is showing");
            for (var i = 0; i < 50 && area.Quarantine.IsLoading; i++)
                Settle();
            Expect(area.Quarantine.Total > 0, "quarantine read when first shown");
            var chip = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "trace" });
            Click(window, chip);
            Expect(area.ShowTrace, "the Message trace page is showing");
            var search = FindButton(window, b => b.Command == area.SearchTraceCommand);
            Click(window, search);
            for (var i = 0; i < 50 && area.Trace.IsLoading; i++)
                Settle();
            Expect(area.Trace.Total > 0, "the search filled the list");
            window.Close();
        });

        Check("Block and allow lists: Add entries opens the panel, and its button stages the typed entries", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "lists" }));
            for (var i = 0; i < 50 && area.Entries.IsLoading; i++)
                Settle();
            Expect(area.Entries.Total > 0, "the lists were read when the page opened");
            Click(window, FindButton(window, b => b.Command == area.ToggleAddCommand));
            Expect(area.IsAddOpen, "the Add panel opened");
            area.AddEntries.Values = "zz-tenantry-test-spam@bad.example";
            Settle();
            var add = FindButton(window, b => b.Command == area.AddEntries.StageCommand);
            add.BringIntoView();
            Settle();
            Click(window, add);
            Expect(main.Pending!.Rows.Any(r => r.Change is AddListEntry), "the entry is in Pending changes");
            window.Close();
        });

        Check("Mail Flow Rules: the tab reads the rules, and the preview's Turn Off stages the change", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "rules" }));
            for (var i = 0; i < 50 && area.Rules.IsLoading; i++)
                Settle();
            Expect(area.Rules.Total > 0, "the rules were read");
            area.Rules.PreviewRow(area.Rules.Rows.Cast<MailFlowRule>().First(r => r.Enabled));
            Settle();
            var preview = (Tenantry.ViewModels.MailSecurity.RulePreviewViewModel)area.Rules.PreviewModel!;
            var off = FindButton(window, b => b.Command == preview.ToggleEnabledCommand);
            off.BringIntoView();
            Settle();
            Click(window, off);
            Expect(main.Pending!.Rows.Any(r => r.Change is SetRuleEnabled { New: false }), "turning it off is in Pending changes");
            window.Close();
        });

        Console.WriteLine(_failed == 0 ? "ALL INTERACTION CHECKS PASSED" : $"{_failed} INTERACTION CHECK(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }

    static void Check(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine($"PASS  {name}");
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine($"FAIL  {name}: {e.Message}");
        }
    }

    static void Expect(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"expected: {what}");
    }

    /// <summary>The demo tenant, editable (Pending changes on, Test mode on), on its Users list.</summary>
    static (MainWindow Window, MainViewModel Main, ShellViewModel Shell) OpenList()
    {
        var demo = new TenantConnector().ConnectDemo();
        if (demo.Directory is Tenantry.Demo.DemoTenantDirectory directory)
            directory.CheckTakes = TimeSpan.Zero;
        var main = new MainViewModel(demo with
        {
            IsDemo = false,
            ListState = new Tenantry.Core.Lists.MemoryListStateStore(),
            Changes = new ChangePipeline(new Tenantry.Demo.DemoChangeExecutor((Tenantry.Demo.DemoTenantDirectory)demo.Directory),
                new ChangeLog(new Tenantry.Core.Cache.MemoryEntityStore()), new MemoryTestModeStore(true), demo.Account),
        }, () => { });
        main.Users.LoadAsync().GetAwaiter().GetResult();
        var shell = new ShellViewModel(new SnapshotConnector(demo));
        shell.ShowPage(main);
        var window = new MainWindow { Width = 1440, Height = 900, DataContext = shell };
        window.Show();
        Settle();
        return (window, main, shell);
    }

    /// <summary>The same, with a person's record page open: the third person, or the first that <paramref name="suits"/>.</summary>
    static (MainWindow Window, MainViewModel Main, UserDetailViewModel Page) OpenRecord(Func<UserDetailViewModel, bool>? suits = null)
    {
        var (window, main, _) = OpenList();
        IEnumerable<object> candidates = suits is null ? [main.Users.Rows[2]] : main.Users.Rows.Cast<object>();
        UserDetailViewModel? page = null;
        foreach (var row in candidates)
        {
            main.OpenUser((UserSummary)row);
            Settle();
            page = (UserDetailViewModel)main.SelectedTab!;
            if (suits is null || suits(page))
                break;
        }
        if (page is null)
            throw new InvalidOperationException("no suitable record");
        page.MailTab.LoadAsync().GetAwaiter().GetResult();
        Settle();
        return (window, main, page);
    }

    static Button FindButton(Visual root, Func<Button, bool> match) =>
        root.GetVisualDescendants().OfType<Button>().FirstOrDefault(match)
        ?? throw new InvalidOperationException("button not found");

    /// <summary>A button whose own text says this (its command may not be bound until it's shown).</summary>
    static bool Labelled(Button button, string text) => button.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text);

    static IEnumerable<Button> Buttons(Flyout flyout) =>
        (flyout.Content as Control)?.GetLogicalDescendants().OfType<Button>() ?? [];

    static MenuItem MenuItem(MenuFlyout menu, Func<MenuItem, bool> match) =>
        menu.Items.OfType<MenuItem>().FirstOrDefault(match) ?? throw new InvalidOperationException("menu item not found");

    /// <summary>A real mouse click (down and up) at the middle of the control, on whatever top level shows it.</summary>
    static void Click(Control control) => Click(TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("not shown"), control);

    static void Click(TopLevel top, Control control)
    {
        var middle = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
            ?? throw new InvalidOperationException("control isn't on screen");
        top.MouseDown(middle, MouseButton.Left);
        top.MouseUp(middle, MouseButton.Left);
        Settle();
    }

    static void Settle()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}
