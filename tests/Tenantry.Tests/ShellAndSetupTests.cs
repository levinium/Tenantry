using System.Diagnostics;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Permissions;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels;

namespace Tenantry.Tests;

public class ShellAndSetupTests
{
    [Fact]
    public void Starts_on_the_welcome_screen_listing_saved_tenants()
    {
        var connector = new FakeConnector();
        connector.Profiles.Add(FakeConnector.Profile());

        var shell = new ShellViewModel(connector);

        var welcome = Assert.IsType<WelcomeViewModel>(shell.CurrentPage);
        Assert.True(welcome.HasProfiles);
        Assert.Equal("Contoso", welcome.Profiles.Single().DisplayName);
        Assert.Equal("Tenantry", shell.Title);
    }

    [Fact]
    public async Task Opening_a_tenant_shows_it_and_switching_returns_to_welcome()
    {
        var connector = new FakeConnector();
        connector.Profiles.Add(FakeConnector.Profile());
        var shell = new ShellViewModel(connector);
        var welcome = (WelcomeViewModel)shell.CurrentPage;

        await welcome.ConnectCommand.ExecuteAsync(welcome.Profiles[0]);

        var main = Assert.IsType<MainViewModel>(shell.CurrentPage);
        Assert.Equal("Tenantry · Contoso", shell.Title);
        Assert.Equal("admin@contoso.example", main.Account);

        main.SwitchTenantCommand.Execute(null);
        Assert.IsType<WelcomeViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task A_sole_tenant_opens_at_start_but_switching_returns_home()
    {
        var connector = new FakeConnector();
        connector.Profiles.Add(FakeConnector.Profile());
        var shell = new ShellViewModel(connector);

        await shell.StartAsync();
        var main = Assert.IsType<MainViewModel>(shell.CurrentPage);

        main.SwitchTenantCommand.Execute(null);
        Assert.IsType<WelcomeViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task Several_tenants_stay_on_the_home_page_at_start()
    {
        var connector = new FakeConnector();
        connector.Profiles.Add(FakeConnector.Profile("p1", "Contoso"));
        connector.Profiles.Add(FakeConnector.Profile("p2", "Fabrikam"));
        var shell = new ShellViewModel(connector);

        await shell.StartAsync();

        Assert.IsType<WelcomeViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task The_default_tenant_opens_at_start_and_can_be_changed_or_cleared()
    {
        var connector = new FakeConnector();
        connector.Profiles.Add(FakeConnector.Profile("p1", "Contoso"));
        connector.Profiles.Add(FakeConnector.Profile("p2", "Fabrikam"));
        var welcome = (WelcomeViewModel)new ShellViewModel(connector).CurrentPage;
        var fabrikam = welcome.Profiles.Single(p => p.DisplayName == "Fabrikam");

        welcome.SetDefaultCommand.Execute(fabrikam);
        Assert.True(fabrikam.IsDefault);
        Assert.Equal(["p2"], connector.Profiles.Where(p => p.IsDefault).Select(p => p.Id));

        // A fresh start opens it.
        ConnectedTenant? opened = null;
        string? openedId = null;
        connector.OnConnect = (profile, _) => { openedId = profile.Id; return Task.FromResult(opened = FakeConnector.Tenant()); };
        var shell = new ShellViewModel(connector);
        await shell.StartAsync();
        Assert.Equal("p2", openedId);
        Assert.IsType<MainViewModel>(shell.CurrentPage);

        // Clearing it means a fresh start with two tenants stays home.
        var again = (WelcomeViewModel)new ShellViewModel(connector).CurrentPage;
        again.ClearDefaultCommand.Execute(again.Profiles.Single(p => p.IsDefault));
        Assert.False(again.HasDefault);
        var shell2 = new ShellViewModel(connector);
        await shell2.StartAsync();
        Assert.IsType<WelcomeViewModel>(shell2.CurrentPage);
    }

    [Fact]
    public void Tenant_search_matches_name_domain_or_account()
    {
        var connector = new FakeConnector();
        foreach (var (id, name) in new[] { ("a", "Contoso"), ("b", "Fabrikam"), ("c", "Northwind"), ("d", "Tailspin") })
            connector.Profiles.Add(FakeConnector.Profile(id, name));
        var welcome = (WelcomeViewModel)new ShellViewModel(connector).CurrentPage;

        Assert.True(welcome.ShowSearch);
        welcome.SearchText = "fabrikam.example";
        Assert.Equal(["Fabrikam"], welcome.VisibleProfiles.Select(p => p.DisplayName));
        welcome.SearchText = "nobody";
        Assert.True(welcome.NoSearchMatches);
        welcome.SearchText = "";
        Assert.Equal(4, welcome.VisibleProfiles.Count);
    }

    [Fact]
    public void Removing_a_tenant_needs_confirmation()
    {
        var connector = new FakeConnector();
        connector.Profiles.Add(FakeConnector.Profile("p1", "Contoso"));
        connector.Profiles.Add(FakeConnector.Profile("p2", "Fabrikam"));
        var welcome = (WelcomeViewModel)new ShellViewModel(connector).CurrentPage;
        var contoso = welcome.Profiles.Single(p => p.DisplayName == "Contoso");

        welcome.ConfirmRemoveCommand.Execute(contoso);          // not asked yet: nothing happens
        Assert.Equal(2, welcome.Profiles.Count);

        welcome.AskRemoveCommand.Execute(contoso);
        Assert.True(contoso.IsConfirmingRemove);
        welcome.CancelRemoveCommand.Execute(contoso);
        Assert.False(contoso.IsConfirmingRemove);

        welcome.AskRemoveCommand.Execute(contoso);
        welcome.ConfirmRemoveCommand.Execute(contoso);
        Assert.Equal(["Fabrikam"], welcome.Profiles.Select(p => p.DisplayName));
        Assert.Equal(["p2"], connector.Profiles.Select(p => p.Id));
    }

    [Fact]
    public async Task A_failed_sign_in_stays_on_welcome_with_a_readable_error()
    {
        var connector = new FakeConnector
        {
            OnConnect = (_, _) => throw new InvalidOperationException("AADSTS50105: The signed in user is not assigned to a role for the application."),
        };
        connector.Profiles.Add(FakeConnector.Profile());
        var shell = new ShellViewModel(connector);
        var welcome = (WelcomeViewModel)shell.CurrentPage;

        await welcome.ConnectCommand.ExecuteAsync(welcome.Profiles[0]);

        Assert.Same(welcome, shell.CurrentPage);
        Assert.StartsWith("Your account isn't assigned to Tenantry", welcome.Error);
        Assert.False(welcome.IsBusy);
    }

    [Fact]
    public async Task Sign_in_can_be_cancelled()
    {
        var connector = new FakeConnector
        {
            OnConnect = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); },
        };
        connector.Profiles.Add(FakeConnector.Profile());
        var welcome = (WelcomeViewModel)new ShellViewModel(connector).CurrentPage;

        var running = welcome.ConnectCommand.ExecuteAsync(welcome.Profiles[0]);
        Assert.True(welcome.IsBusy);
        welcome.CancelSignInCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("Sign-in was cancelled.", welcome.Error);
        Assert.False(welcome.IsBusy);
    }

    [Fact]
    public async Task Automatic_setup_reports_progress_and_unresolved_permissions()
    {
        var connector = new FakeConnector
        {
            OnSetup = (progress, _) =>
            {
                progress.Report("Creating the Tenantry app registration");
                return Task.FromResult(new SetupOutcome(FakeConnector.Profile(), false, ["Microsoft Graph: X.Read"]));
            },
        };
        var wizard = new SetupWizardViewModel(connector, () => { }, () => { });

        wizard.ChooseAutomaticCommand.Execute(null);
        await wizard.RunAutomaticCommand.ExecuteAsync(null);

        Assert.True(wizard.IsDone);
        Assert.Contains("Creating the Tenantry app registration", wizard.Progress);
        Assert.True(wizard.HasUnresolved);
        Assert.Equal("Contoso is ready. The app registration was created.", wizard.DoneMessage);
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("not-a-guid", "11111111-1111-1111-1111-111111111111", false)]
    [InlineData(" 11111111-1111-1111-1111-111111111111 ", "22222222-2222-2222-2222-222222222222", true)]
    public void Manual_verify_needs_two_ids(string tenant, string client, bool enabled)
    {
        var wizard = new SetupWizardViewModel(new FakeConnector(), () => { }, () => { })
        {
            TenantIdInput = tenant,
            ClientIdInput = client,
        };

        Assert.Equal(enabled, wizard.VerifyManualCommand.CanExecute(null));
    }

    [Fact]
    public async Task Manual_verify_passes_trimmed_ids()
    {
        (string, string)? seen = null;
        var connector = new FakeConnector
        {
            OnManual = (t, c, _) => { seen = (t, c); return Task.FromResult(FakeConnector.Profile()); },
        };
        var wizard = new SetupWizardViewModel(connector, () => { }, () => { })
        {
            TenantIdInput = " 11111111-1111-1111-1111-111111111111",
            ClientIdInput = "22222222-2222-2222-2222-222222222222 ",
        };

        await wizard.VerifyManualCommand.ExecuteAsync(null);

        Assert.Equal(("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"), seen);
        Assert.True(wizard.IsDone);
    }

    [Fact]
    public void Missing_roles_and_licenses_gate_features_with_reasons()
    {
        var main = new MainViewModel(FakeConnector.Tenant(roles: [DirectoryRoles.HelpdeskAdministrator]));

        Assert.True(main.Sections.Single(s => s.Key == "users").IsAvailable);
        Assert.False(main.Users.PrimaryAction!.IsEnabled);
        Assert.Equal("Requires the User Administrator role", main.Users.PrimaryAction.Tip);
        var signIns = main.Access.Single(a => a.Title == Capabilities.ReadSignInLogs.Title);
        Assert.StartsWith("Requires one of these roles", signIns.State);
        Assert.Equal("Available", main.Access.Single(a => a.Title == Capabilities.ResetPasswords.Title).State);
    }

    [Fact]
    public void Manual_script_requests_every_permission_in_the_catalogue()
    {
        var script = ManualSetupScript.Generate();

        foreach (var api in TenantryPermissions.All)
        {
            Assert.Contains($"'{api.ResourceAppId}'", script);
            Assert.All(api.DelegatedScopes, s => Assert.Contains($"'{s}'", script));
        }
        Assert.Contains("AppRoleAssignmentRequired", script);
    }

    // The script is handed to admins to run, so it must at least parse. Checked with the real
    // PowerShell parser whenever pwsh is installed (developer machines and CI runners).
    [Fact]
    public async Task Manual_script_parses_in_powershell()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tenantry-setup-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(file, ManualSetupScript.Generate(), TestContext.Current.CancellationToken);
        try
        {
            // The path goes inside the command text: with -Command, trailing arguments are
            // appended to the command rather than passed as $args.
            var quoted = "'" + file.Replace("'", "''") + "'";
            var command = $"$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile({quoted},[ref]$null,[ref]$e); " +
                          "if ($e) { $e | ForEach-Object { Write-Output ($_.Extent.StartLineNumber.ToString() + ': ' + $_.Message) }; exit 1 }; exit 0";
            var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Skip("pwsh is not installed");
                return;
            }
            using (process)
            {
                var output = await process!.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.True(process.ExitCode == 0, $"Parse errors:\n{output}");
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
