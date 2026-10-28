using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.ViewModels.Editing;

namespace Tenantry.ViewModels.Records;

/// <summary>One line on an app's page: a secret, a permission, a person given access; with its flags and an optional button.</summary>
public sealed partial class AppRow
{
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>Monospace text under it (a thumbprint, a redirect URI).</summary>
    public string? Code { get; init; }
    public bool HasCode => !string.IsNullOrEmpty(Code);

    public IReadOnlyList<HealthChip> Badges { get; init; } = [];

    /// <summary>Plain chips under it (the scopes in a consent).</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];
    public bool HasChips => Chips.Count > 0;

    /// <summary>"Adding" or "Removing" while a change waits in Pending changes.</summary>
    public string? StateText { get; init; }
    public bool HasState => StateText is not null;

    public string? ActionText { get; init; }
    public string? ActionTip { get; init; }
    internal Action? Act { get; init; }
    public bool HasAction => ActionText is not null && Act is not null;
    public bool IsUndo => ActionText == "Undo";

    internal Action? OpenAction { get; init; }
    public bool CanOpen => OpenAction is not null;

    /// <summary>A second, gentler button beside the first (Restore beside Delete Permanently).</summary>
    public string? AltText { get; init; }
    public string? AltTip { get; init; }
    internal Action? AltAct { get; init; }
    public bool HasAlt => AltText is not null && AltAct is not null;

    [RelayCommand]
    void Alt() => AltAct?.Invoke();

    [RelayCommand]
    void Do() => Act?.Invoke();

    [RelayCommand]
    void Open() => OpenAction?.Invoke();

    /// <summary>The same row with a button: what it does now (Remove, Revoke) or, while that waits in Pending changes, Undo.</summary>
    internal AppRow With(string? state, string? actionText, string? tip, Action? act) => new()
    {
        Title = Title,
        Detail = Detail,
        Code = Code,
        Badges = Badges,
        Chips = Chips,
        OpenAction = OpenAction,
        StateText = state,
        ActionText = actionText,
        ActionTip = tip,
        Act = act,
    };
}

/// <summary>A card on an app's page: a heading, a line about it, label/value facts and rows.</summary>
/// <param name="Form">Something to fill in above the rows (add a secret, pick a person, switches), shown through its own template.</param>
public sealed record AppCard(string Title, string? Caption = null, IReadOnlyList<SummaryLine>? Facts = null, IReadOnlyList<AppRow>? Rows = null, string? EmptyText = null,
    object? Form = null)
{
    public bool HasForm => Form is not null;
    public bool HasCaption => !string.IsNullOrEmpty(Caption);
    public IReadOnlyList<SummaryLine> FactLines => Facts ?? [];
    public bool HasFacts => FactLines.Count > 0;
    public IReadOnlyList<AppRow> RowItems => Rows ?? [];
    public bool HasRows => RowItems.Count > 0;
    public bool ShowEmpty => !HasRows && !HasFacts && EmptyText is not null;
}

/// <summary>
/// A tab of an app's page: cards built from what the page has read (the list's data, or a live
/// read of the app's details). Built again when the page's data or Pending changes change.
/// </summary>
public sealed class AppSection(string key, string title, string icon, Func<CancellationToken, Task<IReadOnlyList<AppCard>>> build, string? loadingText = null) : LiveSection
{
    public override string Key => key;
    public override string Title => title;
    public override string Icon => icon;

    public override string LoadingText => loadingText ?? base.LoadingText;

    public ObservableCollection<AppCard> Cards { get; } = [];

    protected override async Task ReadAsync(CancellationToken cancellationToken)
    {
        var cards = await build(cancellationToken);
        Cards.Clear();
        foreach (var card in cards)
            Cards.Add(card);
    }

    /// <summary>Builds the cards again if it has been shown (cheap when the page's data is already read).</summary>
    public void Rebuild() => ReadAgainIfShown();
}

