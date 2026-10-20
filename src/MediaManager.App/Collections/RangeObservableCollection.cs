using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MediaManager.App.Collections;

/// <summary>
/// 支持「一次性替换整批数据」的 ObservableCollection。
///
/// 为什么需要：绑定到列表时，逐条 <c>Add</c> 会触发 N 次 CollectionChanged，
/// 虚拟化面板每次都要重新算布局。500 条就是 500 次布局，实测能占到 200~400ms。
/// 这里改成先填内部列表、最后只发一次 Reset 通知。
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>用新的一批数据整体替换旧数据（只发一次 Reset 通知）。</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var buffer = items as IList<T> ?? items.ToList();

        CheckReentrancy();
        Items.Clear();
        foreach (var item in buffer)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
