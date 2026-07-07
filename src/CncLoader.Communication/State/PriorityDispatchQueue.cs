using System.Collections.Concurrent;
using CncLoader.Core.State;

namespace CncLoader.Communication.State;

/// <summary>
/// 优先级派工队列实现：按 Priority 降序出队，同优先级 FIFO。
/// RCS 单任务串行执行——由调度器逐条出队下发，队列本身只负责排序。
/// </summary>
public sealed class PriorityDispatchQueue : IDispatchQueue
{
    private readonly ConcurrentQueue<DispatchItem> _fifo = new();
    private int _count;

    public int Count => _count;

    public void Enqueue(DispatchItem item)
    {
        if (item is null) return;
        _fifo.Enqueue(item);
        Interlocked.Increment(ref _count);
    }

    public DispatchItem? Dequeue()
    {
        // 取 priority 最大者（同优先级保持 FIFO）。队列规模小（加工位数 ×2），线性扫描可接受。
        if (_fifo.IsEmpty) return null;
        DispatchItem? best = null;
        
        // 简单做法：导出快照找最大，再从原队列重建剔除一个。规模小，开销可忽略。
        var items = _fifo.ToArray();
        if (items.Length == 0) return null;
        best = items[0];
        int bestIdx = 0;
        for (var i = 1; i < items.Length; i++)
        {
            if (items[i].Priority > best.Priority)
            {
                best = items[i];
                bestIdx = i;
            }
        }
        // 重建队列（剔除 bestIdx）
        _fifo.Clear();
        for (var i = 0; i < items.Length; i++)
            if (i != bestIdx) _fifo.Enqueue(items[i]);
        Interlocked.Decrement(ref _count);
        return best;
    }

    public void Clear()
    {
        _fifo.Clear();
        Interlocked.Exchange(ref _count, 0);
    }
}