/// <summary>An app in the preview pane: what it is, its flags, a few facts, and a button to open its page.</summary>
public sealed partial class AppPreviewViewModel(string name, string? subtitle, IReadOnlyList<HealthChip> badges, IReadOnlyList<SummaryLine> facts, string openText, Action? open)
{
    public string Name => name;
    public string? Subtitle => subtitle;
    public bool HasSubtitle => !string.IsNullOrEmpty(subtitle);
    public IReadOnlyList<HealthChip> Badges => badges;
    public IReadOnlyList<SummaryLine> Facts => facts;
    public string OpenText => openText;
    public bool CanOpen => open is not null;

    [RelayCommand]
    void Open() => open?.Invoke();
}

/// <summary>A new client secret: a description and how long it lasts. Add puts it in Pending changes.</summary>
public sealed partial class AddSecretForm(Action<string, int> add) : ObservableObject
{
    public static IReadOnlyList<EditorChoice> Lengths { get; } =
    [
        new("6", "6 Months", "Rotate often: the safest."),
        new("12", "12 Months", "A year, as Entra suggests."),
        new("24", "24 Months", "The longest Entra allows."),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem))]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial EditorChoice Length { get; set; } = Lengths[1];

    public string? Problem => Description.Trim().Length > 0 ? null : _tried ? "Say what it's for, like \"Nightly export\"." : null;
    public bool HasProblem => Problem is not null;

    bool _tried;

    [RelayCommand]
    void Add()
    {
        _tried = true;
        if (Description.Trim().Length == 0)
        {
            OnPropertyChanged(nameof(Problem));
            OnPropertyChanged(nameof(HasProblem));
            return;
        }
        add(Description.Trim(), int.Parse(Length.Value, System.Globalization.CultureInfo.InvariantCulture));
        _tried = false;
        Description = "";
    }
}

/// <summary>A picker that adds whoever is chosen (an owner, or someone given access as the chosen role).</summary>
public sealed partial class AppPickForm : ObservableObject
{
    public AppPickForm(PersonPicker picker, IReadOnlyList<EditorChoice>? roles = null)
    {
        Picker = picker;
        Roles = roles ?? [];
        Role = Roles.FirstOrDefault();
    }

    public PersonPicker Picker { get; }

    public IReadOnlyList<EditorChoice> Roles { get; private set; }

    /// <summary>Only when the app offers more than plain access.</summary>
    public bool HasRoles => Roles.Count > 1;

    [ObservableProperty]
    public partial EditorChoice? Role { get; set; }

    public void SetRoles(IReadOnlyList<EditorChoice> roles)
    {
        Roles = roles;
        if (Role is null || roles.All(r => r.Value != Role.Value))
            Role = roles.FirstOrDefault();
        OnPropertyChanged(nameof(Roles));
        OnPropertyChanged(nameof(HasRoles));
    }
}

/// <summary>One on/off setting; flipping it puts the change in Pending changes (flipping it back takes it out).</summary>
public sealed partial class AppToggle : ObservableObject
{
    readonly Action<bool> _set;
    bool _filling;

    public AppToggle(string label, string detail, bool value, bool canEdit, Action<bool> set)
    {
        Label = label;
        Detail = detail;
        CanEdit = canEdit;
        _set = set;
        _filling = true;
        IsOn = value;
        _filling = false;
    }

    public string Label { get; }
    public string Detail { get; }
    public bool CanEdit { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    /// <summary>"Changing" while it waits in Pending changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasState))]
    public partial string? StateText { get; set; }

    public bool HasState => StateText is not null;

    partial void OnIsOnChanged(bool value)
    {
        if (!_filling)
            _set(value);
    }

    /// <summary>Shows the value as read (or as pending) without staging anything.</summary>
    public void Show(bool value, bool pending)
    {
        _filling = true;
        IsOn = value;
        _filling = false;
        StateText = pending ? "Changing" : null;
    }
}

/// <summary>Switches on a card.</summary>
public sealed record AppToggleForm(IReadOnlyList<AppToggle> Toggles);

/// <summary>A button on a card (Grant Admin Consent), with a line about what it does.</summary>
public sealed partial class AppButtonForm(string text, string detail, string tip, Action act) : ObservableObject
{
    public string Text => text;
    public string Detail => detail;
    public string Tip => tip;

    [RelayCommand]
    void Act() => act();
}