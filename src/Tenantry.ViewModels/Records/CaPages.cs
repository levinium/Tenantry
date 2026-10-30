using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels.Records;

/// <summary>A policy in the preview pane: its state and the policy in words, and Open Policy.</summary>
public sealed partial class CaPolicyPreviewViewModel(CaPolicyRow row, Action<CaPolicyRow>? open)
{
    public CaPolicyRow Row => row;
    public string Name => row.Policy.Name;
    public string StateText => CaList.StateBadge(row.Policy).Text;
    public BadgeKind StateKind => CaList.StateBadge(row.Policy).Kind;
    public IReadOnlyList<SummaryLine> Lines => [.. row.Lines.Select(l => new SummaryLine(l.Label, l.Text))];
    public bool CanOpen => open is not null;

    [RelayCommand]
    void Open() => open?.Invoke(row);
}

/// <summary>A choice in a What If list (an app, a platform, a kind of app, a risk level).</summary>
public sealed record WhatIfChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What one policy would do to the sign-in, as a row.</summary>
public sealed class WhatIfRow(WhatIfResult result)
{
    public string Name => result.Name;
    public bool Applies => result.Applies;
    public string StateText => CaPolicy.StateName(result.State);
    public string Verdict => !result.Applies ? "Doesn't Apply" : result.State == "enabled" ? "Applies" : "Would Apply (Report-Only)";
    public BadgeKind VerdictKind => !result.Applies ? BadgeKind.Neutral : result.State == "enabled" ? BadgeKind.Info : BadgeKind.Neutral;
    public string Detail => result.Applies ? result.Requires : result.Reasons.Count == 0 ? "" : "Because " + string.Join("; ", result.Reasons) + ".";
}

/// <summary>
/// What If: pick a person and a sign-in (app, device platform, kind of app, where from, risk), and
/// Entra says which policies would apply, what they'd require, and why the others wouldn't.
/// </summary>
public sealed partial class WhatIfViewModel : ObservableObject
{
    readonly IConditionalAccessRecords? _records;
    readonly IDirectoryIndex _index;

    public WhatIfViewModel(IConditionalAccessRecords? records, IDirectoryIndex index)
    {
        _records = records;
        _index = index;
        Picker = new PersonPicker("Who signs in: type a name", () => GroupAccess.People(index), o => Person = index.User(o.Id));
        App = Apps[0];
        Platform = Platforms[0];
        ClientApp = ClientApps[0];
        SignInRisk = Risks[0];
        UserRisk = Risks[0];
    }

    /// <summary>Common apps by their app IDs (What If needs a real app, not "any").</summary>
    public static IReadOnlyList<WhatIfChoice> Apps { get; } =
    [
        new("00000002-0000-0ff1-ce00-000000000000", "Exchange Online (Mail)"),
        new("00000003-0000-0ff1-ce00-000000000000", "SharePoint and OneDrive"),
        new("cc15fd57-2c6c-4117-a88c-83b1d56b4bbe", "Microsoft Teams"),
        new("4765445b-32c6-49b0-83e6-1d93765276ca", "Microsoft 365 (Office.com)"),
        new("c44b4083-3bb0-49c1-b47d-974e53cbdf3c", "Azure Portal"),
        new("00000003-0000-0000-c000-000000000000", "Microsoft Graph"),
    ];

    public static IReadOnlyList<WhatIfChoice> Platforms { get; } =
        [new("any", "Any Platform"), new("windows", "Windows"), new("macOS", "macOS"), new("iOS", "iOS"), new("android", "Android"), new("linux", "Linux")];

    public static IReadOnlyList<WhatIfChoice> ClientApps { get; } =
        [new("browser", "Browser"), new("mobileAppsAndDesktopClients", "Mobile and Desktop Apps"), new("exchangeActiveSync", "Exchange ActiveSync"), new("other", "Older (Legacy) Sign-In")];

    public static IReadOnlyList<WhatIfChoice> Risks { get; } = [new("none", "None"), new("low", "Low"), new("medium", "Medium"), new("high", "High")];

    public PersonPicker Picker { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPerson), nameof(PersonName))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial UserSummary? Person { get; set; }

    public bool HasPerson => Person is not null;
    public string PersonName => Person?.DisplayName ?? "";

    [RelayCommand]
    void ClearPerson() => Person = null;

    [ObservableProperty] public partial WhatIfChoice App { get; set; }
    [ObservableProperty] public partial WhatIfChoice Platform { get; set; }
    [ObservableProperty] public partial WhatIfChoice ClientApp { get; set; }
    [ObservableProperty] public partial WhatIfChoice SignInRisk { get; set; }
    [ObservableProperty] public partial WhatIfChoice UserRisk { get; set; }
    [ObservableProperty] public partial string Country { get; set; } = "";
    [ObservableProperty] public partial string IpAddress { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    public partial bool IsChecking { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    public ObservableCollection<WhatIfRow> Results { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial string? Summary { get; private set; }

    public bool HasSummary => Summary is not null;

    bool CanCheck => Person is not null && !IsChecking && _records is not null;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    async Task Check()
    {
        if (Person is not { } person || _records is null)
            return;
        IsChecking = true;
        Note = null;
        try
        {
            var results = await _records.WhatIfAsync(new WhatIfInput(person.Id, App.Value, Platform.Value, ClientApp.Value,
                Country.Trim() is { Length: 2 } c ? c.ToUpperInvariant() : null, IpAddress.Trim() is { Length: > 0 } ip ? ip : null, SignInRisk.Value, UserRisk.Value));
            Results.Clear();
            foreach (var r in results)
                Results.Add(new WhatIfRow(r));
            var on = results.Count(r => r.Applies && r.State == "enabled");
            var report = results.Count(r => r.Applies && r.State != "enabled");
            Summary = on == 0 && report == 0 ? $"No policy would apply to {person.DisplayName} signing in like this."
                : $"{(on == 1 ? "1 policy applies" : $"{on} policies apply")}" + (report > 0 ? $", and {report} report-only would" : "") + ".";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Note = $"Couldn't check: {Friendly.Message(e)}";
        }
        finally
        {
            IsChecking = false;
        }
    }
}
