using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>The demo's organization settings, domains, teams and policies (15c-15f), in memory.</summary>
public sealed partial class DemoUserRecords : IOrgObjectRecords, IUsageReportRecords
{
    public Task<UsageReportRead> GetUsageReportAsync(UsageReport report, UsagePeriod period, CancellationToken cancellationToken = default)
    {
        var people = new[] { ("Avery Quinn", "avery.quinn"), ("Rowan Blake", "rowan.blake"), ("Jordan Ellis", "jordan.ellis"), ("Casey Morgan", "casey.morgan"), ("Riley Park", "riley.park") };
        var today = DateTime.UtcNow.Date;
        var rows = people.Select((p, i) => JsonDocument.Parse(new JsonObject
        {
            ["reportRefreshDate"] = today.ToString("yyyy-MM-dd"),
            ["userPrincipalName"] = $"{p.Item2}@northgate.example",
            ["displayName"] = p.Item1,
            ["isDeleted"] = false,
            ["lastActivityDate"] = today.AddDays(-i * 3).ToString("yyyy-MM-dd"),
            ["sendCount"] = 40 - i * 7,
            ["receiveCount"] = 210 - i * 30,
            ["readCount"] = 180 - i * 25,
            ["assignedProducts"] = new JsonArray("Microsoft 365 Business Premium"),
            ["reportPeriod"] = period.Value[1..],
        }.ToJsonString()).RootElement.Clone()).ToArray();
        return Task.FromResult(new UsageReportRead(rows));
    }

    Dictionary<string, List<JsonObject>>? _orgItems;

    static JsonObject O(string json) => JsonNode.Parse(json)!.AsObject();

