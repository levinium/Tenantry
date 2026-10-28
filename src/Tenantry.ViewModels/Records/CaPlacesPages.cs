using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>A named location as a card: what it covers, whether it's trusted, which policies use it; Edit and Delete.</summary>
public sealed partial class LocationRow(NamedLocation location, IReadOnlyList<string> usedBy, string? state, Action edit, Action? delete, Action? undo)
{
    public NamedLocation Location => location;
    public string Name => location.Name;
    public string KindText => location.Kind;
    public bool IsTrusted => location.IsTrusted;
    public string Places => location.Places.Count == 0 ? "Nothing yet" : string.Join(", ", location.Places);
    public string? Extra => location.IsIp ? null : string.Join(" · ", new[]
    {
        location.IncludeUnknown ? "Includes unknown countries" : null,
        location.ByGps ? "Judged by the Authenticator app's GPS" : "Judged by IP address",
    }.OfType<string>());
    public bool HasExtra => Extra is not null;
    public string UsedText => usedBy.Count switch
    {
        0 => "No policy uses it",
        1 => $"Used by “{usedBy[0]}”",
        var n => $"Used by {n} policies",
    };
    public bool IsUsed => usedBy.Count > 0;

    public string? StateText => state;
    public bool HasState => state is not null;

    public bool CanDelete => delete is not null;
    public bool CanUndo => undo is not null;
    public string DeleteTip => IsUsed ? "Take it out of the policies that use it first" : "Delete it (added to Pending changes)";

    [RelayCommand]
    void Edit() => edit();

    [RelayCommand]
    void Delete() => delete?.Invoke();

    [RelayCommand]
    void Undo() => undo?.Invoke();
}

/// <summary>
/// Named locations (8b-3): the places Conditional Access policies name, as IP ranges (trusted or not)
/// or countries. Add, change and delete go to Pending changes; a location a policy names can't be deleted.
/// </summary>
public sealed partial class LocationsViewModel : ObservableObject
{
    readonly SecurityAreaViewModel _area;
    readonly ChangePipeline? _pipeline;

    public LocationsViewModel(SecurityAreaViewModel area, ChangePipeline? pipeline, bool canEdit, string? cantEdit)
    {
        _area = area;
        _pipeline = pipeline;
        CanEdit = pipeline is not null && canEdit;
        EditReason = cantEdit ?? "";
        area.PoliciesRead += (_, _) => Rebuild();
        if (pipeline is not null)
            pipeline.Changed += (_, _) => Rebuild();
    }

    public bool CanEdit { get; }
    public string EditReason { get; }

    public ObservableCollection<LocationRow> Locations { get; } = [];

    public bool HasNone => Locations.Count == 0 && _area.CaDirectory is not null;

    public static IReadOnlyList<EditorChoice> Kinds { get; } =
        [new(NamedLocation.IpRanges, "IP Ranges", "Addresses, like an office's internet connection."), new(NamedLocation.Countries, "Countries", "Where sign-ins come from.")];

    /// <summary>Reads the policies (and with them the locations) if they haven't been read.</summary>
    public void Shown() => _area.ReadPolicies();

    IEnumerable<NamedLocationChange> Pending => _pipeline?.Pending.OfType<NamedLocationChange>() ?? [];

    void Rebuild()
    {
        Locations.Clear();
        if (_area.CaDirectory is not { } directory)
            return;
        var pending = Pending.ToArray();
        foreach (var l in directory.Locations)
        {
            var waiting = pending.FirstOrDefault(p => p.Location.Id == l.Id);
            var used = directory.UsingLocation(l.Id).Select(p => p.Name).ToArray();
            Locations.Add(new LocationRow(l, used,
                waiting switch { DeleteNamedLocation => "Deleting", UpdateNamedLocation => "Changing", _ => null },
                () => Open(l),
                CanEdit && used.Length == 0 && waiting is null ? () => _pipeline!.Stage(new DeleteNamedLocation(Target(l), $"{l.Kind}: {string.Join(", ", l.Places)}")) : null,
                waiting is not null ? () => _pipeline!.Unstage(waiting) : null));
        }
        foreach (var made in pending.OfType<CreateNamedLocation>())
            Locations.Add(new LocationRow(CaPlaces.Read(made.Location.Id, made.Json), [], "Adding", () => { }, null, () => _pipeline!.Unstage(made)));
        OnPropertyChanged(nameof(HasNone));
    }

    static ChangeTarget Target(NamedLocation l) => new("location", l.Id, l.Name, null);

    // ---- The form (add or change) -----------------------------------------------------------------

    NamedLocation? _editing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    public partial bool IsFormOpen { get; private set; }

