using System.Text.Json;
using Tenantry.Core.Directory;

namespace Tenantry.Graph;

/// <summary>Threat policies with their rules, quarantine policies and the connection filter (12f).</summary>
public sealed partial class GraphUserRecords : IThreatPolicyRecords
{
    public async Task<IReadOnlyList<ThreatPolicy>?> GetThreatPoliciesAsync(ThreatPolicyKind kind, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<JsonElement> policies, rules;
        try
        {
            var p = Run(kind.GetPolicy, new(), cancellationToken);
            var r = Run(kind.GetRule, new(), cancellationToken);
            policies = await p;
            rules = await r;
        }
        catch (Core.Exchange.CmdletException) when (kind.NeedsDefender)
        {
            // Without Defender for Office 365 the cmdlets aren't there for this tenant.
            return null;
        }
        var byPolicy = rules.Where(r => Str(r, kind.RulePolicyParameter) is { Length: > 0 })
            .GroupBy(r => Str(r, kind.RulePolicyParameter)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return [.. policies.Select(p =>
            {
                var name = Str(p, "Name") ?? "";
                var isDefault = p.TryGetProperty("IsDefault", out var d) && d.ValueKind == JsonValueKind.True;
                // Microsoft keeps the preset and built-in policies: they can't be edited one by one.
                var readOnly = name.Contains("Preset Security Policy", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Built-In Protection", StringComparison.OrdinalIgnoreCase)
                               || p.TryGetProperty("RecommendedPolicyType", out var rec) && rec.ValueKind == JsonValueKind.String && rec.GetString() is "Standard" or "Strict" or "BuiltInProtection";
                return new ThreatPolicy(kind, name, isDefault, readOnly, p, byPolicy.TryGetValue(name, out var rule) ? rule : null);
            })
            .OrderBy(t => t.IsDefault || t.ReadOnly).ThenBy(t => t.Priority ?? int.MaxValue).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)];
    }

    static readonly HashSet<string> BuiltInQuarantine = new(StringComparer.OrdinalIgnoreCase)
        { "AdminOnlyAccessPolicy", "DefaultFullAccessPolicy", "DefaultFullAccessWithNotificationPolicy", "NotificationEnabledPolicy" };

    public async Task<IReadOnlyList<QuarantinePolicy>> GetQuarantinePoliciesAsync(CancellationToken cancellationToken = default) =>
        [.. (await Run("Get-QuarantinePolicy", new(), cancellationToken)).Select(r => new QuarantinePolicy(
                Str(r, "Name") ?? "", BuiltInQuarantine.Contains(Str(r, "Name") ?? ""),
                QuarantinePermissions.Parse(Str(r, "EndUserQuarantinePermissions") ?? ""),
                r.TryGetProperty("ESNEnabled", out var e) && e.ValueKind == JsonValueKind.True))
            .OrderByDescending(q => q.BuiltIn).ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)];

    public async Task<JsonElement> GetQuarantineGlobalAsync(CancellationToken cancellationToken = default) =>
        (await Run("Get-QuarantinePolicy", new() { ["QuarantinePolicyType"] = "GlobalQuarantinePolicy" }, cancellationToken)).FirstOrDefault();

    public async Task<JsonElement> GetConnectionFilterAsync(CancellationToken cancellationToken = default) =>
        (await Run("Get-HostedConnectionFilterPolicy", new() { ["Identity"] = "Default" }, cancellationToken)).FirstOrDefault();
}
