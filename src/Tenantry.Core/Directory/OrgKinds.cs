using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Directory;

/// <summary>Which service an organization kind is read from and written to.</summary>
public enum OrgApi
{
    /// <summary>Microsoft Graph (paths relative to v1.0, or starting "beta/").</summary>
    Graph,

    /// <summary>The Skype and Teams Tenant Admin API (Teams policies), paths relative to its host.</summary>
    Teams,
}

/// <summary>How a kind's setting changes are sent.</summary>
public enum OrgWrite
{
    /// <summary>PATCH with the changed top-level property, re-read just before sending so other changes to it aren't undone.</summary>
    Patch,

    /// <summary>PUT of the whole object as it is now, with the change.</summary>
    Put,

    /// <summary>PUT of only the changed property, merged (the Teams admin API), with the object's Key envelope when it has one.</summary>
    MergePut,

    /// <summary>Shown, not changed here.</summary>
    ReadOnly,
}

/// <summary>How a value is kept, when it isn't plain JSON for the editor.</summary>
public enum OrgValue
{
    Plain,

    /// <summary>"true" or "false" as text (group settings), edited as a switch.</summary>
    StringBool,

    /// <summary>A list holding one choice (the user-consent policy), edited as a choice; "" means an empty list.</summary>
    FirstOfList,
}

/// <summary>
/// One setting of an organization kind: where it is in the object (dots between levels;
/// <c>values[name=X].value</c> picks from a list of name/value pairs), its words and editor.
/// </summary>
public sealed record OrgField(string Path, string Label, SettingKind Kind = SettingKind.OnOff, string? Help = null,
    IReadOnlyList<(string Value, string Label)>? Choices = null, bool ReadOnly = false, bool Inverted = false, bool Caution = false,
    int Min = 0, int Max = int.MaxValue, string? Unit = null, OrgValue Value = OrgValue.Plain, string? Group = null);

/// <summary>What New makes: a fixed body, the boxes that fill it in, and where it's sent (the kind's collection when null).</summary>
/// <param name="NameField">The box whose text names the new item (Test mode judges it).</param>
/// <param name="Put">Sent with PUT to the collection's item address for the name (Teams policies), not POST.</param>
public sealed record OrgCreate(string Key, string Title, string Description, JsonObject Fixed, IReadOnlyList<CreateField> Fields,
    string NameField = "displayName", string? Url = null, bool Put = false);

/// <summary>An action on one item (verify a domain, archive a team, restore a deleted person), sent to a path under it.</summary>
/// <param name="Path">Under the item's address ("verify"); null for the item itself (DELETE).</param>
/// <param name="When">Offered only on items this says yes to.</param>
public sealed record OrgAction(string Key, string Title, string Done, string? Path, string Method = "POST", JsonObject? Body = null, bool Caution = true,
    Func<JsonElement, bool>? When = null, string? ConfirmName = null);

/// <summary>Something read under each item and shown in words (a domain's DNS records), when the card opens.</summary>
public sealed record OrgExtra(string Title, string Path, Func<JsonElement, string?> Line);

/// <summary>
/// One kind of organization object or settings page (15c-15f): where it's read, what its settings
/// are called, how changes are sent, and what New, Delete and the item actions do. Adding one is an
/// entry in <see cref="OrgKinds"/>; the generic card page shows it.
/// </summary>
/// <param name="ListUrl">A collection, or one settings object. "{org}" is the organization's ID; "{parent}" a parent item's address.</param>
/// <param name="AutoFields">Every plain property becomes a setting (Teams policies, whose properties Microsoft adds to often).</param>
/// <param name="ItemUrl">How an item's address is made from the collection and its ID ("{list}/{id}" by default).</param>
/// <param name="DeleteUrl">Where Delete goes, when not the item's address ("groups/{id}" for a team).</param>
/// <param name="ReadOnlyWhen">Microsoft's own items: shown, not changed.</param>
/// <param name="KeepWhen">Items that can't be deleted (the Global policy, the initial domain).</param>
/// <param name="Children">A kind read under each item when its card opens (a team's channels).</param>
/// <param name="ReadEach">Each item is read again on its own: the list leaves out its settings (teams).</param>
/// <param name="Needs">Shown when Microsoft refuses the read: the license or role it takes.</param>
/// <param name="Where">Which of the collection's items belong (a shared collection like group settings).</param>
/// <param name="OneOnly">The organization has at most one: New shows only while there's none.</param>
public sealed record OrgKind(string Key, string Title, string About, string ListUrl, OrgApi Api = OrgApi.Graph, bool Single = false,
    IReadOnlyList<OrgField>? Fields = null, bool AutoFields = false, OrgWrite Write = OrgWrite.Patch, IReadOnlyList<OrgCreate>? Creates = null,
    bool CanDelete = false, string NameProperty = "displayName", string IdProperty = "id", string? ItemUrl = null, string? DeleteUrl = null,
    Func<JsonElement, bool>? ReadOnlyWhen = null, Func<JsonElement, bool>? KeepWhen = null, IReadOnlyList<OrgAction>? Actions = null,
    IReadOnlyList<OrgExtra>? Extras = null, string? Children = null, bool ReadEach = false, string Noun = "item", string? SubtitleProperty = null,
    string? Needs = null, string? ReadOnlyText = null, Func<JsonElement, string?>? NameOf = null, Func<JsonElement, bool>? Where = null, bool OneOnly = false,
    Func<JsonElement, string?>? SubtitleOf = null)
{
    public string Collection => ListUrl.Split('?')[0];

    public string AddressOf(string id, string? collection = null) =>
        (ItemUrl ?? "{list}/{id}").Replace("{list}", collection ?? Collection, StringComparison.Ordinal).Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);

    public OrgField? Field(string path) => Fields?.FirstOrDefault(f => f.Path == path);

    public bool IsReadOnly => Write == OrgWrite.ReadOnly;
}

/// <summary>An organization object as read: its ID, name, address (for changes) and every property.</summary>
public sealed record OrgObject(string Id, string Name, string Url, JsonElement Raw);

/// <summary>A kind's items as read, or why they couldn't be.</summary>
public sealed record OrgRead(IReadOnlyList<OrgObject> Items, string? Note = null);

/// <summary>Reads organization kinds (15c-15f).</summary>
public interface IOrgObjectRecords
{
    /// <param name="parentUrl">For a child kind: the parent item's address.</param>
    Task<OrgRead> GetOrgObjectsAsync(OrgKind kind, string? parentUrl = null, CancellationToken cancellationToken = default);

    /// <summary>Something under an item (a domain's DNS records), as lines; empty when it can't be read.</summary>
    Task<IReadOnlyList<string>> GetOrgExtraAsync(OrgKind kind, OrgObject item, OrgExtra extra, CancellationToken cancellationToken = default);
}

/// <summary>Reading and writing inside an object by a field's path.</summary>
public static class OrgPath
{
    static (string Name, string? Key, string? Value) Part(string segment)
    {
        var open = segment.IndexOf('[');
        if (open < 0 || !segment.EndsWith(']'))
            return (segment, null, null);
        var inside = segment[(open + 1)..^1].Split('=', 2);
        return (segment[..open], inside[0], inside.Length > 1 ? inside[1] : null);
    }

