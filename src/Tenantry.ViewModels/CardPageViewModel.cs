using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.ViewModels.Records;

namespace Tenantry.ViewModels;

/// <summary>A button in a page's header (Run Search, Close Case, Delete Case).</summary>
public sealed partial class PageButton(string text, string icon, string tip, Action act, bool isDanger = false, bool isAccent = false)
{
    public string Text => text;
    public string Icon => icon;
    public string Tip => tip;
    public bool IsDanger => isDanger;
    public bool IsAccent => isAccent;
    public bool IsPlain => !isDanger && !isAccent;

    [RelayCommand]
    void Act() => act();
}

/// <summary>
/// A record page made of cards (8f): a header with flags, what's waiting in Pending changes and
/// buttons, then tabs of <see cref="AppSection"/> cards. The tabs are built again when Pending
/// changes change, so rows show Adding, Removing or Changing with Undo.
/// </summary>
public abstract partial class CardPageViewModel : TabViewModel, IDisposable
{
    protected CardPageViewModel(ChangePipeline? pipeline)
    {
        Pipeline = pipeline;
        if (pipeline is not null)
            pipeline.Changed += OnPendingChanged;
    }

    protected ChangePipeline? Pipeline { get; }

    public abstract string Subtitle { get; }

    public abstract string Icon { get; }

    public abstract IReadOnlyList<HealthChip> Badges { get; }

    /// <summary>The header's buttons, built again with the page.</summary>
    public abstract IReadOnlyList<PageButton> Buttons { get; }

    public IReadOnlyList<RecordSection> Sections { get; protected init; } = [];

    [ObservableProperty]
    public partial RecordSection? SelectedSection { get; set; }

    partial void OnSelectedSectionChanged(RecordSection? value) => _ = value?.LoadAsync();

    /// <summary>What's waiting in Pending changes for this page's object.</summary>
    protected abstract IEnumerable<ChangeOp> Pending { get; }

    public string? PendingText
    {
        get
        {
            var labels = Pending.Select(p => $"{p.Label} → {p.After}").ToArray();
            return labels.Length == 0 ? null : "Pending: " + string.Join("; ", labels);
        }
    }

    public bool HasPending => PendingText is not null;

    /// <summary>A wheel's message while the page works on something the admin started (Run Search).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial string? BusyText { get; protected set; }

    public bool IsBusy => BusyText is not null;

    /// <summary>A line under the header after something the admin started (a failure, or what happens next).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; protected set; }

    public bool HasNotice => Notice is not null;

    void OnPendingChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Shows the header and every shown tab again.</summary>
    protected void Refresh()
    {
        foreach (var name in new[] { nameof(Title), nameof(Subtitle), nameof(Badges), nameof(Buttons), nameof(PendingText), nameof(HasPending) })
            OnPropertyChanged(name);
        foreach (var section in Sections.OfType<AppSection>())
            section.Rebuild();
    }

    /// <summary>A row that takes something away: a verb, or Undo while that waits in Pending changes.</summary>
    protected AppRow Removable(AppRow row, bool canEdit, ChangeOp? waiting, Func<ChangeOp> make, string verb = "Remove", string? tip = null, string state = "Removing") =>
        !canEdit || Pipeline is null ? row
        : waiting is not null ? row.With(state, "Undo", "Take the change back out of Pending changes", () => Pipeline.Unstage(waiting))
        : row.With(null, verb, tip ?? $"{verb} (added to Pending changes)", () => Pipeline.Stage(make()));

    /// <summary>A row for something waiting to be added, with Undo.</summary>
    protected AppRow Adding(string title, string? detail, ChangeOp waiting) =>
        new AppRow { Title = title, Detail = detail }.With("Adding", "Undo", "Don't add it (takes the change out of Pending changes)", () => Pipeline!.Unstage(waiting));

    public virtual void Dispose()
    {
        if (Pipeline is not null)
            Pipeline.Changed -= OnPendingChanged;
    }
}
