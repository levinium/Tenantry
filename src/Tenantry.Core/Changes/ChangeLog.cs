using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;

namespace Tenantry.Core.Changes;

public enum ChangeOutcome
{
    /// <summary>Applied, and reading it back showed the new value.</summary>
    Applied,

    /// <summary>Accepted by Microsoft 365, but not yet visible on reading it back (replication can lag).</summary>
    AppliedNotYetVisible,

    /// <summary>Microsoft 365 refused it.</summary>
    Failed,

    /// <summary>The write guard stopped it; nothing was sent.</summary>
    Refused,
}

/// <summary>One line of the change log: what was attempted, by whom, when, and what happened.</summary>
public sealed record ChangeLogEntry(string Id, DateTimeOffset At, string Account, ChangeOp Change, ChangeOutcome Outcome, string? Detail);

/// <summary>
/// Every change Tenantry attempts in a tenant, kept in the tenant's encrypted local store
/// (never sent anywhere). Settings changes such as turning Test mode off are logged here too.
/// </summary>
public sealed class ChangeLog(IEntityStore store)
{
    public const string Kind = "changelog";

    // Stored JSON may come back with properties reordered, so don't insist the type tag is first.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { AllowOutOfOrderMetadataProperties = true };

    public void Add(ChangeLogEntry entry)
    {
        var json = JsonSerializer.SerializeToNode(entry, Json)!.AsObject();
        json["id"] = entry.Id;
        store.Apply(Kind, [new EntityChange(entry.Id, json)]);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ChangeLogEntry> All() =>
        store.All(Kind)
            .Select(j => JsonSerializer.Deserialize<ChangeLogEntry>(j, Json))
            .OfType<ChangeLogEntry>()
            .OrderByDescending(e => e.At)
            .ToArray();
}
