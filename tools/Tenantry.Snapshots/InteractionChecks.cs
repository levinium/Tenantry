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

        Check("Rule editor: New Rule opens a tab; a starter, the action list and typing put the rule in Pending changes; Done closes it", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "rules" }));
            for (var i = 0; i < 50 && area.Rules.IsLoading; i++)
                Settle();
            Click(window, FindButton(window, b => b.Command == area.NewRuleCommand));
            var editor = main.SelectedTab as Tenantry.ViewModels.MailSecurity.RuleEditorViewModel ?? throw new InvalidOperationException("no editor tab opened");
            Settle();
            Click(window, FindButton(window, b => b.DataContext is Tenantry.ViewModels.MailSecurity.RuleStarter { Title: "Blank Rule" }));
            Expect(editor.IsReady, "the blank rule is showing");
            editor.Name = "zz-tenantry-test-editor";
            var addAction = FindButton(window, b => b.Name == "AddAction");
            addAction.BringIntoView();
            Settle();
            Click(window, addAction);
            var list = (Flyout)addAction.Flyout!;
            Expect(list.IsOpen, "the action list opened");
            editor.ActionPicker.Search = "prepend the subject";
            Settle();
            var pick = Buttons(list).First(b => b.DataContext is Tenantry.ViewModels.MailSecurity.RulePickerItem { Def.Parameter: "PrependSubject" });
            Click(pick);
            Expect(editor.Actions.Count == 1 && !list.IsOpen, "the action was added and the list closed");

            // Several recipients: typed and Enter, then a pasted list, each a chip.
            var addCondition = FindButton(window, b => b.Name == "AddCondition");
            Click(window, addCondition);
            var conditions = (Flyout)addCondition.Flyout!;
            Click(Buttons(conditions).First(b => b.DataContext is Tenantry.ViewModels.MailSecurity.RulePickerItem { Def.Parameter: "SentTo" }));
            var entry = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "EntryBox" && t.DataContext == editor.Conditions[0].Main);
            entry.Focus();
            entry.Text = "zz-tenantry-test-bo@contoso.example";
            Settle();
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Settle();
            entry.Text = "a@contoso.example, b@contoso.example";
            Settle();
            Expect(editor.Conditions[0].Main!.Chips.Count == 3, $"three recipients as chips (got {editor.Conditions[0].Main!.Chips.Count})");
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.DataContext == editor.Actions[0].Main && t.IsEffectivelyVisible);
            box.Text = "[TEST] ";
            Settle();
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateRule), "the new rule is in Pending changes");
            var done = FindButton(window, b => b.Command == editor.DoneCommand);
            done.BringIntoView();
            Settle();
            Click(window, done);
            Expect(!main.Tabs.Contains(editor), "Done closed the editor");
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateRule), "the rule stayed in Pending changes");
            window.Close();
        });

        Check("Settings › Passwords: choosing Pattern shows the parts, and typing pattern text rebuilds them", () =>
        {
            var (window, main, _) = OpenList();
            main.OpenSettingsCommand.Execute(null);
            Settle();
            var settings = (SettingsViewModel)main.Dialog!.Content;
            settings.SelectedArea = settings.SettingsAreas.Single(a => a.Key == "passwords");
            Settle();
            var passwords = settings.Passwords!;
            var pattern = window.GetVisualDescendants().OfType<RadioButton>().First(r => (r.Content as string ?? "").StartsWith("Pattern:", StringComparison.Ordinal));
            Click(window, pattern);
            Expect(passwords.UsePattern, $"Pattern is chosen (radio checked {pattern.IsChecked})");
            Settle();
            Expect(window.GetVisualDescendants().OfType<ComboBox>().Any(c => c.DataContext is PatternPartRow && c.IsEffectivelyVisible), "the parts show");
            passwords.PatternText = "U2 l4 D3 S1";
            Settle();
            Expect(passwords.UsePattern && passwords.Parts.Count == 4, $"still a pattern, with its 4 parts (pattern {passwords.UsePattern}, parts {passwords.Parts.Count})");
            passwords.UseDefaultCommand.Execute(null);
            window.Close();
        });

        Check("New User: the list's button opens the form; Add puts the new person in Pending changes and closes it", () =>
        {
            var (window, main, _) = OpenList();
            Click(window, FindButton(window, b => b.Command == main.Users.RunPrimaryActionCommand));
            var form = main.Dialog?.Content as Tenantry.ViewModels.Records.NewUserViewModel ?? throw new InvalidOperationException("the form didn't open");
            for (var i = 0; i < 10; i++)
                Settle();
            form.FirstName = "Zz-tenantry-test";
            form.LastName = "Pat";
            Settle();
            var add = FindButton(window, b => b.Command == form.AddCommand);
            add.BringIntoView();
            Settle();
            Click(window, add);
            Expect(main.Dialog is null, "the form closed");
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateUser), "the new person is in Pending changes");
            window.Close();
        });

        Check("Import from CSV: the button opens it, a chosen file is checked row by row, and Add stages the ready rows", () =>
        {
            var (window, main, _) = OpenList();
            var someone = main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member && u.Department != "Testing");
            var domain = someone.UserPrincipalName[(someone.UserPrincipalName.IndexOf('@') + 1)..];
            Platform.OpenCsv = () => Task.FromResult<(string, string)?>(("people.csv",
                $"Sign-In Name,Display Name,Department\nzz-tenantry-test-csv@{domain},Csv Person,Testing\n{someone.UserPrincipalName},,Testing\nnot-an-address,,\n"));
            try
            {
                Click(window, FindButton(window, b => b.Command == main.Users.RunSecondaryActionCommand));
                var import = main.Dialog?.Content as Tenantry.ViewModels.Records.ImportUsersViewModel ?? throw new InvalidOperationException("the import didn't open");
                Settle();
                Click(window, FindButton(window, b => b.Command == import.ChooseFileCommand));
                for (var i = 0; i < 20 && import.Rows.Count == 0; i++)
                    Settle();
                Expect(import.Rows.Count == 3, $"three rows were checked (got {import.Rows.Count})");
                Expect(import.Rows.Select(r => r.Outcome).SequenceEqual([Tenantry.ViewModels.Records.ImportOutcome.New, Tenantry.ViewModels.Records.ImportOutcome.Update,
                    Tenantry.ViewModels.Records.ImportOutcome.Problem]), "new, update and a row to fix");
                var add = FindButton(window, b => b.Command == import.AddCommand);
                add.BringIntoView();
                Settle();
                Click(window, add);
                Expect(main.Dialog is null && main.Pending!.Count == 2, $"two changes in Pending changes (got {main.Pending!.Count})");
            }
            finally
            {
                Platform.OpenCsv = () => Task.FromResult<(string, string)?>(null);
            }
            window.Close();
        });

        Check("New Group: the list's button opens the form; a shared mailbox with a person goes to Pending changes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "groups");
            Settle();
            Click(window, FindButton(window, b => b.Command == main.Groups.RunPrimaryActionCommand));
            var form = main.Dialog?.Content as Tenantry.ViewModels.Records.NewGroupViewModel ?? throw new InvalidOperationException("the form didn't open");
            for (var i = 0; i < 10; i++)
                Settle();
            var shared = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.Records.NewGroupKind { Kind: GroupKind.SharedMailbox });
            Click(window, shared);
            Expect(form.IsShared, "Shared mailbox was chosen");
            form.Name = "Zz-tenantry-test Desk";
            form.MemberPicker.Query = main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member).DisplayName;
            Settle();
            Click(window, FindButton(window, b => b.DataContext is Tenantry.ViewModels.Editing.PickOption && b.FindAncestorOfType<UserControl>()?.DataContext == form.MemberPicker));
            Expect(form.Members.Count() == 1, "the person was added");
            var add = FindButton(window, b => b.Command == form.AddCommand);
            add.BringIntoView();
            Settle();
            Click(window, add);
            Expect(main.Dialog is null && main.Pending!.Rows.Any(r => r.Change is CreateGroup { Kind: GroupKind.SharedMailbox }), "it's in Pending changes and the form closed");
            window.Close();
        });

        Check("Quarantine and block/allow lists: clicking a name opens it as its own page", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            for (var i = 0; i < 50 && area.Quarantine.IsLoading; i++)
                Settle();
            var held = area.Quarantine.Rows.Cast<QuarantinedMessage>().First();
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, held)));
            Expect(main.SelectedTab is Tenantry.ViewModels.MailSecurity.MailRecordPageViewModel { Content: Tenantry.ViewModels.MailSecurity.QuarantinePreviewViewModel },
                "the message's page opened");
            Settle();
            Expect(window.GetVisualDescendants().OfType<Tenantry.App.Views.QuarantinePreviewView>().Any(v => v.IsEffectivelyVisible), "the page shows the message");

            main.SelectedTab = area;
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "lists" }));
            for (var i = 0; i < 50 && area.Entries.IsLoading; i++)
                Settle();
            var entry = area.Entries.Rows.Cast<ListEntry>().First();
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, entry)));
            Expect(main.SelectedTab is Tenantry.ViewModels.MailSecurity.MailRecordPageViewModel { Content: Tenantry.ViewModels.MailSecurity.ListEntryPreviewViewModel },
                "the entry's page opened");
            window.Close();
        });

        Check("Preview pane: turning it off in one list turns it off in every list", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            var was = main.Users.IsPreviewOpen;
            main.Users.IsPreviewOpen = !was;
            Settle();
            Expect(area.Lists.All(l => !l.HasPreview || l.IsPreviewOpen == !was) && main.Groups.IsPreviewOpen == !was, "every list followed the users list");
            area.Quarantine.IsPreviewOpen = was;
            Settle();
            Expect(main.Users.IsPreviewOpen == was && area.Rules.IsPreviewOpen == was, "and the other way round");
            window.Close();
        });

        Check("Mail Flow Rules: clicking a rule's name opens it as its own page", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "rules" }));
            for (var i = 0; i < 50 && area.Rules.IsLoading; i++)
                Settle();
            var first = area.Rules.Rows.Cast<MailFlowRule>().First();
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, first)));
            var page = main.SelectedTab as Tenantry.ViewModels.MailSecurity.RuleEditorViewModel;
            Expect(page?.RuleIdentity == first.Identity, "the rule's page opened");
            window.Close();
        });

        Check("Rule editor: Edit Rule in the preview opens the rule, read from Exchange, in a tab", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "rules" }));
            for (var i = 0; i < 50 && area.Rules.IsLoading; i++)
                Settle();
            area.Rules.PreviewRow(area.Rules.Rows.Cast<MailFlowRule>().First());
            Settle();
            var preview = (Tenantry.ViewModels.MailSecurity.RulePreviewViewModel)area.Rules.PreviewModel!;
            var edit = FindButton(window, b => b.Command == preview.EditCommand);
            edit.BringIntoView();
            Settle();
            Click(window, edit);
            var editor = main.SelectedTab as Tenantry.ViewModels.MailSecurity.RuleEditorViewModel ?? throw new InvalidOperationException("no editor tab opened");
            for (var i = 0; i < 50 && editor.IsLoading; i++)
                Settle();
            Expect(editor.IsReady && editor.Conditions.Count > 0 && editor.Actions.Count > 0, "the rule's lines are showing");
            Click(window, FindButton(window, b => b.Command == editor.DoneCommand));
            window.Close();
        });

        Check("Devices: clicking a device's name opens its page, and the page's Restart goes to Pending changes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "devices");
            main.Devices.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var laptop = main.Devices.Rows.Cast<DeviceSummary>().First(d => d.IsManaged);
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, laptop)));
            var page = main.SelectedTab as DeviceDetailViewModel;
            Expect(page?.Device == laptop, "the device's page opened");
            for (var i = 0; i < 10; i++)
                Settle();
            var actions = page!.Overview.Actions!;
            var restart = actions.Actions.Single(a => a.Title == "Restart");
            var head = FindButton(window, b => b.Classes.Contains("bulkhead") && ReferenceEquals(b.DataContext, restart));
            head.BringIntoView();
            Settle();
            Click(window, head);
            var stage = FindButton(window, b => b.Command == actions.StageCommand && b.IsEffectivelyVisible);
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            Expect(main.Pending!.Rows.Any(r => r.Change is RunDeviceAction { Command: DeviceCommand.Restart } a && a.Device.Id == laptop.ManagedId),
                "the restart is in Pending changes");
            window.Close();
        });

        Check("Device page, Groups tab: picking a security group adds a Joining row; Remove on a group stages leaving; Undo takes it back", () =>
        {
            var (window, main, _) = OpenList();
            main.Groups.LoadAsync().GetAwaiter().GetResult();
            main.SelectedSection = main.Sections.Single(s => s.Key == "devices");
            main.Devices.LoadAsync().GetAwaiter().GetResult();
            var laptop = main.Devices.Rows.Cast<DeviceSummary>().First(d => d.Platform == "Windows" && d.IsManaged && d.ObjectId is not null);
            main.OpenDevice(laptop);
            var page = (DeviceDetailViewModel)main.SelectedTab!;
            var groups = page.Sections.OfType<DeviceGroupsSection>().Single();
            page.SelectedSection = groups;
            for (var i = 0; i < 10; i++)
                Settle();
            var before = groups.Groups.Count;
            Expect(before > 0, "it's already in a group");
            groups.Picker.Query = "SG Fin";
            Settle();
            Click(window, FindButton(window, b => b.DataContext is Tenantry.ViewModels.Editing.PickOption && b.FindAncestorOfType<UserControl>()?.DataContext == groups.Picker));
            Expect(groups.Groups.Count == before + 1 && groups.Groups.Any(g => g.PendingText == "Joining"), "a Joining row appeared");
            Expect(main.Pending!.Rows.Any(r => r.Change is SetGroupMembership { PersonType: "device", New: true }), "joining is in Pending changes");
            var existing = groups.Groups.First(g => !g.HasPending);
            var remove = FindButton(window, b => b.Command == existing.ToggleCommand);
            Click(window, remove);
            Expect(main.Pending.Rows.Any(r => r.Change is SetGroupMembership { PersonType: "device", New: false }), "leaving is in Pending changes");
            var leaving = groups.Groups.First(g => g.PendingText == "Leaving");
            Click(window, FindButton(window, b => b.Command == leaving.ToggleCommand));
            Expect(!main.Pending.Rows.Any(r => r.Change is SetGroupMembership { New: false }), "Undo took leaving back out");
            window.Close();
        });

        Check("Devices › Autopilot: the page tab reads the list, a serial opens its preview, and Set Group Tag goes to Pending changes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "devices");
            Settle();
            var area = main.DevicesArea;
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "autopilot" }));
            for (var i = 0; i < 50 && area.Autopilot.Total == 0; i++)
                Settle();
            Expect(area.Autopilot.Total > 0, "the Autopilot list has rows");
            var first = area.Autopilot.Rows.Cast<AutopilotDevice>().First();
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, first)));
            var preview = area.Autopilot.PreviewModel as AutopilotPreviewViewModel ?? throw new InvalidOperationException("no preview");
            Settle();
            var tag = preview.Actions!.Actions.Single(a => a.Title == "Set Group Tag");
            var head = FindButton(window, b => b.Classes.Contains("bulkhead") && ReferenceEquals(b.DataContext, tag));
            head.BringIntoView();
            Settle();
            Click(window, head);
            tag.Text = "Loaners";
            var stage = FindButton(window, b => b.Command == preview.Actions.StageCommand && b.IsEffectivelyVisible);
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            Expect(main.Pending!.Rows.Any(r => r.Change is SetAutopilotGroupTag { New: "Loaners" }), "the group tag is in Pending changes");
            window.Close();
        });

        Check("SharePoint: the sidebar opens Sites; the OneDrives tab switches lists; a name opens its page with the owner", () =>
        {
            var (window, main, _) = OpenList();
            main.SharePoint.Sites.LoadAsync().GetAwaiter().GetResult();
            main.SharePoint.OneDrives.LoadAsync().GetAwaiter().GetResult();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is NavSection { Key: "sharepoint" }));
            Expect(main.SelectedTab == main.SharePoint && main.SharePoint.ShowSites, "Sites is showing");
            Expect(main.SharePoint.Sites.Rows.Cast<SiteSummary>().All(s => s.Kind != SiteKind.OneDrive), "no OneDrives among the sites");
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "onedrives" }));
            Expect(main.SharePoint.ShowOneDrives, "OneDrives is showing");
            Settle();
            var drive = main.SharePoint.OneDrives.Rows.Cast<SiteSummary>().First();
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, drive) && b.IsEffectivelyVisible));
            var page = main.SelectedTab as SiteDetailViewModel;
            Expect(page?.Site == drive && page.Summary.OwnerText == drive.OwnerName, "its page opened, showing its owner");
            // Every tab reads when shown, and a site the admin can't see inside says why instead of failing.
            foreach (var section in page!.Sections)
            {
                page.SelectedSection = section;
                for (var i = 0; i < 10; i++)
                    Settle();
            }
            Expect(page.Sections.OfType<LiveSection>().All(s => !s.IsLoading), "every tab finished reading");
            var locked = main.SharePoint.OneDrives.Rows.Cast<SiteSummary>().FirstOrDefault(s => s.IsArchived);
            if (locked is not null)
            {
                main.OpenSite(locked);
                var lockedPage = (SiteDetailViewModel)main.SelectedTab!;
                var permissions = lockedPage.Sections.OfType<SitePermissionsSection>().Single();
                lockedPage.SelectedSection = permissions;
                for (var i = 0; i < 10; i++)
                    Settle();
                Expect(permissions.Note?.Contains("archived", StringComparison.Ordinal) == true, "an archived OneDrive says it's archived");
            }
            window.Close();
        });

        Check("Site page: a site you can't see inside offers Add Yourself as Site Admin; the button stages it and the note says what's next", () =>
        {
            var (window, main, _) = OpenList();
            main.SharePoint.Sites.LoadAsync().GetAwaiter().GetResult();
            main.OpenSite(main.SharePoint.Sites.Rows.Cast<SiteSummary>().First(s => s.OwnerName is null && s.Kind == SiteKind.TeamSite && !s.IsArchived));
            var page = (SiteDetailViewModel)main.SelectedTab!;
            var permissions = page.Sections.OfType<SitePermissionsSection>().Single();
            page.SelectedSection = permissions;
            for (var i = 0; i < 10; i++)
                Settle();
            Expect(permissions.NoteActionText == "Add Yourself as Site Admin", "the note offers to add you");
            Click(window, FindButton(window, b => b.Command == permissions.NoteActionCommand && b.IsEffectivelyVisible));
            Expect(main.Pending!.Rows.Any(r => r.Change is SetSiteAdmin { New: true }), "adding you is in Pending changes");
            Expect(!permissions.HasNoteAction && permissions.Note?.Contains("Pending changes", StringComparison.Ordinal) == true, "the button went and the note says to apply");
            Expect(page.HasPending, "the page's header says what's pending");
            window.Close();
        });

        Check("Site page, Permissions: Remove on a member stages it and Undo takes it back; picking someone adds an Adding row", () =>
        {
            var (window, main, _) = OpenList();
            main.Groups.LoadAsync().GetAwaiter().GetResult();
            main.SharePoint.Sites.LoadAsync().GetAwaiter().GetResult();
            main.OpenSite(main.SharePoint.Sites.Rows.Cast<SiteSummary>().First(s => s.GroupId is not null && s.Kind == SiteKind.TeamSite && s.OwnerName is not null && !s.IsArchived));
            var page = (SiteDetailViewModel)main.SelectedTab!;
            var permissions = page.Sections.OfType<SitePermissionsSection>().Single();
            page.SelectedSection = permissions;
            for (var i = 0; i < 10; i++)
                Settle();
            var member = permissions.Groups.Single(g => g.Name == "Members").Members.First(m => m.ActionText == "Remove");
            var remove = FindButton(window, b => b.Command == member.DoCommand);
            remove.BringIntoView();
            Settle();
            Click(window, remove);
            Expect(main.Pending!.Rows.Any(r => r.Change is SetGroupMembership { Role: GroupRole.Member, New: false }), "removing them is in Pending changes");
            var removing = permissions.Groups.Single(g => g.Name == "Members").Members.First(m => m.StateText == "Removing");
            Settle();
            var undo = FindButton(window, b => b.Command == removing.DoCommand);
            undo.BringIntoView();
            Settle();
            Click(window, undo);
            Expect(!main.Pending.Rows.Any(r => r.Change is SetGroupMembership { New: false }), "Undo took it back out");
            var visitors = permissions.Groups.Single(g => g.Name == "Visitors");
            visitors.Picker!.Query = main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member).DisplayName;
            Settle();
            Expect(visitors.Picker.HasResults, "the picker found them");
            var option = FindButton(window, b => b.DataContext is Tenantry.ViewModels.Editing.PickOption && b.FindAncestorOfType<UserControl>()?.DataContext == visitors.Picker);
            option.BringIntoView();
            Settle();
            Click(window, option);
            Expect(main.Pending.Rows.Any(r => r.Change is SetSiteGroupMember { New: true }), "adding them to Visitors is in Pending changes");
            Expect(permissions.Groups.Single(g => g.Name == "Visitors").Members.Any(m => m.StateText == "Adding"), "an Adding row appeared");
            window.Close();
        });

        Check("Site page: Lock from the Overview tab's actions goes to Pending changes as a caution change", () =>
        {
            var (window, main, _) = OpenList();
            main.SharePoint.Sites.LoadAsync().GetAwaiter().GetResult();
            main.OpenSite(main.SharePoint.Sites.Rows.Cast<SiteSummary>().First(s => s.Kind == SiteKind.CommunicationSite));
            var page = (SiteDetailViewModel)main.SelectedTab!;
            for (var i = 0; i < 10; i++)
                Settle();
            var actions = page.Actions!;
            var lockIt = actions.Actions.Single(a => a.Title == "Lock");
            var head = FindButton(window, b => b.Classes.Contains("bulkhead") && ReferenceEquals(b.DataContext, lockIt));
            head.BringIntoView();
            Settle();
            Click(window, head);
            lockIt.Choice = lockIt.Choices.Single(c => c.Value is "ReadOnly");
            var stage = FindButton(window, b => b.Command == actions.StageCommand && b.IsEffectivelyVisible);
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            Expect(main.Pending!.Rows.Any(r => r.Change is SetSiteSetting { Setting: "lock", IsCaution: true }), "locking is in Pending changes");
            window.Close();
        });

        Check("New Site: the Sites list's button opens the form; a communication site goes to Pending changes and the form closes", () =>
        {
            var (window, main, _) = OpenList();
            main.SharePoint.Sites.LoadAsync().GetAwaiter().GetResult();
            main.SelectedSection = main.Sections.Single(s => s.Key == "sharepoint");
            Settle();
            Click(window, FindButton(window, b => b.Command == main.SharePoint.Sites.RunPrimaryActionCommand && b.IsEffectivelyVisible));
            var form = main.Dialog?.Content as Tenantry.ViewModels.Records.NewSiteViewModel ?? throw new InvalidOperationException("the form didn't open");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.Records.NewSiteKind { Kind: SiteKind.CommunicationSite }));
            form.Name = "Zz-tenantry-test News";
            Settle();
            Expect(form.SiteUrl.EndsWith("/sites/zz-tenantry-test-news", StringComparison.Ordinal), "the address followed the name");
            var add = FindButton(window, b => b.Command == form.AddCommand);
            add.BringIntoView();
            Settle();
            Click(window, add);
            Expect(main.Dialog is null && main.Pending!.Rows.Any(r => r.Change is CreateSite { Kind: SiteKind.CommunicationSite }), "it's in Pending changes and the form closed");
            window.Close();
        });

        Check("SharePoint › Deleted Sites: the page tab reads the list; Restore in the preview goes to Pending changes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "sharepoint");
            Settle();
            var area = main.SharePoint;
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "deleted" }));
            for (var i = 0; i < 50 && area.Deleted.Total == 0; i++)
                Settle();
            Expect(area.Deleted.Total > 0, "the deleted sites list has rows");
            var first = area.Deleted.Rows.Cast<DeletedSite>().First();
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, first)));
            var preview = area.Deleted.PreviewModel as DeletedSitePreviewViewModel ?? throw new InvalidOperationException("no preview");
            Settle();
            var restore = preview.Actions!.Actions.Single(a => a.Title == "Restore");
            var head = FindButton(window, b => b.Classes.Contains("bulkhead") && ReferenceEquals(b.DataContext, restore));
            head.BringIntoView();
            Settle();
            Click(window, head);
            var stage = FindButton(window, b => b.Command == preview.Actions.StageCommand && b.IsEffectivelyVisible);
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            Expect(main.Pending!.Rows.Any(r => r.Change is RestoreSite), "restoring is in Pending changes");
            window.Close();
        });

        Check("Security & Compliance: the sidebar opens Admin Roles; a role opens its page; Remove stages it and picking someone then Add gives the role", () =>
        {
            var (window, main, _) = OpenList();
            main.Users.LoadAsync().GetAwaiter().GetResult();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is NavSection { Key: "security" }));
            var area = main.SecurityArea;
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "roles" }));
            for (var i = 0; i < 50 && area.Roles.Total == 0; i++)
                Settle();
            Expect(main.SelectedTab == area && area.Roles.Total > 0, "Admin Roles is showing with roles in use");
            var helpdesk = area.Roles.Rows.Cast<RoleSummary>().First(r => r.Role.Name == "User Administrator");
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, helpdesk) && b.IsEffectivelyVisible));
            var page = main.SelectedTab as RoleDetailViewModel;
            Expect(page?.Role.Role.Id == helpdesk.Role.Id, "the role's page opened");
            for (var i = 0; i < 5; i++)
                Settle();
            var holder = page!.Assignments.Rows.First(r => r.ActionText == "Remove");
            var remove = FindButton(window, b => b.Command == holder.DoCommand);
            remove.BringIntoView();
            Settle();
            Click(window, remove);
            Expect(main.Pending!.Rows.Any(r => r.Change is RemoveRoleAssignment), "taking it away is in Pending changes");
            page.Assignments.Picker.Query = main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member).DisplayName;
            Settle();
            var option = FindButton(window, b => b.DataContext is Tenantry.ViewModels.Editing.PickOption && b.FindAncestorOfType<UserControl>()?.DataContext == page.Assignments.Picker);
            option.BringIntoView();
            Settle();
            Click(window, option);
            Expect(page.Assignments.HasPicked, "the person was chosen");
            var add = FindButton(window, b => b.Command == page.Assignments.AddCommand && b.IsEffectivelyVisible);
            add.BringIntoView();
            Settle();
            Click(window, add);
            Expect(main.Pending.Rows.Any(r => r.Change is AddRoleAssignment { Kind: RoleGrantKind.Active }), "giving the role is in Pending changes");
            Expect(page.Assignments.Rows.Any(r => r.StateText == "Adding") && page.HasPending, "an Adding row appeared and the header says what's pending");
            window.Close();
        });

        Check("New Custom Role: the button opens the editor; Start From ticks permissions; Add stages the role and closes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            main.SecurityArea.SelectedPage = SecurityAreaViewModel.Pages.Single(p => p.Key == "roles");
            for (var i = 0; i < 50 && main.SecurityArea.Roles.Total == 0; i++)
                Settle();
            Settle();
            var primary = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Command == main.SecurityArea.Roles.RunPrimaryActionCommand && b.IsEffectivelyVisible);
            Expect(primary is not null, $"the New Custom Role button is showing (roles {main.SecurityArea.Roles.Total}, tab {main.SelectedTab?.Title})");
            Click(window, primary!);
            var editor = main.Dialog?.Content as CustomRoleEditorViewModel ?? throw new InvalidOperationException("the editor didn't open");
            for (var i = 0; i < 20 && editor.IsLoading; i++)
                Settle();
            editor.Name = "Zz-tenantry-test Desk";
            editor.Template = editor.StartFrom.First(r => r.Name == "Helpdesk Administrator");
            Settle();
            Expect(editor.Shown.Count(p => p.IsChecked) > 0, "the template's permissions were ticked");
            var box = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.DataContext is PermissionChoice { IsChecked: false });
            box.BringIntoView();
            Settle();
            Click(window, box);
            Expect(((PermissionChoice)box.DataContext!).IsChecked, "clicking a box ticks it");
            var save = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Command == editor.SaveCommand);
            Expect(save is not null, $"the editor's Add button is showing (problems: {string.Join("; ", editor.Problems)})");
            save!.BringIntoView();
            Settle();
            Click(window, save!);
            Expect(main.Dialog is null && main.Pending!.Rows.Any(r => r.Change is CreateCustomRole c && c.Actions.Count >= 3), "the role is in Pending changes and the editor closed");
            window.Close();
        });

        Check("PIM: My Roles opens a role's switch-on form; Add asks for a reason, then stages it; Approvals' Approve stages the answer", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            var area = main.SecurityArea;
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "myroles" }));
            for (var i = 0; i < 50 && area.MyRoles.Eligible.Count == 0; i++)
                Settle();
            Expect(area.MyRoles.Eligible.Count > 0, "My Roles lists the eligible roles");
            var row = area.MyRoles.Eligible[0];
            Click(window, FindButton(window, b => b.Command == row.OpenCommand && b.IsEffectivelyVisible));
            for (var i = 0; i < 10; i++)
                Settle();
            Expect(row.IsOpen, "the switch-on form opened");
            var add = FindButton(window, b => b.Command == row.ActivateCommand && b.IsEffectivelyVisible);
            Click(window, add);
            Expect(row.HasError && !main.Pending!.Rows.Any(), "without a reason it says why and stages nothing");
            row.Justification = "Checking an alert";
            Settle();
            Click(window, add);
            Expect(main.Pending!.Rows.Any(r => r.Change is ActivateRole { On: true }) && !row.IsOpen, "switching it on is in Pending changes and the form closed");
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "approvals" }));
            for (var i = 0; i < 50 && area.Approvals.Requests.Count == 0; i++)
                Settle();
            var request = area.Approvals.Requests.First();
            Click(window, FindButton(window, b => b.Command == request.ApproveCommand && b.IsEffectivelyVisible));
            Expect(main.Pending.Rows.Any(r => r.Change is ReviewRoleRequest { Approve: true }) && request.StateText == "Approving", "approving is in Pending changes");
            window.Close();
        });

        Check("Role page, Settings tab (PIM): changing the activation length stages the rule; Extend a Year stages a new end", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            main.SecurityArea.SelectedPage = SecurityAreaViewModel.Pages.Single(p => p.Key == "roles");
            for (var i = 0; i < 50 && main.SecurityArea.Roles.Total == 0; i++)
                Settle();
            main.OpenRole(main.SecurityArea.Roles.Rows.Cast<RoleSummary>().First(r => r.Role.Name == "Security Administrator"));
            var page = (RoleDetailViewModel)main.SelectedTab!;
            Settle();
            var extend = page.Assignments.Rows.First(r => r.CanExtend);
            var button = FindButton(window, b => b.Command == extend.ExtendCommand && b.IsEffectivelyVisible);
            button.BringIntoView();
            Settle();
            Click(window, button);
            Expect(main.Pending!.Rows.Any(r => r.Change is ExtendRoleAssignment), "extending is in Pending changes");
            Click(window, window.GetVisualDescendants().OfType<TabItem>().First(t => t.DataContext is RoleSettingsSection));
            for (var i = 0; i < 20 && !page.Settings.IsLoaded; i++)
                Settle();
            Expect(page.Settings.CanEdit, "the settings read and can be changed");
            page.Settings.MaxActivation = RoleSettingsSection.ActivationChoices.First(c => c.Value == TimeSpan.FromHours(4));
            Settle();
            Expect(main.Pending.Rows.Any(r => r.Change is SetRolePolicyRule { RuleId: RolePolicyRules.ActivationExpiry }), "the shorter activation is in Pending changes");
            window.Close();
        });

        Check("Conditional Access: the sidebar opens the policies; a policy opens its page; choosing Off stages it and Delete stages a delete", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is NavSection { Key: "security" }));
            var area = main.SecurityArea;
            for (var i = 0; i < 50 && area.Policies.Total == 0; i++)
                Settle();
            Expect(area.ShowPolicies && area.Policies.Total > 0, "Conditional Access shows the policies");
            var policy = area.Policies.Rows.Cast<CaPolicyRow>().First(r => r.Policy.State == "enabled");
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, policy) && b.IsEffectivelyVisible));
            var page = main.SelectedTab as CaPolicyDetailViewModel;
            Expect(page?.Policy.Id == policy.Policy.Id && page.Lines.Count > 0, "its page opened with the policy in words");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is CaStateChoice { State: "disabled" }));
            Expect(main.Pending!.Rows.Any(r => r.Change is SetCaPolicyState { New: "disabled" }) && page!.HasPending, "switching it off is in Pending changes");
            Click(window, FindButton(window, b => b.Command == page!.DeleteCommand && b.IsEffectivelyVisible));
            Expect(main.Pending.Rows.Any(r => r.Change is DeleteCaPolicy), "deleting it is in Pending changes");
            window.Close();
        });

        Check("What If: choosing a person and Check lists which policies apply and why the others don't", () =>
        {
            var (window, main, _) = OpenList();
            main.Users.LoadAsync().GetAwaiter().GetResult();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "whatif" }));
            var whatIf = main.SecurityArea.WhatIf;
            whatIf.Picker.Query = main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member).DisplayName;
            Settle();
            Click(window, FindButton(window, b => b.DataContext is Tenantry.ViewModels.Editing.PickOption && b.FindAncestorOfType<UserControl>()?.DataContext == whatIf.Picker));
            Expect(whatIf.HasPerson, "the person was chosen");
            Click(window, FindButton(window, b => b.Command == whatIf.CheckCommand && b.IsEffectivelyVisible));
            for (var i = 0; i < 20 && whatIf.Results.Count == 0; i++)
                Settle();
            Expect(whatIf.Results.Any(r => r.Applies) && whatIf.Results.Any(r => !r.Applies && r.Detail.StartsWith("Because", StringComparison.Ordinal)) && whatIf.HasSummary,
                "the results say what applies and why the rest don't");
            window.Close();
        });

        Check("New Policy: the button opens the editor; a template fills it (report-only, leaving you out); Done closes it with the policy in Pending changes", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            for (var i = 0; i < 50 && main.SecurityArea.Policies.Total == 0; i++)
                Settle();
            Settle();
            var newPolicy = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Command == main.SecurityArea.Policies.RunPrimaryActionCommand && b.IsEffectivelyVisible);
            Expect(newPolicy is not null, "the New Policy button is showing");
            Click(window, newPolicy!);
            var editor = main.SelectedTab as CaPolicyEditorViewModel;
            Expect(editor is { IsNew: true }, "the editor opened as a tab");
            for (var i = 0; i < 20 && !editor!.ShowStarters; i++)
                Settle();
            var template = editor!.Templates.First(t => t.Name.Contains("legacy", StringComparison.OrdinalIgnoreCase));
            Settle();
            var start = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Command == editor.StartCommand && ReferenceEquals(b.CommandParameter, template));
            Expect(start is not null, $"the template's button is showing ({editor.Templates.Count} templates)");
            Click(window, start!);
            Settle();
            Expect(editor.Name == template.Name && editor.State?.State == "enabledForReportingButNotEnforced", "the template filled it, in report-only");
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateCaPolicy), "the new policy is in Pending changes");
            var done = FindButton(window, b => b.Command == editor.DoneCommand && b.IsEffectivelyVisible);
            done.BringIntoView();
            Settle();
            Click(window, done);
            Expect(!main.Tabs.Contains(editor) && main.Pending.Rows.Any(r => r.Change is CreateCaPolicy), "Done closed the editor and the policy stays pending");
            window.Close();
        });

        Check("Edit Policy: the policy page opens the editor; ticking a kind of app stages one change to the policy", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            for (var i = 0; i < 50 && main.SecurityArea.Policies.Total == 0; i++)
                Settle();
            main.OpenCaPolicy(main.SecurityArea.Policies.Rows.Cast<CaPolicyRow>().First(r => r.Policy.Name == "Require MFA for everyone"));
            var page = (CaPolicyDetailViewModel)main.SelectedTab!;
            Settle();
            Click(window, FindButton(window, b => b.Command == page.EditPolicyCommand && b.IsEffectivelyVisible));
            var editor = main.SelectedTab as CaPolicyEditorViewModel;
            Expect(editor is { IsNew: false } && !main.Pending!.Rows.Any(), "the editor opened on it with nothing pending yet");
            Settle();
            var any = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.IsEffectivelyVisible && (c.Content as string) == "Any App");
            any.BringIntoView();
            Settle();
            Click(window, any);
            Settle();
            var browser = window.GetVisualDescendants().OfType<CheckBox>().First(c => c.IsEffectivelyVisible && (c.Content as string) == "Browser");
            browser.BringIntoView();
            Settle();
            Click(window, browser);
            Expect(main.Pending!.Rows.Count(r => r.Change is UpdateCaPolicy) == 1, "one change to the policy is in Pending changes");
            Expect(editor!.Lines.Any(l => l.Label == "Client Apps" && l.Value == "Browsers"), "the words beside it follow");
            window.Close();
        });

        Check("Named Locations: Add Location opens the form; Add to Pending changes stages it; a location a policy uses can't be deleted; Edit stages a change", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "locations" }));
            var page = main.SecurityArea.NamedLocations;
            for (var i = 0; i < 50 && page.Locations.Count == 0; i++)
                Settle();
            Expect(page.Locations.Count == 2, "the page read the locations");
            Click(window, FindButton(window, b => b.Command == page.AddCommand && b.IsEffectivelyVisible));
            Expect(page.IsFormOpen, "the form opened");
            Settle();
            window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.PlaceholderText == "Name, like Head Office").Text = "Branch Office";
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.Editing.EditorChoice { Value: NamedLocation.IpRanges }));
            window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.AcceptsReturn).Text = "198.51.100.0/24";
            Settle();
            Click(window, FindButton(window, b => b.Command == page.SaveCommand && b.IsEffectivelyVisible));
            Expect(!page.IsFormOpen && main.Pending!.Rows.Any(r => r.Change is CreateNamedLocation { Location.Name: "Branch Office" }), "the new location is in Pending changes and the form closed");
            Expect(page.Locations.Any(l => l.Name == "Branch Office" && l.StateText == "Adding"), "it shows as Adding");
            var office = page.Locations.Single(l => l.Name == "Head Office");
            Expect(!FindButton(window, b => b.Command == office.DeleteCommand && b.IsEffectivelyVisible).IsEffectivelyEnabled, "Head Office's Delete is off, since a policy uses it");
            var home = page.Locations.Single(l => l.Name == "Home Countries");
            Click(window, FindButton(window, b => b.Command == home.EditCommand && b.IsEffectivelyVisible));
            Expect(page.IsFormOpen && page.Name == "Home Countries" && !page.IsIp, "Edit opened the form on it");
            Settle();
            window.GetVisualDescendants().OfType<CheckBox>().First(c => c.IsEffectivelyVisible && c.IsChecked == false && (c.Content as string)!.StartsWith("Include", StringComparison.Ordinal)).IsChecked = true;
            Click(window, FindButton(window, b => b.Command == page.SaveCommand && b.IsEffectivelyVisible));
            Expect(main.Pending!.Rows.Any(r => r.Change is UpdateNamedLocation) && page.Locations.Single(l => l.Name == "Home Countries").StateText == "Changing",
                $"the change is in Pending changes ({string.Join("; ", page.Problems)}; open {page.IsFormOpen}; {string.Join(", ", main.Pending.Rows.Select(r => r.Change.GetType().Name))})");
            window.Close();
        });

        Check("Authentication Strengths: the page tab lists Microsoft's built-in strengths with their methods in words", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "strengths" }));
            var page = main.SecurityArea.AuthStrengths;
            for (var i = 0; i < 50 && page.Strengths.Count == 0; i++)
                Settle();
            Expect(page.Strengths.Count == 3 && page.Strengths.All(s => s.TypeText == "Built-In"), "three built-in strengths");
            Expect(page.Strengths.First().Methods.Contains("Password and Authenticator notification"), "methods read in words");
            window.Close();
        });

        Check("App Registrations: the page tab reads the list; a name opens the app's page; its Secrets tab shows the expired secret; Open Enterprise App opens the other half", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "appregs" }));
            var area = main.SecurityArea;
            for (var i = 0; i < 50 && area.Registrations.Total == 0; i++)
                Settle();
            Expect(area.ShowRegistrations && area.Registrations.Total > 0, "App Registrations is showing with apps");
            var payroll = area.Registrations.Rows.Cast<AppRegistration>().First(r => r.Name == "Payroll Export");
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, payroll) && b.IsEffectivelyVisible));
            var page = main.SelectedTab as AppRegistrationPageViewModel;
            Expect(page?.App.Id == payroll.Id, "the app's page opened");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<TabItem>().First(t => t.DataContext is AppSection { Key: "credentials" }));
            for (var i = 0; i < 10; i++)
                Settle();
            var credentials = (AppSection)page!.SelectedSection!;
            Expect(credentials.Key == "credentials" && credentials.Cards[0].RowItems.Any(r => r.Badges.Any(b => b.Text == "Expired")), "the expired secret is flagged");
            Click(window, FindButton(window, b => b.Command == page.OpenPartnerCommand && b.IsEffectivelyVisible));
            Expect(main.SelectedTab is EnterpriseAppPageViewModel { App.Name: "Payroll Export" }, "its enterprise app's page opened");
            window.Close();
        });

        Check("Enterprise Apps: the page tab lists apps; a name opens its page; Permissions shows who consented, with their scopes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "enterprise" }));
            var area = main.SecurityArea;
            for (var i = 0; i < 50 && area.EnterpriseApps.Total == 0; i++)
                Settle();
            var fabrikam = area.EnterpriseApps.Rows.Cast<EnterpriseApp>().FirstOrDefault(r => r.Name == "Fabrikam Whiteboard");
            Expect(fabrikam is not null, "the third-party app is in the default view");
            Click(window, FindButton(window, b => b.Classes.Contains("namelink") && ReferenceEquals(b.DataContext, fabrikam) && b.IsEffectivelyVisible));
            var page = main.SelectedTab as EnterpriseAppPageViewModel;
            Expect(page?.App.Id == fabrikam!.Id, "the app's page opened");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<TabItem>().First(t => t.DataContext is AppSection { Key: "permissions" }));
            for (var i = 0; i < 10; i++)
                Settle();
            var permissions = (AppSection)page!.SelectedSection!;
            Expect(permissions.Cards.Count == 3 && permissions.Cards[2].RowItems.Count == 2 && permissions.Cards[2].RowItems.All(r => r.HasChips),
                "the two people who consented are listed with their scopes");
            Expect(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Files.ReadWrite" && t.IsEffectivelyVisible), "the scopes show as chips");
            window.Close();
        });

        Check("App page: Add Secret stages a secret; Remove on the expired one shows Undo; Delete App stages a delete that needs the name typed", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            main.SecurityArea.SelectedPage = SecurityAreaViewModel.Pages.Single(p => p.Key == "appregs");
            for (var i = 0; i < 50 && main.SecurityArea.Registrations.Total == 0; i++)
                Settle();
            main.OpenAppRegistration(main.SecurityArea.Apps!.Registrations.Single(r => r.Name == "Payroll Export"));
            var page = (AppRegistrationPageViewModel)main.SelectedTab!;
            Settle();
            Click(window, window.GetVisualDescendants().OfType<TabItem>().First(t => t.DataContext is AppSection { Key: "credentials" }));
            for (var i = 0; i < 10; i++)
                Settle();
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.PlaceholderText?.StartsWith("What it's for", StringComparison.Ordinal) == true);
            box.Text = "Rotation";
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "Add Secret")));
            Expect(main.Pending!.Rows.Any(r => r.Change is AddAppSecret { Description: "Rotation" }), "the new secret is in Pending changes");
            for (var i = 0; i < 5; i++)
                Settle();
            var remove = FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is AppRow { Title: "Nightly export", ActionText: "Remove" });
            Click(window, remove);
            for (var i = 0; i < 5; i++)
                Settle();
            Expect(main.Pending.Rows.Any(r => r.Change is RemoveAppCredential), "removing the expired secret is in Pending changes");
            Expect(window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.DataContext is AppRow { Title: "Nightly export", ActionText: "Undo" }),
                "its row now offers Undo");
            Click(window, FindButton(window, b => b.Command == page.DeleteCommand && b.IsEffectivelyVisible));
            Expect(main.Pending.Rows.Any(r => r.Change is DeleteApp { ConfirmPhrase: "Payroll Export" }) && page.HasPending, "deleting it is pending and needs its name typed");
            window.Close();
        });

        Check("New App Registration: the list's button opens the form; Add stages it and closes; an enterprise app's switch stages a setting", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "appregs" }));
            for (var i = 0; i < 50 && main.SecurityArea.Registrations.Total == 0; i++)
                Settle();
            Click(window, FindButton(window, b => b.Command == main.SecurityArea.Registrations.RunPrimaryActionCommand && b.IsEffectivelyVisible));
            var form = main.Dialog?.Content as NewAppViewModel;
            Expect(form is not null, "the New App Registration popup opened");
            Settle();
            Click(window, FindButton(window, b => b.Command == form!.AddCommand && b.IsEffectivelyVisible));
            Expect(main.Dialog is not null && form!.HasProblems, "Add without a name stays open and says why");
            form!.Name = "Expense Reports";
            Settle();
            Click(window, FindButton(window, b => b.Command == form.AddCommand && b.IsEffectivelyVisible));
            Expect(main.Dialog is null && main.Pending!.Rows.Any(r => r.Change is CreateAppRegistration), "Add put it in Pending changes and closed the popup");

            main.OpenEnterpriseApp(main.SecurityArea.Apps!.EnterpriseApps.Single(e => e.Name == "Fabrikam Whiteboard"));
            for (var i = 0; i < 5; i++)
                Settle();
            var signIn = window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => t.IsEffectivelyVisible);
            Click(window, signIn);
            Expect(main.Pending!.Rows.Any(r => r.Change is SetEnterpriseAppSetting { Setting: "enabled", New: false }), "turning sign-in off is in Pending changes");
            window.Close();
        });

        Check("Authentication Methods: the tab lists methods; choosing one opens its editor; Off stages it; Security Defaults' switch stages too", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "authmethods" }));
            var page = main.SecurityArea.AuthMethodsPage;
            for (var i = 0; i < 50 && page.Methods.Count == 0; i++)
                Settle();
            Expect(page.Methods.Count >= 10 && page.Editor is AuthItemEditor { Id: "MicrosoftAuthenticator" }, "the methods are listed with the Authenticator open");
            var fido = window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is AuthItemRow { Id: "Fido2" });
            Expect(fido is not null, $"the Passkeys row is on screen ({window.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.DataContext is AuthItemRow)} rows)");
            Click(window, fido!);
            var editor = page.Editor as AuthItemEditor;
            Expect(editor?.Id == "Fido2", "choosing Passkeys opened its editor");
            Settle();
            var off = window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is Tenantry.ViewModels.Editing.EditorChoice { Value: "disabled" } && i.IsEffectivelyVisible);
            Expect(off is not null, "the Off choice is on screen");
            Click(window, off!);
            Expect(main.Pending!.Rows.Any(r => r.Change is SetAuthMethodConfig { Method.Id: "Fido2", IsCaution: true }), "switching passkeys off is a caution change in Pending changes");
            Expect(page.Methods.Single(m => m.Id == "Fido2").Pending && page.Editor == editor, "its row says Pending and the editor stays open");
            var defaults = window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is AuthItemRow { Id: "securityDefaults" });
            Expect(defaults is not null, "the Security Defaults row is on screen");
            defaults!.BringIntoView();
            Settle();
            Click(window, defaults!);
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => t.IsEffectivelyVisible && t.DataContext is SecurityDefaultsEditor));
            Expect(main.Pending.Rows.Any(r => r.Change is SetSecurityDefaults { New: true }), "turning security defaults on is in Pending changes");
            window.Close();
        });

        Check("Devices: checking rows opens the selection panel; Delete Stale Devices stages only the stale ones", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "devices");
            main.Devices.LoadAsync().GetAwaiter().GetResult();
            main.Devices.ApplyView(main.Devices.Views.Single(v => v.View.Id == "all"));
            Settle();
            var stale = main.Devices.Rows.Cast<DeviceSummary>().First(d => d.LastSeen is { } s && s < DateTimeOffset.UtcNow.AddDays(-90));
            var fresh = main.Devices.Rows.Cast<DeviceSummary>().First(d => d.LastSeen is { } s && s > DateTimeOffset.UtcNow.AddDays(-10));
            main.Devices.SelectedRows = [stale, fresh];
            Settle();
            var panel = main.Devices.PreviewModel as SelectionPanelViewModel ?? throw new InvalidOperationException("no selection panel");
            var delete = panel.Bulk!.Actions.Single(a => a.Title == "Delete Stale Devices");
            var head = FindButton(window, b => b.Classes.Contains("bulkhead") && ReferenceEquals(b.DataContext, delete));
            head.BringIntoView();
            Settle();
            Click(window, head);
            delete.Choice = delete.Choices.Single(c => (int)c.Value == 90);
            var stage = FindButton(window, b => b.Command == panel.Bulk.StageCommand && b.IsEffectivelyVisible);
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            var deletes = main.Pending!.Rows.Select(r => r.Change).OfType<DeleteDevice>().ToArray();
            Expect(deletes.Length == 1 && deletes[0].Device.Name == stale.Name, "only the stale device is in Pending changes");
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
    /// <param name="premium">The demo acting as a tenant with Entra ID P2 (PIM).</param>
    static (MainWindow Window, MainViewModel Main, ShellViewModel Shell) OpenList(bool premium = false)
    {
        var demo = new TenantConnector().ConnectDemo(premium);
        if (demo.Directory is Tenantry.Demo.DemoTenantDirectory directory)
            directory.CheckTakes = TimeSpan.Zero;
        var main = new MainViewModel(demo with
        {
            IsDemo = false,
            ListState = new Tenantry.Core.Lists.MemoryListStateStore(),
            SyncSettings = new MemorySyncSettingsStore(),
            PasswordStyle = demo.PasswordStyle ?? new MemoryPasswordStyleStore(),
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
