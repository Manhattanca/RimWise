using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RimWise.Desktop.Presentation;

/// <summary>Publishes a completed scan with one reset, rather than thousands of UI insertions.</summary>
public sealed class SnapshotCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> values)
    {
        var snapshot = values.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
