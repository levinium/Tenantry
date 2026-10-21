using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Cache;
using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>
/// Applies changes through Graph, then reads the object back to confirm the new value and
/// writes what it read into the local copy, so lists show the change at once. Microsoft 365 can
/// take a few seconds to show a write on reading it back, so the read is retried briefly; a
/// change still not visible after that is reported as applied-but-not-yet-visible, not failed.
/// </summary>
public sealed class GraphChangeExecutor(GraphClient graph, IEntityStore store, Func<TimeSpan, CancellationToken, Task>? delay = null) : IChangeExecutor
{
    static readonly TimeSpan[] ReadBackWaits = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<ApplyResult> ApplyAsync(ChangeOp change, CancellationToken cancellationToken = default) => change switch
    {
        // An emptied text property is cleared (null), not set to "".
        SetUserText t => await PatchUserAsync(t.User.Id, t.Property, string.IsNullOrWhiteSpace(t.New) ? null : JsonValue.Create(t.New.Trim()), cancellationToken),
        SetSignInAllowed s => await PatchUserAsync(s.User.Id, "accountEnabled", JsonValue.Create(s.New), cancellationToken),
        _ => new ApplyResult(ChangeOutcome.Failed, $"Tenantry can't apply \"{change.Label}\" changes yet."),
    };

    async Task<ApplyResult> PatchUserAsync(string id, string property, JsonNode? value, CancellationToken cancellationToken)
    {
        try
        {
            await graph.PatchAsync($"users/{id}", new JsonObject { [property] = value?.DeepClone() }, cancellationToken);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }

        var expected = value?.ToJsonString() ?? "null";
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            var user = await graph.GetAsync($"users/{id}?$select=id,{property}", cancellationToken);
            store.Apply(DirectorySync.Users, [new EntityChange(id, JsonNode.Parse(user.GetRawText())!.AsObject(), OnlyIfExists: true)]);
            var actual = user.TryGetProperty(property, out var v) ? v.GetRawText() : "null";
            if (actual == expected)
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible,
            "Microsoft 365 accepted the change but still shows the old value; it usually appears within a minute.");
    }

    /// <summary>Graph's own words, minus the status-code prefix an admin doesn't need.</summary>
    static string Describe(GraphException e) =>
        e.Status switch
        {
            System.Net.HttpStatusCode.Forbidden => $"Not allowed: {Message(e)} Your role may not cover this change, or the account is protected (for example an administrator).",
            _ => Message(e),
        };

    static string Message(GraphException e)
    {
        var text = e.Message;
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 ? text[(colon + 2)..] : text;
    }
}
