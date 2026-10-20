using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tenantry.Auth;
using Tenantry.Core.Capabilities;
using Tenantry.Core.Exchange;
using Tenantry.Core.Permissions;
using Tenantry.Core.Tenancy;
using Tenantry.Exchange;
using Tenantry.Graph;

// tenantry-spike: proves the Phase 1 API assumptions against a real tenant.
//
//   tenantry-spike setup [--bootstrap-client <appId>] [--device-code]
//   tenantry-spike check [--profile <id>] [--device-code]
//
// Output is pass/fail, counts and status codes only. Error text is redacted (addresses, GUIDs,
// the tenant's names and domains) so the output can be pasted anywhere.

var command = args.FirstOrDefault() ?? "help";
string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var deviceCode = args.Contains("--device-code");

return command switch
{
    "setup" => await Setup(),
    "check" => await Check(),
    _ => Help(),
};

int Help()
{
    Console.WriteLine("usage: tenantry-spike setup [--bootstrap-client <appId>] [--device-code]");
    Console.WriteLine("       tenantry-spike check [--profile <id>] [--device-code]");
    return 2;
}

async Task<int> Setup()
{
    var bootstrapClient = Option("--bootstrap-client") ?? TenantryPermissions.GraphCommandLineToolsClientId;
    Console.WriteLine($"Signing in to create the app registration (bootstrap client {(bootstrapClient == TenantryPermissions.GraphCommandLineToolsClientId ? "Microsoft Graph Command Line Tools fallback" : "custom")}).");
    var tokens = await MsalTokenSource.CreateAsync(bootstrapClient, MsalTokenSource.AnyOrganization, cacheFolder: null);
    if (deviceCode)
        tokens.DeviceCodePrompt = m => { Console.WriteLine(m); return Task.CompletedTask; };

    var graph = new GraphClient(new HttpClient(), tokens, TenantryPermissions.BootstrapScopes);
    var result = await new AppRegistrationProvisioner(graph).ProvisionAsync(new Progress<string>(p => Console.WriteLine($"  {p}")));

    var profile = new TenantProfile(Guid.NewGuid().ToString("N")[..12], result.TenantName, result.TenantId,
        result.PrimaryDomain, result.ClientId, DateTimeOffset.UtcNow, tokens.Account);
    var store = new TenantProfileStore();
    var existing = store.Load().FirstOrDefault(p => p.TenantId == result.TenantId);
    if (existing is not null)
        profile = existing with { ClientId = result.ClientId, LastAccount = tokens.Account };
    store.Upsert(profile);

    Console.WriteLine();
    Console.WriteLine($"App registration: {(result.Reused ? "updated existing" : "created")}");
    Console.WriteLine($"Unresolved permissions: {result.Unresolved.Count}");
    foreach (var u in result.Unresolved)
        Console.WriteLine($"  - {u}");
    Console.WriteLine($"Profile saved: {profile.Id} (in {AppPaths.TenantsFile})");
    Console.WriteLine("New registrations can take a minute before sign-in works. Then run: tenantry-spike check");
    return 0;
}

