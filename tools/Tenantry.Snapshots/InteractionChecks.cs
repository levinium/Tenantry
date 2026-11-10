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
            Expect(main.Pending!.Count == 1, "Sign out everywhere is in Pending Changes");
            window.Close();
        });

        Check("Adding is a button: Add Address and Add License open a form with Add and Cancel, and Add stages it and closes the form", () =>
        {
            var (window, main, page) = OpenRecord(p => p.User.Mailbox is not null);
            page.SelectedSection = page.MailTab;
            Settle();
            page.MailTab.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var mail = page.MailTab;
            Expect(!window.GetVisualDescendants().OfType<TextBox>().Any(t => t.IsEffectivelyVisible && t.PlaceholderText == "Name before the @"),
                "no address box on the page until the button is pressed");
            Click(window, FindButton(window, b => b.Command == mail.StartAddAliasCommand && b.IsEffectivelyVisible));
            Settle();
            var box = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.PlaceholderText == "Name before the @");
            Expect(box is not null, "Add Address opened the form");
            Expect(box!.IsFocused, "the cursor is in the address box");
            Click(window, FindButton(window, b => b.Command == mail.CancelAddAliasCommand && b.IsEffectivelyVisible));
            Expect(!mail.IsAddingAlias, "Cancel closed the form");
            Click(window, FindButton(window, b => b.Command == mail.StartAddAliasCommand && b.IsEffectivelyVisible));
            Settle();
            window.KeyTextInput("checked.alias");
            Settle();
            Click(window, FindButton(window, b => b.Command == mail.AddAliasCommand && b.IsEffectivelyVisible));
            Expect(main.Pending!.Rows.Any(r => r.Change is SetMailAlias { New: true } a && a.Address.StartsWith("checked.alias@", StringComparison.Ordinal)),
                "the address is in Pending Changes");
            Expect(!mail.IsAddingAlias, "Add closed the form");

            page.SelectedSection = page.LicensesTab;
            Settle();
            page.LicensesTab.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var licenses = page.LicensesTab;
            Click(window, FindButton(window, b => b.Command == licenses.StartAddLicenseCommand && b.IsEffectivelyVisible));
            Expect(licenses.IsAddingLicense, "Add License opened the form");
            licenses.SelectedToAdd = licenses.AddOptions.First(o => o.IsAvailable);
            Settle();
            Click(window, FindButton(window, b => b.Command == licenses.AddLicenseCommand && b.IsEffectivelyVisible));
            Expect(main.Pending.Rows.Any(r => r.Change is SetLicenseAssigned { New: true }), "the license is in Pending Changes");
            Expect(!licenses.IsAddingLicense, "Add closed the form");
            window.Close();
        });

        Check("Contacts: a list names its contacts; Add Member's Add an Outside Address opens New Contact for the typed address; the Contacts page opens a contact, whose switch and Delete stage changes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "groups");
            main.Groups.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var partners = main.Groups.Rows.Cast<GroupSummary>().Single(g => g.Id == Tenantry.Demo.DemoTenantDirectory.PartnersListId);
            main.OpenGroup(partners);
            Settle();
            var list = (GroupDetailViewModel)main.SelectedTab!;
            Expect(list.Members.Count(m => m.TypeText == "Contact") == 5 && list.Members.All(m => m.Name != "A mail contact"), "the list's five contacts are named");

            var picker = list.People.MemberPicker;
            var add = window.GetVisualDescendants().OfType<Tenantry.App.Views.AddButton>().First(a => a.DataContext == picker && a.IsEffectivelyVisible);
            var open = add.GetVisualDescendants().OfType<Button>().First();
            open.BringIntoView();
            Settle();
            Click(window, open);
            var flyout = (Flyout)open.Flyout!;
            Expect(flyout.IsOpen, "Add Member opened its popup");
            picker.Query = "zz-tenantry-test-partner@northwind.example";
            Settle();
            var other = Buttons(flyout).First(b => b.Command == picker.UseOtherCommand);
            Expect(other.IsEffectivelyVisible, "the popup offers Add an Outside Address");
            Click(TopLevel.GetTopLevel(other)!, other);
            Expect(!flyout.IsOpen, "the popup closed");
            var form = main.Dialog?.Content as Tenantry.ViewModels.Records.NewContactViewModel;
            Expect(form is not null, "New Contact opened");
            Expect(form!.Email == "zz-tenantry-test-partner@northwind.example" && form.Lists.Single().Id == partners.Id, "with the typed address, joining this list");
            form.DisplayName = "Test Partner";
            Settle();
            Click(window, FindButton(window, b => b.Command == form.AddCommand && b.IsEffectivelyVisible));
            Expect(main.Dialog is null, "Add closed the popup");
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateContact { Contact.Name: "Test Partner" } c && c.Lists.Single().Group.Id == partners.Id),
                "the new contact is in Pending Changes, on the list");
            Expect(list.People.Members.Any(m => m.Name == "Test Partner" && m.PendingText == "Adding"), "the list shows it being added");

            main.SelectedSection = main.Sections.Single(s => s.Key == "contacts");
            Expect(main.SelectedTab == main.Contacts, "the Contacts sidebar entry opens the Contacts list");
            main.Contacts.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var avery = main.Contacts.Rows.Cast<ContactSummary>().Single(c => c.DisplayName == "Avery Quinn");
            Expect(avery.Lists.Count == 1, "the Contacts list knows the lists each is on");
            main.OpenContact(avery);
            Settle();
            var page = (ContactDetailViewModel)main.SelectedTab!;
            var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => t.IsEffectivelyVisible && t.DataContext == page);
            Click(window, toggle);
            Expect(main.Pending.Rows.Any(r => r.Change is SetHiddenFromAddressList { User.Kind: "contact", New: true }), "the switch staged hiding it");
            Click(window, FindButton(window, b => b.Command == page.DeleteCommand && b.IsEffectivelyVisible));
            Expect(main.Pending.Rows.Any(r => r.Change is DeleteContact), "Delete Contact staged the delete");
            Expect(FindButton(window, b => b.Command == page.DeleteCommand && b.IsEffectivelyVisible).Content as string == "Undo", "and offers Undo");
            window.Close();
        });

        Check("Edit fields: the pencil opens the box with ✓ and ✕; ✕ puts it back and closes it; ✓ keeps the change and closes it", () =>
        {
            var (window, main, _) = OpenList();
            main.Groups.LoadAsync().GetAwaiter().GetResult();
            main.Contacts.LoadAsync().GetAwaiter().GetResult();
            main.OpenContact(main.Contacts.Rows.Cast<ContactSummary>().First(c => !c.SyncedFromOnPremises));
            Settle();
            var page = (ContactDetailViewModel)main.SelectedTab!;
            var company = page.Details.SelectMany(d => d.Fields).Single(f => f.Label == "Company");
            var before = company.Value;
            Button Open() => FindButton(window, b => b.Command == company.EditCommand && b.IsEffectivelyVisible);
            Button Done() => FindButton(window, b => b.Command == company.StageCommand && b.IsEffectivelyVisible);
            Button Cancel() => FindButton(window, b => b.Command == company.CancelEditCommand && b.IsEffectivelyVisible);

            Click(window, Open());
            Expect(company.IsEditing, "the pencil opened the box");
            window.KeyTextInput("Changed Ltd");
            Settle();
            Click(window, Cancel());
            Expect(!company.IsEditing && !company.HasPending && company.Value == before, "✕ closed the box and put the value back");

            Click(window, Open());
            window.KeyTextInput("Changed Ltd");
            Settle();
            Click(window, Done());
            Expect(!company.IsEditing, "✓ closed the box");
            Expect(main.Pending!.Rows.Any(r => r.Change is SetContactText { Parameter: "Company", New: "Changed Ltd" }), "✓ kept the change in Pending Changes");
            window.Close();
        });

        Check("Quarantine request: Check the Sender explains the headers; Approve Release with one recipient unticked stages a release to the rest; Deny the Request replaces it", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            var mail = main.MailSecurity!;
            mail.SelectedPage = Tenantry.ViewModels.MailSecurity.MailSecurityViewModel.Pages[0];
            for (var i = 0; i < 50 && mail.Quarantine.IsLoading; i++)
                Settle();
            mail.Quarantine.ApplyView(mail.Quarantine.Views.Single(v => v.View.Id == "requested"));
            Settle();
            var asked = mail.Quarantine.Rows.Cast<QuarantinedMessage>().First(m => m.IsRequested && m.Recipients.Count > 1);
            mail.OpenQuarantined(asked);
            Settle();
            var page = (Tenantry.ViewModels.MailSecurity.QuarantinePreviewViewModel)((Tenantry.ViewModels.MailSecurity.MailRecordPageViewModel)main.SelectedTab!).Content;
            Click(window, FindButton(window, b => b.Command == page.ShowHeadersCommand && b.IsEffectivelyVisible));
            for (var i = 0; i < 30 && !page.HasHeaders; i++)
                Settle();
            Expect(page.Verdicts.Count >= 4, $"the sender checks are listed ({page.Verdicts.Count})");
            var release = FindButton(window, b => b.Command == page.ReleaseCommand && b.IsEffectivelyVisible);
            Expect((string?)release.Content == "Approve Release", $"a request offers Approve Release, not {release.Content}");
            release.BringIntoView();
            Settle();
            Click(window, release);
            var boxes = window.GetVisualDescendants().OfType<CheckBox>().Where(b => b.DataContext is Tenantry.ViewModels.MailSecurity.ReleaseRecipient && b.IsEffectivelyVisible).ToArray();
            Expect(boxes.Length == asked.Recipients.Count, "each recipient has a box");
            boxes[^1].BringIntoView();
            Settle();
            Click(window, boxes[^1]);
            var staged = main.Pending!.Rows.Select(r => r.Change).OfType<ReleaseQuarantined>().SingleOrDefault();
            Expect(staged?.To?.Count == asked.Recipients.Count - 1, $"the release is for the ticked recipients only (staged {(staged is null ? "none" : staged.To?.Count.ToString() ?? "all")}; ticks {string.Join(",", page.Recipients.Select(r => r.IsTicked))}; boxes {boxes.Length})");
            foreach (var command in new[] { page.ReleaseCommand, page.DenyCommand })
            {
                var button = FindButton(window, b => b.Command == command && b.IsEffectivelyVisible);
                button.BringIntoView();
                Settle();
                Click(window, button);
            }
            Expect(main.Pending.Rows.Select(r => r.Change).OfType<DenyQuarantineRelease>().Count() == 1, "Deny the Request staged a deny");
            main.Pending.DiscardAllCommand.Execute(null);
            window.Close();
        });

        Check("Message trace: a failed message explains why; Everything from This Sender searches again; a Trace Report needs a sender and then is asked for", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            var mail = main.MailSecurity!;
            mail.SelectedPage = Tenantry.ViewModels.MailSecurity.MailSecurityViewModel.Pages.Single(p => p.Key == "trace");
            Settle();
            Click(window, FindButton(window, b => b.Command == mail.SearchTraceCommand && b.IsEffectivelyVisible));
            for (var i = 0; i < 50 && mail.Trace.IsLoading; i++)
                Settle();
            var failed = mail.Trace.Rows.Cast<TracedMessage>().First(m => m.Status == "Failed");
            mail.Trace.PreviewRow(failed);
            for (var i = 0; i < 20; i++)
                Settle();
            var preview = (Tenantry.ViewModels.MailSecurity.TracePreviewViewModel)mail.Trace.PreviewModel!;
            Expect(preview.HasProblem, "the failed message explains why");
            var candidates = window.GetVisualDescendants().OfType<Button>().Where(b => b.Command == preview.TraceSenderCommand).ToArray();
            Expect(candidates.Any(b => b.IsEffectivelyVisible), $"the sender button shows ({candidates.Length} found, visible {candidates.Count(b => b.IsEffectivelyVisible)}, previews {window.GetVisualDescendants().OfType<Tenantry.App.Views.TracePreviewView>().Count()}, open {mail.Trace.IsPreviewOpen})");
            var everything = FindButton(window, b => b.Command == preview.TraceSenderCommand && b.IsEffectivelyVisible);
            everything.BringIntoView();
            Settle();
            Click(window, everything);
            Expect(mail.TraceSender == failed.Sender && mail.TraceRecipient.Length == 0, "the search is now everything from the sender");
            for (var i = 0; i < 50 && mail.Trace.IsLoading; i++)
                Settle();

            mail.SelectedPage = Tenantry.ViewModels.MailSecurity.MailSecurityViewModel.Pages.Single(p => p.Key == "reports");
            for (var i = 0; i < 30 && !mail.Reports.IsLoaded; i++)
                Settle();
            Settle();
            Settle();
            var before = mail.Reports.Reports.Count;
            var ask = FindButton(window, b => b.Command == mail.Reports.StartCommand && b.IsEffectivelyVisible);
            Click(window, ask);
            Expect(mail.Reports.HasProblems && mail.Reports.Reports.Count == before, "without a sender or recipient it says so and asks nothing");
            mail.Reports.Senders = "billing@invoices.example";
            Click(window, ask);
            for (var i = 0; i < 10; i++)
                Settle();
            Expect(!mail.Reports.HasProblems && mail.Reports.Reports.Count == before + 1, "with a sender the report is asked for");
            window.Close();
        });

        Check("Blocked senders and email authentication: Unblock stages an unblock; Turn Signing On waits for DNS; Set Up DKIM stages the keys", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            var mail = main.MailSecurity!;
            mail.SelectedPage = Tenantry.ViewModels.MailSecurity.MailSecurityViewModel.Pages.Single(p => p.Key == "blocked");
            for (var i = 0; i < 30 && !mail.Blocked.IsLoaded; i++)
                Settle();
            Settle();
            var test = mail.Blocked.Senders.Single(s => s.Address.StartsWith("zz-tenantry-test-", StringComparison.Ordinal));
            var unblock = FindButton(window, b => b.Command == test.UnblockCommand && b.IsEffectivelyVisible);
            Click(window, unblock);
            Expect(main.Pending!.Rows.Any(r => r.Change is UnblockSender), "Unblock staged an unblock");
            main.Pending.DiscardAllCommand.Execute(null);

            mail.SelectedPage = Tenantry.ViewModels.MailSecurity.MailSecurityViewModel.Pages.Single(p => p.Key == "domains");
            for (var i = 0; i < 30 && !mail.Domains.IsLoaded; i++)
                Settle();
            Settle();
            var half = mail.Domains.Domains.Single(d => d.Name == "northgate-events.example");
            var on = FindButton(window, b => b.Command == half.TurnOnCommand && b.IsEffectivelyVisible);
            Expect(!on.IsEnabled, "Turn Signing On is off until DNS shows the CNAMEs");
            var parked = mail.Domains.Domains.Single(d => d.Name == "old-northgate.example");
            Expect(!parked.CanCreateKeys, "a parked domain isn't offered DKIM");
            var good = mail.Domains.Domains.Single(d => d.Name == "northgate.example");
            var rotate = FindButton(window, b => b.Command == good.RotateCommand && b.IsEffectivelyVisible);
            rotate.BringIntoView();
            Settle();
            Click(window, rotate);
            Expect(main.Pending.Rows.Any(r => r.Change is RotateDkimKeys), "Rotate Keys staged a rotation");
            main.Pending.DiscardAllCommand.Execute(null);
            window.Close();
        });

        Check("Reported messages: It's Phishing stages a review; changing where reports go stages one change", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            var mail = main.MailSecurity!;
            mail.SelectedPage = Tenantry.ViewModels.MailSecurity.MailSecurityViewModel.Pages.Single(p => p.Key == "reported");
            for (var i = 0; i < 40 && (!mail.Reported.Routing.IsLoaded || mail.Reported.Submissions.IsLoading); i++)
                Settle();
            var report = mail.Reported.Submissions.Rows.Cast<ThreatSubmission>().First(s => s.CanReview && !s.IsReviewed && s.CreatedBy!.StartsWith("zz-tenantry-test-", StringComparison.Ordinal));
            mail.Reported.Submissions.PreviewRow(report);
            for (var i = 0; i < 10; i++)
                Settle();
            var preview = (Tenantry.ViewModels.MailSecurity.SubmissionPreviewViewModel)mail.Reported.Submissions.PreviewModel!;
            var phishing = FindButton(window, b => b.Command == preview.ReviewCommand && (string?)b.CommandParameter == "phishing" && b.IsEffectivelyVisible);
            phishing.BringIntoView();
            Settle();
            Click(window, phishing);
            Expect(main.Pending!.Rows.Any(r => r.Change is ReviewSubmission { Verdict: "phishing" }), "It's Phishing staged a review");
            main.Pending.DiscardAllCommand.Execute(null);
            mail.Reported.Routing.ThankAfter = !mail.Reported.Routing.ThankAfter;
            Expect(main.Pending.Rows.Count(r => r.Change is SetReportRouting) == 1, "the settings staged one change");
            main.Pending.DiscardAllCommand.Execute(null);
            window.Close();
        });

        Check("Reorder Rules: the button opens the page; ticking two (Shift for a range) and To the Top moves them and stages one new order; Put Them All Back undoes it", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            var mail = main.MailSecurity!;
            mail.SelectedPage = Tenantry.ViewModels.MailSecurity.MailSecurityViewModel.Pages[2];
            for (var i = 0; i < 50 && mail.Rules.IsLoading; i++)
                Settle();
            Settle();
            Click(window, FindButton(window, b => b.Command == mail.ReorderRulesCommand && b.IsEffectivelyVisible));
            var page = main.SelectedTab as Tenantry.ViewModels.MailSecurity.MailRecordPageViewModel;
            var order = page?.Content as Tenantry.ViewModels.MailSecurity.RuleOrderViewModel;
            Expect(order is not null, "Reorder Rules opened its page");
            Settle();
            var boxes = window.GetVisualDescendants().OfType<CheckBox>().Where(b => b.Tag is Tenantry.ViewModels.MailSecurity.RuleOrderRow && b.IsEffectivelyVisible).ToArray();
            Expect(boxes.Length == order!.Rows.Count, "each rule has a box to tick");
            var last = order.Rows[^1].Identity;
            var nextToLast = order.Rows[^2].Identity;
            Click(window, boxes.First(b => ((Tenantry.ViewModels.MailSecurity.RuleOrderRow)b.Tag!).Identity == nextToLast));
            Click(window, boxes.First(b => ((Tenantry.ViewModels.MailSecurity.RuleOrderRow)b.Tag!).Identity == last), RawInputModifiers.Shift);
            Expect(order.TickedCount == 2, $"two ticked (Shift ticked the range), not {order.TickedCount}");
            Click(window, FindButton(window, b => b.Command == order.MoveToTopCommand && b.IsEffectivelyVisible));
            Expect(order.Rows[0].Identity == nextToLast && order.Rows[1].Identity == last, "To the Top moved both, in their order");
            var pending = main.Pending!.Rows.Select(r => r.Change).OfType<ReorderRules>().ToArray();
            Expect(pending.Length == 1 && pending[0].New[0].Identity == nextToLast, "one new order is in Pending Changes");
            var back = FindButton(window, b => b.Command == order.UndoAllCommand && b.IsEffectivelyVisible);
            back.BringIntoView();
            Settle();
            Click(window, back);
            Expect(!main.Pending.Rows.Any(r => r.Change is ReorderRules) && !order.HasMoved, "Put Them All Back undid it");
            window.Close();
        });

        Check("Group lifecycle: a Microsoft 365 group says when it expires and Renew Now stages it; Add a License gives a security group's members a license", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "groups");
            main.Groups.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var groups = main.Groups.Rows.Cast<GroupSummary>().ToArray();
            main.OpenGroup(groups.First(g => g.Kind == GroupKind.Microsoft365));
            Settle();
            var team = ((GroupDetailViewModel)main.SelectedTab!).Settings!.Lifecycle!;
            Expect(team.IsReady && team.ExpiryText.StartsWith("Expires", StringComparison.Ordinal), "the page read when it expires");
            var renew = FindButton(window, b => b.Command == team.RenewCommand && b.IsEffectivelyVisible);
            renew.BringIntoView();
            Settle();
            Click(window, renew);
            Expect(main.Pending!.Rows.Any(r => r.Change is RenewGroup), "Renew Now staged the renewal");

            main.OpenGroup(groups.First(g => g.Kind == GroupKind.Security && !g.IsDynamic));
            Settle();
            var security = ((GroupDetailViewModel)main.SelectedTab!).Settings!.Lifecycle!;
            var added = AddVia(window, security.LicensePicker, "");
            Expect(security.Licenses.Any(l => l.Name == added && l.PendingText == "Adding"), "the license shows as being added");
            Expect(main.Pending.Rows.Any(r => r.Change is SetGroupLicense { New: true }), "the license is in Pending Changes");
            window.Close();
        });

        Check("Group mail: the page reads the mail settings; a switch stages its setting; Add a Sender adds to who can send; Add Address opens a form and Add stages it", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "groups");
            main.Groups.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var list = main.Groups.Rows.Cast<GroupSummary>().First(g => g.Kind == GroupKind.Distribution);
            main.OpenGroup(list);
            Settle();
            var mail = ((GroupDetailViewModel)main.SelectedTab!).Settings!.Mail!;
            Expect(mail.IsReady, "the mail settings were read when the page opened");

            var outside = mail.Delivery.Single(t => t.Label == "Accept Mail from Outside");
            var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => t.DataContext == outside && t.IsEffectivelyVisible);
            toggle.BringIntoView();
            Settle();
            Click(window, toggle);
            Expect(main.Pending!.Rows.Any(r => r.Change is SetGroupMailValue { Parameter: "RequireSenderAuthenticationEnabled" }), "the switch staged outside senders");

            var added = AddVia(window, mail.AcceptList.Picker, "");
            Expect(mail.AcceptList.Rows.Any(r => r.Name == added && r.PendingText == "Adding"), "the sender shows as being added");
            Expect(main.Pending.Rows.Any(r => r.Change is SetGroupMailValue { Parameter: "AcceptMessagesOnlyFromSendersOrMembers" }), "who can send is in Pending Changes");

            var start = FindButton(window, b => b.Command == mail.StartAddAddressCommand && b.IsEffectivelyVisible);
            start.BringIntoView();
            Settle();
            Click(window, start);
            Expect(mail.IsAddingAddress, "Add Address opened the form");
            window.KeyTextInput("checked-list");
            Settle();
            Click(window, FindButton(window, b => b.Command == mail.AddAddressCommand && b.IsEffectivelyVisible));
            Expect(main.Pending.Rows.Any(r => r.Change is SetGroupAddress { New: true } a && a.Address.StartsWith("checked-list@", StringComparison.Ordinal)),
                "the address is in Pending Changes");
            Expect(!mail.IsAddingAddress, "Add closed the form");
            window.Close();
        });

        Check("Group settings: Edit stages a new name; By Rule shows the rule box, Test on Someone checks it, and Add stages it; Delete stages a typed delete; Deleted Groups opens", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "groups");
            main.Groups.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var group = main.Groups.Rows.Cast<GroupSummary>().First(g => g.Kind == GroupKind.Security && !g.IsDynamic);
            main.OpenGroup(group);
            Settle();
            var settings = ((GroupDetailViewModel)main.SelectedTab!).Settings!;
            var name = settings.Details.SelectMany(d => d.Fields).Single(f => f.Label == "Name");
            Click(window, FindButton(window, b => b.Command == name.EditCommand && b.IsEffectivelyVisible));
            Expect(name.IsEditing, "Edit opened the name box");
            window.KeyTextInput(" (renamed)");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle();
            Expect(main.Pending!.Rows.Any(r => r.Change is SetGroupSetting { Setting: SetGroupSetting.DisplayName } s && s.New!.EndsWith("(renamed)", StringComparison.Ordinal)),
                "the new name is in Pending Changes");

            var byRule = window.GetVisualDescendants().OfType<RadioButton>().First(r => r.IsEffectivelyVisible && (r.Content as string ?? "").StartsWith("By Rule", StringComparison.Ordinal));
            Click(window, byRule);
            Expect(settings.IsDynamicDraft, "By Rule chose a rule");
            var box = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.AcceptsReturn && t.DataContext == settings);
            Expect(box is not null, "the rule box shows");
            Expect(!settings.StageRuleCommand.CanExecute(null), "Add waits for a rule");
            box!.Focus();
            window.KeyTextInput("(user.department -eq \"Finance\")");
            Settle();
            Expect(settings.RuleDraft == "(user.department -eq \"Finance\")", $"the typed rule reached the draft, not '{settings.RuleDraft}'");
            Expect(settings.StageRuleCommand.CanExecute(null), "Add is ready once a rule is typed");
            AddVia(window, settings.TestPicker, "");
            Settle();
            Expect(settings.TestRows.Count == 1, "testing someone shows whether they'd be a member");
            var stage = FindButton(window, b => b.Command == settings.StageRuleCommand && b.IsEffectivelyVisible);
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            Expect(main.Pending.Rows.Any(r => r.Change is SetMembershipRule { NewDynamic: true }), "the rule is in Pending Changes");

            var deleteButton = FindButton(window, b => b.Command == settings.DeleteCommand && b.IsEffectivelyVisible);
            deleteButton.BringIntoView();
            Settle();
            Click(window, deleteButton);
            var delete = main.Pending.Rows.Select(r => r.Change).OfType<DeleteGroup>().SingleOrDefault();
            Expect(delete?.ConfirmPhrase == group.DisplayName, "Delete staged a delete that needs the name typed");

            main.SelectedSection = main.Sections.Single(s => s.Key == "groups");
            main.SelectedTab = main.Tabs[0];
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "Deleted Groups")));
            Expect(main.Dialog?.Content is Tenantry.ViewModels.Records.DeletedGroupsViewModel, "Deleted Groups opened its popup");
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
            Expect(main.Pending!.Count == 1, "the wipe is in Pending Changes");
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

        Check("List ticks: a plain click on a row keeps them; the pane has no ✕ while they show; ticking all says so", () =>
        {
            var (window, main, _) = OpenList();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.IsEffectivelyVisible);
            var rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsEffectivelyVisible).Take(4).ToArray();
            foreach (var row in rows.Take(3))
                Click(window, row.GetVisualDescendants().OfType<CheckBox>().First());
            Expect(main.Users.SelectionCount == 3, $"three rows are ticked (got {main.Users.SelectionCount})");
            // A plain text cell of the fourth row: not its name link or a button.
            var cell = rows[3].GetVisualDescendants().OfType<DataGridCell>().Last(c => c.IsEffectivelyVisible && c.Bounds.Width > 0);
            Click(window, cell);
            Expect(main.Users.SelectionCount == 3, $"the plain click kept all three ticks (now {main.Users.SelectionCount})");
            Expect(!window.GetVisualDescendants().OfType<Button>().Any(b => b.Command == main.Users.ClosePreviewCommand && b.IsEffectivelyVisible),
                "the pane shows Clear Selection, not a ✕");
            main.Users.ClosePreview();
            Expect(main.Users.SelectionCount == 3, "closing never unticks");
            var all = grid.GetVisualDescendants().OfType<CheckBox>().First(c => c.FindAncestorOfType<DataGridColumnHeader>() is not null);
            Click(window, all);
            Expect(main.Users.SelectionCount == main.Users.Rows.Count && main.Users.AllRowsTicked, "the header box ticked every matching row");
            Expect(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == main.Users.AllTickedText && t.IsEffectivelyVisible), "a banner says all are ticked");
            window.Close();
        });

        Check("A chip in a column too narrow for it ends in … inside the column", () =>
        {
            var (window, _, _) = OpenList();
            var grid = window.GetVisualDescendants().OfType<DataGrid>().First(g => g.IsEffectivelyVisible);
            var chip = grid.GetVisualDescendants().OfType<Tenantry.App.Controls.Badge>().First(b => b.Text == "Member" && b.IsEffectivelyVisible);
            var cell = chip.FindAncestorOfType<DataGridCell>()!;
            var row = chip.FindAncestorOfType<DataGridRow>()!;
            var column = grid.Columns.First(c => c.GetCellContent(row) is { } content && (content == chip || content.IsVisualAncestorOf(chip)));
            column.Width = new DataGridLength(70);
            Settle();
            var text = chip.GetVisualDescendants().OfType<TextBlock>().First();
            var right = chip.TranslatePoint(new Point(chip.Bounds.Width, 0), cell)!.Value.X;
            Expect(right <= cell.Bounds.Width + 0.5, $"the chip ends inside its cell ({right:0} of {cell.Bounds.Width:0})");
            Expect(text.Bounds.Width < text.DesiredSize.Width + 0.5 && text.Bounds.Width < 60, $"its text is shortened ({text.Bounds.Width:0} px)");
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

        Check("Groups & Access: Add to Group opens a popup that adds; the list's own search only hides rows", () =>
        {
            var (window, main, page) = OpenRecord(p => p.GroupsTab.Groups.Count >= 2);
            var tab = page.GroupsTab;
            page.SelectedSection = tab;
            Settle();
            var before = tab.Groups.Count;
            AddVia(window, tab.GroupPicker, "sg");
            Expect(main.Pending!.Count == 1 && tab.Groups.Count == before + 1, "the group was added, as a pending row");

            var lists = window.GetVisualDescendants().OfType<Tenantry.App.Views.ListSearchBox>().Where(s => s.IsEffectivelyVisible).ToArray();
            var search = lists.First(s => s.Target?.ItemsSource == tab.Groups);
            var rows = search.Target!.GetRealizedContainers().ToArray();
            var first = tab.Groups[0].Name;
            search.Text = first;
            Settle();
            Expect(rows.Count(r => r.IsVisible) >= 1 && rows.Where(r => r.IsVisible).All(r => ((ToggleRow)search.Target.ItemFromContainer(r)!).Name.Contains(first, StringComparison.CurrentCultureIgnoreCase)),
                "typing a group's name leaves only matching rows");
            Expect(main.Pending.Count == 1, "searching added nothing");
            search.Text = "no group is called this";
            Settle();
            Expect(rows.All(r => !r.IsVisible) && search.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Nothing here matches." && t.IsVisible),
                "no match hides every row and says so");
            search.Text = "";
            Settle();
            Expect(rows.All(r => r.IsVisible), "clearing the search shows every row again");
            window.Close();
        });

        Check("Account tab: typing alone stages nothing; ✓ stages it; reopened, one click on its pending chip undoes the change", () =>
        {
            var (window, main, page) = OpenRecord();
            page.SelectedSection = page.Account;
            page.Account.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var field = page.Account.Groups.SelectMany(g => g.Fields).First(f => f.Property == "businessPhones");
            var edit = FindButton(window, b => b.Command == field.EditCommand && b.IsEffectivelyVisible);
            edit.BringIntoView();
            Settle();
            Click(window, edit);
            Settle();
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.DataContext == field && t.IsEffectivelyVisible);
            Expect(box.IsFocused, "the box has the cursor");
            box.SelectAll();
            window.KeyTextInput("+1 555 0199");
            // Longer than the old pause-to-stage delay: typing alone no longer stages.
            var waitUntil = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < waitUntil)
                Settle();
            Expect(!field.HasPending && main.Pending!.Count == 0, "nothing is pending while still typing");
            Click(window, FindButton(window, b => b.Command == field.StageCommand && b.IsEffectivelyVisible));
            Expect(field.HasPending && main.Pending!.Count == 1 && !field.IsEditing, "✓ staged the number and closed the box");
            Click(window, FindButton(window, b => b.Command == field.EditCommand && b.IsEffectivelyVisible));
            Settle();
            Expect(field.IsEditing, "the field is open again, with its change pending");
            var chip = FindButton(window, b => b.Command == field.UnstageCommand && b.IsEffectivelyVisible);
            Click(window, chip);
            var pending = main.Pending!;
            Expect(!field.HasPending && pending.Count == 0, $"one click took it back out (pending {pending.Count})");
            Expect(!field.IsEditing, "and ended the edit");
            window.Close();
        });

        Check("Sign-in methods: Add Another Phone opens a form that adds a row; Change and Set Up Authenticator open theirs", () =>
        {
            var (window, main, page) = OpenRecord(p => p.User.Security?.Methods.Contains("mobilePhone") == true);
            var security = page.Sections.OfType<SecuritySection>().Single();
            page.SelectedSection = security;
            security.LoadAsync().GetAwaiter().GetResult();
            Settle();
            var add = FindButton(window, b => b.Command == security.AddPhoneCommand && b.IsEffectivelyVisible);
            add.BringIntoView();
            Settle();
            Click(window, add);
            var form = security.Form as ContactMethodForm;
            Expect(form is not null, "the phone form opened");
            var box = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.DataContext == form && t.IsEffectivelyVisible);
            Expect(box?.IsFocused == true, "the cursor is in its box");
            form!.Value = "+1 555 0100 333";
            Settle();
            Click(window, FindButton(window, b => b.Command == form.SaveCommand && b.IsEffectivelyVisible));
            Expect(security.Form is null && security.Methods.Any(m => m.PendingText == "Adding"), "the form closed and the phone shows as Adding");
            var phone = security.Methods.First(m => m.Kind == "Phone" && m.CanChange);
            var change = FindButton(window, b => b.Command == phone.ChangeCommand && b.IsEffectivelyVisible);
            Click(window, change);
            Expect(phone.Form is ContactMethodForm { Value.Length: > 0 } && security.Form is null, "Change opened the form under its row, with the number in it");
            var rowBox = window.GetVisualDescendants().OfType<TextBox>().First(t => t.DataContext == phone.Form && t.IsEffectivelyVisible);
            var rowTop = change.TranslatePoint(default, window)!.Value.Y;
            Expect(rowBox.TranslatePoint(default, window)!.Value.Y > rowTop, "the form is below the row it changes");
            Expect(rowBox.IsFocused, "the cursor is in it");
            var cancel = FindButton(window, b => b.Command == ((ContactMethodForm)phone.Form!).CancelCommand && b.IsEffectivelyVisible);
            cancel.BringIntoView();
            Settle();
            Click(window, cancel);
            Expect(phone.Form is null, "Cancel closed it");
            Click(window, FindButton(window, b => b.Command == security.SetUpAuthenticatorCommand && b.IsEffectivelyVisible));
            Expect(main.Dialog?.Content is AuthenticatorSetupViewModel, "Set Up Authenticator opened its guide");
            main.CloseDialog();
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
            var name = AddVia(window, tab.AccessPicker, "a");
            Expect(main.Pending!.Count == 1, "the access change is in Pending Changes");
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

        Check("Record Actions menu: Offboard closes the menu, and Add to Pending Changes stages the checklist", () =>
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
            workflow!.Sections.SelectMany(s => s.Steps).Single(s => s.Key == "mailaccess").IsOn = true;
            Settle();
            Expect(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Their Manager" && t.IsEffectivelyVisible),
                "the mailbox step shows its people, starting with Their Manager");
            var stage = FindButton(window, b => b.Content as string == "Add to Pending Changes");
            stage.BringIntoView();
            Settle();
            Click(window, stage);
            for (var i = 0; i < 50 && workflow!.IsPlanning; i++)
                Settle();
            Expect(main.Pending!.Count > 3, $"the checklist's changes are in Pending Changes (planning {workflow!.IsPlanning}, pending {main.Pending.Count}, " +
                $"result '{workflow.Result}', notes '{string.Join(" | ", workflow.Notes)}', dialog {main.Dialog?.Content?.GetType().Name})");
            Expect(main.Pending.Rows.Any(r => r.Change is RevokeSessions), "sign out everywhere is among them");
            Expect(main.Pending.Rows.Any(r => r.Change is SetMailboxAccess { Right: MailboxRight.FullAccess, New: true }), "their manager gets the mailbox");
            window.Close();
        });

        Check("Pending Changes: after Apply the bar says it's done, and its Close button hides it", () =>
        {
            var (window, main, page) = OpenRecord();
            // Demo people aren't test-marked: Test mode off (by typing the name) so the change goes through.
            main.Pending!.StartTurnOffCommand.Execute(null);
            main.Pending.TurnOffText = main.TenantName;
            main.Pending.TurnOffTestModeCommand.Execute(null);
            page.ToggleSignInCommand.Execute(null);
            Settle();
            var apply = FindButton(window, b => ToolTip.GetTip(b) as string == "Send these changes to Microsoft 365, then read each one back to confirm it");
            Click(window, apply);
            Settle();
            Click(window, apply);
            for (var i = 0; i < 50 && main.Pending!.IsApplying; i++)
                Settle();
            Expect(main.Pending!.HeaderText == "Changes Applied",
                $"the header says the changes are applied (was '{main.Pending.HeaderText}', confirming {main.Pending.IsConfirming}, result '{main.Pending.Result}', " +
                $"problem '{main.Pending.Rows.FirstOrDefault()?.Problem}')");
            var close = FindButton(window, b => ToolTip.GetTip(b) as string == main.Pending.CloseTip && b.IsEffectivelyVisible);
            Click(window, close);
            Settle();
            Expect(!main.Pending.IsVisible, "the bar is gone");
            main.Pending.TurnOnTestModeCommand.Execute(null);
            window.Close();
        });

        Check("Record Actions menu: Respond to a compromised account checks, then Add to Pending Changes stages and closes", () =>
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
            Expect(main.Pending!.Rows.Any(r => r.Change is ResetPassword) && main.Pending.Rows.Any(r => r.Change is RevokeSessions), "the response is in Pending Changes");
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
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.SecurityPageGroup { Key: "rules" }));
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
            Expect(main.Pending!.Rows.Any(r => r.Change is AddListEntry), "the entry is in Pending Changes");
            window.Close();
        });

        Check("Mail Flow Rules: the tab reads the rules, and the preview's Turn Off stages the change", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.SecurityPageGroup { Key: "rules" }));
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
            Expect(main.Pending!.Rows.Any(r => r.Change is SetRuleEnabled { New: false }), "turning it off is in Pending Changes");
            window.Close();
        });

        Check("Rule editor: New Rule opens a tab; a starter, the action list and typing put the rule in Pending Changes; Done closes it", () =>
        {
            var (window, main, _) = OpenList();
            var area = main.MailSecurity!;
            main.SelectedSection = main.Sections.Single(s => s.Key == "mail");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.SecurityPageGroup { Key: "rules" }));
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
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateRule), "the new rule is in Pending Changes");
            var done = FindButton(window, b => b.Command == editor.DoneCommand);
            done.BringIntoView();
            Settle();
            Click(window, done);
            Expect(!main.Tabs.Contains(editor), "Done closed the editor");
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateRule), "the rule stayed in Pending Changes");
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

        Check("New User: the list's button opens the form; Add puts the new person in Pending Changes and closes it", () =>
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
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateUser), "the new person is in Pending Changes");
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
                Expect(main.Dialog is null && main.Pending!.Count == 2, $"two changes in Pending Changes (got {main.Pending!.Count})");
            }
            finally
            {
                Platform.OpenCsv = () => Task.FromResult<(string, string)?>(null);
            }
            window.Close();
        });

        Check("New Group: the list's button opens the form; a shared mailbox with a person goes to Pending Changes", () =>
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
            AddVia(window, form.MemberPicker, main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member).DisplayName);
            Expect(form.Members.Count() == 1, "the person was added");
            var add = FindButton(window, b => b.Command == form.AddCommand);
            add.BringIntoView();
            Settle();
            Click(window, add);
            Expect(main.Dialog is null && main.Pending!.Rows.Any(r => r.Change is CreateGroup { Kind: GroupKind.SharedMailbox }), "it's in Pending Changes and the form closed");
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
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.SecurityPageGroup { Key: "rules" }));
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
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.SecurityPageGroup { Key: "rules" }));
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
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.SecurityPageGroup { Key: "rules" }));
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

        Check("Devices: clicking a device's name opens its page, and the page's Restart goes to Pending Changes", () =>
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
                "the restart is in Pending Changes");
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
            AddVia(window, groups.Picker, "SG Fin");
            Expect(groups.Groups.Count == before + 1 && groups.Groups.Any(g => g.PendingText == "Joining"), "a Joining row appeared");
            Expect(main.Pending!.Rows.Any(r => r.Change is SetGroupMembership { PersonType: "device", New: true }), "joining is in Pending Changes");
            var existing = groups.Groups.First(g => !g.HasPending);
            var remove = FindButton(window, b => b.Command == existing.ToggleCommand);
            Click(window, remove);
            Expect(main.Pending.Rows.Any(r => r.Change is SetGroupMembership { PersonType: "device", New: false }), "leaving is in Pending Changes");
            var leaving = groups.Groups.First(g => g.PendingText == "Leaving");
            Click(window, FindButton(window, b => b.Command == leaving.ToggleCommand));
            Expect(!main.Pending.Rows.Any(r => r.Change is SetGroupMembership { New: false }), "Undo took leaving back out");
            window.Close();
        });

        Check("Devices › Autopilot: the page tab reads the list, a serial opens its preview, and Set Group Tag goes to Pending Changes", () =>
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
            Expect(main.Pending!.Rows.Any(r => r.Change is SetAutopilotGroupTag { New: "Loaners" }), "the group tag is in Pending Changes");
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
            Expect(main.Pending!.Rows.Any(r => r.Change is SetSiteAdmin { New: true }), "adding you is in Pending Changes");
            Expect(!permissions.HasNoteAction && permissions.Note?.Contains("Pending Changes", StringComparison.Ordinal) == true, "the button went and the note says to apply");
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
            Expect(main.Pending!.Rows.Any(r => r.Change is SetGroupMembership { Role: GroupRole.Member, New: false }), "removing them is in Pending Changes");
            var removing = permissions.Groups.Single(g => g.Name == "Members").Members.First(m => m.StateText == "Removing");
            Settle();
            var undo = FindButton(window, b => b.Command == removing.DoCommand);
            undo.BringIntoView();
            Settle();
            Click(window, undo);
            Expect(!main.Pending.Rows.Any(r => r.Change is SetGroupMembership { New: false }), "Undo took it back out");
            var visitors = permissions.Groups.Single(g => g.Name == "Visitors");
            AddVia(window, visitors.Picker!, main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member).DisplayName);
            Expect(main.Pending.Rows.Any(r => r.Change is SetSiteGroupMember { New: true }), "adding them to Visitors is in Pending Changes");
            Expect(permissions.Groups.Single(g => g.Name == "Visitors").Members.Any(m => m.StateText == "Adding"), "an Adding row appeared");
            window.Close();
        });

        Check("Site page: Lock from the Overview tab's actions goes to Pending Changes as a caution change", () =>
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
            Expect(main.Pending!.Rows.Any(r => r.Change is SetSiteSetting { Setting: "lock", IsCaution: true }), "locking is in Pending Changes");
            window.Close();
        });

        Check("New Site: the Sites list's button opens the form; a communication site goes to Pending Changes and the form closes", () =>
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
            Expect(main.Dialog is null && main.Pending!.Rows.Any(r => r.Change is CreateSite { Kind: SiteKind.CommunicationSite }), "it's in Pending Changes and the form closed");
            window.Close();
        });

        Check("SharePoint › Deleted Sites: the page tab reads the list; Restore in the preview goes to Pending Changes", () =>
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
            Expect(main.Pending!.Rows.Any(r => r.Change is RestoreSite), "restoring is in Pending Changes");
            window.Close();
        });

        Check("Security & Compliance: the sidebar opens Admin Roles; a role opens its page; Remove stages it and picking someone then Add gives the role", () =>
        {
            var (window, main, _) = OpenList();
            main.Users.LoadAsync().GetAwaiter().GetResult();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is NavSection { Key: "security" }));
            var area = main.SecurityArea;
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "roles"));
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
            Expect(main.Pending!.Rows.Any(r => r.Change is RemoveRoleAssignment), "taking it away is in Pending Changes");
            Expect(!page.Assignments.IsGiving, "the Give the Role form starts closed: only its button shows");
            var give = FindButton(window, b => b.Command == page.Assignments.StartGiveCommand && b.IsEffectivelyVisible);
            give.BringIntoView();
            Settle();
            Click(window, give);
            Expect(page.Assignments.IsGiving, "the button opened the form");
            page.Assignments.Picker.Query = main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member).DisplayName;
            Settle();
            var option = FindButton(window, b => b.DataContext is Tenantry.ViewModels.Editing.PickRow && b.FindAncestorOfType<UserControl>()?.DataContext == page.Assignments.Picker);
            option.BringIntoView();
            Settle();
            Click(window, option);
            Expect(page.Assignments.HasPicked, "the person was chosen");
            var add = FindButton(window, b => b.Command == page.Assignments.AddCommand && b.IsEffectivelyVisible);
            add.BringIntoView();
            Settle();
            Click(window, add);
            Expect(main.Pending.Rows.Any(r => r.Change is AddRoleAssignment { Kind: RoleGrantKind.Active }), "giving the role is in Pending Changes");
            Expect(!page.Assignments.IsGiving, "adding closed the form");
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
            Expect(main.Dialog is null && main.Pending!.Rows.Any(r => r.Change is CreateCustomRole c && c.Actions.Count >= 3), "the role is in Pending Changes and the editor closed");
            window.Close();
        });

        Check("PIM: My Roles opens a role's switch-on form; Add asks for a reason, then stages it; Approvals' Approve stages the answer", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            var area = main.SecurityArea;
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "myroles"));
            Settle();
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
            Expect(main.Pending!.Rows.Any(r => r.Change is ActivateRole { On: true }) && !row.IsOpen, "switching it on is in Pending Changes and the form closed");
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "approvals"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "approvals" }));
            for (var i = 0; i < 50 && area.Approvals.Requests.Count == 0; i++)
                Settle();
            var request = area.Approvals.Requests.First();
            Click(window, FindButton(window, b => b.Command == request.ApproveCommand && b.IsEffectivelyVisible));
            Expect(main.Pending.Rows.Any(r => r.Change is ReviewRoleRequest { Approve: true }) && request.StateText == "Approving", "approving is in Pending Changes");
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
            Expect(main.Pending!.Rows.Any(r => r.Change is ExtendRoleAssignment), "extending is in Pending Changes");
            Click(window, window.GetVisualDescendants().OfType<TabItem>().First(t => t.DataContext is RoleSettingsSection));
            for (var i = 0; i < 20 && !page.Settings.IsLoaded; i++)
                Settle();
            Expect(page.Settings.CanEdit, "the settings read and can be changed");
            page.Settings.MaxActivation = RoleSettingsSection.ActivationChoices.First(c => c.Value == TimeSpan.FromHours(4));
            Settle();
            Expect(main.Pending.Rows.Any(r => r.Change is SetRolePolicyRule { RuleId: RolePolicyRules.ActivationExpiry }), "the shorter activation is in Pending Changes");
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
            Expect(main.Pending!.Rows.Any(r => r.Change is SetCaPolicyState { New: "disabled" }) && page!.HasPending, "switching it off is in Pending Changes");
            Click(window, FindButton(window, b => b.Command == page!.DeleteCommand && b.IsEffectivelyVisible));
            Expect(main.Pending.Rows.Any(r => r.Change is DeleteCaPolicy), "deleting it is in Pending Changes");
            window.Close();
        });

        Check("What If: choosing a person and Check lists which policies apply and why the others don't", () =>
        {
            var (window, main, _) = OpenList();
            main.Users.LoadAsync().GetAwaiter().GetResult();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "whatif"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "whatif" }));
            var whatIf = main.SecurityArea.WhatIf;
            whatIf.Picker.Query = main.Users.Rows.Cast<UserSummary>().First(u => u.Kind == UserKind.Member).DisplayName;
            Settle();
            Click(window, FindButton(window, b => b.DataContext is Tenantry.ViewModels.Editing.PickRow && b.FindAncestorOfType<UserControl>()?.DataContext == whatIf.Picker));
            Expect(whatIf.HasPerson, "the person was chosen");
            Click(window, FindButton(window, b => b.Command == whatIf.CheckCommand && b.IsEffectivelyVisible));
            for (var i = 0; i < 20 && whatIf.Results.Count == 0; i++)
                Settle();
            Expect(whatIf.Results.Any(r => r.Applies) && whatIf.Results.Any(r => !r.Applies && r.Detail.StartsWith("Because", StringComparison.Ordinal)) && whatIf.HasSummary,
                "the results say what applies and why the rest don't");
            window.Close();
        });

        Check("New Policy: the button opens the editor; a template fills it (report-only, leaving you out); Done closes it with the policy in Pending Changes", () =>
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
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateCaPolicy), "the new policy is in Pending Changes");
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
            Expect(main.Pending!.Rows.Count(r => r.Change is UpdateCaPolicy) == 1, "one change to the policy is in Pending Changes");
            Expect(editor!.Lines.Any(l => l.Label == "Client Apps" && l.Value == "Browsers"), "the words beside it follow");
            // Leaving people out: an Add button whose popup lists people; tick one, Shift-click a third, Add 3.
            var before = editor.ExcludePeople.Items.Count;
            var names = AddSeveralVia(window, editor.ExcludePeople.Picker, "", 3);
            Expect(editor.ExcludePeople.Items.Count == before + 3 && names.All(n => editor.ExcludePeople.Items.Any(i => i.Name == n)), "all three are chips");
            Expect(main.Pending!.Rows.Count(r => r.Change is UpdateCaPolicy) == 1, "still one change to the policy");
            window.Close();
        });

        Check("Named Locations: Add Location opens the form; Add to Pending Changes stages it; a location a policy uses can't be deleted; Edit stages a change", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "locations"));
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
            Expect(!page.IsFormOpen && main.Pending!.Rows.Any(r => r.Change is CreateNamedLocation { Location.Name: "Branch Office" }), "the new location is in Pending Changes and the form closed");
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
                $"the change is in Pending Changes ({string.Join("; ", page.Problems)}; open {page.IsFormOpen}; {string.Join(", ", main.Pending.Rows.Select(r => r.Change.GetType().Name))})");
            window.Close();
        });

        Check("Authentication Strengths: the page tab lists Microsoft's built-in strengths with their methods in words", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "strengths"));
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
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "appregs"));
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
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "enterprise"));
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
            static bool SecretBox(TextBox t) => t.IsEffectivelyVisible && t.PlaceholderText?.StartsWith("What it's for", StringComparison.Ordinal) == true;
            Expect(!window.GetVisualDescendants().OfType<TextBox>().Any(SecretBox), "no secret form on the page until Add Secret is pressed");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is AddSecretForm f && b.Command == f.OpenCommand));
            Settle();
            var box = window.GetVisualDescendants().OfType<TextBox>().First(SecretBox);
            Expect(box.IsFocused, "the cursor is in the description box");
            box.Text = "Rotation";
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "Add Secret")));
            Expect(main.Pending!.Rows.Any(r => r.Change is AddAppSecret { Description: "Rotation" }), "the new secret is in Pending Changes");
            for (var i = 0; i < 5; i++)
                Settle();
            var remove = FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is AppRow { Title: "Nightly export", ActionText: "Remove" });
            Click(window, remove);
            for (var i = 0; i < 5; i++)
                Settle();
            Expect(main.Pending.Rows.Any(r => r.Change is RemoveAppCredential), "removing the expired secret is in Pending Changes");
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
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "appregs"));
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
            Expect(main.Dialog is null && main.Pending!.Rows.Any(r => r.Change is CreateAppRegistration), "Add put it in Pending Changes and closed the popup");

            main.OpenEnterpriseApp(main.SecurityArea.Apps!.EnterpriseApps.Single(e => e.Name == "Fabrikam Whiteboard"));
            for (var i = 0; i < 5; i++)
                Settle();
            var signIn = window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => t.IsEffectivelyVisible);
            Click(window, signIn);
            Expect(main.Pending!.Rows.Any(r => r.Change is SetEnterpriseAppSetting { Setting: "enabled", New: false }), "turning sign-in off is in Pending Changes");
            window.Close();
        });

        Check("Authentication Methods: the tab lists methods; choosing one opens its editor; Off stages it; Security Defaults' switch stages too", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "authmethods"));
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
            Expect(main.Pending!.Rows.Any(r => r.Change is SetAuthMethodConfig { Method.Id: "Fido2", IsCaution: true }), "switching passkeys off is a caution change in Pending Changes");
            Expect(page.Methods.Single(m => m.Id == "Fido2").Pending && page.Editor == editor, "its row says Pending and the editor stays open");
            var defaults = window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is AuthItemRow { Id: "securityDefaults" });
            Expect(defaults is not null, "the Security Defaults row is on screen");
            defaults!.BringIntoView();
            Settle();
            Click(window, defaults!);
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ToggleSwitch>().First(t => t.IsEffectivelyVisible && t.DataContext is SecurityDefaultsEditor));
            Expect(main.Pending.Rows.Any(r => r.Change is SetSecurityDefaults { New: true }), "turning security defaults on is in Pending Changes");
            window.Close();
        });

        Check("Risky Users (P2): the tab lists people at risk; a row's preview shows detections; Dismiss Risk stages it and the preview offers Undo", () =>
        {
            var (window, main, _) = OpenList(premium: true);
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "riskyusers"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "riskyusers" }));
            var area = main.SecurityArea;
            for (var i = 0; i < 50 && area.RiskyUsers.Total == 0; i++)
                Settle();
            Expect(area.RiskyUsers.Rows.Count == 3 && !area.HasRiskNote, "three people at risk, no note");
            area.RiskyUsers.IsPreviewOpen = true;
            area.RiskyUsers.SelectedRow = area.RiskyUsers.Rows[0];
            Settle();
            var preview = area.RiskyUsers.PreviewModel as RiskPreviewViewModel;
            Expect(preview is { HasDetections: true }, "the preview lists what was detected");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is RiskButton { Text: "Dismiss Risk" }));
            Expect(main.Pending!.Rows.Any(r => r.Change is SetUserRisk { Compromised: false }), "dismissing is in Pending Changes");
            Settle();
            Expect(window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.DataContext is RiskButton { Text: "Undo" }), "the preview now offers Undo");
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "riskdetections"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "riskdetections" }));
            for (var i = 0; i < 10; i++)
                Settle();
            Expect(area.RiskDetections.Total > 0, "Risk Detections shows the detections");
            window.Close();
        });

        Check("Risky Users without P2: the page says it needs Microsoft Entra ID P2", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "riskyusers"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "riskyusers" }));
            for (var i = 0; i < 20 && !main.SecurityArea.HasRiskNote; i++)
                Settle();
            Expect(main.SecurityArea.RiskNote == Tenantry.Core.Directory.Risk.NeedsP2, "the P2 note shows");
            Expect(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == Tenantry.Core.Directory.Risk.NeedsP2), "and is on screen");
            window.Close();
        });

        Check("Audit Log: a range button and a record type, then Search; the search waits, finishes, and its records open with the whole record in the preview", () =>
        {
            AuditLogViewModel.CheckEvery = TimeSpan.Zero;
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "auditlog"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "auditlog" }));
            var page = main.SecurityArea.AuditLog;
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is Tenantry.ViewModels.Editing.EditorChoice { Value: "30" }));
            Expect(page.StartDate == DateTime.Now.Date.AddDays(-29), "Last 30 Days set the dates");
            var types = window.GetVisualDescendants().OfType<Expander>().First(e => (e.Header as string)?.StartsWith("Record Types", StringComparison.Ordinal) == true);
            Click(window, types.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First());
            Settle();
            Expect(types.IsExpanded, "clicking Record Types opened the list");
            var check =window.GetVisualDescendants().OfType<CheckBox>().First(c => c.IsEffectivelyVisible && (c.Content as string) == "SharePoint and OneDrive: Files");
            check.BringIntoView();
            Settle();
            Click(window, check);
            var search = FindButton(window, b => b.Command == page.SearchCommand && b.IsEffectivelyVisible);
            search.BringIntoView();
            Settle();
            Click(window, search);
            for (var i = 0; i < 50 && page.SelectedSearch?.Search.IsDone != true; i++)
                Settle();
            for (var i = 0; i < 20 && page.Results.Total == 0; i++)
                Settle();
            Expect(page.SelectedSearch?.StatusText == "Done" && page.Results.Total > 0, "the search finished and its records are listed");
            page.Results.IsPreviewOpen = true;
            page.Results.SelectedRow = page.Results.Rows[0];
            Settle();
            Expect(page.Results.PreviewModel is AuditRecordPreviewViewModel { Json.Length: > 10 }, "the preview shows the whole record");
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
            Expect(deletes.Length == 1 && deletes[0].Device.Name == stale.Name, "only the stale device is in Pending Changes");
            window.Close();
        });

        Check("eDiscovery: the sidebar opens Cases; a case opens its page; a hold's Turn Off stages it; New Hold opens the editor, a picked mailbox becomes a chip, and Save stages the hold and closes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "cases"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "cases" }));
            var area = main.SecurityArea;
            for (var i = 0; i < 20 && area.Cases.Total == 0; i++)
                Settle();
            Expect(area.Cases.Total > 0, "the cases are listed");
            area.Cases.ApplyView(area.Cases.Views.Single(v => v.View.Id == "active"));
            area.Cases.IsPreviewOpen = true;
            area.Cases.SelectedRow = area.Cases.Rows.Cast<Tenantry.Core.Directory.EdCase>().Single(c => c.Name == "Vendor Contract Review");
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "Open Case")));
            var page = main.SelectedTab as EdCasePageViewModel ?? throw new InvalidOperationException("the case page didn't open");
            Click(window, window.GetVisualDescendants().OfType<TabItem>().First(t => t.DataContext is AppSection { Key: "holds" }));
            for (var i = 0; i < 10; i++)
                Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is AppRow { Title: "Contract hold" } && b.Content as string == "Change"));
            var changing = main.Dialog?.Content as EdItemEditorViewModel ?? throw new InvalidOperationException("Change didn't open the hold");
            Expect(!changing.IsNew && changing.Mailboxes.Chips.Count == 2, "the editor shows the hold's two mailboxes");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == changing.CancelCommand));
            Expect(main.Dialog is null && main.Pending!.Rows.Count == 0, "Cancel closes it with nothing staged");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is AppRow { Title: "Contract hold" } && b.Content as string == "Turn Off"));
            Expect(main.Pending!.Rows.Any(r => r.Change is SetHoldEnabled { New: false }), "turning the hold off is in Pending Changes");
            Settle();
            Expect(window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.DataContext is AppRow { Title: "Contract hold", IsUndo: true }),
                "the hold's row now offers Undo");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is AppButtonForm && b.Content as string == "New Hold"));
            var editor = main.Dialog?.Content as EdItemEditorViewModel ?? throw new InvalidOperationException("the hold editor didn't open");
            editor.Name = "Keep it";
            Settle();
            var chosen = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsEffectivelyVisible && i.DataContext is Tenantry.ViewModels.Editing.EditorChoice { Value: "chosen" });
            Click(window, chosen);
            Expect(editor.Mailboxes.IsChosen, "Chosen shows the mailbox picker");
            AddVia(window, editor.Mailboxes.Picker, main.Users.Rows.Cast<UserSummary>().First(u => u.Mailbox is not null).DisplayName);
            Expect(editor.Mailboxes.Chips.Count == 1, "the picked mailbox is a chip");
            Expect(window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.DataContext is PlaceChip), "with a × to take it off");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == editor.SaveCommand));
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateHold { Hold.Name: "Keep it" }), "the new hold is in Pending Changes");
            Expect(main.Dialog is null, "the editor closed");
            window.Close();
        });

        Check("Content Searches: a search opens its page; What It Found lists each place; Run Search shows the wheel, then finishes", () =>
        {
            EdSearchPageViewModel.CheckEvery = TimeSpan.Zero;
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "searches"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "searches" }));
            var area = main.SecurityArea;
            for (var i = 0; i < 20 && area.Searches.Total == 0; i++)
                Settle();
            main.OpenSearch(area.Searches.Rows.Cast<Tenantry.Core.Directory.EdSearch>().Single(s => s.Name == "Contract emails"));
            Settle();
            var page = main.SelectedTab as EdSearchPageViewModel ?? throw new InvalidOperationException("the search page didn't open");
            Click(window, window.GetVisualDescendants().OfType<TabItem>().First(t => t.DataContext is AppSection { Key: "found" }));
            for (var i = 0; i < 10; i++)
                Settle();
            Expect(window.GetVisualDescendants().OfType<SelectableTextBlock>().Count(t => t.IsEffectivelyVisible && t.DataContext is AppRow) >= 3, "each place is a row");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is PageButton { Text: "Run Search" }));
            Expect(page.IsBusy || page.Search.LastRun > DateTimeOffset.UtcNow.AddMinutes(-1), "running shows the wheel (or has finished)");
            for (var i = 0; i < 30 && page.IsBusy; i++)
                Settle();
            Expect(!page.IsBusy && page.Search.Status == "Completed", "the run finished");
            Expect(window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.DataContext is PageButton { Text: "Run Search" }), "Run Search is back");
            window.Close();
        });

        Check("Search page: Search and Delete opens its popup and Add stages a purge; Export picks a folder, saves the file and says where", () =>
        {
            EdExportViewModel.CheckEvery = TimeSpan.Zero;
            var folder = Directory.CreateTempSubdirectory("tenantry-check-export").FullName;
            Platform.PickFolder = () => Task.FromResult<string?>(folder);
            try
            {
                var (window, main, _) = OpenList();
                main.SelectedSection = main.Sections.Single(s => s.Key == "security");
                Settle();
                // Security & Compliance pages sit in sections: show the one holding it first.
                main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "searches"));
                Settle();
                Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "searches" }));
                for (var i = 0; i < 20 && main.SecurityArea.Searches.Total == 0; i++)
                    Settle();
                main.OpenSearch(main.SecurityArea.Searches.Rows.Cast<Tenantry.Core.Directory.EdSearch>().Single(s => s.Name == "Contract emails"));
                for (var i = 0; i < 10; i++)
                    Settle();
                Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is PageButton { Text: "Search and Delete" }));
                var purge = main.Dialog?.Content as EdPurgeViewModel ?? throw new InvalidOperationException("Search and Delete didn't open");
                Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsEffectivelyVisible && ReferenceEquals(i.DataContext, EdPurgeViewModel.Kinds[1])));
                Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == purge.AddCommand));
                Expect(main.Pending!.Rows.Any(r => r.Change is PurgeSearch { Hard: true }) && main.Dialog is null, "a hard delete is in Pending Changes and the popup closed");
                Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is PageButton { Text: "Export" }));
                var export = main.Dialog?.Content as EdExportViewModel ?? throw new InvalidOperationException("Export didn't open");
                Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == export.ChooseFolderCommand));
                Expect(export.Folder == folder, "Choose Folder filled in the folder");
                Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == export.StartCommand));
                for (var i = 0; i < 30 && !export.IsDone && !export.HasProblem; i++)
                    Settle();
                Expect(export.IsDone && Directory.GetFiles(folder).Length == 1, $"the export finished and saved its file ({export.Problem})");
                Expect(window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.Command == export.ShowFolderCommand), "Show Folder is offered");
                Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == export.CloseCommand));
                Expect(main.Dialog is null, "Close closes it");
                window.Close();
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        });

        Check("Retention: the page tab lists policies; a policy opens its page and Turn Off stages it; New Policy's editor stages a policy for all mailboxes and closes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "retentionpolicies"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "retentionpolicies" }));
            var area = main.SecurityArea;
            for (var i = 0; i < 20 && area.RetentionPolicies.Total == 0; i++)
                Settle();
            Expect(area.RetentionPolicies.Total > 0, "the policies are listed");
            area.RetentionPolicies.IsPreviewOpen = true;
            area.RetentionPolicies.SelectedRow = area.RetentionPolicies.Rows.Cast<RetentionPolicy>().Single(p => p.Name == "Keep mail seven years");
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "Open Policy")));
            var page = main.SelectedTab as RetentionPolicyPageViewModel ?? throw new InvalidOperationException("the policy page didn't open");
            Click(window, window.GetVisualDescendants().OfType<TabItem>().First(t => t.DataContext is AppSection { Key: "where" }));
            for (var i = 0; i < 5; i++)
                Settle();
            Expect(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "All of them"), "Where shows all mailboxes");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is PageButton { Text: "Turn Off" }));
            Expect(main.Pending!.Rows.Any(r => r.Change is SetRetentionPolicyEnabled { New: false }), "turning it off is in Pending Changes");
            main.SelectedTab = area;
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "New Policy")));
            var editor = main.Dialog?.Content as RetentionPolicyEditorViewModel ?? throw new InvalidOperationException("the policy editor didn't open");
            editor.Name = "Keep invoices";
            Settle();
            var all = window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsEffectivelyVisible && i.DataContext is Tenantry.ViewModels.Editing.EditorChoice { Value: "all" });
            all.BringIntoView();
            Settle();
            Click(window, all);
            Expect(editor.Places[0].Mode.Value == "all", "All mailboxes chosen");
            var save = FindButton(window, b => b.IsEffectivelyVisible && b.Command == editor.SaveCommand);
            save.BringIntoView();
            Settle();
            Click(window, save);
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateRetentionPolicy { Policy.Name: "Keep invoices" }) && main.Dialog is null, "the new policy is staged and the editor closed");
            window.Close();
        });

        Check("Retention Events: New Event opens its form and Add stages the event as a caution change", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "retentionevents"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "retentionevents" }));
            for (var i = 0; i < 20 && main.SecurityArea.RetentionEvents?.Section.Cards.Count is null or 0; i++)
                Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is AppButtonForm && b.Content as string == "New Event"));
            var form = main.Dialog?.Content as NewRetentionEventViewModel ?? throw new InvalidOperationException("New Event didn't open");
            form.Name = "Lease ended";
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == form.AddCommand));
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateRetentionEvent { IsCaution: true }) && main.Dialog is null, "the event is staged and the form closed");
            window.Close();
        });

        Check("Reports: the sidebar opens the counts; Open List shows Users with that view; the Change Log lists a change once it's applied", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "reports");
            for (var i = 0; i < 20 && main.Reports.Reports.Cards.Count == 0; i++)
                Settle();
            Expect(main.Reports.Reports.Cards.Count >= 5, "the report groups are shown");
            var row = main.Reports.Reports.Cards.SelectMany(c => c.RowItems).Single(r => r.Title == "Licensed But Blocked");
            Expect(row.Badges.Count == 1, "each report shows its count");
            for (var i = 0; i < 10; i++)
                Settle();
            var open = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is AppRow { Title: "Licensed But Blocked" } && b.Content as string == "Open List")
                       ?? throw new InvalidOperationException($"Open List not on screen; buttons with AppRow: {window.GetVisualDescendants().OfType<Button>().Count(b => b.DataContext is AppRow)}, area tab {main.SelectedTab?.GetType().Name}");
            open.BringIntoView();
            Settle();
            Click(window, open);
            Expect(main.SelectedTab == main.Users && main.Users.CurrentView?.View.Id == "licensed-blocked", "Users is shown with that view");
            var person = main.Users.Rows.Cast<UserSummary>().First();
            _pipeline.Stage(new SetSignInAllowed(new ChangeTarget("user", person.Id, person.DisplayName, person.UserPrincipalName), person.AccountEnabled, !person.AccountEnabled));
            _pipeline.ApplyAsync().GetAwaiter().GetResult();
            main.SelectedSection = main.Sections.Single(s => s.Key == "reports");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.TabStripItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "changelog" }));
            for (var i = 0; i < 20 && main.Reports.ChangeLog.Total == 0; i++)
                Settle();
            Expect(main.Reports.ChangeLog.Total == 1, "the change is in the Change Log");
            Expect(main.Reports.ChangeLog.Rows.Cast<Tenantry.Core.Changes.ChangeLogEntry>().Single().Outcome == ChangeOutcome.Refused, "stopped by Test mode, and logged as such");
            window.Close();
        });

        Check("Sign-In History: the page tab reads the archive; a range chip re-reads it; Failed narrows the list; the preview opens the person", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "reports");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.TabStripItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "signins" }));
            var history = main.Reports.SignIns;
            for (var i = 0; i < 30 && history.List.Total == 0; i++)
                Settle();
            var week = history.List.Total;
            Expect(week > 0 && history.Summary.StartsWith("This computer holds", StringComparison.Ordinal), "the last 7 days are listed, with what the archive holds");
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsEffectivelyVisible && i.DataContext is Tenantry.ViewModels.Editing.EditorChoice { Value: "30" }));
            for (var i = 0; i < 30 && history.List.Total == week; i++)
                Settle();
            Expect(history.List.Total > week, "Last 30 Days shows more");
            history.List.ApplyView(history.List.Views.Single(v => v.View.Id == "failed"));
            Settle();
            Expect(history.List.Rows.Count > 0 && history.List.Rows.Cast<SignInEntry>().All(s => !s.Succeeded), "Failed shows only failed sign-ins");
            history.List.IsPreviewOpen = true;
            history.List.SelectedRow = history.List.Rows[0];
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "Open Person")));
            Expect(main.SelectedTab is UserDetailViewModel, "Open Person opened their page");
            window.Close();
        });

        Check("New Case: the list's button opens the form; Add with no name says why; with one it stages the case and closes", () =>
        {
            var (window, main, _) = OpenList();
            main.SelectedSection = main.Sections.Single(s => s.Key == "security");
            Settle();
            // Security & Compliance pages sit in sections: show the one holding it first.
            main.SecurityArea.SelectedSection = SecurityAreaViewModel.SectionOf(SecurityAreaViewModel.Pages.Single(p => p.Key == "cases"));
            Settle();
            Click(window, window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "cases" }));
            for (var i = 0; i < 20 && main.SecurityArea.Cases.Total == 0; i++)
                Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "New Case")));
            var form = main.Dialog?.Content as NewCaseViewModel ?? throw new InvalidOperationException("the form didn't open");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == form.AddCommand));
            Expect(form.HasProblems && main.Dialog is not null, "no name: it says why and stays open");
            form.Name = "Insurance Claim";
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == form.AddCommand));
            Expect(main.Pending!.Rows.Any(r => r.Change is CreateCase { Case.Name: "Insurance Claim" }) && main.Dialog is null, "the case is in Pending Changes and the form closed");
            window.Close();
        });

        Check("PowerShell: the sidebar button opens the console; Tab completes what's typed; Enter runs it, shows its output and logs it", () =>
        {
            var (window, main, _) = OpenList();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == main.OpenPowerShellCommand));
            var console = main.SelectedTab as PowerShellConsoleViewModel ?? throw new InvalidOperationException("the console didn't open");
            for (var i = 0; i < 10 && console.Status != Tenantry.Core.PowerShell.PsSessionStatus.Ready; i++)
                Settle();
            Expect(console.Connections.Any(c => c.State == "Connected") && console.Connections.Any(c => c.HasAction), "it shows each connection, with a button where one needs something");
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CommandBox");
            box.Focus();
            Settle();
            window.KeyTextInput("Get-Mai");
            Settle();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Settle();
            Expect(console.Input == "Get-Mailbox" && box.IsFocused, "Tab completed the command and kept the cursor in the box");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();
            var block = console.Blocks.SingleOrDefault() ?? throw new InvalidOperationException("nothing ran");
            Expect(block.Command == "Get-Mailbox" && !block.IsRunning && block.Chunks.Count > 0 && console.Input == "", "Enter ran it, showed its output and cleared the box");
            Expect(_pipeline.Log.All().Any(e => e.Change is PowerShellCommand { Script: "Get-Mailbox" }), "the command is in the Change Log");
            window.Close();
        });

        Check("Pending Changes: PowerShell on a change shows its script; Open in Console puts it in the console's box and closes the popup", () =>
        {
            var (window, main, _) = OpenList();
            var person = main.Users.Rows.Cast<UserSummary>().First();
            var target = new ChangeTarget("user", person.Id, person.DisplayName, person.UserPrincipalName);
            _pipeline.Stage(new SetHiddenFromAddressList(target, false, true));
            _pipeline.Stage(new SetUserText(target, "department", "Department", person.Department, "Finance"));
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.DataContext is Tenantry.ViewModels.Editing.PendingRow { Change: SetHiddenFromAddressList } && Labelled(b, "PowerShell")));
            var script = main.Dialog?.Content as PowerShellScriptViewModel ?? throw new InvalidOperationException("the script didn't open");
            Expect(script.Text.Contains("Set-Mailbox", StringComparison.Ordinal) && script.Text.Contains("-HiddenFromAddressListsEnabled:$true", StringComparison.Ordinal), "it shows the Exchange command");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && b.Command == script.OpenInConsoleCommand));
            var console = main.SelectedTab as PowerShellConsoleViewModel ?? throw new InvalidOperationException("the console didn't open");
            Expect(main.Dialog is null && console.Input.Contains("Set-Mailbox", StringComparison.Ordinal), "the popup closed and the script is in the box");
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "Show PowerShell")));
            var all = main.Dialog?.Content as PowerShellScriptViewModel ?? throw new InvalidOperationException("Show PowerShell didn't open");
            Expect(all.Text.Contains("# 1.", StringComparison.Ordinal) && all.Text.Contains("# 2.", StringComparison.Ordinal) && all.Runs == "Runs with Microsoft Graph and Exchange Online.",
                "Show PowerShell lists both changes and what they need");
            main.CloseDialog();
            window.Close();
        });

        Check("Change Log: an applied change's preview offers Show PowerShell, which opens its script", () =>
        {
            var (window, main, _) = OpenList();
            var person = main.Users.Rows.Cast<UserSummary>().First();
            _pipeline.Record(new RevokeSessions(new ChangeTarget("user", person.Id, person.DisplayName, person.UserPrincipalName)));
            main.SelectedSection = main.Sections.Single(s => s.Key == "reports");
            Settle();
            Click(window, window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.TabStripItem>().First(i => i.DataContext is Tenantry.ViewModels.MailSecurity.MailSecurityPage { Key: "changelog" }));
            for (var i = 0; i < 20 && main.Reports.ChangeLog.Total == 0; i++)
                Settle();
            main.Reports.ChangeLog.IsPreviewOpen = true;
            main.Reports.ChangeLog.SelectedRow = main.Reports.ChangeLog.Rows[0];
            Settle();
            Click(window, FindButton(window, b => b.IsEffectivelyVisible && Labelled(b, "Show PowerShell")));
            var script = main.Dialog?.Content as PowerShellScriptViewModel ?? throw new InvalidOperationException("the script didn't open");
            Expect(script.Text.Contains("revokeSignInSessions", StringComparison.Ordinal), "it shows the Graph request");
            main.CloseDialog();
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
            // Where it broke too, for anything other than a failed expectation.
            var at = e is InvalidOperationException && e.Message.StartsWith("expected", StringComparison.Ordinal) ? ""
                : " at " + (e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Tenantry", StringComparison.Ordinal)) ?? "");
            Console.WriteLine($"FAIL  {name}: {e.Message}{at}");
        }
    }

    static void Expect(bool condition, string what)
    {
        if (!condition)
            throw new InvalidOperationException($"expected: {what}");
    }

    /// <summary>The pipeline of the window <see cref="OpenList"/> opened last.</summary>
    static ChangePipeline _pipeline = null!;

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
            Changes = _pipeline = new ChangePipeline(new Tenantry.Demo.DemoChangeExecutor((Tenantry.Demo.DemoTenantDirectory)demo.Directory),
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

    /// <summary>
    /// Adds through an Add button as a person would: click it, the popup opens with the cursor in its
    /// search box, type, tick the first match, press Add; the popup closes. Returns the name chosen.
    /// </summary>
    static string AddVia(Window window, Tenantry.ViewModels.Editing.PersonPicker picker, string query) =>
        AddSeveralVia(window, picker, query, 1)[0];

    /// <summary>
    /// The same, ticking several: the first match, then Shift-click on the <paramref name="count"/>th
    /// ticks the range between. Add adds them all and closes the popup. Returns the names ticked.
    /// </summary>
    static string[] AddSeveralVia(Window window, Tenantry.ViewModels.Editing.PersonPicker picker, string query, int count)
    {
        var add = window.GetVisualDescendants().OfType<Tenantry.App.Views.AddButton>().First(a => a.DataContext == picker && a.IsEffectivelyVisible);
        var open = add.GetVisualDescendants().OfType<Button>().First();
        open.BringIntoView();
        Settle();
        Click(window, open);
        var flyout = (Flyout)open.Flyout!;
        Expect(flyout.IsOpen, "the Add button opened its popup");
        Settle();
        var box = (flyout.Content as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        Expect(box?.IsFocused == true, "the cursor is in the popup's search box");
        Expect(Buttons(flyout).Any(b => b.DataContext is Tenantry.ViewModels.Editing.PickRow), "the popup lists what can be added before anything is typed");
        picker.Query = query;
        Settle();
        // Each of the first rows' button on screen now: the list is rebuilt as the search changes, and
        // old buttons linger in the tree, so they're found from the picker's rows, not by position.
        var shown = Buttons(flyout).Where(b => b.IsAttachedToVisualTree() && b.IsEffectivelyVisible).ToArray();
        var rows = picker.Rows.Take(count).Select(r => shown.Last(b => ReferenceEquals(b.DataContext, r))).ToArray();
        Expect(rows.Length >= count, $"at least {count} to choose from");
        var top = TopLevel.GetTopLevel(rows[0])!;
        Click(top, rows[0]);
        Expect(flyout.IsOpen, "ticking leaves the popup open for more");
        if (count > 1)
        {
            rows[count - 1].BringIntoView();
            Settle();
            Click(top, rows[count - 1], RawInputModifiers.Shift);
        }
        var names = rows.Take(count).Select(b => ((Tenantry.ViewModels.Editing.PickRow)b.DataContext!)).ToArray();
        Expect(names.All(r => r.IsTicked) && picker.Ticks.Ticked.Count == count, $"{count} ticked{(count > 1 ? " (Shift-click ticked the range)" : "")}, not {picker.Ticks.Ticked.Count}");
        var addAll = Buttons(flyout).First(b => b.Command == picker.AddTickedCommand);
        Expect(addAll.Content as string == $"Add {count}", $"the button says Add {count}");
        Click(top, addAll);
        Expect(!flyout.IsOpen, "Add closed the popup");
        return names.Select(r => r.Option.Name).ToArray();
    }

    static IEnumerable<Button> Buttons(Flyout flyout) =>
        (flyout.Content as Control)?.GetLogicalDescendants().OfType<Button>() ?? [];

    static MenuItem MenuItem(MenuFlyout menu, Func<MenuItem, bool> match) =>
        menu.Items.OfType<MenuItem>().FirstOrDefault(match) ?? throw new InvalidOperationException("menu item not found");

    /// <summary>A real mouse click (down and up) at the middle of the control, on whatever top level shows it.</summary>
    static void Click(Control control) => Click(TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("not shown"), control);

    static void Click(TopLevel top, Control control, RawInputModifiers keys = RawInputModifiers.None)
    {
        var middle = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
            ?? throw new InvalidOperationException("control isn't on screen");
        top.MouseDown(middle, MouseButton.Left, keys);
        top.MouseUp(middle, MouseButton.Left, keys);
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
