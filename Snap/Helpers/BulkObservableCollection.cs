using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Snap.Helpers;

/// <summary>
/// ObservableCollection that can swap its whole content with one Reset notification (#16).
/// Replacing thousands of rows one Add at a time raised one CollectionChanged per row
/// (and one layout pass of the list per row).
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces every item with <paramref name="items"/> and raises a single Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
