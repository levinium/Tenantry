using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;
using Tenantry.Core.Lists;
using Tenantry.ViewModels.Lists;

namespace Tenantry.ViewModels;

/// <summary>Retention (8f-3): policies, labels, adaptive scopes and events, read live from Purview together.</summary>
public sealed partial class SecurityAreaViewModel
{
    IRetentionRecords? _retention;
    Task<RetentionDirectory?>? _retentionRead;
    bool _retentionShown;

    public DataListViewModel RetentionPolicies { get; private set; } = null!;
    public DataListViewModel RetentionLabels { get; private set; } = null!;
    public DataListViewModel AdaptiveScopes { get; private set; } = null!;

    /// <summary>Event types and events.</summary>
    public RetentionEventsViewModel? RetentionEvents { get; private set; }

    /// <summary>Everything retention as last read, for pages and editors.</summary>
    public RetentionDirectory? Retention { get; private set; }

    /// <summary>Raised after each read, so open policy, label and scope pages can show it.</summary>
    public event EventHandler? RetentionRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRetentionNote))]
    public partial string? RetentionNote { get; private set; }

    public bool HasRetentionNote => RetentionNote is not null;

    /// <summary>Why adaptive scopes can't be used here (licensing), above their list.</summary>
    public string? ScopesNote => Retention?.ScopesUnavailable;
    public bool HasScopesNote => ScopesNote is not null;

    public bool ShowRetentionPolicies => SelectedPage.Key == "retentionpolicies";
    public bool ShowRetentionLabels => SelectedPage.Key == "retentionlabels";
    public bool ShowAdaptiveScopes => SelectedPage.Key == "adaptivescopes";
    public bool ShowRetentionEvents => SelectedPage.Key == "retentionevents";

    void InitRetention(IRetentionRecords? records, ChangePipeline? pipeline, IListStateStore? listState, Func<DateTimeOffset> now, RetentionContext? context,
        Action<AdaptiveScope>? openScope, string? cantEdit)
    {
        _retention = records;
        var editable = context?.Editable == true;
        ListPrimaryAction New(string text, string tip, Action? act) => editable && act is not null
            ? new ListPrimaryAction(text, "Add", true, tip, act)
            : new ListPrimaryAction(text, "Add", false, cantEdit ?? "This tenant can't be edited");
        RetentionPolicies = DataListViewModel.Create(Records.RetentionPages.Policies(p => Records.RetentionPages.PolicyPreview(p, context?.OpenPolicy)),
            async ct => (await ReadRetentionAsync(ct))?.Policies ?? [], row => context?.OpenPolicy((RetentionPolicy)row), listState, now);
        RetentionPolicies.EmptyText = "No retention policies. Use New Policy to keep or delete content, or to publish labels.";
        RetentionPolicies.PrimaryAction = New("New Policy", "Keep or delete content, publish labels, or apply one automatically", () => context!.EditPolicy(null));
        RetentionLabels = DataListViewModel.Create(Records.RetentionPages.Labels(l => Records.RetentionPages.LabelPreview(l, Retention, context?.OpenLabel)),
            async ct => (await ReadRetentionAsync(ct))?.Labels ?? [], row => context?.OpenLabel((RetentionLabel)row), listState, now);
        RetentionLabels.EmptyText = "No retention labels. Use New Label, then publish it with a policy so people can choose it.";
        RetentionLabels.PrimaryAction = New("New Label", "A label people (or a policy) apply to content to keep or delete it", () => context!.EditLabel(null));
        AdaptiveScopes = DataListViewModel.Create(Records.RetentionPages.Scopes(s => Records.RetentionPages.ScopePreview(s, Retention, openScope)),
            async ct => (await ReadRetentionAsync(ct))?.Scopes ?? [], row => openScope?.Invoke((AdaptiveScope)row), listState, now);
        AdaptiveScopes.EmptyText = "No adaptive scopes.";
        AdaptiveScopes.PrimaryAction = New("New Scope", "Users, groups or sites picked by a query, for policies to follow", () => context!.EditScope(null));
        RetentionEvents = context is null ? null : new RetentionEventsViewModel(context, now, ReadRetentionAsync);
        if (pipeline is not null)
        {
            pipeline.Applied += (_, applied) =>
            {
                if (applied.Any(c => c is RetentionChange))
                    _ = RefreshRetention();
            };
            pipeline.Changed += (_, _) => RetentionEvents?.Rebuild();
        }
    }

    Task<RetentionDirectory?> ReadRetentionAsync(CancellationToken cancellationToken) => _retentionRead ??= ReadRetentionNowAsync(cancellationToken);

    async Task<RetentionDirectory?> ReadRetentionNowAsync(CancellationToken cancellationToken)
    {
        if (_retention is null)
        {
            RetentionNote = "Retention isn't available for this tenant.";
            return null;
        }
        try
        {
            Retention = await _retention.GetRetentionAsync(cancellationToken);
            RetentionNote = null;
            OnPropertyChanged(nameof(ScopesNote));
            OnPropertyChanged(nameof(HasScopesNote));
            RetentionRead?.Invoke(this, EventArgs.Empty);
            RetentionEvents?.Rebuild();
            return Retention;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RetentionNote = $"Couldn't read retention from Purview: {Friendly.Message(e)}";
            _retentionRead = null;
            return null;
        }
    }

    [RelayCommand]
    async Task RefreshRetention()
    {
        _retentionRead = null;
        if (!_retentionShown)
        {
            await ReadRetentionAsync(CancellationToken.None);
            return;
        }
        const string Busy = "Reading retention from Purview…";
        await Task.WhenAll(RetentionPolicies.ReloadAsync(busyText: Busy), RetentionLabels.ReloadAsync(busyText: Busy), AdaptiveScopes.ReloadAsync(busyText: Busy));
    }

    void RetentionShown()
    {
        if ((ShowRetentionPolicies || ShowRetentionLabels || ShowAdaptiveScopes || ShowRetentionEvents) && !_retentionShown)
        {
            _retentionShown = true;
            _ = RetentionPolicies.LoadAsync();
            _ = RetentionLabels.LoadAsync();
            _ = AdaptiveScopes.LoadAsync();
        }
        if (ShowRetentionEvents && RetentionEvents is not null)
            _ = RetentionEvents.Section.LoadAsync();
    }
}