    Dictionary<string, List<JsonObject>> OrgItems()
    {
        if (_orgItems is not null)
            return _orgItems;
        var past = DateTimeOffset.UtcNow.AddDays(-12).ToString("yyyy-MM-ddTHH:mm:ssZ");
        _orgItems = new Dictionary<string, List<JsonObject>>
        {
            ["licenses"] = [O("""{"id":"demo-sku-1","skuId":"cbdc14ab-d96c-4c30-b9f4-6ada7cdc1d46","skuPartNumber":"SPB","consumedUnits":46,"prepaidUnits":{"enabled":50,"warning":0,"suspended":0},"capabilityStatus":"Enabled","appliesTo":"User"}"""),
                O("""{"id":"demo-sku-2","skuId":"f30db892-07e9-47e9-837c-80727f46fd3d","skuPartNumber":"FLOW_FREE","consumedUnits":12,"prepaidUnits":{"enabled":10000,"warning":0,"suspended":0},"capabilityStatus":"Enabled","appliesTo":"User"}""")],
            ["subscriptions"] = [O($$"""{"id":"demo-sub-1","skuId":"cbdc14ab-d96c-4c30-b9f4-6ada7cdc1d46","skuPartNumber":"SPB","status":"Enabled","totalLicenses":50,"isTrial":false,"createdDateTime":"2024-03-01T00:00:00Z","nextLifecycleDateTime":"{{DateTimeOffset.UtcNow.AddDays(110):yyyy-MM-ddT00:00:00Z}}","ownerType":"Company"}"""),
                O($$"""{"id":"demo-sub-2","skuId":"f30db892-07e9-47e9-837c-80727f46fd3d","skuPartNumber":"FLOW_FREE","status":"Warning","totalLicenses":10000,"isTrial":true,"createdDateTime":"2026-09-01T00:00:00Z","nextLifecycleDateTime":"{{DateTimeOffset.UtcNow.AddDays(9):yyyy-MM-ddT00:00:00Z}}","ownerType":"Company"}""")],
            ["orgprofile"] = [O("""{"id":"demo-org","displayName":"Northgate (demo)","technicalNotificationMails":["it@northgate.example"],"marketingNotificationEmails":[],"securityComplianceNotificationMails":["security@northgate.example"],"securityComplianceNotificationPhones":[],"privacyProfile":{"contactEmail":"privacy@northgate.example","statementUrl":"https://northgate.example/privacy"},"street":"1 Harbor Way","city":"Springfield","state":"OR","postalCode":"97477","countryLetterCode":"US","preferredLanguage":"en","onPremisesSyncEnabled":null}""")],
            ["branding"] = [O("""{"id":"0","signInPageText":"Sign in with your Northgate account.","usernameHintText":"name@northgate.example","backgroundColor":"#F4F6F8","headerBackgroundColor":null,"customForgotMyPasswordText":null,"customPrivacyAndCookiesUrl":"https://northgate.example/privacy","customTermsOfUseUrl":null}""")],
            ["m365apps"] = [O("""{"updateChannel":"monthlyEnterprise","appsForWindows":{"isMicrosoft365AppsEnabled":true,"isProjectEnabled":true,"isSkypeForBusinessEnabled":false,"isVisioEnabled":true},"appsForMac":{"isMicrosoft365AppsEnabled":true,"isSkypeForBusinessEnabled":false}}""")],
            ["forms"] = [O("""{"isExternalSendFormEnabled":true,"isExternalShareCollaborationEnabled":false,"isExternalShareResultEnabled":false,"isExternalShareTemplateEnabled":true,"isRecordIdentityByDefaultEnabled":true,"isInOrgFormsPhishingScanEnabled":true,"isBingImageSearchEnabled":true}""")],
            ["todo"] = [O("""{"isExternalShareEnabled":false,"isExternalJoinEnabled":false,"isPushNotificationEnabled":true}""")],
            ["reportsettings"] = [O("""{"displayConcealedNames":true}""")],
            ["spsettings"] = [O("""{"sharingCapability":"externalUserSharingOnly","isResharingByExternalUsersEnabled":false,"sharingDomainRestrictionMode":"none","sharingAllowedDomainList":[],"sharingBlockedDomainList":[],"isSiteCreationEnabled":true,"isSitePagesCreationEnabled":true,"isCommentingOnSitePagesEnabled":true,"isLoopEnabled":true,"siteCreationDefaultStorageLimitInMB":1048576,"personalSiteDefaultStorageLimitInMB":1048576,"deletedUserPersonalSiteRetentionPeriodInDays":30,"isFileActivityNotificationEnabled":true,"isMacSyncAppEnabled":true,"isUnmanagedSyncAppForTenantRestricted":false,"isSyncButtonHiddenOnPersonalSite":false,"isLegacyAuthProtocolsEnabled":false,"idleSessionSignOut":{"isEnabled":false,"warnAfterInSeconds":0,"signOutAfterInSeconds":0}}""")],
            ["pronouns"] = [O("""{"isEnabledInOrganization":false}""")],
            ["iteminsights"] = [O("""{"isEnabledInOrganization":true,"disabledForGroup":null}""")],
            ["usersettings"] = [O("""{"id":"authorizationPolicy","allowInvitesFrom":"adminsAndGuestInviters","allowedToSignUpEmailBasedSubscriptions":false,"allowedToUseSSPR":true,"allowEmailVerifiedUsersToJoinOrganization":false,"allowUserConsentForRiskyApps":false,"blockMsolPowerShell":true,"guestUserRoleId":"10dae51f-b6af-4016-8d66-8c2a99b929b3","defaultUserRolePermissions":{"allowedToCreateApps":false,"allowedToCreateSecurityGroups":false,"allowedToCreateTenants":false,"allowedToReadBitlockerKeysForOwnedDevice":true,"allowedToReadOtherUsers":true,"permissionGrantPoliciesAssigned":["ManagePermissionGrantsForSelf.microsoft-user-default-low"]}}""")],
            ["externalidentities"] = [O("""{"id":"externalIdentitiesPolicy","allowExternalIdentitiesToLeave":true,"allowDeletedIdentitiesDataRemoval":false}""")],
            ["crosstenantdefault"] = [O(CrossTenant("""{"isServiceDefault":true}"""))],
            ["crosstenantpartners"] = [O(CrossTenant("""{"tenantId":"3f1c2a7e-0d9b-4c5e-9a1f-6b2d8e4c7a90","isServiceProvider":false}""")), O(CrossTenant("""{"tenantId":"zz-tenantry-test-partner","isServiceProvider":false}"""))],
            ["groupsettings"] = [O("""{"id":"demo-gs","displayName":"Group.Unified","templateId":"62375ab9-6b52-47ed-826b-58e47e0e304b","values":[{"name":"EnableGroupCreation","value":"true"},{"name":"GroupCreationAllowedGroupId","value":""},{"name":"PrefixSuffixNamingRequirement","value":"GRP_[GroupName]"},{"name":"CustomBlockedWordsList","value":"CEO,Payroll"},{"name":"AllowToAddGuests","value":"true"},{"name":"AllowGuestsToAccessGroups","value":"true"},{"name":"AllowGuestsToBeGroupOwner","value":"false"},{"name":"GuestUsageGuidelinesUrl","value":""},{"name":"UsageGuidelinesUrl","value":"https://northgate.example/groups"},{"name":"EnableMIPLabels","value":"false"}]}""")],
            ["groupexpiry"] = [O("""{"id":"demo-expiry","groupLifetimeInDays":365,"managedGroupTypes":"All","alternateNotificationEmails":"it@northgate.example"}""")],
            ["adminunits"] = [O("""{"id":"demo-au-1","displayName":"Field Offices","description":"People at the field offices","membershipType":"Dynamic","membershipRule":"(user.officeLocation -eq \"Field\")","membershipRuleProcessingState":"On","visibility":null,"isMemberManagementRestricted":false}"""),
                O("""{"id":"demo-au-2","displayName":"zz-tenantry-test-unit","description":"For trying changes","membershipType":"Assigned","isMemberManagementRestricted":false}""")],
            ["domains"] = [O("""{"id":"northgate.example","isVerified":true,"isDefault":true,"isInitial":false,"passwordValidityPeriodInDays":2147483647,"passwordNotificationWindowInDays":14,"authenticationType":"Managed","supportedServices":["Email","OfficeCommunicationsOnline","Intune"]}"""),
                O("""{"id":"northgatedemo.onmicrosoft.com","isVerified":true,"isDefault":false,"isInitial":true,"passwordValidityPeriodInDays":2147483647,"passwordNotificationWindowInDays":14,"authenticationType":"Managed","supportedServices":["Email"]}"""),
                O("""{"id":"zz-tenantry-test.example","isVerified":false,"isDefault":false,"isInitial":false,"passwordValidityPeriodInDays":2147483647,"passwordNotificationWindowInDays":14,"authenticationType":"Managed","supportedServices":[]}""")],
            ["dirsync"] = [],
            ["attributesets"] = [O("""{"id":"Finance","description":"Cost centers and budgets","maxAttributesPerSet":25}""")],
            ["attributedefs"] = [O("""{"id":"Finance_CostCenter","attributeSet":"Finance","name":"CostCenter","description":"The cost center that pays","type":"String","status":"Available","isCollection":false,"usePreDefinedValuesOnly":false}""")],
            ["deletedusers"] = [O($$"""{"id":"demo-del-1","displayName":"Jordan Ellis","userPrincipalName":"jordan.ellis@northgate.example","deletedDateTime":"{{past}}","jobTitle":"Analyst","department":"Finance"}"""),
                O($$"""{"id":"demo-del-2","displayName":"zz-tenantry-test-gone","userPrincipalName":"zz-tenantry-test-gone@northgate.example","deletedDateTime":"{{past}}"}""")],
            ["adminconsent"] = [O("""{"isEnabled":true,"notifyReviewers":true,"remindersEnabled":true,"requestDurationInDays":30,"version":2,"reviewers":[]}""")],
            ["consentrequests"] = [O("""{"id":"demo-cr-1","appId":"5b2c9d4e-1a3f-4e6b-8c7d-9f0a1b2c3d4e","appDisplayName":"Diagram Maker","pendingScopes":[{"displayName":"Files.Read.All"},{"displayName":"User.Read"}]}""")],
            ["stagedrollout"] = [],
            ["idlepolicies"] = [],
            ["termsofuse"] = [],
            ["accessreviews"] = [], ["accesspackages"] = [], ["lifecycleworkflows"] = [],
            ["recommendations"] = [O("""{"id":"demo-rec-1","displayName":"Remove unused apps","status":"active","priority":"medium","benefits":"Fewer ways in for attackers.","currentScore":2,"maxScore":10}"""),
                O("""{"id":"demo-rec-2","displayName":"Renew expiring app credentials","status":"completedBySystem","priority":"high","benefits":"Apps keep working.","currentScore":10,"maxScore":10}""")],
            ["iemode"] = [O("""{"id":"demo-ie-1","displayName":"Old intranet sites","description":"The timesheet and the old wiki","status":"published"}""")],
            ["autopatch"] = [],
            ["rooms"] = [O("""{"id":"demo-room-1","displayName":"Harbor Room","emailAddress":"harbor@northgate.example","capacity":12,"building":"Main","floorNumber":2,"audioDeviceName":"Ceiling microphones","videoDeviceName":"Teams Room","isWheelChairAccessible":true}""")],
            ["teams"] = [O("""{"id":"demo-team-1","displayName":"Operations","description":"Everyday operations","visibility":"private","isArchived":false,"memberSettings":{"allowCreateUpdateChannels":true,"allowCreatePrivateChannels":true,"allowDeleteChannels":false,"allowAddRemoveApps":true,"allowCreateUpdateRemoveTabs":true},"guestSettings":{"allowCreateUpdateChannels":false,"allowDeleteChannels":false},"messagingSettings":{"allowUserEditMessages":true,"allowUserDeleteMessages":true,"allowOwnerDeleteMessages":true,"allowTeamMentions":true,"allowChannelMentions":true},"funSettings":{"allowGiphy":true,"giphyContentRating":"moderate","allowStickersAndMemes":true,"allowCustomMemes":false}}"""),
                O("""{"id":"demo-team-2","displayName":"zz-tenantry-test-team","description":"For trying changes","visibility":"private","isArchived":false,"memberSettings":{"allowCreateUpdateChannels":true},"messagingSettings":{"allowUserEditMessages":true},"funSettings":{"allowGiphy":false,"giphyContentRating":"strict"}}""")],
            ["channels"] = [O("""{"id":"demo-ch-1","parent":"teams/demo-team-1","displayName":"General","description":"","membershipType":"standard"}"""),
                O("""{"id":"demo-ch-2","parent":"teams/demo-team-1","displayName":"Planning","description":"Weekly plans","membershipType":"private"}""")],
            ["teamsappsettings"] = [O("""{"isUserPersonalScopeResourceSpecificConsentEnabled":false,"isChatResourceSpecificConsentEnabled":true}""")],
            ["teamsorgapps"] = [O("""{"id":"demo-app-1","displayName":"Help Desk Bot","externalId":"7d1e2f3a-4b5c-6d7e-8f90-a1b2c3d4e5f6"}""")],
            ["teams-meeting"] = [O("""{"Identity":"Global","Description":"","AllowCloudRecording":true,"AllowTranscription":true,"AllowAnonymousUsersToJoinMeeting":true,"AutoAdmittedUsers":"EveryoneInCompanyExcludingGuests","AllowPowerPointSharing":true,"AllowWhiteboard":true,"MeetingChatEnabledType":"Enabled","NewMeetingRecordingExpirationDays":120}"""),
                O("""{"Identity":"Tag:Front Desk","Description":"No recording","AllowCloudRecording":false,"AllowTranscription":false,"AllowAnonymousUsersToJoinMeeting":false,"AutoAdmittedUsers":"EveryoneInCompany","AllowPowerPointSharing":true,"AllowWhiteboard":false,"MeetingChatEnabledType":"Enabled","NewMeetingRecordingExpirationDays":60}"""),
                O("""{"Identity":"Tag:AllOn","Description":"","AllowCloudRecording":true,"AllowTranscription":true}""")],
            ["teams-messaging"] = [O("""{"Identity":"Global","AllowUserEditMessage":true,"AllowUserDeleteMessage":true,"AllowGiphy":true,"GiphyRatingType":"Moderate","ReadReceiptsEnabledType":"UserPreference","AllowUserTranslation":true}""")],
            ["teams-apppermission"] = [O("""{"Identity":"Global","DefaultCatalogAppsType":"BlockedAppList","GlobalCatalogAppsType":"BlockedAppList","PrivateCatalogAppsType":"BlockedAppList","DefaultCatalogApps":[],"GlobalCatalogApps":[]}""")],
            ["teams-appsetup"] = [O("""{"Identity":"Global","AllowUserPinning":true,"AllowSideLoading":false,"PinnedAppBarApps":[{"Id":"14d6962d-6eeb-4f48-8890-de55454bb136","Order":1}]}""")],
            ["teams-calling"] = [O("""{"Identity":"Global","AllowPrivateCalling":true,"AllowVoicemail":"UserOverride","AllowCallForwardingToUser":true,"AllowCallGroups":true}""")],
            ["teams-events"] = [O("""{"Identity":"Global","AllowWebinars":"Enabled","AllowTownhalls":"Enabled","EventAccessType":"Everyone"}""")],
            ["teams-broadcast"] = [O("""{"Identity":"Global","AllowBroadcastScheduling":true,"AllowBroadcastTranscription":false,"BroadcastAttendeeVisibilityMode":"EveryoneInCompany"}""")],
            ["teams-update"] = [O("""{"Identity":"Global","AllowPreview":false,"AllowPublicPreview":"FollowOfficePreview"}""")],
            ["teams-channels"] = [O("""{"Identity":"Global","AllowPrivateChannelCreation":true,"AllowSharedChannelCreation":true,"AllowChannelSharingToExternalUser":false,"AllowUserToParticipateInExternalSharedChannel":true}""")],
            ["teams-client"] = [O("""{"Identity":"Global","AllowEmailIntoChannel":true,"AllowDropBox":false,"AllowBox":false,"AllowGoogleDrive":false,"AllowShareFile":false,"AllowEgnyte":false,"AllowOrganizationTab":true,"RestrictedSenderList":""}""")],
            ["teams-guestmeeting"] = [O("""{"Identity":"Global","AllowIPVideo":true,"ScreenSharingMode":"EntireScreen","AllowMeetNow":true}""")],
            ["teams-guestmessaging"] = [O("""{"Identity":"Global","AllowUserEditMessage":true,"AllowUserDeleteMessage":false,"AllowUserChat":true,"AllowGiphy":true,"AllowMemes":true,"AllowStickers":true}""")],
            ["teams-guestcalling"] = [O("""{"Identity":"Global","AllowPrivateCalling":true}""")],
        };
        return _orgItems;
    }

