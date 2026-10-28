using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

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

    [RelayCommand]
    void Do() => Act?.Invoke();

    [RelayCommand]
    void Open() => OpenAction?.Invoke();
}

/// <summary>A card on an app's page: a heading, a line about it, label/value facts and rows.</summary>
public sealed record AppCard(string Title, string? Caption = null, IReadOnlyList<SummaryLine>? Facts = null, IReadOnlyList<AppRow>? Rows = null, string? EmptyText = null)
{
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
