using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Tenantry.App.Views;

/// <summary>
/// A search box over a list's own rows. With <see cref="Target"/> set it hides the rows that don't
/// match (every word typed must appear in a row's name or detail) and hides itself while the list is
/// empty. Without a target its <see cref="Text"/> is bound to a view model that filters for itself.
/// </summary>
public partial class ListSearchBox : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ListSearchBox, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly StyledProperty<string> PlaceholderProperty = AvaloniaProperty.Register<ListSearchBox, string>(nameof(Placeholder), "Search this list");

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public static readonly StyledProperty<ItemsControl?> TargetProperty = AvaloniaProperty.Register<ListSearchBox, ItemsControl?>(nameof(Target));

    /// <summary>The list whose rows are searched.</summary>
    public ItemsControl? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    IDisposable? _count;

    public ListSearchBox() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetProperty)
        {
            if (change.OldValue is ItemsControl old)
                old.ContainerPrepared -= OnContainerPrepared;
            _count?.Dispose();
            if (change.NewValue is ItemsControl list)
            {
                list.ContainerPrepared += OnContainerPrepared;
                _count = list.GetObservable(ItemsControl.ItemCountProperty).Subscribe(new Watch(_ => Apply()));
            }
            Apply();
        }
        else if (change.Property == TextProperty)
            Apply();
    }

    void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var words = Words();
        e.Container.IsVisible = words.Length == 0 || Matches(Target?.ItemFromContainer(e.Container), words);
        UpdateNoMatch();
    }

    void Apply()
    {
        if (Target is not { } list)
            return;
        // Shown while there is something to search, or something typed (so it can be cleared).
        IsVisible = list.ItemCount > 0 || Text.Length > 0;
        var words = Words();
        foreach (var container in list.GetRealizedContainers())
            container.IsVisible = words.Length == 0 || Matches(list.ItemFromContainer(container), words);
        UpdateNoMatch();
    }

    void UpdateNoMatch() =>
        NoMatch.IsVisible = Target is { } list && Text.Trim().Length > 0 && list.ItemCount > 0 && !list.GetRealizedContainers().Any(c => c.IsVisible);

    string[] Words() => (Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // Rows name themselves in one of these; read once per row type.
    static readonly string[] TextProperties = ["Name", "TargetName", "Title", "Label", "DisplayName", "Detail", "Address"];
    static readonly ConcurrentDictionary<Type, PropertyInfo[]> Readers = new();

    static bool Matches(object? item, string[] words)
    {
        if (item is null)
            return true;
        var readers = Readers.GetOrAdd(item.GetType(), t => TextProperties
            .Select(n => t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p is not null && p.PropertyType == typeof(string)).ToArray()!);
        var text = string.Join(' ', readers.Select(p => p.GetValue(item) as string));
        return words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    sealed class Watch(Action<int> next) : IObserver<int>
    {
        public void OnNext(int value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
