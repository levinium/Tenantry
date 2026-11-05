using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tenantry.Core.Changes;
using Tenantry.Core.Directory;

namespace Tenantry.ViewModels.Records;

/// <summary>A deleted group in the Deleted Groups popup, with Restore (or Undo once staged).</summary>
public sealed partial class DeletedGroupRow(DeletedGroup group, Func<DateTimeOffset> now, Action toggle) : ObservableObject
{
    public DeletedGroup Group => group;
    public string Name => group.DisplayName;

    public string Detail => string.Join(" · ", new[]
    {
        group.Mail ?? GroupSummary.KindName(group.Kind),
        group.DeletedAt is { } at ? $"deleted {Display.Relative(at, now())}" : null,
        group.DeletedAt is { } d ? $"restorable until {d.AddDays(30).ToLocalTime():d MMM}" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty]
    public partial bool IsPending { get; set; }

    [RelayCommand]
    void Restore() => toggle();
}

/// <summary>
/// Microsoft 365 and security groups deleted in the last 30 days, each with Restore. Distribution
/// lists aren't here: Exchange deletes them for good.
/// </summary>
public sealed partial class DeletedGroupsViewModel : ObservableObject, IDisposable
{
    readonly IGroupRecords _records;
    readonly ChangePipeline _pipeline;
    readonly Func<DateTimeOffset> _now;

    public DeletedGroupsViewModel(IGroupRecords records, ChangePipeline pipeline, Func<DateTimeOffset> now)
    {
        _records = records;
        _pipeline = pipeline;
        _now = now;
        _pipeline.Changed += OnPendingChanged;
        _ = LoadAsync();
    }

    public ObservableCollection<DeletedGroupRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LoadError { get; private set; }

    public bool HasError => LoadError is not null;

    public bool IsEmpty => !IsLoading && LoadError is null && Rows.Count == 0;

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            Rows.Clear();
            foreach (var g in (await _records.GetDeletedGroupsAsync()).OrderByDescending(g => g.DeletedAt))
            {
                DeletedGroupRow? row = null;
                row = new DeletedGroupRow(g, _now, () => Toggle(row!));
                Rows.Add(row);
            }
            OnPendingChanged(null, EventArgs.Empty);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LoadError = $"Couldn't read the deleted groups: {Friendly.Message(e)}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    static ChangeTarget Target(DeletedGroup g) => new("group", g.Id, g.DisplayName, g.Mail);

    void Toggle(DeletedGroupRow row)
    {
        if (_pipeline.PendingFor(row.Group.Id, "restore") is { } pending)
            _pipeline.Unstage(pending);
        else
            _pipeline.Stage(new RestoreGroup(Target(row.Group), row.Group.Kind, row.Detail));
    }

    void OnPendingChanged(object? sender, EventArgs e)
    {
        foreach (var row in Rows)
            row.IsPending = _pipeline.PendingFor(row.Group.Id, "restore") is not null;
    }

    public void Dispose() => _pipeline.Changed -= OnPendingChanged;
}
