using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels;

/// <summary>eDiscovery (8f-1): cases and searches, read live from Purview when first shown.</summary>
public sealed partial class SecurityAreaViewModel
{
    IEDiscoveryRecords? _eDiscovery;
    Task<EdDirectory?>? _eDiscoveryRead;
    bool _eDiscoveryShown;

    /// <summary>eDiscovery cases.</summary>
    public DataListViewModel Cases { get; private set; } = null!;

    /// <summary>Every content search, in a case or not.</summary>
    public DataListViewModel Searches { get; private set; } = null!;

    /// <summary>The cases and searches as last read, for open pages and names already taken.</summary>
    public EdDirectory? EDiscovery { get; private set; }

    /// <summary>Raised after each read, so open case and search pages can show it.</summary>
    public event EventHandler? EDiscoveryRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEDiscoveryNote))]
    public partial string? EDiscoveryNote { get; private set; }

    public bool HasEDiscoveryNote => EDiscoveryNote is not null;

    public bool ShowCases => SelectedPage.Key == "cases";
    public bool ShowSearches => SelectedPage.Key == "searches";

    void InitEDiscovery(IEDiscoveryRecords? records, ChangePipeline? pipeline, IListStateStore? listState, Func<DateTimeOffset> now,
        Action<EdCase>? openCase, Action<EdSearch>? openSearch, Action? newCase, Action? newSearch, string? cantEdit)
    {
        _eDiscovery = records;
        Cases = DataListViewModel.Create(Records.EdLists.Cases(c => Records.EdLists.CasePreview(c, openCase)),
            async ct => (await ReadEDiscoveryAsync(ct))?.Cases ?? [], row => openCase?.Invoke((EdCase)row), listState, now);
        Cases.EmptyText = "No cases match.";
        Cases.PrimaryAction = newCase is not null
            ? new ListPrimaryAction("New Case", "Add", true, "Make an eDiscovery case to hold searches and holds", newCase)
            : new ListPrimaryAction("New Case", "Add", false, cantEdit ?? "This tenant can't be edited");
        Searches = DataListViewModel.Create(Records.EdLists.Searches(s => Records.EdLists.SearchPreview(s, openSearch)),
            async ct => (await ReadEDiscoveryAsync(ct))?.Searches ?? [], row => openSearch?.Invoke((EdSearch)row), listState, now);
        Searches.EmptyText = "No searches match.";
        Searches.PrimaryAction = newSearch is not null
            ? new ListPrimaryAction("New Search", "Add", true, "Search mailboxes, sites and OneDrives outside any case (to search within a case, open the case)", newSearch)
            : new ListPrimaryAction("New Search", "Add", false, cantEdit ?? "This tenant can't be edited");
        if (pipeline is not null)
            pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is EdChange))
                    _ = RefreshEDiscovery();
            };
    }

    /// <summary>One read serves both lists; Refresh starts a new one.</summary>
    Task<EdDirectory?> ReadEDiscoveryAsync(CancellationToken cancellationToken) => _eDiscoveryRead ??= ReadEDiscoveryNowAsync(cancellationToken);

    async Task<EdDirectory?> ReadEDiscoveryNowAsync(CancellationToken cancellationToken)
    {
        if (_eDiscovery is null)
        {
            EDiscoveryNote = "eDiscovery isn't available for this tenant.";
            return null;
        }
        try
        {
            EDiscovery = await _eDiscovery.GetEDiscoveryAsync(cancellationToken);
            EDiscoveryNote = null;
            EDiscoveryRead?.Invoke(this, EventArgs.Empty);
            return EDiscovery;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            EDiscoveryNote = $"Couldn't read eDiscovery from Purview: {Friendly.Message(e)}";
            _eDiscoveryRead = null;
            return null;
        }
    }

    /// <summary>The cases and searches, read now if no page has read them yet.</summary>
    public Task<EdDirectory?> EDiscoveryAsync() => ReadEDiscoveryAsync(CancellationToken.None);

    [RelayCommand]
    async Task RefreshEDiscovery()
    {
        _eDiscoveryRead = null;
        if (!_eDiscoveryShown)
        {
            await ReadEDiscoveryAsync(CancellationToken.None);
            return;
        }
        await Task.WhenAll(Cases.ReloadAsync(busyText: "Reading eDiscovery from Purview…"), Searches.ReloadAsync(busyText: "Reading eDiscovery from Purview…"));
    }

    void EDiscoveryShown()
    {
        if ((ShowCases || ShowSearches) && !_eDiscoveryShown)
        {
            _eDiscoveryShown = true;
            _ = Cases.LoadAsync();
            _ = Searches.LoadAsync();
        }
    }
}
