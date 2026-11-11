using System.Text.Json;
using Tenantry.Core.Cache;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Graph;
using Tenantry.Tests.Fakes;
using Tenantry.ViewModels.MailSecurity;

namespace Tenantry.Tests;

/// <summary>Alert policies and the alerts that fired.</summary>
public sealed class AlertTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    static ChangePipeline Pipeline(bool testMode = false) => new(new Nothing(), new ChangeLog(new MemoryEntityStore()), new MemoryTestModeStore(testMode), "admin");

    [Fact]
    public void An_alert_policy_reads_in_words_without_Microsofts_version_stamp_or_hidden_spaces()
    {
        var policy = GraphUserRecords.ToAlertPolicy(Json("""
            {"Name":"Email messages removed after delivery​","Identity":"x/Email messages removed after delivery","Severity":"Informational","Category":"ThreatManagement",
             "Comment":"Malicious messages were removed after delivery. -V1.0.0.2","Disabled":false,"NotificationEnabled":true,"NotifyUser":["TenantAdmins"],"IsSystemRule":true}
            """));

        Assert.Equal(("Email messages removed after delivery", "Malicious messages were removed after delivery.", "All global admins", "Threats"),
            (policy.Name, policy.Explanation, policy.NotifyText, AlertPolicy.CategoryText(policy.Category)));
    }

    [Fact]
    public void Switching_an_alert_policy_off_stages_a_caution_and_back_on_drops_it()
    {
        var pipeline = Pipeline();
        var page = new AlertsViewModel(null, pipeline, Availability.Yes);
        var row = new AlertPolicyRow(new AlertPolicy("zz-tenantry-test-alert", "id/zz-tenantry-test-alert", "Low", "Others", null, false, true, ["TenantAdmins"], false), page);

        row.IsOn = false;
        var change = (SetAlertPolicy)Assert.Single(pipeline.Pending);
        Assert.Equal(("Disabled", true, true), (change.Parameter, change.Value!.GetValue<bool>(), change.IsCaution));
        Assert.Equal("id/zz-tenantry-test-alert", (string)GraphChangeExecutor.AlertPolicyParameters(change)["Identity"]!);
        row.IsOn = true;
        Assert.Empty(pipeline.Pending);

        row.RecipientsText = "secops@contoso.example";
        row.CommitRecipientsCommand.Execute(null);
        Assert.Equal(("All global admins", "secops@contoso.example"), (((SetAlertPolicy)Assert.Single(pipeline.Pending)).OldText, ((SetAlertPolicy)pipeline.Pending[0]).NewText));
    }

    [Fact]
    public async Task An_alert_policy_change_goes_to_Security_and_Compliance_and_is_read_back_there()
    {
        var exchange = new FakeCmdlets((cmdlet, _) => cmdlet == "Get-ProtectionAlert" ? """[{"Name":"zz-tenantry-test-alert","Disabled":true}]""" : "[]");
        var executor = new GraphChangeExecutor(new GraphClient(new FakeHttp().Client(), new FakeTokens(), delay: NoDelay.Instance), new MemoryEntityStore(), NoDelay.Instance, exchange);
        var change = new SetAlertPolicy(SetAlertPolicy.TargetFor("zz-tenantry-test-alert"), "zz-tenantry-test-alert", "Disabled", "Alert Policy", System.Text.Json.Nodes.JsonValue.Create(true), "On", "Off");

        var result = await executor.ApplyAsync(change, Ct);

        Assert.Equal(ChangeOutcome.Applied, result.Outcome);
        Assert.All(exchange.Endpoints, e => Assert.Equal(Tenantry.Core.Exchange.CmdletEndpoint.Compliance, e));
        Assert.Equal(["Set-ProtectionAlert", "Get-ProtectionAlert"], exchange.Calls.Select(c => c.Cmdlet));
    }

    [Fact]
    public void Resolving_an_alert_sends_its_classification_and_test_mode_refuses_real_alerts()
    {
        var pipeline = Pipeline();
        var page = new AlertsViewModel(null, pipeline, Availability.Yes);
        var row = new AlertRow(new SecurityAlert("a1", "Creation of forwarding/redirect rule", "informational", "new", null, "microsoftDefenderForOffice365", null, null, null, null, null, null, null, []), page);

        row.ResolveCommand.Execute("falsePositive");
        var change = (UpdateSecurityAlert)Assert.Single(pipeline.Pending);
        var body = GraphChangeExecutor.AlertUpdateBody(change);
        Assert.Equal(("resolved", "falsePositive", "notMalicious"), (body["status"]!.GetValue<string>(), body["classification"]!.GetValue<string>(), body["determination"]!.GetValue<string>()));
        Assert.Equal("Resolved as false alarm", change.After);
        Assert.NotNull(WriteGuard.Refusal(change, testMode: true));

        row.ResolveCommand.Execute("falsePositive");
        Assert.Empty(pipeline.Pending);
    }

    [Fact]
    public void Fired_alerts_name_who_they_are_about()
    {
        var alert = GraphUserRecords.ToSecurityAlert(Json("""
            {"id":"a1","title":"Suspicious email sending","severity":"medium","status":"new","serviceSource":"microsoftDefenderForOffice365","classification":"unknown",
             "evidence":[{"userAccount":{"userPrincipalName":"pat@contoso.example"}},{"primaryAddress":"pat@contoso.example"},{"primaryAddress":"shared@contoso.example"}]}
            """));

        Assert.Equal(["pat@contoso.example", "shared@contoso.example"], alert.Involved);
        Assert.Null(alert.Classification);
        Assert.Equal("Defender for Office 365", SecurityAlert.SourceText(alert.Source));
    }

    sealed class Nothing : IChangeExecutor
    {
        public Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => Task.FromResult(new ApplyResult(ChangeOutcome.Applied));
    }
}
