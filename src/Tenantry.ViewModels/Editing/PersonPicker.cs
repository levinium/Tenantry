using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Editing;

/// <summary>Something a picker offers: a person, a group or a mailbox.</summary>
/// <param name="Type">Graph's type ("user", "group").</param>
/// <param name="SearchText">Lower-case text the query is matched against.</param>
public sealed record PickOption(string Id, string Name, string Detail, string Type, string? Address, string SearchText)
{
    public ChangeTarget Target(string kind) => new(kind, Id, Name, Address);

    public static PickOption Of(UserSummary u)
    {
        var address = u.Kind == UserKind.Guest && u.Mail is { Length: > 0 } m ? m : u.UserPrincipalName;
        return new(u.Id, u.DisplayName, u.Department is { Length: > 0 } d ? $"{address} · {d}" : address, "user", u.UserPrincipalName,
            string.Join(' ', u.DisplayName, u.UserPrincipalName, u.Mail, u.Department).ToLowerInvariant());
    }

    public static PickOption Of(GroupSummary g) =>
        new(g.Id, g.DisplayName, g.Mail is { } mail ? $"{mail} · {GroupSummary.KindName(g.Kind)}" : GroupSummary.KindName(g.Kind),
            "group", g.Mail, string.Join(' ', g.DisplayName, g.Mail, GroupSummary.KindName(g.Kind)).ToLowerInvariant());
}

/// <summary>
/// A search box that offers matching people (or groups) as you type; choosing one hands it to
/// the owner and clears the box. Every word typed must appear somewhere in the option.
/// </summary>
public sealed partial class PersonPicker : ObservableObject
{
    const int MaxResults = 8;

    readonly Func<IEnumerable<PickOption>> _options;
    readonly Action<PickOption> _picked;

    public PersonPicker(string placeholder, Func<IEnumerable<PickOption>> options, Action<PickOption> picked)
    {
        Placeholder = placeholder;
        _options = options;
        _picked = picked;
    }

    public string Placeholder { get; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults), nameof(ShowNoMatch))]
    public partial IReadOnlyList<PickOption> Results { get; private set; } = [];

    public bool HasResults => Results.Count > 0;

    public bool ShowNoMatch => !HasResults && !string.IsNullOrWhiteSpace(Query);

    partial void OnQueryChanged(string value) => Results = Search(value);

    IReadOnlyList<PickOption> Search(string query)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return [];
        return _options()
            .Where(o => words.All(w => o.SearchText.Contains(w, StringComparison.Ordinal)))
            // Names that start with what was typed come first.
            .OrderByDescending(o => o.Name.StartsWith(words[0], StringComparison.CurrentCultureIgnoreCase))
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .ToArray();
    }

    [RelayCommand]
    void Pick(PickOption option)
    {
        Query = "";
        _picked(option);
    }
}

/// <summary>
/// One checkbox on a row ("Member", "Owner", "Full access"): shows the state once pending changes
/// apply, and stages a change when ticked or cleared.
/// </summary>
public sealed partial class RightToggle(string label, bool isOn, bool isPending, bool canEdit, string tip, Action<bool> set) : ObservableObject
{
    public string Label => label;

    public bool CanEdit => canEdit;

    public string Tip => tip;

    public bool IsPending => isPending;

    public bool IsOn
    {
        get => isOn;
        set
        {
            if (value == isOn || !canEdit)
                return;
            isOn = value;
            set(value);
        }
    }
}
