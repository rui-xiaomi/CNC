using CncLoader.Core.State;

namespace CncLoader.Communication.State;

/// <summary>
/// 优先级派工队列实现：按 Priority 降序出队，同优先级 FIFO。
/// RCS 单任务串行执行——由调度器逐条出队下发，队列本身只负责排序。
/// Enqueue/Dequeue/Clear 同锁，避免 ToArray→Clear 窗口与入队竞态丢项。
/// </summary>
public sealed class PriorityDispatchQueue : IDispatchQueue
{
    private readonly object _gate = new();
    private readonly List<DispatchItem> _items = new();

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public void Enqueue(DispatchItem item)
    {
        if (item is null) return;
        lock (_gate) _items.Add(item);
    }

    public DispatchItem? Dequeue()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;

            var bestIdx = 0;
            var best = _items[0];
            for (var i = 1; i < _items.Count; i++)
            {
                if (_items[i].Priority > best.Priority)
                {
                    best = _items[i];
                    bestIdx = i;
                }
            }

            _items.RemoveAt(bestIdx);
            return best;
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }
}