    /// <summary>The value at the path, or default when it isn't there.</summary>
    public static JsonElement Get(JsonElement root, string path)
    {
        var at = root;
        foreach (var segment in path.Split('.'))
        {
            var (name, key, value) = Part(segment);
            if (at.ValueKind == JsonValueKind.Array && int.TryParse(name, out var index))
            {
                if (index >= at.GetArrayLength())
                    return default;
                at = at[index];
                continue;
            }
            if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(name, out var next))
                return default;
            at = next;
            if (key is not null)
            {
                if (at.ValueKind != JsonValueKind.Array)
                    return default;
                var found = at.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var k)
                    && string.Equals(k.ToString(), value, StringComparison.OrdinalIgnoreCase));
                if (found.ValueKind == JsonValueKind.Undefined)
                    return default;
                at = found;
            }
        }
        return at;
    }

    /// <summary>Sets the value at the path, making objects (and a name/value pair) on the way when missing.</summary>
    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        JsonNode at = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            var (name, key, keyValue) = Part(segments[i]);
            if (at is JsonArray array && int.TryParse(name, out var index))
            {
                while (array.Count <= index)
                    array.Add(new JsonObject());
                if (last)
                {
                    array[index] = value?.DeepClone();
                    return;
                }
                at = array[index]!;
                continue;
            }
            var obj = (JsonObject)at;
            if (last && key is null)
            {
                obj[name] = value?.DeepClone();
                return;
            }
            if (key is not null)
            {
                if (obj[name] is not JsonArray list)
                    obj[name] = list = [];
                var item = list.OfType<JsonObject>().FirstOrDefault(o => string.Equals(o[key]?.ToString(), keyValue, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    item = new JsonObject { [key] = keyValue };
                    list.Add(item);
                }
                at = item;
                continue;
            }
            // The next part is a position: a list (made when missing).
            if (int.TryParse(segments[i + 1], out _))
            {
                if (obj[name] is not JsonArray items)
                    obj[name] = items = [];
                at = items;
                continue;
            }
            if (obj[name] is not JsonObject child)
                obj[name] = child = [];
            at = child;
        }
    }

    /// <summary>The top-level property a path changes.</summary>
    public static string Top(string path) => Part(path.Split('.')[0]).Name;
}

