using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Exchange;
using Tenantry.Core.PowerShell;

namespace Tenantry.Graph;

/// <summary>
/// Purview protection (14a-14f) through Purview's cmdlets: sensitivity labels and their policies,
/// auto-labeling, DLP, information types, insider risk, communication compliance, information
/// barriers, the file plan and role groups. DLP alerts come from Microsoft Graph.
/// </summary>
public sealed partial class GraphUserRecords : IPurviewObjectRecords
{
    Task<IReadOnlyList<string>>? _sensitiveTypes;

    public async Task<PurviewRead> GetObjectsAsync(PurviewKind kind, CancellationToken cancellationToken = default)
    {
        if (kind.Graph is { } path)
            try
            {
                var answer = await graph.GetAsync(path, cancellationToken);
                return new PurviewRead([.. answer.GetProperty("value").EnumerateArray().Select(a =>
                    new PurviewObject(Text(a, "id") ?? "", Text(a, kind.NameProperty) ?? "An alert", null, When(a, "lastUpdateDateTime") ?? When(a, "createdDateTime"), a.Clone()))
                    .OrderByDescending(o => o.Modified)]);
            }
            catch (GraphException e)
            {
                return new PurviewRead([], e.Status == System.Net.HttpStatusCode.Forbidden
                    ? "Reading alerts needs the SecurityAlert.Read.All permission and a Security Reader (or higher) role."
                    : $"Couldn't read these: {e.Message}");
            }
        IReadOnlyList<JsonElement> rows;
        try
        {
            rows = await Compliance(kind.Get, null, cancellationToken);
        }
        catch (CmdletException e)
        {
            var text = e.Explanation;
            var licensing = text.Contains("licens", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith('<');
            return new PurviewRead([], licensing && kind.Licensing is { } need ? $"Purview refused: {need}"
                : e.IsNotFound ? null : $"Couldn't read these from Purview: {text}");
        }
        var items = rows.Where(r => r.ValueKind == JsonValueKind.Object).Select(r => ToPurviewObject(kind, r)).ToList();
        if (kind.LabelActions)
        {
            // Sublabels name their parent by ID; the parent's shown name reads better.
            var names = items.ToDictionary(i => EdStr(i.Raw, "Guid") ?? i.Id, i => EdStr(i.Raw, "DisplayName") ?? i.Name, StringComparer.OrdinalIgnoreCase);
            items = [.. items.Select(i =>
            {
                var o = JsonNode.Parse(i.Raw.GetRawText())!.AsObject();
                foreach (var (k, v) in PurviewKinds.LiftLabelActions(i.Raw))
                    o[k] = v?.DeepClone();
                if (EdStr(i.Raw, "ParentId") is { Length: > 0 } parent)
                    o["ParentLabelDisplayName"] = names.GetValueOrDefault(parent, parent);
                return i with { Raw = JsonDocument.Parse(o.ToJsonString()).RootElement.Clone() };
            })];
        }
        return new PurviewRead(items);
    }

    internal static PurviewObject ToPurviewObject(PurviewKind kind, JsonElement r)
    {
        var name = EdStr(r, kind.NameProperty) ?? EdStr(r, "Name") ?? EdStr(r, "Identity") ?? "An item";
        return new PurviewObject(EdStr(r, "Guid") ?? EdStr(r, "Identity") ?? name, name, kind.ParentOf(r), Utc(r, "WhenChangedUTC") ?? Utc(r, "WhenChanged"), r.Clone());
    }

    public Task<IReadOnlyList<string>> GetSensitiveTypeNamesAsync(CancellationToken cancellationToken = default) => _sensitiveTypes ??= ReadSensitiveTypesAsync(cancellationToken);

    async Task<IReadOnlyList<string>> ReadSensitiveTypesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return [.. (await Compliance("Get-DlpSensitiveInformationType", null, cancellationToken)).Select(r => EdStr(r, "Name")).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (CmdletException)
        {
            _sensitiveTypes = null;
            return [];
        }
    }
}

/// <summary>Purview protection changes (14a-14f).</summary>
public sealed partial class GraphChangeExecutor
{
    Task<ApplyResult> ApplyPurviewAsync(PurviewChange change, CancellationToken cancellationToken) => Plain(change switch
    {
        SetPurviewSetting s => Purview(s.Cmdlet, SettingParameters(s), null, _ => true, cancellationToken),
        SetPurviewList l => SetPurviewListAsync(l, cancellationToken),
        CreatePurviewObject c => Purview(c.Cmdlet, CreateParameters(c), null, _ => true, cancellationToken),
        DeletePurviewObject d => Purview(d.Cmdlet, new() { ["Identity"] = d.Item.Name, ["Confirm"] = false }, null, _ => true, cancellationToken),
        RunPurviewAction a => Purview(a.Cmdlet, new() { ["Confirm"] = false }, null, _ => true, cancellationToken),
        _ => Task.FromResult(new ApplyResult(ChangeOutcome.Failed, $"Tenantry can't apply \"{change.Label}\" changes yet.")),
    });

