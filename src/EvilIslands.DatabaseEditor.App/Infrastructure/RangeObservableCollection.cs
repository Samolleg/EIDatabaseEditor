using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EvilIslands.DatabaseEditor.App.Infrastructure;

internal sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var values = source as IReadOnlyCollection<T> ?? source.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var value in values)
        {
            Items.Add(value);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