    public string FormTitle => _editing is null ? "New Location" : $"Change “{_editing.Name}”";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIp), nameof(PlacesHint))]
    public partial EditorChoice? Kind { get; set; }

    public bool IsIp => Kind?.Value != NamedLocation.Countries;

    public string PlacesHint => IsIp ? "One range per line, like 203.0.113.0/24 or 2001:db8::/32" : "Two-letter country codes, one per line or separated by commas, like US";

    [ObservableProperty]
    public partial string PlacesText { get; set; } = "";

    [ObservableProperty]
    public partial bool Trusted { get; set; }

    [ObservableProperty]
    public partial bool IncludeUnknown { get; set; }

    [ObservableProperty]
    public partial bool ByGps { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    [RelayCommand]
    void Add() => Open(null);

    void Open(NamedLocation? location)
    {
        if (!CanEdit)
            return;
        _editing = location;
        Name = location?.Name ?? "";
        Kind = Kinds.First(k => k.Value == (location?.Kind ?? NamedLocation.IpRanges));
        PlacesText = string.Join(Environment.NewLine, location?.Places ?? []);
        Trusted = location?.IsTrusted ?? false;
        IncludeUnknown = location?.IncludeUnknown ?? false;
        ByGps = location?.ByGps ?? false;
        Problems = [];
        IsFormOpen = true;
        OnPropertyChanged(nameof(FormTitle));
    }

    IReadOnlyList<string> Places() =>
        [.. PlacesText.Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    static string Describe(bool ip, bool trusted, IReadOnlyList<string> places, bool includeUnknown = false, bool byGps = false) =>
        $"{(ip ? "IP ranges" : "Countries")}{(ip && trusted ? " (trusted)" : "")}: {string.Join(", ", places)}"
        + (!ip && includeUnknown ? "; includes unknown countries" : "") + (!ip && byGps ? "; judged by GPS" : "");

    [RelayCommand]
    void Save()
    {
        var places = Places();
        var ip = IsIp;
        Problems = CaPlaces.Problems(Name, ip, places, _area.CaDirectory?.Locations ?? [], _editing?.Id);
        if (Problems.Count > 0)
            return;
        var json = CaPlaces.Json(Name, ip, ip && Trusted, places, IncludeUnknown, ByGps).ToJsonString();
        var trusted = ip && Trusted;
        if (_editing is null)
            _pipeline!.Stage(new CreateNamedLocation(new ChangeTarget("location", Name.Trim(), Name.Trim(), null), json, Describe(ip, trusted, places, IncludeUnknown, ByGps), trusted));
        else
        {
            var renamed = _editing.Name != Name.Trim() ? $"Named “{Name.Trim()}”; " : "";
            _pipeline!.Stage(new UpdateNamedLocation(Target(_editing), json, Describe(_editing.IsIp, _editing.IsTrusted, _editing.Places, _editing.IncludeUnknown, _editing.ByGps),
                renamed + Describe(ip, trusted, places, IncludeUnknown, ByGps), trusted && !_editing.IsTrusted));
        }
        IsFormOpen = false;
    }

    [RelayCommand]
    void Cancel() => IsFormOpen = false;
}

/// <summary>An authentication strength as a card: what counts, in words, and which policies require it.</summary>
public sealed class StrengthRow(AuthStrength strength, int usedBy)
{
    public string Name => strength.Name;
    public string? Description => strength.Description;
    public bool HasDescription => !string.IsNullOrEmpty(strength.Description);
    public string TypeText => strength.IsBuiltIn ? "Built-In" : "Custom";
    public IReadOnlyList<string> Methods => [.. strength.Combinations.Select(CaPlaces.Combination)];
    public string UsedText => usedBy switch { 0 => "No policy requires it", 1 => "Required by 1 policy", var n => $"Required by {n} policies" };
}

/// <summary>Authentication strengths (8b-3): which sign-in methods each counts, for choosing one in a policy.</summary>
public sealed class StrengthsViewModel : ObservableObject
{
    readonly SecurityAreaViewModel _area;

    public StrengthsViewModel(SecurityAreaViewModel area)
    {
        _area = area;
        area.PoliciesRead += (_, _) => Rebuild();
    }

    public ObservableCollection<StrengthRow> Strengths { get; } = [];

    public void Shown() => _area.ReadPolicies();

    void Rebuild()
    {
        Strengths.Clear();
        if (_area.CaDirectory is not { } directory)
            return;
        foreach (var s in directory.AllStrengths)
            Strengths.Add(new StrengthRow(s, directory.Policies.Count(p => p.Node["grantControls"]?["authenticationStrength"]?["id"]?.GetValue<string>() == s.Id)));
    }
}
