using System.Text.RegularExpressions;

namespace Tenantry.Core.Changes;

/// <summary>
/// A command run in the PowerShell console, logged once it finished (never staged): it went
/// straight to Microsoft 365, outside Pending changes and Test mode. The output isn't kept.
/// </summary>
/// <param name="Script">What was run, as typed, with typed secrets hidden (<see cref="Masked"/>).</param>
public sealed partial record PowerShellCommand(ChangeTarget Tenant, string Script) : ChangeOp
{
    public override ChangeTarget Target => Tenant;
    public override string Key => "powershell";
    public override string Label => "PowerShell Command";
    public override string Before => "Console";

    /// <summary>The command on one line, shortened (the full text is in <see cref="Script"/>).</summary>
    public override string After
    {
        get
        {
            var line = string.Join(" ", Script.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return line.Length <= 200 ? line : line[..199] + "…";
        }
    }

    public override bool IsNoChange => false;
    public override ChangeOp Since(ChangeOp earlier) => this;

    public const string Hidden = "'(hidden)'";

    /// <summary>
    /// The command with what looks like a typed secret replaced by '(hidden)', so the change log
    /// never keeps one: the text given to ConvertTo-SecureString, a -Password/-Secret/-Token
    /// parameter's value, and a password, secret or token set in a variable, hashtable or JSON.
    /// </summary>
    public static string Masked(string script)
    {
        script = SecureStringText().Replace(script, m => m.Groups["lead"].Value + Hidden);
        script = SecretParameter().Replace(script, m => m.Groups["lead"].Value + Hidden);
        script = SecretAssignment().Replace(script, m => m.Groups["lead"].Value + Hidden);
        return SecretJson().Replace(script, m => m.Groups["lead"].Value + "\"(hidden)\"");
    }

    const string Quoted = "(?:'(?:[^']|'')*'|\"(?:[^\"`]|`.|\"\")*\")";

    [GeneratedRegex(@"(?<lead>ConvertTo-SecureString\s+(?:-String\s+)?)" + Quoted, RegexOptions.IgnoreCase)]
    private static partial Regex SecureStringText();

    [GeneratedRegex(@"(?<lead>-\w*(?:Password|Secret|Token|Passphrase)\w*(?:\s+|\s*:\s*))" + Quoted, RegexOptions.IgnoreCase)]
    private static partial Regex SecretParameter();

    [GeneratedRegex(@"(?<lead>\b\$?\w*(?:password|secret|token|passphrase)\w*\s*=\s*)" + Quoted, RegexOptions.IgnoreCase)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?<lead>""\w*(?:password|secret|token|passphrase)\w*""\s*:\s*)""(?:[^""\\]|\\.)*""", RegexOptions.IgnoreCase)]
    private static partial Regex SecretJson();
}
