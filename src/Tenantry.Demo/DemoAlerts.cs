using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's alert policies (some of Microsoft's own, and a test one) and a few alerts that fired.</summary>
public sealed partial class DemoUserRecords : IAlertRecords
{
    List<AlertPolicy>? _alertPolicies;

    List<AlertPolicy> AlertPolicyList() => _alertPolicies ??=
    [
        new("Suspicious email sending patterns detected", "Suspicious email sending patterns detected", "Medium", "ThreatManagement",
            "User has been detected as sending suspicious messages outside the organization and will be restricted if this activity continues. -V1.0.0.1", false, true, ["TenantAdmins"], true),
        new("User restricted from sending email", "User restricted from sending email", "High", "ThreatManagement",
            "User has been restricted from sending email due to suspicious activity. -V1.0.0.3", false, true, ["TenantAdmins"], true),
        new("Email messages removed after delivery", "Email messages removed after delivery", "Informational", "ThreatManagement",
            "Malicious messages were removed from inboxes after delivery. -V1.0.0.2", false, false, ["TenantAdmins"], true),
        new("Creation of forwarding/redirect rule", "Creation of forwarding/redirect rule", "Informational", "ThreatManagement",
            "Someone created an inbox rule that forwards or redirects mail. -V1.0.0.1", false, true, ["TenantAdmins"], true),
        new("Elevation of Exchange admin privilege", "Elevation of Exchange admin privilege", "Low", "PermissionsManagement",
            "Someone was given Exchange admin permissions. -V1.0.0.1", true, true, ["TenantAdmins"], true),
        new("Messages have been delayed", "Messages have been delayed", "High", "MailFlow", "Mail is queuing in Microsoft 365. -V1.0.0.1", false, true, ["TenantAdmins"], true),
        new("zz-tenantry-test-alert", "zz-tenantry-test-alert", "Low", "Others", "A test alert policy.", false, false, ["admin@northgate.example"], false),
    ];

    public Task<IReadOnlyList<AlertPolicy>> GetAlertPoliciesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AlertPolicy>>([.. AlertPolicyList()]);

    List<SecurityAlert>? _alerts;

    List<SecurityAlert> AlertList() => _alerts ??=
    [
        new("demo-a-1", "Creation of forwarding/redirect rule", "informational", "new", "ThreatManagement", "microsoftDefenderForOffice365", DateTimeOffset.UtcNow.AddHours(-2),
            DateTimeOffset.UtcNow.AddHours(-2), "An inbox rule was made that forwards mail outside the organization.", null, null, null, "https://security.microsoft.com/alerts", ["amara.hermann@northgate.example"]),
        new("demo-a-2", "Suspicious email sending patterns detected", "medium", "inProgress", "ThreatManagement", "microsoftDefenderForOffice365", DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddHours(-20), "Someone sent many messages outside the organization in a short time.", "admin@northgate.example", null, null,
            "https://security.microsoft.com/alerts", ["frontdesk@northgate.example"]),
        new("demo-a-3", "Email messages removed after delivery", "informational", "resolved", "ThreatManagement", "microsoftDefenderForOffice365", DateTimeOffset.UtcNow.AddDays(-4),
            DateTimeOffset.UtcNow.AddDays(-4), "Phishing messages were removed from 3 mailboxes after delivery.", null, "truePositive", "phishing", "https://security.microsoft.com/alerts", []),
    ];

    public Task<IReadOnlyList<SecurityAlert>> GetAlertsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SecurityAlert>>([.. AlertList()]);

    /// <summary>Applies an alert change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyAlert(ChangeOp change)
    {
        switch (change)
        {
            case SetAlertPolicy p:
                var i = AlertPolicyList().FindIndex(a => a.Identity == p.Identity);
                if (i >= 0)
                    _alertPolicies![i] = p.Parameter switch
                    {
                        "Disabled" => _alertPolicies[i] with { Disabled = p.Value!.GetValue<bool>() },
                        "NotificationEnabled" => _alertPolicies[i] with { Emails = p.Value!.GetValue<bool>() },
                        "NotifyUser" => _alertPolicies[i] with { NotifyUser = [.. p.Value!.AsArray().Select(v => v!.GetValue<string>())] },
                        _ => _alertPolicies[i],
                    };
                return new ApplyResult(ChangeOutcome.Applied);
            case UpdateSecurityAlert u:
                var at = AlertList().FindIndex(a => a.Id == u.Alert.Id);
                if (at >= 0)
                    _alerts![at] = _alerts[at] with { Status = u.Status, Classification = u.Classification ?? _alerts[at].Classification, Determination = u.Determination ?? _alerts[at].Determination };
                return new ApplyResult(ChangeOutcome.Applied);
            default:
                return null;
        }
    }
}
