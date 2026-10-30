using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>
/// The PowerShell for mail changes (addresses, forwarding, replies, mailbox settings, what's inside
/// a mailbox), the manager, signing out and OneDrive access. The parameter builders here are the
/// ones the executor sends, so the two can't drift.
/// </summary>
public sealed partial class GraphChangeExecutor
{
    static PsScript? MailScript(ChangeOp change) => change switch
    {
        SetManager { NewId: { } id } m => PsScript.Graph(Ps.Graph("PUT", $"users/{m.User.Id}/manager/$ref", new JsonObject { ["@odata.id"] = DirectoryObjects + id })),
        SetManager m => PsScript.Graph(Ps.Graph("DELETE", $"users/{m.User.Id}/manager/$ref")),
        RevokeSessions r => PsScript.Graph(Ps.Graph("POST", $"users/{r.User.Id}/revokeSignInSessions", new JsonObject())),
        SetMailAlias a => MailboxCmdlet(a.User, "Set-Mailbox", AliasParameters(a)),
        SetPrimaryAddress p => MailboxCmdlet(p.User, "Set-Mailbox", PrimaryAddressParameters(p)),
        SetForwarding f => MailboxCmdlet(f.User, "Set-Mailbox", ForwardingParameters(f)),
        SetHiddenFromAddressList h => MailboxCmdlet(h.User, "Set-Mailbox", HiddenParameters(h)),
        SetAutoReply r => PsScript.Exchange(Ps.Cmdlet("Set-MailboxAutoReplyConfiguration", AutoReplyParameters(r))),
        SetMailboxValue v => MailboxCmdlet(v.User, "Set-Mailbox", MailboxValueParameters(v)),
        SetEmailApp a => MailboxCmdlet(a.User, "Set-CASMailbox", new() { [a.Parameter] = EmailAppValue(a) }),
        SetArchive a => ArchiveCall(a) is var (cmdlet, parameters) ? MailboxCmdlet(a.User, cmdlet, parameters) : null,
        EnableAutoExpandingArchive a => MailboxCmdlet(a.User, "Enable-Mailbox", new() { ["AutoExpandingArchive"] = true },
            "This can't be turned off again once it's on."),
        SetLitigationHold h => MailboxCmdlet(h.User, "Set-Mailbox", LitigationHoldParameters(h)),
        SetAllowedSender s => MailboxCmdlet(s.User, "Set-Mailbox", AllowedSenderParameters(s)),
        SetInboxRuleEnabled r => PsScript.Exchange(Ps.Cmdlet(r.New ? "Enable-InboxRule" : "Disable-InboxRule",
            new Dictionary<string, object?> { ["Identity"] = r.RuleIdentity, ["Confirm"] = false })),
        RemoveInboxRule r => PsScript.Exchange(Ps.Cmdlet("Remove-InboxRule", new Dictionary<string, object?> { ["Identity"] = r.RuleIdentity, ["Confirm"] = false })),
        SetFolderPermission f => FolderPermissionCall(f) is var (cmdlet, parameters) ? PsScript.Exchange(Ps.Cmdlet(cmdlet, parameters)) : null,
        SetMobileDevice d => MobileDeviceCall(d) is var (cmdlet, parameters) ? PsScript.Exchange(Ps.Cmdlet(cmdlet, parameters)) : null,
        RestoreDeletedItem r => MailboxCmdlet(r.User, "Restore-RecoverableItems", new() { ["EntryID"] = r.EntryId }),
        ConvertMailbox c => MailboxCmdlet(c.User, "Set-Mailbox", ConvertParameters(c)),
        SetOneDriveAccess a => PsScript.SharePoint(SiteAdminScript(a.SiteUrl, a.Login, a.New)),
        _ => null,
    };

    /// <summary>
    /// The tenant's SetSiteAdmin, as Tenantry calls it on the admin site (PnP has no cmdlet that
    /// both adds and removes a site admin from there), through PnP's own connection.
    /// </summary>
    static string SiteAdminScript(string siteUrl, string login, bool isAdmin) => Ps.Lines(
        "$tenant = [Microsoft.Online.SharePoint.TenantAdministration.Tenant]::new((Get-PnPContext))",
        $"$tenant.SetSiteAdmin({Ps.Quote(siteUrl)}, {Ps.Quote(login)}, {Ps.Value(isAdmin)}) | Out-Null",
        "Invoke-PnPQuery");

    // ---- Parameters shared with the executor -----------------------------------------------------

    static Dictionary<string, object?> AliasParameters(SetMailAlias a) => new() { ["EmailAddresses"] = AddOrRemove(a.New, "smtp:" + a.Address) };

    static Dictionary<string, object?> PrimaryAddressParameters(SetPrimaryAddress p) => new() { ["WindowsEmailAddress"] = p.New };

