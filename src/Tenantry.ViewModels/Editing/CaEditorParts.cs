using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Tenantry.ViewModels.Editing;

/// <summary>A choice in an editor list ("Everyone", "Report-Only").</summary>
public sealed record EditorChoice(string Value, string Label, string? Detail = null)
{
    public bool HasDetail => Detail is not null;

    public override string ToString() => Label;
}

/// <summary>A tickable option ("Windows", "High risk") that tells the editor when it changes.</summary>
public sealed partial class CheckOption(string value, string label, Action changed) : ObservableObject
{
    public string Value => value;
    public string Label => label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();

    /// <summary>Sets it without telling the editor (while it's being filled).</summary>
    internal void Fill(bool on)
    {
#pragma warning disable MVVMTK0034 // set quietly on purpose
        IsChecked = on;
#pragma warning restore MVVMTK0034
    }
}

/// <summary>
/// Things chosen for one slot of a policy (people to include, groups to leave out…), shown as chips
/// with ×, and a picker to add more. Tells the editor when the list changes.
/// </summary>
public sealed partial class ChipPicker : ObservableObject
{
    readonly Action _changed;

    public ChipPicker(string placeholder, Func<IEnumerable<PickOption>> options, Action changed)
    {
        _changed = changed;
        Picker = new PersonPicker(placeholder, () => options().Where(o => Items.All(i => i.Id != o.Id)), Add);
    }

    public PersonPicker Picker { get; }

    public ObservableCollection<PickOption> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public IEnumerable<string> Ids => Items.Select(i => i.Id);

    void Add(PickOption option)
    {
        Items.Add(option);
        OnPropertyChanged(nameof(HasItems));
        _changed();
    }

    [RelayCommand]
    void Remove(PickOption option)
    {
        Items.Remove(option);
        OnPropertyChanged(nameof(HasItems));
        _changed();
    }

    /// <summary>Fills the chips without telling the editor.</summary>
    internal void Fill(IEnumerable<PickOption> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(nameof(HasItems));
    }
}

/// <summary>
/// Apps chosen for a policy, found by typing a name (searched live in Entra), shown as chips with ×.
/// </summary>
public sealed partial class AppPicker : ObservableObject
{
    readonly Func<string, CancellationToken, Task<IReadOnlyList<(string AppId, string Name)>>> _search;
    readonly Action _changed;
    CancellationTokenSource? _typing;

    public AppPicker(Func<string, CancellationToken, Task<IReadOnlyList<(string AppId, string Name)>>> search, Action changed)
    {
        _search = search;
        _changed = changed;
    }

    public ObservableCollection<PickOption> Items { get; } = [];
    public ObservableCollection<PickOption> Results { get; } = [];

    public bool HasItems => Items.Count > 0;

    public IEnumerable<string> Ids => Items.Select(i => i.Id);

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>Searches a moment after typing stops.</summary>
    partial void OnQueryChanged(string value)
    {
        _typing?.Cancel();
        _typing = new CancellationTokenSource();
        _ = SearchAsync(value, _typing.Token);
    }

    async Task SearchAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            IsSearching = true;
            var found = await _search(text, cancellationToken);
            Results.Clear();
            foreach (var (appId, name) in found.Where(f => Items.All(i => i.Id != f.AppId)))
                Results.Add(new PickOption(appId, name, appId, "app", null, name));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Results.Clear();
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    void Pick(PickOption option)
    {
        Items.Add(option);
        Results.Clear();
        Query = "";
        OnPropertyChanged(nameof(HasItems));
        _changed();
    }

    [RelayCommand]
    void Remove(PickOption option)
    {
        Items.Remove(option);
        OnPropertyChanged(nameof(HasItems));
        _changed();
    }

    internal void Fill(IEnumerable<PickOption> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(nameof(HasItems));
    }
}
