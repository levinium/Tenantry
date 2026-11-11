namespace Tenantry.Core.Changes;

/// <summary>
/// An alert policy switched on or off, or its recipients or email changed (Set-ProtectionAlert, on the
/// Security &amp; Compliance endpoint). The target is the policy, so Test mode allows only test-named ones.
/// </summary>
/// <param name="Parameter">"Disabled", "NotificationEnabled" or "NotifyUser".</param>
public sealed record SetAlertPolicy(ChangeTarget Policy, string Identity, string Parameter, string SettingLabel, System.Text.Json.Nodes.JsonNode? Value, string OldText, string NewText,
    bool Caution = false) : ChangeOp
{
    public static ChangeTarget TargetFor(string name) => new("alertpolicy", name, name, null);

    public override ChangeTarget Target => Policy;
    public override string Key => "alert:" + Parameter;
    public override string Label => SettingLabel;
    public override string Before => OldText;
    public override string After => NewText;
    public override bool IsCaution => Caution;
    public override bool IsNoChange => OldText == NewText;
    public override ChangeOp Since(ChangeOp earlier) => earlier is SetAlertPolicy e ? this with { OldText = e.OldText } : this;
}

/// <summary>
/// An alert worked on (Graph PATCH security/alerts_v2): its status, and when resolved, what it turned
/// out to be. The target is the alert, so Test mode refuses it (alerts are never test objects).
/// </summary>
public sealed record UpdateSecurityAlert(ChangeTarget Alert, string OldStatus, string Status, string? Classification, string? Determination, string? Comment) : ChangeOp
{
    public static ChangeTarget TargetFor(string id, string title) => new("alert", id, title, null);

    public override ChangeTarget Target => Alert;
    public override string Key => "alert";
    public override string Label => "Alert";
    public override string Before => Directory.SecurityAlert.StatusText(OldStatus);
    public override string After => Status == "resolved"
        ? $"Resolved as {Directory.SecurityAlert.ClassificationText(Classification).ToLowerInvariant()}"
        : Directory.SecurityAlert.StatusText(Status);
    public override bool IsNoChange => OldStatus == Status && Classification is null;
    public override ChangeOp Since(ChangeOp earlier) => earlier is UpdateSecurityAlert e ? this with { OldStatus = e.OldStatus } : this;
}
