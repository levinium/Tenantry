using System.Collections;
using System.Collections.Specialized;

namespace Tenantry.App.Lists;

/// <summary>
/// The table's rows as one collection that stays put while its contents change. A new list given
/// to the grid makes it throw its row controls away and build new ones (about 20 ms a visible
/// row, measured: half a second per keystroke in the search box); a reset of the same collection
/// lets it reuse them.
/// </summary>
sealed class RowSource : IList, INotifyCollectionChanged
{
    IReadOnlyList<object> _rows = [];

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public IReadOnlyList<object> Rows => _rows;

    public void Show(IReadOnlyList<object> rows)
    {
        if (ReferenceEquals(rows, _rows))
            return;
        _rows = rows;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public int Count => _rows.Count;
    public object? this[int index] { get => _rows[index]; set => throw new NotSupportedException(); }
    public bool IsReadOnly => true;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public IEnumerator GetEnumerator() => _rows.GetEnumerator();
    public bool Contains(object? value) => value is not null && IndexOf(value) >= 0;

    public int IndexOf(object? value)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (ReferenceEquals(_rows[i], value))
                return i;
        return -1;
    }

    public void CopyTo(Array array, int index)
    {
        for (var i = 0; i < _rows.Count; i++)
            array.SetValue(_rows[i], index + i);
    }

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