    /// <summary>Forwarding is one address at a time: an SMTP address replaces any directory recipient.</summary>
    static Dictionary<string, object?> ForwardingParameters(SetForwarding f) => new()
    {
        ["ForwardingSmtpAddress"] = f.NewTo is null ? null : "smtp:" + f.NewTo,
        ["ForwardingAddress"] = null,
        ["DeliverToMailboxAndForward"] = f.NewTo is not null && f.NewKeepCopy,
    };

    static Dictionary<string, object?> HiddenParameters(SetHiddenFromAddressList h) => new() { ["HiddenFromAddressListsEnabled"] = h.New };

    static Dictionary<string, object?> MailboxValueParameters(SetMailboxValue v) => new() { [v.Parameter] = v.Value?.DeepClone() };

    /// <summary>SMTP sending's parameter says "disabled", so its value is inverted.</summary>
    static bool EmailAppValue(SetEmailApp a) => a.Parameter == "SmtpClientAuthenticationDisabled" ? !a.New : a.New;

    static (string Cmdlet, Dictionary<string, object?> Parameters) ArchiveCall(SetArchive a) => a.New
        ? ("Enable-Mailbox", new() { ["Archive"] = true })
        : ("Disable-Mailbox", new() { ["Archive"] = true, ["Confirm"] = false });

    static Dictionary<string, object?> LitigationHoldParameters(SetLitigationHold h)
    {
        var parameters = new Dictionary<string, object?> { ["LitigationHoldEnabled"] = h.NewOn };
        if (h.NewOn)
        {
            parameters["LitigationHoldDuration"] = h.NewDays is { } days ? days.ToString(System.Globalization.CultureInfo.InvariantCulture) : "Unlimited";
            parameters["RetentionComment"] = h.NewNote ?? "";
        }
        return parameters;
    }

    static Dictionary<string, object?> AllowedSenderParameters(SetAllowedSender s) =>
        new() { ["AcceptMessagesOnlyFromSendersOrMembers"] = AddOrRemove(s.New, s.Sender.Address ?? s.Sender.Name) };

    /// <summary>Messages go as HTML (line breaks kept); scheduled times as UTC.</summary>
    static Dictionary<string, object?> AutoReplyParameters(SetAutoReply change)
    {
        var reply = change.New;
        var parameters = new Dictionary<string, object?>
        {
            ["Identity"] = change.User.Address ?? change.User.Id,
            ["AutoReplyState"] = reply.State switch { AutoReplyState.On => "Enabled", AutoReplyState.Scheduled => "Scheduled", _ => "Disabled" },
            ["InternalMessage"] = Html(reply.Internal),
            ["ExternalMessage"] = Html(reply.External),
            ["ExternalAudience"] = reply.Audience.ToString(),
        };
        if (reply.State == AutoReplyState.Scheduled)
        {
            parameters["StartTime"] = reply.Start?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            parameters["EndTime"] = reply.End?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        }
        return parameters;
    }

    /// <summary>Add-, Set- or Remove-MailboxFolderPermission, depending on whether the person had an entry and keeps one.</summary>
    static (string Cmdlet, Dictionary<string, object?> Parameters) FolderPermissionCall(SetFolderPermission change)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.FolderIdentity, ["User"] = change.Person };
        if (change.New is null)
        {
            parameters["Confirm"] = false;
            return ("Remove-MailboxFolderPermission", parameters);
        }
        parameters["AccessRights"] = new[] { change.New };
        return (change.Old is null ? "Add-MailboxFolderPermission" : "Set-MailboxFolderPermission", parameters);
    }

    /// <summary>Wiping and removing go to the device partnership; blocking is the mailbox's list of blocked device IDs.</summary>
    static (string Cmdlet, Dictionary<string, object?> Parameters) MobileDeviceCall(SetMobileDevice change)
    {
        switch (change.Action)
        {
            case DeviceAction.Wipe:
            case DeviceAction.WipeAccountOnly:
            {
                var parameters = new Dictionary<string, object?> { ["Identity"] = change.DeviceIdentity, ["Confirm"] = false };
                if (change.Action == DeviceAction.WipeAccountOnly)
                    parameters["AccountOnly"] = true;
                return ("Clear-MobileDevice", parameters);
            }
            case DeviceAction.Remove:
                return ("Remove-MobileDevice", new() { ["Identity"] = change.DeviceIdentity, ["Confirm"] = false });
            default:
                return ("Set-CASMailbox", new()
                {
                    ["Identity"] = change.User.Address ?? change.User.Id,
                    ["ActiveSyncBlockedDeviceIDs"] = AddOrRemove(change.Action == DeviceAction.Block, change.DeviceId),
                });
        }
    }

    static Dictionary<string, object?> ConvertParameters(ConvertMailbox c) => new() { ["Type"] = c.NewShared ? "Shared" : "Regular" };
}
