using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.Demo;

/// <summary>Demo Purview protection (14a-14f): labels, policies, DLP, information types, barriers, the file plan.</summary>
public sealed partial class DemoUserRecords : IPurviewObjectRecords
{
    Dictionary<string, List<JsonObject>>? _purview;

    static JsonObject Row(string json) => JsonNode.Parse(json)!.AsObject();

    Dictionary<string, List<JsonObject>> Purview() => _purview ??= new Dictionary<string, List<JsonObject>>
    {
        ["labels"] =
        [
            Row("""{"Name":"Public","DisplayName":"Public","Tooltip":"Fine to share with anyone.","Priority":0,"ContentType":"File, Email","Guid":"l-1","LabelActions":[]}"""),
            Row("""{"Name":"General","DisplayName":"General","Tooltip":"Day-to-day work, not for outside the organization.","Priority":1,"ContentType":"File, Email","Guid":"l-2","LabelActions":["{\"Type\":\"applycontentmarking\",\"SubType\":\"footer\",\"Settings\":[{\"Key\":\"text\",\"Value\":\"General\"}]}"]}"""),
            Row("""{"Name":"Confidential","DisplayName":"Confidential","Tooltip":"Sensitive: only people inside the organization can open it.","Priority":2,"ContentType":"File, Email","Guid":"l-3","LabelActions":["{\"Type\":\"encrypt\",\"SubType\":null,\"Settings\":[{\"Key\":\"protectiontype\",\"Value\":\"Template\"},{\"Key\":\"disabled\",\"Value\":\"false\"}]}","{\"Type\":\"applycontentmarking\",\"SubType\":\"header\",\"Settings\":[{\"Key\":\"text\",\"Value\":\"CONFIDENTIAL\"}]}","{\"Type\":\"applywatermarking\",\"SubType\":null,\"Settings\":[{\"Key\":\"text\",\"Value\":\"Confidential\"}]}"]}"""),
            Row("""{"Name":"Confidential-Finance","DisplayName":"Finance","Tooltip":"Finance only.","Priority":3,"ContentType":"File, Email","Guid":"l-4","ParentId":"l-3","LabelActions":[]}"""),
            Row("""{"Name":"Private Team","DisplayName":"Private Team","Tooltip":"For sites and groups only members can join.","Priority":4,"ContentType":"Site, UnifiedGroup","Guid":"l-5","LabelActions":["{\"Type\":\"protectgroup\",\"SubType\":null,\"Settings\":[{\"Key\":\"privacy\",\"Value\":\"Private\"},{\"Key\":\"allowaccesstoguestusers\",\"Value\":\"false\"}]}"]}"""),
        ],
        ["labelpolicies"] =
        [
            Row("""{"Name":"Global Label Policy","Comment":"Every label, for everyone.","Labels":["Public","General","Confidential","Confidential-Finance"],"ExchangeLocation":["All"],"ModernGroupLocation":[],"Priority":0,"Mode":"Enforce","DistributionStatus":"Success"}"""),
        ],
        ["autolabel"] =
        [
            Row("""{"Name":"Card Numbers in Email","Comment":"Labels email with card numbers Confidential.","ApplySensitivityLabel":"Confidential","Mode":"TestWithoutNotifications","ExchangeLocation":["All"],"SharePointLocation":[],"OneDriveLocation":[],"Priority":0,"DistributionStatus":"Success"}"""),
        ],
        ["autolabelrules"] =
        [
            Row("""{"Name":"Card Numbers in Email - Rule","ParentPolicyName":"Card Numbers in Email","Workload":"Exchange","Disabled":false,"ReportSeverityLevel":"Low","ContentContainsSensitiveInformation":[{"name":"Credit Card Number","mincount":"1"}]}"""),
        ],
        ["dlp"] =
        [
            Row("""{"Name":"U.S. Financial Data","Comment":"From Microsoft's template.","Mode":"Enable","ExchangeLocation":["All"],"SharePointLocation":["All"],"OneDriveLocation":["All"],"TeamsLocation":["All"],"EndpointDlpLocation":[],"ExchangeSenderMemberOf":[],"Priority":0,"DistributionStatus":"Success"}"""),
            Row("""{"Name":"Health Records","Comment":"Trial run.","Mode":"TestWithNotifications","ExchangeLocation":["All"],"SharePointLocation":[],"OneDriveLocation":["All"],"TeamsLocation":[],"EndpointDlpLocation":[],"ExchangeSenderMemberOf":[],"Priority":1,"DistributionStatus":"Success"}"""),
        ],
        ["dlprules"] =
        [
            Row("""{"Name":"Low volume of financial data","ParentPolicyName":"U.S. Financial Data","Disabled":false,"AccessScope":"NotInOrganization","BlockAccess":false,"NotifyUser":["Owner"],"ReportSeverityLevel":"Low","Priority":0,"ContentContainsSensitiveInformation":[{"name":"Credit Card Number","mincount":"1"},{"name":"U.S. Bank Account Number","mincount":"1"}]}"""),
            Row("""{"Name":"High volume of financial data","ParentPolicyName":"U.S. Financial Data","Disabled":false,"AccessScope":"NotInOrganization","BlockAccess":true,"BlockAccessScope":"All","NotifyUser":["Owner","SiteAdmin"],"GenerateIncidentReport":["SiteAdmin"],"ReportSeverityLevel":"High","Priority":1,"ContentContainsSensitiveInformation":[{"name":"Credit Card Number","mincount":"10"},{"name":"U.S. Bank Account Number","mincount":"10"}]}"""),
            Row("""{"Name":"Health records shared outside","ParentPolicyName":"Health Records","Disabled":false,"AccessScope":"NotInOrganization","BlockAccess":false,"ReportSeverityLevel":"Medium","Priority":0,"ContentContainsSensitiveInformation":[{"name":"International Classification of Diseases (ICD-10-CM)","mincount":"1"}]}"""),
        ],
        ["dlpalerts"] =
        [
            Row("""{"id":"a-1","title":"DLP policy match for document 'Q3 forecast.xlsx' in OneDrive","severity":"low","status":"new","description":"Shared outside the organization with card numbers.","createdDateTime":"2026-11-10T14:02:00Z","policyName":"U.S. Financial Data"}"""),
            Row("""{"id":"a-2","title":"DLP policy match for email with subject 'Invoice details'","severity":"high","status":"inProgress","description":"Ten card numbers sent outside.","createdDateTime":"2026-11-08T09:41:00Z","policyName":"U.S. Financial Data","assignedTo":"Avery Quinn"}"""),
        ],
        ["endpointdlp"] = [Row("""{"Name":"PolicyConfig","EnableLabelCoauth":true,"EnableSpoAipMigration":true}""")],
        ["infotypes"] =
        [
            Row("""{"Name":"Credit Card Number","Publisher":"Microsoft Corporation","Description":"Detects card numbers from all major brands.","Type":"Entity","RecommendedConfidence":85}"""),
            Row("""{"Name":"U.S. Bank Account Number","Publisher":"Microsoft Corporation","Description":"Detects 6 to 17 digit account numbers near banking words.","Type":"Entity","RecommendedConfidence":75}"""),
            Row("""{"Name":"U.S. Social Security Number (SSN)","Publisher":"Microsoft Corporation","Description":"Detects nine-digit social security numbers.","Type":"Entity","RecommendedConfidence":85}"""),
            Row("""{"Name":"International Classification of Diseases (ICD-10-CM)","Publisher":"Microsoft Corporation","Description":"Detects diagnosis codes.","Type":"Entity","RecommendedConfidence":85}"""),
            Row("""{"Name":"Project Code Names","Publisher":"Northgate","Description":"Our unreleased project names.","Type":"Entity","RecommendedConfidence":75}"""),
        ],
        ["keywords"] = [Row("""{"Name":"Project Words","Description":"Unreleased project names.","KeywordDictionary":"bluebird,nightjar,kestrel"}""")],
        ["edm"] = [],
        ["insiderrisk"] = [Row("""{"Name":"Data theft by departing users","InsiderRiskScenario":"IntellectualPropertyTheft","Enabled":true}""")],
        ["commcompliance"] = [Row("""{"Name":"Inappropriate text","Comment":"Reviews a sample of messages.","Reviewers":["avery.quinn@northgate.example"],"Enabled":true}""")],
        ["commrules"] = [Row("""{"Name":"Inappropriate text - Rule","Policy":"Inappropriate text","SamplingRate":10,"Condition":"","ExchangeLocation":["All"]}""")],
        ["segments"] =
        [
            Row("""{"Name":"Research","UserGroupFilter":"Department -eq 'Research'"}"""),
            Row("""{"Name":"Sales","UserGroupFilter":"Department -eq 'Sales'"}"""),
        ],
        ["barriers"] = [Row("""{"Name":"Research-Sales","AssignedSegment":"Research","SegmentsBlocked":["Sales"],"SegmentsAllowed":[],"State":"Inactive","ModerationAllowed":false}""")],
        ["fp-departments"] = [Row("""{"Name":"Finance"}"""), Row("""{"Name":"Human Resources"}""")],
        ["fp-categories"] = [Row("""{"Name":"Accounts"}"""), Row("""{"Name":"Personnel"}""")],
        ["fp-subcategories"] = [Row("""{"Name":"Payable","ParentId":"Accounts"}""")],
        ["fp-authorities"] = [Row("""{"Name":"Business"}"""), Row("""{"Name":"Legal"}""")],
        ["fp-citations"] = [Row("""{"Name":"Sarbanes-Oxley Act of 2002","CitationUrl":"https://www.sec.gov/about/laws/soa2002.pdf","CitationJurisdiction":"U.S. SEC"}""")],
        ["fp-references"] = [Row("""{"Name":"FIN-001"}""")],
        ["purviewroles"] =
        [
            Row("""{"Name":"Compliance Administrator","Description":"Manages compliance settings.","Roles":["Compliance Administrator","DLP Compliance Management","Retention Management"],"Members":["Avery Quinn"]}"""),
            Row("""{"Name":"eDiscovery Manager","Description":"Runs searches and holds.","Roles":["Case Management","Compliance Search","Hold"],"Members":[]}"""),
        ],
    };

