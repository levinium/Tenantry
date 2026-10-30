using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tenantry.Core.Changes;
using Tenantry.Core.PowerShell;
using Tenantry.Graph;
using Tenantry.PowerShell;

namespace Tenantry.Tests;

/// <summary>"Show PowerShell" (the console phase): every change has PowerShell, and all of it parses.</summary>
public sealed class PowerShellScriptTests
{
    /// <summary>Tenantry's own records: nothing in Microsoft 365 to show.</summary>
    static readonly HashSet<Type> NoScript = [typeof(SetTestModeChange), typeof(SecretViewed), typeof(SearchExported), typeof(PowerShellCommand)];

    static readonly IChangeScripts Scripts = new GraphChangeScripts();

    static IEnumerable<Type> ChangeTypes() =>
        typeof(ChangeOp).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.DerivedType);

    /// <summary>Every kind of change, with made-up values, in each of its enum values where it has one.</summary>
    static IEnumerable<ChangeOp> Samples() => ChangeTypes().SelectMany(Sample.Variants).OfType<ChangeOp>();

    [Fact]
    public void Every_change_has_PowerShell_except_Tenantry_records()
    {
        var problems = new List<string>();
        foreach (var change in Samples())
        {
            try
            {
                var script = Scripts.For(change);
                if (NoScript.Contains(change.GetType()))
                {
                    if (script is not null)
                        problems.Add($"{change.GetType().Name}: has a script but is listed as having none");
                }
                else if (script is null && !CantBeMade(change))
                    problems.Add($"{change.GetType().Name}: no script");
                else if (script is null)
                    continue;
                else if (script.Needs == PsService.None || string.IsNullOrWhiteSpace(script.Text))
                    problems.Add($"{change.GetType().Name}: empty script or no service");
            }
            catch (Exception e)
            {
                problems.Add($"{change.GetType().Name}: {e.GetType().Name} {e.Message}");
            }
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Kinds the executor refuses too (Tenantry makes no room or equipment mailboxes).</summary>
    static bool CantBeMade(ChangeOp change) => change is CreateGroup
    {
        Kind: not (Core.Directory.GroupKind.Microsoft365 or Core.Directory.GroupKind.Security or Core.Directory.GroupKind.Distribution
            or Core.Directory.GroupKind.MailEnabledSecurity or Core.Directory.GroupKind.SharedMailbox),
    };

    [Fact]
    public void Typed_text_never_becomes_code_but_earlier_answers_do()
    {
        var text = Ps.Graph("POST", $"groups/{Ps.Ref("group", "id")}/members/$ref", new JsonObject
        {
            ["note"] = "$(Remove-Item x) and `n and \"quoted\"",
            ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/" + Ps.Ref("user", "id"),
        });

        Assert.Contains("-Uri \"https://graph.microsoft.com/v1.0/groups/$($group.id)/members/`$ref\"", text);
        Assert.Contains("directoryObjects/$($user.id)", text);
        Assert.Contains("`$(Remove-Item x) and ``n", text);
        // A forged marker in typed text with something other than a variable path stays text.
        Assert.DoesNotContain("$(Get-Date)", Ps.Expandable("Get-Date)"));
    }

    [Fact]
    public async Task Every_script_parses_in_PowerShell()
    {
        if (PwshLocator.Find() is not { } pwsh)
        {
            Assert.Skip("PowerShell 7 isn't installed here.");
            return;
        }
        // A sample that can't be drawn at all is the other test's to report.
        string? Draw(ChangeOp c)
        {
            try
            {
                return Scripts.For(c)?.Full;
            }
            catch (Exception)
            {
                return null;
            }
        }
        var scripts = Samples().Select(c => (c.GetType().Name, Full: Draw(c))).Where(s => s.Full is not null).ToArray();
        scripts = [.. scripts, ("Batch", ChangeScripts.Batch(Scripts, Samples().Take(12).ToArray()).Full)];
        // TENANTRY_DUMP_SCRIPTS=<file> writes every sample script there, to read them by eye.
        if (Environment.GetEnvironmentVariable("TENANTRY_DUMP_SCRIPTS") is { Length: > 0 } dump)
            await File.WriteAllTextAsync(dump, string.Join("\n\n", scripts.Select(s => $"########## {s.Item1}\n{s.Full}")), TestContext.Current.CancellationToken);
        var file = Path.Combine(Path.GetTempPath(), $"tenantry-scripts-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(scripts.Select(s => new { name = s.Item1, text = s.Item2 })), TestContext.Current.CancellationToken);
        try
        {
            const string check = """
                $items = Get-Content -Raw -LiteralPath $args[0] | ConvertFrom-Json
                foreach ($item in $items) {
                    $tokens = $null; $errors = $null
                    [void][System.Management.Automation.Language.Parser]::ParseInput($item.text, [ref]$tokens, [ref]$errors)
                    foreach ($e in $errors) { "$($item.name): $($e.Message) [$($e.Extent.Text)]" }
                }
                """;
            var info = new ProcessStartInfo(pwsh, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", check, file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("", output.Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Quotes_survive_including_typographic_ones()
    {
        Assert.Equal("'O''Brien'", Ps.Quote("O'Brien"));
        Assert.Equal("'it’’s'", Ps.Quote("it’s"));
        Assert.Equal("$null", Ps.Quote(null));
    }

    [Fact]
    public void Cmdlets_write_switches_lists_and_add_remove_tables()
    {
        var text = Ps.Cmdlet("Set-Mailbox", new Dictionary<string, object?>
        {
            ["Identity"] = "ada@contoso.example",
            ["HiddenFromAddressListsEnabled"] = true,
            ["GrantSendOnBehalfTo"] = new Dictionary<string, object?> { ["@odata.type"] = "#Exchange.GenericHashTable", ["Add"] = new[] { "bo@contoso.example" } },
        });

        Assert.Contains("-Identity 'ada@contoso.example'", text);
        Assert.Contains("-HiddenFromAddressListsEnabled:$true", text);
        Assert.Contains("-GrantSendOnBehalfTo @{ Add = @('bo@contoso.example') }", text);
        Assert.DoesNotContain("odata", text);
    }

    [Fact]
    public void Graph_requests_carry_their_body_as_literal_json()
    {
        var text = Ps.Graph("PATCH", "users/u1", new JsonObject { ["department"] = "Sales $(not code)" });

        Assert.StartsWith("$body = @'", text);
        Assert.Contains("Invoke-MgGraphRequest -Method PATCH -Uri 'https://graph.microsoft.com/v1.0/users/u1' -Body $body", text);
        Assert.Contains("Sales $(not code)", text);
    }

    [Fact]
    public void A_batch_lists_every_change_in_order_and_says_what_it_needs()
    {
        var ada = new ChangeTarget("user", "u1", "Ada Example", "ada@contoso.example");
        var batch = ChangeScripts.Batch(Scripts,
        [
            new SetUserText(ada, "department", "Department", "Sales", "Finance"),
            new SetHiddenFromAddressList(ada, false, true),
            new SetTestModeChange(new ChangeTarget("tenant", "tenant", "Contoso", null), true, false),
        ]);

        Assert.Equal(PsService.Graph | PsService.Exchange, batch.Needs);
        var lines = batch.Text.Split('\n');
        Assert.StartsWith("# 1. Ada Example: Department", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("# 2. Ada Example", StringComparison.Ordinal));
        Assert.Contains("No PowerShell for this one", batch.Text);
        Assert.Equal("Microsoft Graph and Exchange Online", ChangeScripts.ServiceNames(batch.Needs));
    }

    [Fact]
    public void A_change_logged_by_an_older_Tenantry_without_a_field_has_no_PowerShell_rather_than_failing()
    {
        // As the change log reads back an entry written before the field existed: null.
        var old = new SetSiteSetting(new ChangeTarget("site", "s1", "Sales", null), "https://contoso.sharepoint.com/sites/sales",
            "sharing", "Sharing", "Off", "On", Values: null!);

        Assert.Null(Scripts.For(old));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-5", "-5")]
    [InlineData("+1 555 0100", "+1 555 0100")]
    [InlineData("-", "-")]
    [InlineData("Ada, Example", "\"Ada, Example\"")]
    public void Csv_cells_are_never_spreadsheet_formulas(string value, string cell) =>
        Assert.Equal(cell, Tenantry.ViewModels.Lists.DataListViewModel.Csv(value));

    [Fact]
    public void A_password_never_appears_in_its_script()
    {
        var reset = new ResetPassword(new ChangeTarget("user", "u1", "Ada Example", "ada@contoso.example"), true, Chosen: "Typed-Secret-123");

        var script = Scripts.For(reset)!;

        Assert.DoesNotContain("Typed-Secret-123", script.Full);
        Assert.Contains("<new password>", script.Text);
    }

    /// <summary>Builds made-up instances of any change record from its constructor.</summary>
    static class Sample
    {
        const string Text = "zz-tenantry-test-sample";

        public static IEnumerable<object?> Variants(Type type)
        {
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            var parameters = ctor.GetParameters();
            // One with every enum at its first value, then one per other value of each enum.
            yield return ctor.Invoke(parameters.Select(p => Make(p.ParameterType, p.Name ?? "", 0)).ToArray());
            for (var i = 0; i < parameters.Length; i++)
            {
                var enumType = Nullable.GetUnderlyingType(parameters[i].ParameterType) ?? parameters[i].ParameterType;
                if (!enumType.IsEnum)
                    continue;
                foreach (var value in Enum.GetValues(enumType).Cast<object>().Skip(1))
                {
                    var args = parameters.Select(p => Make(p.ParameterType, p.Name ?? "", 0)).ToArray();
                    args[i] = value;
                    yield return ctor.Invoke(args);
                }
            }
            // Booleans the other way (adding vs removing).
            var flipped = parameters.Select(p => p.ParameterType == typeof(bool) ? false : Make(p.ParameterType, p.Name ?? "", 0)).ToArray();
            yield return ctor.Invoke(flipped);
        }

        static object? Make(Type type, string name, int depth)
        {
            if (Nullable.GetUnderlyingType(type) is { } inner)
                return Make(inner, name, depth);
            if (type == typeof(string))
                return name.Contains("json", StringComparison.OrdinalIgnoreCase) ? "{\"id\":\"x\"}"
                    // A mail flow rule's parameters and changes are JSON too.
                    : name is "Parameters" or "Changes" ? "{\"SentToScope\":\"InOrganization\",\"SetSCL\":-1,\"StopRuleProcessing\":true}"
                    // So are a site setting's values and a PIM policy rule.
                    : name == "Values" ? "{\"SharingCapability\":2,\"LockState\":\"NoAccess\",\"DisableSharingForNonOwners\":true}"
                    : name == "Rule" ? "{\"id\":\"Expiration_Admin_Eligibility\",\"isExpirationRequired\":true}"
                    : name.EndsWith("Url", StringComparison.Ordinal) ? "https://contoso.sharepoint.com/sites/" + Text
                    : Text;
            if (type == typeof(bool))
                return true;
            if (type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal))
                return Convert.ChangeType(1, type);
            if (type == typeof(DateTimeOffset))
                return new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            if (type == typeof(DateTime))
                return new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            if (type == typeof(TimeSpan))
                return TimeSpan.FromHours(1);
            if (type == typeof(Guid))
                return Guid.Parse("00000000-0000-0000-0000-000000000001");
            if (type.IsEnum)
                return Enum.GetValues(type).GetValue(0);
            if (type == typeof(ChangeTarget))
                return new ChangeTarget("user", "00000000-0000-0000-0000-000000000002", Text, Text + "@contoso.example");
            if (type == typeof(JsonObject) || type == typeof(JsonNode))
                return new JsonObject { ["id"] = "x" };
            if (type == typeof(JsonElement))
                return JsonDocument.Parse("{\"id\":\"x\"}").RootElement.Clone();
            if (type.IsArray)
            {
                var element = type.GetElementType()!;
                var array = Array.CreateInstance(element, 1);
                array.SetValue(Make(element, name, depth + 1), 0);
                return array;
            }
            if (type.IsGenericType)
            {
                var args = type.GetGenericArguments();
                var definition = type.GetGenericTypeDefinition();
                if (args.Length == 2 && (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(Dictionary<,>)))
                {
                    var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args))!;
                    dictionary[Make(args[0], "key", depth + 1)!] = Make(args[1], name, depth + 1);
                    return dictionary;
                }
                if (args.Length == 1 && typeof(IEnumerable).IsAssignableFrom(type))
                {
                    var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(args))!;
                    list.Add(Make(args[0], name, depth + 1));
                    return type.IsAssignableFrom(list.GetType()) ? list : Activator.CreateInstance(type, list);
                }
            }
            if (depth > 4 || type.IsAbstract || type.IsInterface)
                return null;
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            return ctor is null ? Activator.CreateInstance(type) : ctor.Invoke(ctor.GetParameters().Select(p => Make(p.ParameterType, p.Name ?? "", depth + 1)).ToArray());
        }
    }
}