    static string CrossTenant(string start)
    {
        var o = JsonNode.Parse(start)!.AsObject();
        JsonObject Access(string type) => new() { ["usersAndGroups"] = new JsonObject { ["accessType"] = type, ["targets"] = new JsonArray(new JsonObject { ["target"] = "AllUsers", ["targetType"] = "user" }) },
            ["applications"] = new JsonObject { ["accessType"] = type, ["targets"] = new JsonArray(new JsonObject { ["target"] = "AllApplications", ["targetType"] = "application" }) } };
        o["b2bCollaborationInbound"] = Access("allowed");
        o["b2bCollaborationOutbound"] = Access("allowed");
        o["b2bDirectConnectInbound"] = Access("blocked");
        o["b2bDirectConnectOutbound"] = Access("blocked");
        o["inboundTrust"] = new JsonObject { ["isMfaAccepted"] = false, ["isCompliantDeviceAccepted"] = false, ["isHybridAzureADJoinedDeviceAccepted"] = false };
        o["automaticUserConsentSettings"] = new JsonObject { ["inboundAllowed"] = false, ["outboundAllowed"] = false };
        return o.ToJsonString();
    }

    static string? Text(JsonObject o, string name) => o[name] is JsonValue v ? v.ToString() : null;

    /// <summary>An item's address, as the Graph reader makes it.</summary>
    static string AddressOf(OrgKind kind, JsonObject item)
    {
        var collection = kind.Collection.Replace("{org}", "demo-org", StringComparison.Ordinal).Replace("{parent}", Text(item, "parent") ?? "", StringComparison.Ordinal);
        return kind.Single ? collection : kind.AddressOf(Text(item, kind.IdProperty) ?? "", collection);
    }