    public Task<PurviewRead> GetObjectsAsync(PurviewKind kind, CancellationToken cancellationToken = default)
    {
        var rows = Purview().GetValueOrDefault(kind.Key) ?? [];
        var items = rows.Select(r => JsonDocument.Parse(r.ToJsonString()).RootElement.Clone())
            .Select(e => new PurviewObject((string?)JsonNode.Parse(e.GetRawText())!["Guid"] ?? (e.TryGetProperty(kind.NameProperty, out var n) ? n.ToString() : "item"),
                e.TryGetProperty(kind.NameProperty, out var name) ? name.ToString() : "An item", kind.ParentOf(e), DateTimeOffset.UtcNow.AddDays(-12), e)).ToList();
        if (kind.LabelActions)
        {
            var names = items.ToDictionary(i => i.Id, i => i.Raw.TryGetProperty("DisplayName", out var d) ? d.GetString() ?? i.Name : i.Name);
            items = [.. items.Select(i =>
            {
                var o = JsonNode.Parse(i.Raw.GetRawText())!.AsObject();
                foreach (var (k, v) in PurviewKinds.LiftLabelActions(i.Raw))
                    o[k] = v?.DeepClone();
                if (i.Raw.TryGetProperty("ParentId", out var p) && p.GetString() is { } parent)
                    o["ParentLabelDisplayName"] = names.GetValueOrDefault(parent, parent);
                return i with { Raw = JsonDocument.Parse(o.ToJsonString()).RootElement.Clone() };
            })];
        }
        var note = kind.Key == "edm" ? kind.Licensing is { } need ? $"Purview refused: {need}" : null : null;
        return Task.FromResult(new PurviewRead(items, note));
    }