    /// <summary>A keyword dictionary's words, as Purview's file data: UTF-16 text, one word per line, sent as bytes.</summary>
    internal static JsonArray KeywordBytes(IEnumerable<string> words) =>
        new([.. Encoding.Unicode.GetBytes(string.Join("\r\n", words)).Select(b => (JsonNode)JsonValue.Create((int)b))]);

    static IEnumerable<string> Words(JsonNode? value) => value switch
    {
        JsonArray a => a.Select(n => n?.ToString()).OfType<string>(),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    internal static Dictionary<string, object?> SettingParameters(SetPurviewSetting change) => new()
    {
        ["Identity"] = change.Item.Name,
        [change.Parameter] = change.Parameter == "FileData" ? KeywordBytes(Words(change.Value)) : change.Value?.DeepClone(),
    };

    internal static Dictionary<string, object?> CreateParameters(CreatePurviewObject change)
    {
        var parameters = new Dictionary<string, object?>();
        foreach (var (key, value) in change.Parameters)
            parameters[key] = key == "FileData" ? KeywordBytes(Words(value)) : value?.DeepClone();
        return parameters;
    }

    internal static Dictionary<string, object?> ListParameters(SetPurviewList change)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = change.Item.Name };
        if (change.Added.Count > 0 && change.AddParameter is { } add)
            parameters[add] = change.Added.ToArray();
        if (change.Removed.Count > 0 && change.RemoveParameter is { } remove)
            parameters[remove] = change.Removed.ToArray();
        return parameters;
    }

    async Task<ApplyResult> SetPurviewListAsync(SetPurviewList change, CancellationToken cancellationToken)
    {
        if (change.MemberCmdlets is not { } members)
            return await Purview(change.Cmdlet, ListParameters(change), null, _ => true, cancellationToken);
        foreach (var (cmdlet, member) in change.Added.Select(a => (members.AddCmdlet, a)).Concat(change.Removed.Select(r => (members.RemoveCmdlet, r))))
        {
            var one = await Purview(cmdlet, new() { ["Identity"] = change.Item.Name, [members.MemberParameter] = member, ["Confirm"] = false }, null, _ => true, cancellationToken);
            if (one.Outcome != ChangeOutcome.Applied)
                return one with { Detail = $"{member}: {one.Detail}" };
        }
        return new ApplyResult(ChangeOutcome.Applied);
    }

    // ---- PowerShell ------------------------------------------------------------------------------

    const string FileDataMark = "__TENANTRY_FILEDATA__";

    /// <summary>Purview's file data shown as the words it's made from, not as bytes.</summary>
    static string WithFileData(string cmdlet, Dictionary<string, object?> parameters, JsonNode? words)
    {
        if (!parameters.ContainsKey("FileData"))
            return Ps.Cmdlet(cmdlet, parameters);
        parameters["FileData"] = FileDataMark;
        var expression = $"([System.Text.Encoding]::Unicode.GetBytes(({Ps.Value(Words(words).ToArray())} -join \"`r`n\")))";
        return Ps.Cmdlet(cmdlet, parameters).Replace(Ps.Quote(FileDataMark), expression, StringComparison.Ordinal);
    }

    internal static PsScript? PurviewObjectScript(ChangeOp change) => change switch
    {
        SetPurviewSetting s => PurviewText(WithFileData(s.Cmdlet, SettingParameters(s), s.Value)),
        SetPurviewList { IsNoChange: true } => PurviewText(Ps.Comment("Nothing to send: the list is unchanged.")),
        SetPurviewList { MemberCmdlets: { } m } l => PurviewText(Ps.Lines(
            [.. l.Added.Select(a => Ps.Cmdlet(m.AddCmdlet, new Dictionary<string, object?> { ["Identity"] = l.Item.Name, [m.MemberParameter] = a, ["Confirm"] = false })),
             .. l.Removed.Select(r => Ps.Cmdlet(m.RemoveCmdlet, new Dictionary<string, object?> { ["Identity"] = l.Item.Name, [m.MemberParameter] = r, ["Confirm"] = false }))])),
        SetPurviewList l => PurviewText(Ps.Cmdlet(l.Cmdlet, ListParameters(l))),
        CreatePurviewObject c => PurviewText(WithFileData(c.Cmdlet, CreateParameters(c), c.Parameters["FileData"]),
            "Purview takes a while to put new policies in place, and refuses changes to them until it has."),
        DeletePurviewObject d => PurviewText(Ps.Cmdlet(d.Cmdlet, new Dictionary<string, object?> { ["Identity"] = d.Item.Name, ["Confirm"] = false })),
        RunPurviewAction a => PurviewText(Ps.Cmdlet(a.Cmdlet, new Dictionary<string, object?> { ["Confirm"] = false })),
        _ => null,
    };
}