    public Task<OrgRead> GetOrgObjectsAsync(OrgKind kind, string? parentUrl = null, CancellationToken cancellationToken = default)
    {
        if (!OrgItems().TryGetValue(kind.Key, out var rows))
            return Task.FromResult(new OrgRead([], null));
        if (kind.Key == "dirsync")
            return Task.FromResult(new OrgRead([], "This organization doesn't sync from on-premises Active Directory."));
        if (kind.Key is "accessreviews" or "accesspackages" or "lifecycleworkflows")
            return Task.FromResult(new OrgRead([], $"Microsoft refused: reading {kind.Title.ToLowerInvariant()} needs {kind.Needs}"));
        var items = rows.Where(r => parentUrl is null || Text(r, "parent") == parentUrl).Select(r =>
        {
            var raw = JsonDocument.Parse(r.ToJsonString()).RootElement.Clone();
            var id = Text(r, kind.IdProperty) ?? "";
            return new OrgObject(id, kind.NameOf?.Invoke(raw) ?? Text(r, kind.NameProperty) ?? (id.Length > 0 ? id : kind.Title), AddressOf(kind, r), raw);
        });
        return Task.FromResult(new OrgRead([.. items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)]));
    }

    public Task<IReadOnlyList<string>> GetOrgExtraAsync(OrgKind kind, OrgObject item, OrgExtra extra, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(extra.Path switch
        {
            "verificationDnsRecords" => [$"TXT @ → MS=ms{Math.Abs(item.Id.Length * 7919):00000000}", $"MX @ → ms{item.Id.Length * 31}.mail.protection.outlook.com (priority 32767)"],
            "serviceConfigurationRecords" => ["MX @ → northgate-example.mail.protection.outlook.com (priority 0) · Email", "TXT @ → v=spf1 include:spf.protection.outlook.com -all · Email",
                "CNAME autodiscover → autodiscover.outlook.com · Email"],
            _ when extra.Title == "Members" => item.Id == "demo-au-1" ? ["Avery Quinn (avery.quinn@northgate.example)", "Rowan Blake (rowan.blake@northgate.example)"] : [],
            _ => [],
        });

    /// <summary>Applies an organization change to the demo's copy.</summary>
    public ApplyResult? ApplyOrg(ChangeOp change)
    {
        if (change is not OrgChange o)
            return null;
        var all = OrgItems();
        (OrgKind Kind, List<JsonObject> Rows, JsonObject Item)? Find(string url)
        {
            foreach (var kind in OrgKinds.All)
                if (all.TryGetValue(kind.Key, out var rows) && rows.FirstOrDefault(r => AddressOf(kind, r) == url || kind.DeleteUrl is { } d && d.Replace("{id}", Text(r, kind.IdProperty) ?? "", StringComparison.Ordinal) == url) is { } item)
                    return (kind, rows, item);
            return null;
        }
        switch (o)
        {
            case SetOrgSetting s when Find(s.Url) is { } found:
                OrgPath.Set(found.Item, s.Path, s.Value);
                break;
            case CreateOrgObject c:
                var kindKey = o.Item.Id.Split(':')[0];
                var kind = OrgKinds.Get(kindKey);
                var made = c.Body.DeepClone().AsObject();
                made[kind.IdProperty] ??= c.Put ? c.Item.Name : $"demo-new-{all[kindKey].Count + 1}";
                if (kind.Collection.Contains("{parent}", StringComparison.Ordinal))
                    made["parent"] = c.Url[..c.Url.LastIndexOf('/')];
                all[kindKey].Add(made);
                break;
            case DeleteOrgObject d when Find(d.Url) is { } gone:
                gone.Rows.Remove(gone.Item);
                break;
            case RunOrgAction a when Find(a.Url[..a.Url.LastIndexOf('/')]) is { } target:
                var action = a.Url[(a.Url.LastIndexOf('/') + 1)..];
                switch (action)
                {
                    case "verify": target.Item["isVerified"] = true; break;
                    case "archive": target.Item["isArchived"] = true; break;
                    case "unarchive": target.Item["isArchived"] = false; break;
                    case "restore": target.Rows.Remove(target.Item); break;
                    case "complete": target.Item["status"] = "completedByUser"; break;
                    case "dismiss": target.Item["status"] = "dismissed"; break;
                    case "postpone": target.Item["status"] = "postponed"; break;
                    case "reactivate": target.Item["status"] = "active"; break;
                }
                break;
            default:
                return new ApplyResult(ChangeOutcome.Failed, "The demo couldn't find that.");
        }
        return new ApplyResult(ChangeOutcome.Applied);
    }
}
