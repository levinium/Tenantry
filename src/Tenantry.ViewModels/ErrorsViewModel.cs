using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Diagnostics;

namespace Tenantry.ViewModels;

/// <summary>
/// The Errors button (bottom left): every failure shown this session, each with a full report to
/// copy and paste to whoever helps (request, Microsoft's request IDs, where in Tenantry it failed).
/// </summary>
public sealed partial class ErrorsViewModel : ObservableObject
{
    public static ErrorsViewModel Current { get; } = new();

    readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    ErrorsViewModel()
    {
        ErrorLog.Changed += (_, _) =>
        {
            if (_ui is null || SynchronizationContext.Current == _ui)
                Update();
            else
                _ui.Post(_ => Update(), null);
        };
        Update();
    }

    public ObservableCollection<ErrorRow> Reports { get; } = [];

    public bool HasErrors => Reports.Count > 0;

    public string ButtonText => Reports.Count == 1 ? "1 Error" : $"{Reports.Count} Errors";

    public static string Caution { get; } =
        "Reports can include names, addresses and site links from this tenant. Read one before posting it anywhere public.";

    void Update()
    {
        CopyAllLabel = "Copy All";
        Reports.Clear();
        foreach (var report in ErrorLog.Recent)
            Reports.Add(new ErrorRow(report));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ButtonText));
    }

    [RelayCommand]
    void Copy(ErrorRow row)
    {
        Platform.CopyText(row.Report.ToText());
        foreach (var other in Reports)
            other.CopyLabel = other == row ? "Copied" : "Copy";
        CopyAllLabel = "Copy All";
    }

    /// <summary>"Copy All", then "Copied" so the click is seen to have worked (the panel stays open).</summary>
    [ObservableProperty]
    public partial string CopyAllLabel { get; private set; } = "Copy All";

    [RelayCommand]
    void CopyAll()
    {
        Platform.CopyText(string.Join(Environment.NewLine + Environment.NewLine + new string('-', 40) + Environment.NewLine + Environment.NewLine,
            Reports.Select(r => r.Report.ToText())));
        foreach (var row in Reports)
            row.CopyLabel = "Copy";
        CopyAllLabel = "Copied";
    }

    [RelayCommand]
    void Clear() => ErrorLog.Clear();

    /// <summary>The report for an error message shown on screen, for a banner's Copy Details button.</summary>
    public static void CopyDetailsFor(string? shown)
    {
        if (!string.IsNullOrWhiteSpace(shown))
            Platform.CopyText(ErrorLog.DetailsFor(shown));
    }
}

public sealed partial class ErrorRow(ErrorReport report) : ObservableObject
{
    [ObservableProperty]
    public partial string CopyLabel { get; set; } = "Copy";

    public ErrorReport Report { get; } = report;
    public string When => $"{Report.At:h:mm:ss tt}";
    public string Shown => Report.Shown;

    /// <summary>"GET users/delta · 502 Bad Gateway · 5 tries", or the kind of failure.</summary>
    public string Summary => Report.Request is { } r
        ? $"{r.Method} {ShortUrl(r.Url)} · {(r.Status is { } s ? $"{s} {r.Reason}" : "no answer")} · {(r.Attempts == 1 ? "1 try" : $"{r.Attempts} tries")}"
        : Report.Error.GetType().Name;

    static string ShortUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var path = uri.AbsolutePath.Trim('/');
        return path.Length > 70 ? path[..69] + "…" : path;
    }
}