async Task<int> Check()
{
    var profiles = new TenantProfileStore().Load();
    var profileId = Option("--profile");
    var profile = profileId is null
        ? profiles.Count == 1 ? profiles[0] : null
        : profiles.FirstOrDefault(p => p.Id == profileId);
    if (profile is null)
    {
        Console.WriteLine(profiles.Count == 0 ? "No profiles. Run setup first." : $"Pick one with --profile: {string.Join(", ", profiles.Select(p => p.Id))}");
        return 2;
    }

    var tokens = await MsalTokenSource.CreateAsync(profile.ClientId, profile.TenantId, AppPaths.TenantFolder(profile.Id), profile.LastAccount);
    if (deviceCode)
        tokens.DeviceCodePrompt = m => { Console.WriteLine(m); return Task.CompletedTask; };

    var counter = new CountingHandler(new HttpClientHandler());
    var noRedirect = new CountingHandler(new HttpClientHandler { AllowAutoRedirect = false });
    var http = new HttpClient(counter);
    var graph = new GraphClient(http, tokens);
    var runner = new RestCmdletRunner(http, new HttpClient(noRedirect), tokens, profile.TenantId);

    var secrets = new List<string> { profile.TenantId, profile.PrimaryDomain, profile.DisplayName, profile.ClientId };
    var failures = 0;

    async Task Step(string name, Func<Task<string>> run)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var detail = await run();
            Console.WriteLine($"PASS  {name,-28} {detail} ({watch.ElapsedMilliseconds} ms)");
        }
        catch (Exception e)
        {
            failures++;
            Console.WriteLine($"FAIL  {name,-28} {e.GetType().Name}: {Redact(e.Message, secrets)}");
        }
    }

    Console.WriteLine($"OS: {Environment.OSVersion.Platform} {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")}, token cache persistent: {tokens.IsPersistent}");

    string initialPrefix = "";
    await Step("graph.organization", async () =>
    {
        var org = (await graph.GetFirstAsync("organization?$select=verifiedDomains"))!.Value;
        foreach (var d in org.GetProperty("verifiedDomains").EnumerateArray())
        {
            var name = d.GetProperty("name").GetString()!;
            secrets.Add(name);
            if (d.GetProperty("isInitial").GetBoolean())
                initialPrefix = name.Split('.')[0];
        }
        secrets.Add(initialPrefix);
        return $"domains={org.GetProperty("verifiedDomains").GetArrayLength()}";
    });

    await Step("graph.context (roles, plans)", async () =>
    {
        var context = await new TenantContextLoader(graph).LoadAsync();
        var available = Capabilities.All.Count(c => CapabilityEvaluator.Evaluate(c, context).IsAvailable);
        return $"roles={context.RoleTemplateIds.Count} globalAdmin={context.RoleTemplateIds.Contains(DirectoryRoles.GlobalAdministrator)} plans={context.ServicePlans.Count} capabilities={available}/{Capabilities.All.Count}";
    });

    await Step("graph.users (first page)", async () =>
    {
        var page = await graph.GetAsync("users?$select=id&$top=5");
        return $"count={page.GetProperty("value").GetArrayLength()}";
    });

    await Step("graph.authMethods (me, read)", async () =>
    {
        var methods = await graph.GetAsync("me/authentication/methods");
        return $"count={methods.GetProperty("value").GetArrayLength()}";
    });

    await Step("exchange.token", async () => Claims(await tokens.GetTokenAsync([$"{RestCmdletRunner.ExchangeHost}/.default"])));

    await Step("exchange.Get-Mailbox", async () =>
    {
        var before = counter.Count;
        var rows = await runner.InvokeAsync(CmdletEndpoint.Exchange, "Get-Mailbox", new Dictionary<string, object?> { ["ResultSize"] = 2 });
        return $"count={rows.Count} requests={counter.Count - before}";
    });

    await Step("exchange.paging (>1000)", async () =>
    {
        var before = counter.Count;
        var rows = await runner.InvokeAsync(CmdletEndpoint.Exchange, "Get-Recipient", new Dictionary<string, object?> { ["ResultSize"] = 1500 });
        var requests = counter.Count - before;
        return rows.Count <= 1000
            ? $"SKIPPED-ish: only {rows.Count} recipients, paging not exercised (requests={requests})"
            : $"count={rows.Count} requests={requests} (expect 2)";
    });

    await Step("compliance.token", async () => Claims(await tokens.GetTokenAsync([$"{RestCmdletRunner.ComplianceHost}/.default"])));

    await Step("compliance.Get-DlpCompliancePolicy", async () =>
    {
        var rows = await runner.InvokeAsync(CmdletEndpoint.Compliance, "Get-DlpCompliancePolicy");
        var regional = counter.Hosts.Any(h => h.EndsWith(".ps.compliance.protection.outlook.com") && h != "ps.compliance.protection.outlook.com");
        var redirected = noRedirect.Statuses.Any(s => s is >= 300 and < 400);
        return $"count={rows.Count} discoveryRedirect={redirected} regionalHostUsed={regional}";
    });

    await Step("compliance.Get-RetentionCompliancePolicy", async () =>
        $"count={(await runner.InvokeAsync(CmdletEndpoint.Compliance, "Get-RetentionCompliancePolicy")).Count}");

    foreach (var (label, site) in new[] { ("sharepoint.root", ""), ("sharepoint.admin", "-admin") })
    {
        await Step($"{label} (root-audience token)", async () =>
        {
            if (initialPrefix.Length == 0)
                throw new InvalidOperationException("initial domain unknown");
            var token = await tokens.GetTokenAsync([$"https://{initialPrefix}.sharepoint.com/.default"]);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{initialPrefix}{site}.sharepoint.com/_api/web?$select=Id");
            request.Headers.Authorization = new("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json;odata=nometadata");
            using var response = await http.SendAsync(request);
            return $"status={(int)response.StatusCode}";
        });
    }

    await Step("sharepoint.admin (admin-audience token)", async () =>
    {
        var token = await tokens.GetTokenAsync([$"https://{initialPrefix}-admin.sharepoint.com/.default"]);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{initialPrefix}-admin.sharepoint.com/_api/web?$select=Id");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json;odata=nometadata");
        using var response = await http.SendAsync(request);
        return $"status={(int)response.StatusCode}";
    });

    await Step("graph.messageTrace", async () =>
    {
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-1);
        var page = await graph.GetAsync($"admin/exchange/tracing/messageTraces?$filter=receivedDateTime ge {start:yyyy-MM-ddTHH:mm:ssZ} and receivedDateTime le {end:yyyy-MM-ddTHH:mm:ssZ}&$top=1");
        return $"count={page.GetProperty("value").GetArrayLength()}";
    });

    Console.WriteLine();
    Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
    return failures == 0 ? 0 : 1;
}

// Audience and scope/role names from a JWT, which describe the API, not the tenant.
static string Claims(string jwt)
{
    var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
    part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
    using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part)));
    var root = doc.RootElement;
    string Get(string name) => root.TryGetProperty(name, out var v) ? v.ToString() : "-";
    return $"aud={Get("aud")} scp=[{Get("scp")}]";
}

static string Redact(string text, IEnumerable<string> secrets)
{
    text = Regex.Replace(text, @"[\w.+-]+@[\w-]+(\.[\w-]+)+", "<email>");
    text = Regex.Replace(text, @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", "<guid>");
    foreach (var s in secrets.Where(s => s.Length >= 3).OrderByDescending(s => s.Length))
        text = text.Replace(s, "<tenant>", StringComparison.OrdinalIgnoreCase);
    return text.Length > 300 ? text[..300] + "..." : text;
}

sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public int Count;
    public List<string> Hosts { get; } = [];
    public List<int> Statuses { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Count);
        lock (Hosts)
            Hosts.Add(request.RequestUri!.Host);
        var response = await base.SendAsync(request, cancellationToken);
        lock (Statuses)
            Statuses.Add((int)response.StatusCode);
        return response;
    }
}
