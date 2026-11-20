using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>
/// Find Files' changes: restoring from a site's recycle bins (SharePoint REST, the only way to reach
/// the second stage), giving people access (Graph invite) and copying (Graph copy, which runs on
/// Microsoft's side and is watched until it's done). A change that goes through only in part says
/// what's left in <see cref="ApplyResult.Remaining"/>.
/// </summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>How long one copy is watched before Tenantry stops waiting (Microsoft carries on with it).</summary>
    static readonly TimeSpan CopyGiveUp = TimeSpan.FromMinutes(15);

    Task<ApplyResult> RestoreFilesAsync(RestoreFiles change, CancellationToken cancellationToken) => SiteAsync(async admin =>
    {
        string? problem = null;
        try
        {
            await admin.RestoreFromRecycleBinAsync(change.SiteUrl, change.BinIds, cancellationToken);
        }
        catch (SharePointException e) when (change.BinIds.Count > 1 && e.Status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
        {
            // One that can't come back (its name is taken where it was) stops the rest: try each alone.
            foreach (var id in change.BinIds)
            {
                try
                {
                    await admin.RestoreFromRecycleBinAsync(change.SiteUrl, [id], cancellationToken);
                }
                catch (SharePointException one) when (one.Status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
                {
                    problem ??= one.Explanation;
                }
            }
        }
        catch (SharePointException e) when (e.Status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
        {
            problem = e.Explanation;
        }
        var still = (await admin.GetRecycleBinAsync(change.SiteUrl, cancellationToken)) is var bin && bin.TryGetProperty("value", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Select(r => r.TryGetProperty("Id", out var id) ? id.GetString() : null).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var left = change.BinIds.Select((id, i) => (id, name: change.Names[i])).Where(x => still.Contains(x.id)).ToArray();
        if (left.Length == 0)
            return new ApplyResult(ChangeOutcome.Applied);
        var reason = problem ?? "SharePoint didn't say why.";
        return left.Length == change.BinIds.Count
            ? new ApplyResult(ChangeOutcome.Failed, reason)
            : new ApplyResult(ChangeOutcome.Failed, $"{left.Length:N0} of {change.BinIds.Count:N0} couldn't be restored: {reason}")
                { Remaining = change with { BinIds = [.. left.Select(x => x.id)], Names = [.. left.Select(x => x.name)] } };
    });

    async Task<ApplyResult> ShareFilesAsync(ShareFiles change, CancellationToken cancellationToken)
    {
        var failed = new List<FileRef>();
        string? problem = null;
        foreach (var file in change.Files)
        {
            try
            {
                await InviteAsync(file, change.People, change, cancellationToken);
            }
            catch (GraphException e) when (e.Status == HttpStatusCode.BadRequest && change.People.Count > 1 && Unresolved(e))
            {
                // Someone SharePoint doesn't know stops the whole invite: give the rest access, and name who.
                foreach (var person in change.People)
                {
                    try
                    {
                        await InviteAsync(file, [person], change, cancellationToken);
                    }
                    catch (GraphException one) when (Unresolved(one))
                    {
                        problem ??= NotKnown(person);
                        if (!failed.Contains(file))
                            failed.Add(file);
                    }
                }
            }
            catch (GraphException e)
            {
                problem ??= Unresolved(e) && change.People.Count == 1 ? NotKnown(change.People[0]) : Describe(e);
                failed.Add(file);
            }
        }
        if (failed.Count == 0)
            return new ApplyResult(ChangeOutcome.Applied);
        return failed.Count == change.Files.Count
            ? new ApplyResult(ChangeOutcome.Failed, problem)
            : new ApplyResult(ChangeOutcome.Failed, $"{failed.Count:N0} of {change.Files.Count:N0} weren't shared with everyone: {problem}") { Remaining = change with { Files = failed } };
    }

    Task InviteAsync(FileRef file, IReadOnlyList<ChangeTarget> people, ShareFiles change, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["recipients"] = new JsonArray([.. people.Select(p => (JsonNode)new JsonObject { ["objectId"] = p.Id })]),
            ["roles"] = new JsonArray(change.CanEdit ? "write" : "read"),
            ["requireSignIn"] = true,
            ["sendInvitation"] = change.SendEmail,
        };
        if (change.SendEmail && !string.IsNullOrWhiteSpace(change.Message))
            body["message"] = change.Message.Trim();
        return graph.PostAsync($"drives/{file.DriveId}/items/{file.ItemId}/invite", body, cancellationToken);
    }

    /// <summary>"One or more users could not be resolved": SharePoint doesn't know them (seen live for unlicensed people).</summary>
    static bool Unresolved(GraphException e) => e.Message.Contains("could not be resolved", StringComparison.OrdinalIgnoreCase);

    static string NotKnown(ChangeTarget person) =>
        $"SharePoint doesn't know {person.Name} yet. People need a license that includes SharePoint or OneDrive before files can be shared with them.";

    async Task<ApplyResult> CopyFilesAsync(CopyFiles change, CancellationToken cancellationToken)
    {
        string driveId, parentId;
        try
        {
            (driveId, parentId) = await CopyDestinationAsync(change, cancellationToken);
        }
        catch (GraphException e) when (e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            return new ApplyResult(ChangeOutcome.Failed, "You can't put files there: add yourself as its site admin, or choose somewhere you can open.");
        }
        var failed = new List<FileRef>();
        string? problem = null;
        foreach (var file in change.Files)
        {
            try
            {
                var watch = await graph.PostAcceptedAsync($"drives/{file.DriveId}/items/{file.ItemId}/copy?@microsoft.graph.conflictBehavior=rename",
                    new JsonObject { ["parentReference"] = new JsonObject { ["driveId"] = driveId, ["id"] = parentId } }, cancellationToken);
                if (watch is not null && await WatchCopyAsync(watch, cancellationToken) is { } why)
                {
                    problem ??= why;
                    failed.Add(file);
                }
            }
            catch (GraphException e)
            {
                problem ??= e.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? "You can't open where it is now: search its place again with Add Me and Search, then copy it."
                    : Describe(e);
                failed.Add(file);
            }
        }
        if (failed.Count == 0)
            return new ApplyResult(ChangeOutcome.Applied);
        return failed.Count == change.Files.Count
            ? new ApplyResult(ChangeOutcome.Failed, problem)
            : new ApplyResult(ChangeOutcome.Failed, $"{failed.Count:N0} of {change.Files.Count:N0} weren't copied: {problem}") { Remaining = change with { Files = failed } };
    }

    /// <summary>The library copies go into (the place's main one) and the folder in it, made when it isn't there.</summary>
    async Task<(string DriveId, string ParentId)> CopyDestinationAsync(CopyFiles change, CancellationToken cancellationToken)
    {
        var uri = new Uri(change.SiteUrl);
        var path = uri.AbsolutePath.TrimEnd('/');
        var drive = await graph.GetAsync(path.Length == 0 ? $"sites/{uri.Host}/drive?$select=id" : $"sites/{uri.Host}:{path}:/drive?$select=id", cancellationToken);
        var driveId = drive.GetProperty("id").GetString()!;
        if (change.Folder.Length == 0)
            return (driveId, (await graph.GetAsync($"drives/{driveId}/root?$select=id", cancellationToken)).GetProperty("id").GetString()!);
        var at = $"drives/{driveId}/root:/{Uri.EscapeDataString(change.Folder)}?$select=id";
        try
        {
            return (driveId, (await graph.GetAsync(at, cancellationToken)).GetProperty("id").GetString()!);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
        {
        }
        try
        {
            var made = await graph.PostAsync($"drives/{driveId}/root/children", new JsonObject
            {
                ["name"] = change.Folder,
                ["folder"] = new JsonObject(),
                ["@microsoft.graph.conflictBehavior"] = "fail",
            }, cancellationToken);
            return (driveId, made.GetProperty("id").GetString()!);
        }
        catch (GraphException e) when (e.Status == HttpStatusCode.Conflict)
        {
            // Made by someone else a moment ago.
            return (driveId, (await graph.GetAsync(at, cancellationToken)).GetProperty("id").GetString()!);
        }
    }

    /// <summary>Waits for a copy Microsoft runs on its side; null when it finished, else why not.</summary>
    async Task<string?> WatchCopyAsync(Uri watch, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        for (var wait = TimeSpan.FromSeconds(1); ; wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 10)))
        {
            await _delay(wait, cancellationToken);
            var state = await graph.GetWithoutTokenAsync(watch, cancellationToken);
            var status = state.ValueKind == JsonValueKind.Object && state.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "completed")
                return null;
            if (status == "failed")
                return state.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                    ? m.GetString()
                    : "Microsoft couldn't finish the copy.";
            if (DateTimeOffset.UtcNow - started > CopyGiveUp)
                return "It's still copying after 15 minutes. Microsoft carries on with it: look in the folder later.";
        }
    }
}
