using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tenantry.Core.Changes;

namespace Tenantry.Graph;

/// <summary>Security &amp; sign-in changes (4c-1): sign-in methods, Temporary Access Passes, roles, app consents, risk.</summary>
public sealed partial class GraphChangeExecutor
{
    /// <summary>
    /// Sends one Graph write, then reads back with <paramref name="read"/> until
    /// <paramref name="done"/> holds; a read answered "not found" counts as an empty answer.
    /// </summary>
    async Task<ApplyResult> GraphCheckedAsync(Func<CancellationToken, Task> write, Func<CancellationToken, Task<JsonElement>>? read,
        Func<JsonElement?, bool> done, CancellationToken cancellationToken)
    {
        try
        {
            await write(cancellationToken);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
        if (read is null)
            return new ApplyResult(ChangeOutcome.Applied);
        foreach (var wait in ReadBackWaits)
        {
            if (wait > TimeSpan.Zero)
                await _delay(wait, cancellationToken);
            JsonElement? answer;
            try
            {
                answer = await read(cancellationToken);
            }
            catch (GraphException e) when (e.Status == HttpStatusCode.NotFound)
            {
                answer = null;
            }
            if (done(answer))
                return new ApplyResult(ChangeOutcome.Applied);
        }
        return new ApplyResult(ChangeOutcome.AppliedNotYetVisible, "Microsoft 365 accepted the change but still shows the old state; it usually appears within a minute.");
    }

    static IEnumerable<JsonElement> Values(JsonElement? page) =>
        page is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    async Task<ApplyResult> RemoveAuthMethodAsync(RemoveAuthMethod change, CancellationToken cancellationToken)
    {
        Task<ApplyResult> Remove() => GraphCheckedAsync(
            ct => graph.DeleteAsync($"users/{change.User.Id}/authentication/{change.Segment}/{change.MethodId}", ct),
            ct => graph.GetAsync($"users/{change.User.Id}/authentication/methods", ct),
            page => !Values(page).Any(m => m.TryGetProperty("id", out var id) && id.GetString() == change.MethodId),
            cancellationToken);
        var result = await Remove();
        // Microsoft won't remove someone's default method while they have others, so the default
        // moves to a method they keep first.
        if (result.Outcome != ChangeOutcome.Failed || result.Detail?.Contains("default authentication method", StringComparison.OrdinalIgnoreCase) != true)
            return result;
        if (await MoveDefaultAsync(change, cancellationToken) is not { } now)
            return new ApplyResult(ChangeOutcome.Failed,
                "This is their default sign-in method, and Microsoft won't remove it while they have other methods. " +
                "Remove their other methods first, or they can pick a new default at https://aka.ms/mysecurityinfo.");
        var retried = await Remove();
        return retried.Succeeded ? retried with { Detail = $"Their default sign-in method is now {now}." } : retried;
    }

    /// <summary>A kept method's name for <c>signInPreferences</c>, in the order a new default is picked.</summary>
    static readonly (Func<JsonElement, bool> Is, string Preference, string Name)[] DefaultChoices =
    [
        (m => Type(m) == "microsoftAuthenticatorAuthenticationMethod", "push", "Microsoft Authenticator"),
        (m => Type(m) == "softwareOathAuthenticationMethod", "oath", "the authenticator app code"),
        (m => Type(m) == "phoneAuthenticationMethod" && PhoneType(m) == "mobile", "sms", "their mobile phone"),
        (m => Type(m) == "phoneAuthenticationMethod" && PhoneType(m) == "alternateMobile", "voiceAlternateMobile", "their alternate phone"),
        (m => Type(m) == "phoneAuthenticationMethod" && PhoneType(m) == "office", "voiceOffice", "their office phone"),
    ];

    static string Type(JsonElement m) => m.TryGetProperty("@odata.type", out var t) ? (t.GetString() ?? "").Replace("#microsoft.graph.", "") : "";

    static string? PhoneType(JsonElement m) => m.TryGetProperty("phoneType", out var t) ? t.GetString() : null;

    /// <summary>Makes another of their methods the default; the method's name, or null when none would take.</summary>
    async Task<string?> MoveDefaultAsync(RemoveAuthMethod change, CancellationToken cancellationToken)
    {
        JsonElement page;
        try
        {
            page = await graph.GetAsync($"users/{change.User.Id}/authentication/methods", cancellationToken);
        }
        catch (GraphException)
        {
            return null;
        }
        var kept = Values(page).Where(m => !(m.TryGetProperty("id", out var id) && id.GetString() == change.MethodId)).ToArray();
        foreach (var (isIt, preference, name) in DefaultChoices)
        {
            if (!kept.Any(isIt))
                continue;
            try
            {
                // Only Microsoft's preview (beta) API has sign-in preferences. A method the tenant's
                // policy doesn't allow for this person is refused, so the next one is tried.
                await graph.PatchAsync($"{Beta}users/{change.User.Id}/authentication/signInPreferences",
                    new JsonObject { ["userPreferredMethodForSecondaryAuthentication"] = preference }, cancellationToken);
                return name;
            }
            catch (GraphException)
            {
            }
        }
        return null;
    }

    Task<ApplyResult> AddPhoneMethodAsync(AddPhoneMethod change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PostAsync($"users/{change.User.Id}/authentication/phoneMethods",
                new JsonObject { ["phoneNumber"] = change.Number, ["phoneType"] = change.PhoneType }, ct),
            ct => graph.GetAsync($"users/{change.User.Id}/authentication/phoneMethods", ct),
            page => Values(page).Any(m => m.TryGetProperty("phoneType", out var t) && t.GetString() == change.PhoneType),
            cancellationToken);

    Task<ApplyResult> UpdatePhoneMethodAsync(UpdatePhoneMethod change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PatchAsync($"users/{change.User.Id}/authentication/phoneMethods/{change.MethodId}",
                new JsonObject { ["phoneNumber"] = change.NewNumber, ["phoneType"] = change.PhoneType }, ct),
            ct => graph.GetAsync($"users/{change.User.Id}/authentication/phoneMethods/{change.MethodId}", ct),
            answer => answer is { } a && a.TryGetProperty("phoneNumber", out var n) && PhoneNumbers.Same(n.GetString(), change.NewNumber),
            cancellationToken);

    /// <summary>Adds the one email method, or changes it when they have one.</summary>
    Task<ApplyResult> SetEmailMethodAsync(SetEmailMethod change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => change.MethodId is { } id
                ? graph.PatchAsync($"users/{change.User.Id}/authentication/emailMethods/{id}", new JsonObject { ["emailAddress"] = change.NewAddress }, ct)
                : graph.PostAsync($"users/{change.User.Id}/authentication/emailMethods", new JsonObject { ["emailAddress"] = change.NewAddress }, ct),
            ct => graph.GetAsync($"users/{change.User.Id}/authentication/emailMethods", ct),
            page => Values(page).Any(m => m.TryGetProperty("emailAddress", out var a) && string.Equals(a.GetString(), change.NewAddress, StringComparison.OrdinalIgnoreCase)),
            cancellationToken);

    const string Beta = "https://graph.microsoft.com/beta/";

    /// <summary>Adds the token to the tenant and gives it to them in one request (Microsoft's preview API).</summary>
    Task<ApplyResult> AddHardwareTokenAsync(AddHardwareToken change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PostAsync(Beta + "directory/authenticationMethodDevices/hardwareOathDevices", new JsonObject
            {
                ["displayName"] = $"{change.Manufacturer} {change.Model} {change.Serial}",
                ["serialNumber"] = change.Serial,
                ["manufacturer"] = change.Manufacturer,
                ["model"] = change.Model,
                ["secretKey"] = change.SecretKey,
                ["timeIntervalInSeconds"] = change.Seconds,
                ["hashFunction"] = change.Hash,
                ["assignTo"] = new JsonObject { ["id"] = change.User.Id },
            }, ct),
            ct => graph.GetAsync($"{Beta}users/{change.User.Id}/authentication/hardwareOathMethods", ct),
            page => Values(page).Any(m => m.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.Object
                                          && d.TryGetProperty("serialNumber", out var s) && s.GetString() == change.Serial),
            cancellationToken);

    Task<ApplyResult> ActivateHardwareTokenAsync(ActivateHardwareToken change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PostAsync($"{Beta}users/{change.User.Id}/authentication/hardwareOathMethods/{change.MethodId}/activate",
                new JsonObject { ["verificationCode"] = change.Code }, ct),
            null, _ => true, cancellationToken);

    /// <summary>The pass comes back once, in the answer, and is handed to the admin (never stored).</summary>
    async Task<ApplyResult> CreateTemporaryAccessPassAsync(CreateTemporaryAccessPass change, CancellationToken cancellationToken)
    {
        try
        {
            var created = await graph.PostAsync($"users/{change.User.Id}/authentication/temporaryAccessPassMethods",
                new JsonObject { ["lifetimeInMinutes"] = change.Minutes, ["isUsableOnce"] = change.OneTime }, cancellationToken);
            var pass = created.TryGetProperty("temporaryAccessPass", out var p) ? p.GetString() : null;
            return pass is null
                ? new ApplyResult(ChangeOutcome.Failed, "Microsoft 365 created the pass but didn't return it.")
                : new ApplyResult(ChangeOutcome.Applied, "Pass them this code; they sign in with it and set up MFA.", Reveal: pass);
        }
        catch (GraphException e)
        {
            return new ApplyResult(ChangeOutcome.Failed, Describe(e));
        }
    }

    Task<ApplyResult> RevokeAppConsentAsync(RevokeAppConsent change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.DeleteAsync($"oauth2PermissionGrants/{change.GrantId}", ct),
            ct => graph.GetAsync($"oauth2PermissionGrants/{change.GrantId}", ct),
            answer => answer is null,
            cancellationToken);

    Task<ApplyResult> SetUserRiskAsync(SetUserRisk change, CancellationToken cancellationToken) =>
        GraphCheckedAsync(
            ct => graph.PostAsync($"identityProtection/riskyUsers/{(change.Compromised ? "confirmCompromised" : "dismiss")}",
                new JsonObject { ["userIds"] = new JsonArray(change.User.Id) }, ct),
            null, _ => true, cancellationToken);
}