/// <summary>Every organization kind (15c-15f), with its words.</summary>
public static class OrgKinds
{
    static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    static bool Is(JsonElement e, string property, string value) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var p) && string.Equals(p.ToString(), value, StringComparison.OrdinalIgnoreCase);

    static bool True(JsonElement e, string property) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.True;

    static readonly (string, string)[] Allowed = [("allowed", "Allowed"), ("blocked", "Blocked")];

    // ---- Billing (15b) --------------------------------------------------------------------------

    static string? SkuName(JsonElement s) => Plain(s, "skuId") is { } id ? Licensing.LicenseNames.For(id, Plain(s, "skuPartNumber")) : null;

    static string? Plain(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    static int Int(JsonElement e, string path) => OrgPath.Get(e, path) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var i) ? i : 0;

    public static readonly OrgKind Licenses = new("licenses", "Licenses",
        "Each product the organization has: how many it pays for, how many are given out, and how many are free. Buy or cancel them in the admin center's Billing.",
        "subscribedSkus", Write: OrgWrite.ReadOnly, NameProperty: "skuPartNumber", Noun: "license", ReadOnlyText: "", NameOf: SkuName,
        SubtitleOf: s => $"{Int(s, "consumedUnits"):N0} of {Int(s, "prepaidUnits.enabled"):N0} given out · {Math.Max(0, Int(s, "prepaidUnits.enabled") - Int(s, "consumedUnits")):N0} free",
        Fields:
        [
            new("prepaidUnits.enabled", "Paid For", SettingKind.Text, ReadOnly: true),
            new("consumedUnits", "Given Out", SettingKind.Text, ReadOnly: true),
            new("prepaidUnits.warning", "In Grace Period", SettingKind.Text, ReadOnly: true, Help: "Not renewed: still working, but they stop at the end of the grace period."),
            new("prepaidUnits.suspended", "Suspended", SettingKind.Text, ReadOnly: true),
            new("capabilityStatus", "Status", SettingKind.Text, ReadOnly: true),
            new("appliesTo", "For", SettingKind.Text, ReadOnly: true),
            new("skuPartNumber", "Product Code", SettingKind.Text, ReadOnly: true),
        ]);

    public static readonly OrgKind Subscriptions = new("subscriptions", "Subscriptions",
        "Each subscription behind the licenses: how many it has, whether it's a trial, and when it renews or ends. Invoices and payment methods are only in the admin center (Microsoft has no API for them).",
        "directory/subscriptions", Write: OrgWrite.ReadOnly, NameProperty: "skuPartNumber", Noun: "subscription", ReadOnlyText: "", NameOf: SkuName,
        SubtitleOf: s => $"{Display(Plain(s, "status"))}{(OrgPath.Get(s, "isTrial").ValueKind == JsonValueKind.True ? " · Trial" : "")}"
            + (Plain(s, "nextLifecycleDateTime") is { } next && DateTimeOffset.TryParse(next, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
                ? $" · {(Plain(s, "status") == "Enabled" ? "renews or ends" : "next step")} {when.ToLocalTime():d MMM yyyy}" : ""),
        Fields:
        [
            new("status", "Status", SettingKind.Text, ReadOnly: true),
            new("totalLicenses", "Licenses", SettingKind.Text, ReadOnly: true),
            new("isTrial", "Trial", ReadOnly: true),
            new("createdDateTime", "Started", SettingKind.Text, ReadOnly: true),
            new("nextLifecycleDateTime", "Renews or Ends", SettingKind.Text, ReadOnly: true),
            new("ownerType", "Bought Through", SettingKind.Text, ReadOnly: true),
        ]);

    static string Display(string? status) => status switch { "Enabled" => "Active", "Warning" => "In Grace Period", "Suspended" => "Suspended", "Deleted" => "Ended", "LockedOut" => "Locked", null => "", _ => status };

    // ---- Organization settings (15c) ------------------------------------------------------------

    public static readonly OrgKind Profile = new("orgprofile", "Organization Profile",
        "Who Microsoft contacts about the organization, and its privacy statement. The name and address come from billing and change in the admin center.",
        "organization", Single: true, Noun: "organization",
        Fields:
        [
            new("displayName", "Name", SettingKind.Text, ReadOnly: true, Group: "About"),
            new("technicalNotificationMails", "Technical Contacts", SettingKind.List, "Addresses that get Microsoft's technical notices (service problems, required actions).", Group: "Contacts"),
            new("marketingNotificationEmails", "Marketing Contacts", SettingKind.List, "Addresses that get product news.", Group: "Contacts"),
            new("securityComplianceNotificationMails", "Security Contacts", SettingKind.List, "Addresses that get security and compliance notices.", Group: "Contacts"),
            new("securityComplianceNotificationPhones", "Security Phones", SettingKind.List, Group: "Contacts"),
            new("privacyProfile.contactEmail", "Privacy Contact", SettingKind.Text, "Shown to guests and in app consent.", Group: "Privacy"),
            new("privacyProfile.statementUrl", "Privacy Statement Link", SettingKind.Text, Group: "Privacy"),
            new("street", "Street", SettingKind.Text, ReadOnly: true, Group: "Address"),
            new("city", "City", SettingKind.Text, ReadOnly: true, Group: "Address"),
            new("state", "State", SettingKind.Text, ReadOnly: true, Group: "Address"),
            new("postalCode", "Postal Code", SettingKind.Text, ReadOnly: true, Group: "Address"),
            new("countryLetterCode", "Country", SettingKind.Text, ReadOnly: true, Group: "Address"),
            new("preferredLanguage", "Language", SettingKind.Text, ReadOnly: true, Group: "Address"),
            new("onPremisesSyncEnabled", "Synced from On-Premises", ReadOnly: true, Group: "Directory Sync"),
            new("onPremisesLastSyncDateTime", "Last Sync", SettingKind.Text, ReadOnly: true, Group: "Directory Sync"),
        ]);

    public static readonly OrgKind Branding = new("branding", "Sign-In Branding",
        "What the Microsoft sign-in page shows people of the organization: text, colors and help links. Pictures are changed in the Entra admin center.",
        "organization/{org}/branding", Single: true, Noun: "branding", NameOf: _ => "Sign-In Branding",
        Fields:
        [
            new("signInPageText", "Sign-In Page Text", SettingKind.LongText, "Up to 1,024 characters, shown under the sign-in box."),
            new("usernameHintText", "User Name Hint", SettingKind.Text, "The grey text in the user name box, like \"someone@example.com\"."),
            new("backgroundColor", "Background Color", SettingKind.Text, "Shown while the background picture loads, like #FFFFFF."),
            new("headerBackgroundColor", "Header Color", SettingKind.Text),
            new("customForgotMyPasswordText", "Forgot Password Text", SettingKind.Text, Group: "Help Links"),
            new("customAccountResetCredentialsUrl", "Self-Service Password Reset Link", SettingKind.Text, Group: "Help Links"),
            new("customCannotAccessYourAccountText", "Can't Access Your Account Text", SettingKind.Text, Group: "Help Links"),
            new("customCannotAccessYourAccountUrl", "Can't Access Your Account Link", SettingKind.Text, Group: "Help Links"),
            new("customPrivacyAndCookiesText", "Privacy Link Text", SettingKind.Text, Group: "Help Links"),
            new("customPrivacyAndCookiesUrl", "Privacy Link", SettingKind.Text, Group: "Help Links"),
            new("customTermsOfUseText", "Terms Link Text", SettingKind.Text, Group: "Help Links"),
            new("customTermsOfUseUrl", "Terms Link", SettingKind.Text, Group: "Help Links"),
        ]);

    public static readonly OrgKind M365Apps = new("m365apps", "Microsoft 365 Apps Installs",
        "Which desktop apps people can install themselves from the Microsoft 365 portal, and the update channel those installs use.",
        "beta/admin/microsoft365Apps/installationOptions", Single: true, Noun: "settings", NameOf: _ => "Microsoft 365 Apps Installs",
        Needs: "an Office Apps Administrator or Global Administrator role.",
        Fields:
        [
            new("updateChannel", "Update Channel", SettingKind.Choice, Choices: [("current", "Current Channel"), ("monthlyEnterprise", "Monthly Enterprise Channel"), ("semiAnnual", "Semi-Annual Enterprise Channel")]),
            new("appsForWindows.isMicrosoft365AppsEnabled", "Microsoft 365 Apps on Windows", Caution: true, Group: "Windows"),
            new("appsForWindows.isProjectEnabled", "Project on Windows", Group: "Windows"),
            new("appsForWindows.isVisioEnabled", "Visio on Windows", Group: "Windows"),
            new("appsForWindows.isSkypeForBusinessEnabled", "Skype for Business on Windows", Group: "Windows"),
            new("appsForMac.isMicrosoft365AppsEnabled", "Microsoft 365 Apps on Mac", Caution: true, Group: "Mac"),
            new("appsForMac.isSkypeForBusinessEnabled", "Skype for Business on Mac", Group: "Mac"),
        ]);

    public static readonly OrgKind Forms = new("forms", "Microsoft Forms",
        "What people can do with forms outside the organization, and whether Forms checks for phishing.",
        "beta/admin/forms/settings", Single: true, Noun: "settings", NameOf: _ => "Microsoft Forms",
        Fields:
        [
            new("isExternalSendFormEnabled", "Send Forms Outside", Help: "Share a form so people outside can fill it in."),
            new("isExternalShareCollaborationEnabled", "Collaborate with People Outside"),
            new("isExternalShareResultEnabled", "Share Results Outside"),
            new("isExternalShareTemplateEnabled", "Share Forms as Templates Outside"),
            new("isRecordIdentityByDefaultEnabled", "Record Names by Default"),
            new("isInOrgFormsPhishingScanEnabled", "Scan Forms for Phishing", Caution: true),
            new("isBingImageSearchEnabled", "Bing Image Search"),
        ]);

    public static readonly OrgKind Todo = new("todo", "Microsoft To Do",
        "Whether people can share lists outside the organization and get reminders on their phones.",
        "beta/admin/todo/settings", Single: true, Noun: "settings", NameOf: _ => "Microsoft To Do",
        Fields:
        [
            new("isExternalShareEnabled", "Share Lists Outside"),
            new("isExternalJoinEnabled", "Join Lists from Outside"),
            new("isPushNotificationEnabled", "Phone Reminders"),
        ]);

    public static readonly OrgKind ReportSettings = new("reportsettings", "Report Privacy",
        "Whether usage reports show people's names or hide them behind codes. Hidden names apply here and in the admin center.",
        "admin/reportSettings", Single: true, Noun: "settings", NameOf: _ => "Report Privacy",
        Fields: [new("displayConcealedNames", "Hide Names in Reports", Help: "People show as codes in every usage report.")]);

    public static readonly OrgKind SharePointSettings = new("spsettings", "SharePoint and OneDrive",
        "Organization-wide SharePoint and OneDrive settings: sharing outside the organization, new sites, sync and storage.",
        "admin/sharepoint/settings", Single: true, Noun: "settings", NameOf: _ => "SharePoint and OneDrive", Needs: "a SharePoint Administrator or Global Administrator role.",
        Fields:
        [
            new("sharingCapability", "Sharing Outside", SettingKind.Choice, "The most any site can share; each site can be set lower.", Caution: true, Group: "Sharing",
                Choices: [("disabled", "Only People in the Organization"), ("existingExternalUserSharingOnly", "Existing Guests"), ("externalUserSharingOnly", "New and Existing Guests"), ("externalUserAndGuestSharing", "Anyone (Links Without Sign-In)")]),
            new("isResharingByExternalUsersEnabled", "Guests Can Share Further", Group: "Sharing"),
            new("sharingDomainRestrictionMode", "Limit Sharing by Domain", SettingKind.Choice, Group: "Sharing", Choices: [("none", "No Limit"), ("allowList", "Only These Domains"), ("blockList", "All but These Domains")]),
            new("sharingAllowedDomainList", "Allowed Domains", SettingKind.List, Group: "Sharing"),
            new("sharingBlockedDomainList", "Blocked Domains", SettingKind.List, Group: "Sharing"),
            new("isSiteCreationEnabled", "People Can Create Sites", Group: "Sites"),
            new("isSitePagesCreationEnabled", "People Can Create Pages", Group: "Sites"),
            new("isCommentingOnSitePagesEnabled", "Comments on Pages", Group: "Sites"),
            new("isLoopEnabled", "Loop Components", Group: "Sites"),
            new("siteCreationDefaultStorageLimitInMB", "New Site Storage", SettingKind.Number, Unit: "MB", Group: "Sites"),
            new("personalSiteDefaultStorageLimitInMB", "OneDrive Storage", SettingKind.Number, Unit: "MB", Group: "OneDrive"),
            new("deletedUserPersonalSiteRetentionPeriodInDays", "Keep a Leaver's OneDrive For", SettingKind.Number, Min: 30, Max: 3650, Unit: "days", Group: "OneDrive"),
            new("isFileActivityNotificationEnabled", "Email About File Activity", Group: "OneDrive"),
            new("isMacSyncAppEnabled", "Sync on Mac", Group: "Sync"),
            new("isUnmanagedSyncAppForTenantRestricted", "Sync Only on Domain-Joined PCs", Group: "Sync"),
            new("isSyncButtonHiddenOnPersonalSite", "Hide the Sync Button", Group: "Sync"),
            new("isLegacyAuthProtocolsEnabled", "Older Sign-In Apps", Help: "Apps that can't use modern sign-in, which skip Conditional Access.", Group: "Security"),
            new("idleSessionSignOut.isEnabled", "Sign Out Idle Sessions", Group: "Security"),
            new("idleSessionSignOut.warnAfterInSeconds", "Warn After", SettingKind.Number, Unit: "seconds", Group: "Security"),
            new("idleSessionSignOut.signOutAfterInSeconds", "Sign Out After", SettingKind.Number, Unit: "seconds", Group: "Security"),
        ]);

    public static readonly OrgKind Pronouns = new("pronouns", "Pronouns",
        "Whether people can add their pronouns to their profile cards.",
        "admin/people/pronouns", Single: true, Noun: "settings", NameOf: _ => "Pronouns",
        Fields: [new("isEnabledInOrganization", "People Can Add Pronouns")]);

    public static readonly OrgKind ItemInsights = new("iteminsights", "Item Insights",
        "Whether Microsoft 365 suggests files and shows who works on what (Delve, the profile card's files).",
        "beta/admin/people/itemInsights", Single: true, Noun: "settings", NameOf: _ => "Item Insights",
        Fields:
        [
            new("isEnabledInOrganization", "Item Insights"),
            new("disabledForGroup", "Turned Off for the Group", SettingKind.Text, "A group's ID: its members get no insights."),
        ]);

    // ---- Identity settings (15d) ----------------------------------------------------------------

    static readonly (string, string)[] GuestRoles =
    [
        ("a0b1b346-4d3e-4e8b-98f8-753987be4970", "Same as Members"),
        ("10dae51f-b6af-4016-8d66-8c2a99b929b3", "Limited (Microsoft's Default)"),
        ("2af84b1e-32c8-42b7-82bc-daa82404023b", "Most Restricted"),
    ];

    public static readonly OrgKind UserSettings = new("usersettings", "User Settings",
        "What everyone in the organization can do without an admin role: invite guests, register apps, consent to apps, create groups and tenants.",
        "policies/authorizationPolicy", Single: true, Noun: "settings", NameOf: _ => "User Settings",
        Fields:
        [
            new("defaultUserRolePermissions.allowedToCreateApps", "Register Apps", Help: "People can make app registrations.", Group: "Everyone Can"),
            new("defaultUserRolePermissions.allowedToCreateSecurityGroups", "Create Security Groups", Group: "Everyone Can"),
            new("defaultUserRolePermissions.allowedToCreateTenants", "Create Tenants", Group: "Everyone Can"),
            new("defaultUserRolePermissions.allowedToReadOtherUsers", "See Other People", Help: "Turning this off breaks many apps; Microsoft advises leaving it on.", Caution: true, Group: "Everyone Can"),
            new("defaultUserRolePermissions.allowedToReadBitlockerKeysForOwnedDevice", "Read Their Own Devices' BitLocker Keys", Group: "Everyone Can"),
            new("allowedToUseSSPR", "Reset Their Own Password", Group: "Everyone Can"),
            new("allowedToSignUpEmailBasedSubscriptions", "Sign Up for Trials by Email", Group: "Everyone Can"),
            new("allowEmailVerifiedUsersToJoinOrganization", "Join by Verifying Email", Group: "Everyone Can"),
            new("defaultUserRolePermissions.permissionGrantPoliciesAssigned", "Consent to Apps", SettingKind.Choice, "What people may let apps reach on their behalf.", Caution: true,
                Value: OrgValue.FirstOfList, Group: "Apps",
                Choices: [("", "Never (an Admin Approves)"), ("ManagePermissionGrantsForSelf.microsoft-user-default-low", "Verified Publishers, Low-Risk Permissions"), ("ManagePermissionGrantsForSelf.microsoft-user-default-legacy", "Any App (Not Recommended)")]),
            new("allowUserConsentForRiskyApps", "Consent to Risky Apps", Caution: true, Group: "Apps"),
            new("allowInvitesFrom", "Who Can Invite Guests", SettingKind.Choice, Group: "Guests",
                Choices: [("none", "Nobody (Not Even Admins)"), ("adminsAndGuestInviters", "Admins and Guest Inviters"), ("adminsGuestInvitersAndAllMembers", "Members Too"), ("everyone", "Everyone, Guests Included")]),
            new("guestUserRoleId", "What Guests Can See", SettingKind.Choice, Group: "Guests", Choices: GuestRoles),
            new("blockMsolPowerShell", "Block the Old MSOnline PowerShell", Group: "Security"),
        ]);

    public static readonly OrgKind ExternalLeave = new("externalidentities", "External Users Leaving",
        "Whether guests can remove themselves from the organization, and what happens to their data.",
        "beta/policies/externalIdentitiesPolicy", Single: true, Noun: "settings", NameOf: _ => "External Users Leaving",
        Fields:
        [
            new("allowExternalIdentitiesToLeave", "Guests Can Leave"),
            new("allowDeletedIdentitiesDataRemoval", "Remove Their Data When They Leave"),
        ]);

    static OrgField[] CrossTenantFields(bool partner) =>
    [
        new("b2bCollaborationInbound.usersAndGroups.accessType", "Their People Can Be Guests Here", SettingKind.Choice, Choices: Allowed, Caution: true, Group: "Coming In"),
        new("b2bCollaborationInbound.applications.accessType", "Apps Their Guests Can Use", SettingKind.Choice, Choices: Allowed, Group: "Coming In"),
        new("b2bDirectConnectInbound.usersAndGroups.accessType", "Shared Channels With Them", SettingKind.Choice, Choices: Allowed, Group: "Coming In"),
        new("inboundTrust.isMfaAccepted", "Trust Their MFA", Help: "Their people's second sign-in step counts for Conditional Access here.", Group: "Trust"),
        new("inboundTrust.isCompliantDeviceAccepted", "Trust Their Compliant Devices", Group: "Trust"),
        new("inboundTrust.isHybridAzureADJoinedDeviceAccepted", "Trust Their Hybrid-Joined Devices", Group: "Trust"),
        new("b2bCollaborationOutbound.usersAndGroups.accessType", "Our People Can Be Guests There", SettingKind.Choice, Choices: Allowed, Group: "Going Out"),
        new("b2bCollaborationOutbound.applications.accessType", "Apps Our People Can Use There", SettingKind.Choice, Choices: Allowed, Group: "Going Out"),
        new("b2bDirectConnectOutbound.usersAndGroups.accessType", "Our Shared Channels There", SettingKind.Choice, Choices: Allowed, Group: "Going Out"),
        new("automaticUserConsentSettings.inboundAllowed", "Skip Their Consent Prompt (Coming In)", Group: "Consent"),
        new("automaticUserConsentSettings.outboundAllowed", "Skip Our Consent Prompt (Going Out)", Group: "Consent"),
        .. partner ? new OrgField[] { new("isServiceProvider", "A Service Provider", ReadOnly: true, Group: "About") } : [],
    ];

    public static readonly OrgKind CrossTenantDefault = new("crosstenantdefault", "Cross-Tenant Defaults",
        "How the organization works with every other Microsoft 365 organization, unless one has its own settings below.",
        "policies/crossTenantAccessPolicy/default", Single: true, Noun: "defaults", NameOf: _ => "Cross-Tenant Defaults", Fields: CrossTenantFields(false));

    public static readonly OrgKind CrossTenantPartners = new("crosstenantpartners", "Cross-Tenant Organizations",
        "Organizations with their own settings, which replace the defaults for them. Each is named by its tenant ID.",
        "policies/crossTenantAccessPolicy/partners", NameProperty: "tenantId", IdProperty: "tenantId", CanDelete: true, Noun: "organization",
        Fields: CrossTenantFields(true),
        Creates: [new("new", "Organization", "Settings for one other organization, starting from the defaults.", J("{}"), [new("tenantId", "Their Tenant ID", Placeholder: "00000000-0000-0000-0000-000000000000")], NameField: "tenantId")]);

    public static readonly OrgKind GroupSettings = new("groupsettings", "Microsoft 365 Groups",
        "Who can create Microsoft 365 groups and Teams, naming rules, guests in groups, and usage guidelines. Made when first changed.",
        "groupSettings", Noun: "settings", NameOf: _ => "Microsoft 365 Groups", OneOnly: true,
        Where: s => Is(s, "templateId", "62375ab9-6b52-47ed-826b-58e47e0e304b"),
        Fields:
        [
            new("values[name=EnableGroupCreation].value", "Everyone Can Create Groups", Help: "Off: only the group below (and admins) can make Microsoft 365 groups and Teams.", Value: OrgValue.StringBool, Caution: true, Group: "Creating Groups"),
            new("values[name=GroupCreationAllowedGroupId].value", "Who Can Create Them", SettingKind.Text, "A security group's ID, when everyone can't.", Group: "Creating Groups"),
            new("values[name=PrefixSuffixNamingRequirement].value", "Naming Rule", SettingKind.Text, "Like GRP_[GroupName]_[Department]; needs Entra ID P1.", Group: "Naming"),
            new("values[name=CustomBlockedWordsList].value", "Blocked Words", SettingKind.Text, "Separated by commas.", Group: "Naming"),
            new("values[name=AllowToAddGuests].value", "Owners Can Add Guests", Value: OrgValue.StringBool, Group: "Guests"),
            new("values[name=AllowGuestsToAccessGroups].value", "Guests Can Use Groups", Value: OrgValue.StringBool, Group: "Guests"),
            new("values[name=AllowGuestsToBeGroupOwner].value", "Guests Can Own Groups", Value: OrgValue.StringBool, Group: "Guests"),
            new("values[name=GuestUsageGuidelinesUrl].value", "Guest Guidelines Link", SettingKind.Text, Group: "Guidelines"),
            new("values[name=UsageGuidelinesUrl].value", "Usage Guidelines Link", SettingKind.Text, Group: "Guidelines"),
            new("values[name=EnableMIPLabels].value", "Sensitivity Labels on Groups", Value: OrgValue.StringBool, Group: "Labels"),
        ],
        Creates: [new("new", "Microsoft 365 Groups Settings", "Starts from Microsoft's defaults; change them on its card after.",
            J("""{"templateId":"62375ab9-6b52-47ed-826b-58e47e0e304b","values":[]}"""), [], NameField: "")]);

    public static readonly OrgKind GroupExpiry = new("groupexpiry", "Group Expiry",
        "Microsoft 365 groups that nobody renews are deleted after this long (owners are emailed first; deleted groups can be restored for 30 days).",
        "groupLifecyclePolicies", CanDelete: true, Noun: "policy", NameOf: _ => "Group Expiry", OneOnly: true,
        Fields:
        [
            new("groupLifetimeInDays", "Expire After", SettingKind.Number, Min: 30, Max: 3650, Unit: "days", Caution: true),
            new("managedGroupTypes", "Which Groups", SettingKind.Choice, Choices: [("All", "All"), ("Selected", "Chosen Groups"), ("None", "None")], Caution: true),
            new("alternateNotificationEmails", "Email When a Group Has No Owner", SettingKind.Text, "Addresses separated by semicolons."),
        ],
        Creates: [new("new", "Expiry Policy", "", J("""{"managedGroupTypes":"None"}"""),
            [new("groupLifetimeInDays", "Expire After (Days)", SettingKind.Number, Default: "365"), new("alternateNotificationEmails", "Email When No Owner", Required: false)], NameField: "")]);

    public static readonly OrgKind AdminUnits = new("adminunits", "Administrative Units",
        "Parts of the organization (a department, a site) that admins can be given roles over, instead of the whole organization.",
        "directory/administrativeUnits", CanDelete: true, Noun: "unit", SubtitleProperty: "description",
        Fields:
        [
            new("displayName", "Name", SettingKind.Text),
            new("description", "Description", SettingKind.Text),
            new("membershipType", "Members", SettingKind.Choice, Choices: [("Assigned", "Chosen"), ("Dynamic", "By Rule")]),
            new("membershipRule", "Rule", SettingKind.Text, "Like (user.department -eq \"Sales\"); needs Entra ID P1."),
            new("membershipRuleProcessingState", "Rule Running", SettingKind.Choice, Choices: [("On", "On"), ("Paused", "Paused")]),
            new("visibility", "Who Sees Members", SettingKind.Choice, Choices: [("Public", "Everyone"), ("HiddenMembership", "Only Members and Admins")], ReadOnly: true),
            new("isMemberManagementRestricted", "Restricted Management", ReadOnly: true),
        ],
        Extras: [new("Members", "members?$select=displayName,userPrincipalName", m => (m.TryGetProperty("displayName", out var n) ? n.GetString() : null) is { } name
            ? name + (m.TryGetProperty("userPrincipalName", out var u) && u.ValueKind == JsonValueKind.String ? $" ({u.GetString()})" : "") : null)],
        Creates: [new("new", "Administrative Unit", "", J("{}"), [new("displayName", "Name"), new("description", "Description", Required: false)])]);

    static string? DnsLine(JsonElement r)
    {
        string? S(string p) => r.TryGetProperty(p, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
        var kind = S("recordType") ?? "";
        var value = S("text") ?? S("mailExchange") ?? S("canonicalName") ?? S("nameTarget") ?? "";
        var label = S("label") ?? "";
        var service = S("supportedService");
        return $"{kind.ToUpperInvariant()} {label} → {value}{(S("preference") is { } p && kind == "Mx" ? $" (priority {p})" : "")}{(service is { Length: > 0 } ? $" · {service}" : "")}";
    }

    public static readonly OrgKind Domains = new("domains", "Domains",
        "The organization's domains: verification, the default for new people, password expiry, and the DNS records each needs.",
        "domains", NameProperty: "id", CanDelete: true, Noun: "domain",
        KeepWhen: d => True(d, "isInitial") || True(d, "isDefault"),
        Fields:
        [
            new("isVerified", "Verified", ReadOnly: true),
            new("isDefault", "Default for New People", Caution: true, Help: "Only one domain is the default; choosing this one moves it."),
            new("passwordValidityPeriodInDays", "Passwords Expire After", SettingKind.Number, "2147483647 means never (Microsoft's recommendation).", Min: 14, Max: int.MaxValue, Unit: "days"),
            new("passwordNotificationWindowInDays", "Warn Before Expiry", SettingKind.Number, Min: 1, Max: 30, Unit: "days"),
            new("authenticationType", "Sign-In", SettingKind.Text, ReadOnly: true),
            new("supportedServices", "Used For", SettingKind.List, ReadOnly: true),
            new("isInitial", "The Initial onmicrosoft.com Domain", ReadOnly: true),
        ],
        Extras:
        [
            new("Records to Verify It", "verificationDnsRecords", DnsLine),
            new("Records Its Services Need", "serviceConfigurationRecords", DnsLine),
        ],
        Actions: [new("verify", "Verify", "Checks its DNS for the verification record and verifies it.", "verify", Caution: false, When: d => !True(d, "isVerified"))],
        Creates: [new("new", "Domain", "Add it, then put its verification record in DNS and Verify.", J("{}"), [new("id", "Domain", Placeholder: "example.com")], NameField: "id")]);

    public static readonly OrgKind DirectorySync = new("dirsync", "Entra Connect Sync",
        "How on-premises Active Directory syncs to Entra: what's on and the safety limit on deletions. Changed here only for synced organizations.",
        "directory/onPremisesSynchronization", Noun: "settings", NameOf: _ => "Entra Connect Sync", Needs: "a Hybrid Identity Administrator or Global Administrator role.",
        Fields:
        [
            new("configuration.accidentalDeletionPrevention.synchronizationPreventionType", "Stop Mass Deletions", SettingKind.Choice, Caution: true,
                Choices: [("disabled", "Off"), ("enabledForCount", "Above a Number")]),
            new("configuration.accidentalDeletionPrevention.alertThreshold", "Most Deletions in One Sync", SettingKind.Number, Min: 1, Max: 100000),
            new("features.passwordSyncEnabled", "Password Hash Sync", ReadOnly: true, Group: "Features"),
            new("features.passwordWritebackEnabled", "Password Writeback", ReadOnly: true, Group: "Features"),
            new("features.deviceWritebackEnabled", "Device Writeback", ReadOnly: true, Group: "Features"),
            new("features.groupWriteBackEnabled", "Group Writeback", ReadOnly: true, Group: "Features"),
            new("features.blockSoftMatchEnabled", "Block Soft Matching", Group: "Features"),
            new("features.blockCloudObjectTakeoverThroughHardMatchEnabled", "Block Takeover by Hard Matching", Group: "Features"),
            new("features.userWritebackEnabled", "User Writeback", ReadOnly: true, Group: "Features"),
        ]);

    public static readonly OrgKind AttributeSets = new("attributesets", "Custom Security Attribute Sets",
        "Groups of custom security attributes, which only Attribute Administrators can read or set (Global Administrators can't by default).",
        "directory/attributeSets", NameProperty: "id", Noun: "attribute set", SubtitleProperty: "description",
        Needs: "an Attribute Definition Administrator role (Global Administrator isn't enough).",
        Fields: [new("description", "Description", SettingKind.Text), new("maxAttributesPerSet", "Most Attributes", SettingKind.Number, Min: 1, Max: 500)],
        Creates: [new("new", "Attribute Set", "", J("{}"), [new("id", "Name", Placeholder: "Letters and numbers, no spaces"), new("description", "Description", Required: false)], NameField: "id")]);

    public static readonly OrgKind AttributeDefinitions = new("attributedefs", "Custom Security Attributes",
        "Attributes only Attribute Administrators can set on people and apps (cost centers, clearance levels), usable in Conditional Access filters.",
        "directory/customSecurityAttributeDefinitions", NameProperty: "id", Noun: "attribute", SubtitleProperty: "description",
        Needs: "an Attribute Definition Administrator role (Global Administrator isn't enough).",
        Fields:
        [
            new("description", "Description", SettingKind.Text),
            new("status", "Status", SettingKind.Choice, Choices: [("Available", "Available"), ("Deprecated", "Retired")], Help: "A retired attribute can't be set any more; attributes can't be deleted."),
            new("type", "Type", SettingKind.Text, ReadOnly: true),
            new("isCollection", "Several Values", ReadOnly: true),
            new("usePreDefinedValuesOnly", "Only Listed Values"),
        ],
        Creates: [new("new", "Attribute", "", J("""{"isCollection":false,"isSearchable":true,"status":"Available","usePreDefinedValuesOnly":false}"""),
            [new("attributeSet", "Attribute Set"), new("name", "Name"), new("type", "Type", SettingKind.Choice, Choices: [("String", "Text"), ("Integer", "Number"), ("Boolean", "Yes or No")], Default: "String"),
             new("description", "Description", Required: false)], NameField: "name")]);

    public static readonly OrgKind DeletedUsers = new("deletedusers", "Deleted Users",
        "People deleted in the last 30 days, with their licenses, mail and OneDrive still held. Restore brings them back as they were.",
        "directory/deletedItems/microsoft.graph.user?$select=id,displayName,userPrincipalName,deletedDateTime,jobTitle,department", Write: OrgWrite.ReadOnly,
        ItemUrl: "directory/deletedItems/{id}", CanDelete: true, Noun: "deleted person", SubtitleProperty: "userPrincipalName", ReadOnlyText: "",
        NameOf: u => u.TryGetProperty("userPrincipalName", out var upn) && upn.ValueKind == JsonValueKind.String ? upn.GetString() : null,
        Fields:
        [
            new("displayName", "Name", SettingKind.Text, ReadOnly: true),
            new("deletedDateTime", "Deleted", SettingKind.Text, ReadOnly: true),
            new("jobTitle", "Job Title", SettingKind.Text, ReadOnly: true),
            new("department", "Department", SettingKind.Text, ReadOnly: true),
        ],
        Actions: [new("restore", "Restore", "Brings the person back with their licenses, groups and data.", "restore", Caution: false)]);

    public static readonly OrgKind AdminConsent = new("adminconsent", "Admin Consent Requests",
        "Whether people can ask an admin to approve an app they aren't allowed to consent to, and who reviews the requests.",
        "policies/adminConsentRequestPolicy", Single: true, Write: OrgWrite.Put, Noun: "settings", NameOf: _ => "Admin Consent Requests",
        Fields:
        [
            new("isEnabled", "People Can Ask", Caution: true),
            new("notifyReviewers", "Email Reviewers"),
            new("remindersEnabled", "Remind Reviewers"),
            new("requestDurationInDays", "Requests Expire After", SettingKind.Number, Min: 1, Max: 30, Unit: "days"),
        ]);

    public static readonly OrgKind ConsentRequests = new("consentrequests", "Apps People Asked For",
        "Apps people asked an admin to approve. Approve or deny them in the Entra admin center's admin consent requests.",
        "identityGovernance/appConsent/appConsentRequests", NameProperty: "appDisplayName", Write: OrgWrite.ReadOnly, Noun: "request", ReadOnlyText: "",
        Fields: [new("appId", "App ID", SettingKind.Text, ReadOnly: true), new("pendingScopes", "Permissions Asked For", SettingKind.List, ReadOnly: true)]);

    public static readonly OrgKind StagedRollout = new("stagedrollout", "Staged Rollout",
        "Moves chosen groups to cloud sign-in (password hash sync, pass-through) before the whole organization, for hybrid organizations.",
        "policies/featureRolloutPolicies", CanDelete: true, Noun: "rollout",
        Fields:
        [
            new("feature", "Feature", SettingKind.Text, ReadOnly: true),
            new("isEnabled", "On", Caution: true),
            new("isAppliedToOrganization", "Whole Organization", ReadOnly: true),
            new("description", "Description", SettingKind.Text),
        ],
        Creates: [new("new", "Staged Rollout", "Starts off; add groups in the Entra admin center.", J("""{"isEnabled":false,"isAppliedToOrganization":false}"""),
            [new("displayName", "Name"), new("feature", "Feature", SettingKind.Choice, Default: "passwordHashSync",
                Choices: [("passwordHashSync", "Password Hash Sync"), ("passthroughAuthentication", "Pass-Through Authentication"), ("seamlessSso", "Seamless Single Sign-On"), ("certificateBasedAuthentication", "Certificate-Based Authentication"), ("multiFactorAuthentication", "Cloud MFA")])])]);

    public static readonly OrgKind SignOutPolicies = new("idlepolicies", "Idle Sign-Out",
        "Signs people out of web apps after a time without activity (an activity-based timeout policy).",
        "policies/activityBasedTimeoutPolicies", CanDelete: true, Noun: "policy", OneOnly: true,
        Fields: [new("definition.0", "Definition", SettingKind.LongText, "Microsoft's JSON: WebSessionIdleTimeout is hours:minutes:seconds."), new("isOrganizationDefault", "Organization Default", ReadOnly: true)],
        Creates: [new("new", "Idle Sign-Out Policy", "One per organization.", J("""{"isOrganizationDefault":true,"definition":[""]}"""),
            [new("displayName", "Name"), new("definition.0", "Definition", SettingKind.LongText,
                Default: """{"ActivityBasedTimeoutPolicy":{"Version":1,"ApplicationPolicies":[{"ApplicationId":"default","WebSessionIdleTimeout":"01:00:00"}]}}""")])]);

    public static readonly OrgKind TermsOfUse = new("termsofuse", "Terms of Use",
        "Terms people must accept before signing in to chosen apps, required through Conditional Access. New terms need a PDF, added in the Entra admin center.",
        "identityGovernance/termsOfUse/agreements", CanDelete: true, Noun: "terms",
        Fields:
        [
            new("isViewingBeforeAcceptanceRequired", "Must Open Before Accepting"),
            new("isPerDeviceAcceptanceRequired", "Accept on Each Device"),
            new("userReacceptRequiredFrequency", "Accept Again Every", SettingKind.Text, "Like P90D for 90 days; empty for never."),
        ]);

    // ---- The rest (15f) -------------------------------------------------------------------------

    const string Governance = "Microsoft Entra ID P2 or Entra ID Governance, and an Identity Governance Administrator role.";

    public static readonly OrgKind AccessReviews = new("accessreviews", "Access Reviews",
        "Regular checks where owners or managers confirm who still needs a group, app or role. Create them in the Entra admin center.",
        "identityGovernance/accessReviews/definitions", CanDelete: true, Noun: "review", Needs: Governance, SubtitleProperty: "status",
        Fields: [new("status", "Status", SettingKind.Text, ReadOnly: true), new("descriptionForAdmins", "Description", SettingKind.Text, ReadOnly: true)], Write: OrgWrite.ReadOnly, ReadOnlyText: "");

    public static readonly OrgKind AccessPackages = new("accesspackages", "Access Packages",
        "Bundles of groups, apps and sites people can request, with approval and expiry.",
        "identityGovernance/entitlementManagement/accessPackages", CanDelete: true, Noun: "package", Needs: Governance, SubtitleProperty: "description",
        Fields: [new("displayName", "Name", SettingKind.Text), new("description", "Description", SettingKind.Text), new("isHidden", "Hidden from Requests")]);

    public static readonly OrgKind LifecycleWorkflows = new("lifecycleworkflows", "Lifecycle Workflows",
        "Automatic joiner, mover and leaver tasks (welcome emails, removing access on the last day).",
        "identityGovernance/lifecycleWorkflows/workflows", CanDelete: true, Noun: "workflow", Needs: Governance, SubtitleProperty: "category",
        Fields: [new("isEnabled", "On", Caution: true), new("isSchedulingEnabled", "Runs on Schedule"), new("description", "Description", SettingKind.Text)]);

    public static readonly OrgKind Recommendations = new("recommendations", "Entra Recommendations",
        "Microsoft's suggestions for the organization's sign-in and app security, with how far along each is.",
        "beta/directory/recommendations", Write: OrgWrite.ReadOnly, Noun: "recommendation", SubtitleProperty: "status", ReadOnlyText: "",
        Fields:
        [
            new("status", "Status", SettingKind.Text, ReadOnly: true), new("priority", "Priority", SettingKind.Text, ReadOnly: true),
            new("benefits", "Why", SettingKind.Text, ReadOnly: true), new("currentScore", "Score", SettingKind.Text, ReadOnly: true), new("maxScore", "Out Of", SettingKind.Text, ReadOnly: true),
        ],
        Actions:
        [
            new("complete", "Mark Done", "Marks it complete.", "complete", When: r => !Is(r, "status", "completedByUser") && !Is(r, "status", "completedBySystem")),
            new("postpone", "Postpone a Month", "Hides it for 30 days.", "postpone", Body: J("""{"postponeUntilDateTime":"{month}"}"""), When: r => Is(r, "status", "active")),
            new("dismiss", "Dismiss", "Dismisses it.", "dismiss", Body: J("""{"dismissReason":"Not relevant for this organization."}"""), When: r => Is(r, "status", "active")),
            new("reactivate", "Reactivate", "Shows it as active again.", "reactivate", When: r => !Is(r, "status", "active")),
        ]);

    public static readonly OrgKind IeModeLists = new("iemode", "Edge Internet Explorer Mode",
        "Site lists that make Edge open old sites in Internet Explorer mode for everyone.",
        "beta/admin/edge/internetExplorerMode/siteLists", CanDelete: true, Noun: "site list", SubtitleProperty: "status",
        Fields: [new("displayName", "Name", SettingKind.Text), new("description", "Description", SettingKind.Text), new("status", "Status", SettingKind.Text, ReadOnly: true)],
        Creates: [new("new", "Site List", "", J("{}"), [new("displayName", "Name"), new("description", "Description", Required: false)])]);

    public static readonly OrgKind Autopatch = new("autopatch", "Windows Autopatch Deployments",
        "Windows updates Microsoft deploys for the organization through Windows Autopatch.",
        "beta/admin/windows/updates/deployments", Write: OrgWrite.ReadOnly, Noun: "deployment", ReadOnlyText: "", Needs: "Windows Autopatch (Windows Enterprise E3 or higher) and an Intune role.",
        NameOf: d => OrgPath.Get(d, "content.catalogEntry.displayName") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null,
        Fields: [new("state.effectiveValue", "State", SettingKind.Text, ReadOnly: true), new("createdDateTime", "Made", SettingKind.Text, ReadOnly: true)]);

    public static readonly OrgKind Rooms = new("rooms", "Rooms",
        "Meeting rooms people can book, with their capacity and equipment (change them on the room's mailbox).",
        "places/microsoft.graph.room", Write: OrgWrite.ReadOnly, Noun: "room", SubtitleProperty: "emailAddress", ReadOnlyText: "",
        Fields:
        [
            new("capacity", "Seats", SettingKind.Text, ReadOnly: true), new("building", "Building", SettingKind.Text, ReadOnly: true), new("floorNumber", "Floor", SettingKind.Text, ReadOnly: true),
            new("audioDeviceName", "Audio", SettingKind.Text, ReadOnly: true), new("videoDeviceName", "Video", SettingKind.Text, ReadOnly: true), new("isWheelChairAccessible", "Wheelchair Accessible", ReadOnly: true),
        ]);

    // ---- Teams (15e) ----------------------------------------------------------------------------

    public static readonly OrgKind Teams = new("teams", "Teams",
        "Every team, with its settings for members, guests, messages and fun. Members and owners are changed on the team's group.",
        "teams", ReadEach: true, CanDelete: true, DeleteUrl: "groups/{id}", Noun: "team", SubtitleProperty: "description", Children: "channels",
        Fields:
        [
            new("description", "Description", SettingKind.Text, Group: "About"),
            new("visibility", "Privacy", SettingKind.Choice, Choices: [("private", "Private"), ("public", "Public")], ReadOnly: true, Group: "About"),
            new("isArchived", "Archived", ReadOnly: true, Group: "About"),
            new("memberSettings.allowCreateUpdateChannels", "Members Can Make Channels", Group: "Members"),
            new("memberSettings.allowCreatePrivateChannels", "Members Can Make Private Channels", Group: "Members"),
            new("memberSettings.allowDeleteChannels", "Members Can Delete Channels", Group: "Members"),
            new("memberSettings.allowAddRemoveApps", "Members Can Add Apps", Group: "Members"),
            new("memberSettings.allowCreateUpdateRemoveTabs", "Members Can Change Tabs", Group: "Members"),
            new("guestSettings.allowCreateUpdateChannels", "Guests Can Make Channels", Group: "Guests"),
            new("guestSettings.allowDeleteChannels", "Guests Can Delete Channels", Group: "Guests"),
            new("messagingSettings.allowUserEditMessages", "Edit Sent Messages", Group: "Messages"),
            new("messagingSettings.allowUserDeleteMessages", "Delete Sent Messages", Group: "Messages"),
            new("messagingSettings.allowOwnerDeleteMessages", "Owners Can Delete Messages", Group: "Messages"),
            new("messagingSettings.allowTeamMentions", "@Team Mentions", Group: "Messages"),
            new("messagingSettings.allowChannelMentions", "@Channel Mentions", Group: "Messages"),
            new("funSettings.allowGiphy", "GIFs", Group: "Fun"),
            new("funSettings.giphyContentRating", "GIF Rating", SettingKind.Choice, Choices: [("moderate", "Moderate"), ("strict", "Strict")], Group: "Fun"),
            new("funSettings.allowStickersAndMemes", "Stickers and Memes", Group: "Fun"),
            new("funSettings.allowCustomMemes", "Custom Memes", Group: "Fun"),
        ],
        Actions:
        [
            new("archive", "Archive", "Makes the team read-only; it can be brought back.", "archive", When: t => !True(t, "isArchived")),
            new("unarchive", "Unarchive", "Makes the team active again.", "unarchive", Caution: false, When: t => True(t, "isArchived")),
        ],
        Creates: [new("new", "Team", "A new team with a new Microsoft 365 group; you're its first owner. Teams takes a minute or two to make it.",
            J("""{"template@odata.bind":"https://graph.microsoft.com/v1.0/teamsTemplates('standard')"}"""),
            [new("displayName", "Name"), new("description", "Description", Required: false), new("visibility", "Privacy", SettingKind.Choice, Choices: [("private", "Private"), ("public", "Public")], Default: "private")])]);

    public static readonly OrgKind Channels = new("channels", "Channels",
        "The team's channels.", "{parent}/channels", CanDelete: true, Noun: "channel", SubtitleProperty: "membershipType",
        KeepWhen: c => Is(c, "displayName", "General"),
        Fields: [new("displayName", "Name", SettingKind.Text), new("description", "Description", SettingKind.Text), new("membershipType", "Kind", SettingKind.Text, ReadOnly: true)],
        Creates: [new("new", "Channel", "A standard channel everyone in the team sees.", J("""{"membershipType":"standard"}"""), [new("displayName", "Name"), new("description", "Description", Required: false)])]);

    public static readonly OrgKind TeamsAppSettings = new("teamsappsettings", "Teams App Settings",
        "Whether apps can be given access to one person's data or one chat, by the person or chat members themselves.",
        "beta/teamwork/teamsAppSettings", Single: true, Noun: "settings", NameOf: _ => "Teams App Settings",
        Fields:
        [
            new("isUserPersonalScopeResourceSpecificConsentEnabled", "People Can Let Apps Read Their Data"),
            new("isChatResourceSpecificConsentEnabled", "Chat Members Can Let Apps Into Chats"),
        ]);

    public static readonly OrgKind TeamsOrgApps = new("teamsorgapps", "Your Organization's Teams Apps",
        "Teams apps the organization made or uploaded for itself.",
        "appCatalogs/teamsApps?$filter=distributionMethod eq 'organization'", CanDelete: true, Write: OrgWrite.ReadOnly, Noun: "app", ReadOnlyText: "",
        Fields: [new("externalId", "App ID", SettingKind.Text, ReadOnly: true)]);

    static readonly HashSet<string> TeamsPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tag:AllOn", "Tag:AllOff", "Tag:RestrictedAnonymousAccess", "Tag:RestrictedAnonymousNoRecording", "Tag:Kiosk", "Tag:Default", "Tag:FirstLineWorker",
    };

    static bool IsPreset(JsonElement p) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Identity", out var id) && id.GetString() is { } name
        && (TeamsPresets.Contains(name) || name.StartsWith("Tag:Education_", StringComparison.OrdinalIgnoreCase));

    static OrgKind TeamsPolicy(string key, string type, string title, string about, bool single = false) => new(key, title, about,
        $"Skype.Policy/configurations/{type}", OrgApi.Teams, Single: single, AutoFields: true, Write: OrgWrite.MergePut, CanDelete: !single,
        NameProperty: "Identity", IdProperty: "Identity", ItemUrl: "{list}/configuration/{id}", Noun: single ? "settings" : "policy",
        ReadOnlyWhen: IsPreset, KeepWhen: p => Is(p, "Identity", "Global") || IsPreset(p), ReadOnlyText: "Microsoft's preset: shown, not changed.",
        NameOf: p => p.TryGetProperty("Identity", out var id) && id.GetString() is { } name ? name == "Global" ? "Global (Everyone Else)" : name.StartsWith("Tag:", StringComparison.Ordinal) ? name[4..] : name : null,
        Needs: "the Skype and Teams Tenant Admin API permission (grant it from Settings) and a Teams Administrator role.",
        Creates: single ? null : [new("new", title.TrimEnd('s') is var one && one.EndsWith("ie", StringComparison.Ordinal) ? one[..^2] + "y" : one,
            "Starts as Microsoft's defaults; assign it to people in the Teams admin center.", J("{}"), [new("Identity", "Name")], NameField: "Identity", Put: true)]);

    public static readonly OrgKind MeetingPolicies = TeamsPolicy("teams-meeting", "TeamsMeetingPolicy", "Meeting Policies",
        "What people can do in meetings they organize: recording, transcription, lobby, who can present.");
    public static readonly OrgKind MessagingPolicies = TeamsPolicy("teams-messaging", "TeamsMessagingPolicy", "Messaging Policies",
        "What people can do in chats and channel messages: edit and delete, read receipts, GIFs, translation.");
    public static readonly OrgKind AppPermissionPolicies = TeamsPolicy("teams-apppermission", "TeamsAppPermissionPolicy", "App Permission Policies",
        "Which Teams apps people can use: Microsoft's, other companies', the organization's own.");
    public static readonly OrgKind AppSetupPolicies = TeamsPolicy("teams-appsetup", "TeamsAppSetupPolicy", "App Setup Policies",
        "Which apps are pinned and installed for people, and whether they can upload their own.");
    public static readonly OrgKind CallingPolicies = TeamsPolicy("teams-calling", "TeamsCallingPolicy", "Calling Policies",
        "Private calls, call forwarding, voicemail and call groups.");
    public static readonly OrgKind EventsPolicies = TeamsPolicy("teams-events", "TeamsEventsPolicy", "Events Policies",
        "Webinars and town halls: who can make them and who can attend.");
    public static readonly OrgKind LiveEventsPolicies = TeamsPolicy("teams-broadcast", "TeamsMeetingBroadcastPolicy", "Live Events Policies",
        "Live events (being replaced by town halls): scheduling, recording and who can join.");
    public static readonly OrgKind UpdatePolicies = TeamsPolicy("teams-update", "TeamsUpdateManagementPolicy", "Update Policies",
        "Whether people get Teams preview features early.");
    public static readonly OrgKind ChannelsPolicies = TeamsPolicy("teams-channels", "TeamsChannelsPolicy", "Teams Policies",
        "Whether people can make private and shared channels and join outside shared channels.");
    public static readonly OrgKind TeamsClient = TeamsPolicy("teams-client", "TeamsClientConfiguration", "Teams Settings",
        "Organization-wide Teams settings: email into channels, cloud storage choices, and the organization tab.", single: true);
    public static readonly OrgKind GuestMeetings = TeamsPolicy("teams-guestmeeting", "TeamsGuestMeetingConfiguration", "Guest Meetings",
        "What guests can do in meetings.", single: true);
    public static readonly OrgKind GuestMessaging = TeamsPolicy("teams-guestmessaging", "TeamsGuestMessagingConfiguration", "Guest Messaging",
        "What guests can do in chats and channels.", single: true);
    public static readonly OrgKind GuestCalling = TeamsPolicy("teams-guestcalling", "TeamsGuestCallingConfiguration", "Guest Calling",
        "Whether guests can make private calls.", single: true);

    /// <summary>Every kind.</summary>
    public static IReadOnlyList<OrgKind> All { get; } =
    [
        Licenses, Subscriptions, Profile, Branding, M365Apps, Forms, Todo, ReportSettings, SharePointSettings, Pronouns, ItemInsights,
        UserSettings, ExternalLeave, CrossTenantDefault, CrossTenantPartners, GroupSettings, GroupExpiry, AdminUnits, Domains, DirectorySync,
        AttributeSets, AttributeDefinitions, DeletedUsers, AdminConsent, ConsentRequests, StagedRollout, SignOutPolicies, TermsOfUse,
        AccessReviews, AccessPackages, LifecycleWorkflows, Recommendations, IeModeLists, Autopatch, Rooms,
        Teams, Channels, TeamsAppSettings, TeamsOrgApps, MeetingPolicies, MessagingPolicies, AppPermissionPolicies, AppSetupPolicies, CallingPolicies,
        EventsPolicies, LiveEventsPolicies, UpdatePolicies, ChannelsPolicies, TeamsClient, GuestMeetings, GuestMessaging, GuestCalling,
    ];

    public static OrgKind Get(string key) => All.Single(k => k.Key == key);

    /// <summary>The Organization area's card pages and the kinds each shows, top to bottom.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<OrgKind>> Pages { get; } = new Dictionary<string, IReadOnlyList<OrgKind>>
    {
        ["licenses"] = [Licenses],
        ["subscriptions"] = [Subscriptions],
        ["profile"] = [Profile, Branding],
        ["apps"] = [M365Apps, Forms, Todo, Pronouns, ItemInsights],
        ["spsettings"] = [SharePointSettings],
        ["reportprivacy"] = [ReportSettings],
        ["usersettings"] = [UserSettings, ExternalLeave],
        ["crosstenant"] = [CrossTenantDefault, CrossTenantPartners],
        ["groupsettings"] = [GroupSettings, GroupExpiry],
        ["adminunits"] = [AdminUnits],
        ["domains"] = [Domains],
        ["dirsync"] = [DirectorySync, StagedRollout],
        ["attributes"] = [AttributeSets, AttributeDefinitions],
        ["deletedusers"] = [DeletedUsers],
        ["consent"] = [AdminConsent, ConsentRequests],
        ["signinpolicies"] = [SignOutPolicies, TermsOfUse],
        ["governance"] = [AccessReviews, AccessPackages, LifecycleWorkflows],
        ["recommendations"] = [Recommendations],
        ["iemode"] = [IeModeLists],
        ["autopatch"] = [Autopatch],
        ["rooms"] = [Rooms],
        ["teams"] = [Teams],
        ["teamsapps"] = [TeamsAppSettings, TeamsOrgApps, AppPermissionPolicies, AppSetupPolicies],
        ["teamsmeetings"] = [MeetingPolicies, EventsPolicies, LiveEventsPolicies],
        ["teamsmessaging"] = [MessagingPolicies, ChannelsPolicies],
        ["teamscalling"] = [CallingPolicies],
        ["teamsorg"] = [TeamsClient, GuestMeetings, GuestMessaging, GuestCalling, UpdatePolicies],
    };
}
