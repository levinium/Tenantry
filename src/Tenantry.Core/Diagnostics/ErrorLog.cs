using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Tenantry.Core.Http;

namespace Tenantry.Core.Diagnostics;

/// <summary>
/// One failure as a report someone can paste to whoever helps them: what the screen said, what
/// failed underneath, the request and Microsoft's IDs, and where in Tenantry it happened.
/// </summary>
public sealed partial record ErrorReport(int Number, DateTimeOffset At, string Shown, Exception Error)
{
    public RequestDetails? Request { get; } = RequestDetails.Of(Error);

    /// <summary>"14:03 · Couldn't download the directory: Graph 502…" for a list of reports.</summary>
    public string Title => $"{At:h:mm tt} · {Shown}";

    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine("Tenantry error report");
        text.AppendLine($"When: {At:yyyy-MM-dd HH:mm:ss zzz} (UTC {At.UtcDateTime:HH:mm:ss})");
        text.AppendLine($"App: {ErrorLog.AppText}");
        text.AppendLine($"Shown: {Shown}");
        text.AppendLine();
        if (Request is { } request)
        {
            text.AppendLine(request.ToText());
            text.AppendLine();
        }
        var depth = 0;
        for (var e = Error; e is not null && depth < 5; e = e.InnerException, depth++)
        {
            text.AppendLine($"{(depth == 0 ? "Error" : "Caused by")}: {e.GetType().FullName}: {e.Message}");
            if (StackOf(e) is { Length: > 0 } stack)
                text.AppendLine(stack);
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The stack without file paths (they'd show the builder's folders) and without framework
    /// plumbing, which says nothing about where Tenantry was.
    /// </summary>
    static string StackOf(Exception e)
    {
        if (e.StackTrace is not { } stack)
            return "";
        var lines = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("at System.", StringComparison.Ordinal) && !l.StartsWith("---", StringComparison.Ordinal)
                && !l.Contains("CompilerServices", StringComparison.Ordinal))
            .Select(l => FilePath().Replace(l, m => $" (line {m.Groups[1].Value})"))
            .Take(15)
            .Select(l => "   " + l);
        return string.Join(Environment.NewLine, lines);
    }

    [GeneratedRegex(@" in .*:line (\d+)$")]
    private static partial Regex FilePath();
}

/// <summary>
/// The failures shown on screen this session, newest first, kept in memory only (they can name
/// people and sites in the tenant, so nothing is written to disk or sent anywhere). The Errors
/// button lists them for copying.
/// </summary>
public static class ErrorLog
{
    const int Keep = 50;
    static readonly object Gate = new();
    static readonly List<ErrorReport> Reports = [];
    static readonly ConditionalWeakTable<Exception, ErrorReport> Seen = [];
    static int _number;

    /// <summary>Raised after a report is added or the list is cleared (on any thread).</summary>
    public static event EventHandler? Changed;

    public static string AppText { get; } =
        $"Tenantry {Version()} on {RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture}), {RuntimeInformation.FrameworkDescription}";

    static string Version()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ErrorLog).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "?";
        // "0.2.4+0a1b2c3…": keep the short commit.
        var plus = version.IndexOf('+');
        return plus > 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }

    /// <summary>
    /// Keeps a report of a failure the screen is about to show. The same exception shown twice
    /// (the sync line and a list banner) is kept once.
    /// </summary>
    public static ErrorReport Record(Exception error, string shown)
    {
        ErrorReport report;
        lock (Gate)
        {
            if (Seen.TryGetValue(error, out var known))
                return known;
            report = new ErrorReport(++_number, DateTimeOffset.Now, shown, error);
            Seen.Add(error, report);
            Reports.Insert(0, report);
            if (Reports.Count > Keep)
                Reports.RemoveAt(Reports.Count - 1);
        }
        Changed?.Invoke(null, EventArgs.Empty);
        return report;
    }

    public static IReadOnlyList<ErrorReport> Recent
    {
        get
        {
            lock (Gate)
                return [.. Reports];
        }
    }

    /// <summary>
    /// The newest report behind a message on screen, which usually wraps the recorded text
    /// ("Couldn't download the directory: {error} Use Refresh…").
    /// </summary>
    public static ErrorReport? Find(string? shown)
    {
        if (string.IsNullOrWhiteSpace(shown))
            return null;
        lock (Gate)
            return Reports.FirstOrDefault(r => shown.Contains(r.Shown, StringComparison.Ordinal));
    }

    /// <summary>
    /// The text to copy for a message on screen: its report when there is one, else the message
    /// with the app's version and the time, which still helps.
    /// </summary>
    public static string DetailsFor(string shown) =>
        Find(shown) is { } report
            ? report.ToText().Replace($"Shown: {report.Shown}", $"Shown: {shown}", StringComparison.Ordinal)
            : $"Tenantry error report{Environment.NewLine}When: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}App: {AppText}{Environment.NewLine}Shown: {shown}";

    public static void Clear()
    {
        lock (Gate)
            Reports.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