    public Task<IReadOnlyList<string>> GetSensitiveTypeNamesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. Purview()["infotypes"].Select(r => (string?)r["Name"]).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase)]);

    /// <summary>A Purview protection change to the demo; null when it isn't one.</summary>
    public ApplyResult? ApplyPurview(ChangeOp change)
    {
        if (change is not PurviewChange p)
            return null;
        var store = Purview();
        if (p is RunPurviewAction)
            return new ApplyResult(ChangeOutcome.Applied, "Applying (in the demo, at once).");
        var key = p.Item.Id.Split(':', 2)[0];
        var kind = PurviewKinds.All.FirstOrDefault(k => k.Key == key);
        if (kind is null || !store.TryGetValue(key, out var rows))
            return null;
        if (p is CreatePurviewObject c)
        {
            var row = new JsonObject();
            foreach (var (k, v) in c.Parameters)
                row[k == "Policy" ? kind.ParentProperties?.FirstOrDefault() ?? k : k == "FileData" ? "KeywordDictionary" : k] =
                    k == "FileData" && v is JsonArray words ? string.Join(",", words.Select(w => (string?)w)) : v?.DeepClone();
            rows.Add(row);
            return new ApplyResult(ChangeOutcome.Applied);
        }
        var target = rows.FirstOrDefault(r => string.Equals((string?)r[kind.NameProperty], p.Item.Name, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return new ApplyResult(ChangeOutcome.Failed, "It isn't there any more.");
        switch (p)
        {
            case SetPurviewSetting s:
                var property = kind.Fields?.FirstOrDefault(f => f.Parameter == s.Parameter)?.Property ?? s.Parameter;
                target[property] = s.Parameter == "FileData" && s.Value is JsonValue v && v.TryGetValue<string>(out var text)
                    ? string.Join(",", text.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    : s.Value?.DeepClone();
                break;
            case SetPurviewList l:
                var listProperty = l.MemberCmdlets?.Property ?? kind.Fields?.FirstOrDefault(f => f.Add == l.AddParameter)?.Property ?? l.ListLabel;
                target[listProperty] = new JsonArray([.. l.New.Select(n => (JsonNode)n)]);
                break;
            case DeletePurviewObject:
                rows.Remove(target);
                break;
        }
        return new ApplyResult(ChangeOutcome.Applied);
    }
}
