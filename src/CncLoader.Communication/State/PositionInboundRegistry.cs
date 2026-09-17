using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace CncLoader.Communication.State;

/// <summary>
/// 工序间交接的进程内单例视图：待入库登记、待闸内清理、COMPLETED 超宽限告警去重。
/// 派工与主循环必须共用同一实例，禁止各持一份字典。
/// </summary>
internal sealed class PositionInboundRegistry
{
    private readonly ConcurrentDictionary<(long Eq, long Pos), InboundHandoff> _expectedInbound = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), InboundClearRequest> _pendingInboundClears = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), byte> _inboundCompletedGraceWarned = new();

    public bool Contains((long Eq, long Pos) key) => _expectedInbound.ContainsKey(key);

    public bool TryGet((long Eq, long Pos) key, [NotNullWhen(true)] out InboundHandoff? handoff)
        => _expectedInbound.TryGetValue(key, out handoff);

    public bool TryAdd((long Eq, long Pos) key, InboundHandoff handoff)
        => _expectedInbound.TryAdd(key, handoff);

    public void Seed((long Eq, long Pos) key, InboundHandoff handoff)
        => _expectedInbound[key] = handoff;

    public bool TryRemove((long Eq, long Pos) key, [NotNullWhen(true)] out InboundHandoff? handoff)
    {
        var ok = _expectedInbound.TryRemove(key, out var removed);
        if (ok) _inboundCompletedGraceWarned.TryRemove(key, out _);
        handoff = removed;
        return ok;
    }

    /// <summary>仅当当前值仍是 <paramref name="expected"/> 时删除（防误删同位新登记）。</summary>
    public bool TryRemoveExact((long Eq, long Pos) key, InboundHandoff expected)
    {
        if (!_expectedInbound.TryRemove(new KeyValuePair<(long Eq, long Pos), InboundHandoff>(key, expected)))
            return false;
        _inboundCompletedGraceWarned.TryRemove(key, out _);
        return true;
    }

    public void RequestClearBySourceTask(string taskId, string reason)
    {
        foreach (var kv in _expectedInbound)
        {
            if (string.Equals(kv.Value.SourceTaskId, taskId, StringComparison.Ordinal))
                _pendingInboundClears[kv.Key] = new InboundClearRequest(taskId, reason);
        }
    }

    public bool HasPendingClear((long Eq, long Pos) key) => _pendingInboundClears.ContainsKey(key);

    public bool TryTakePendingClear((long Eq, long Pos) key, out InboundClearRequest request)
        => _pendingInboundClears.TryRemove(key, out request!);

    public bool TryWarnOverdueOnce((long Eq, long Pos) key)
        => _inboundCompletedGraceWarned.TryAdd(key, 0);

    /// <summary>消费交接时无论登记是否仍在，都清超期告警去重（与拆分前 ConsumeInbound 一致）。</summary>
    public void ForgetWarned((long Eq, long Pos) key)
        => _inboundCompletedGraceWarned.TryRemove(key, out _);

    public bool TryRemoveIfSource((long Eq, long Pos) key, string taskId)
    {
        if (!_expectedInbound.TryGetValue(key, out var handoff)
            || !string.Equals(handoff.SourceTaskId, taskId, StringComparison.Ordinal))
            return false;
        return TryRemoveExact(key, handoff);
    }

    public bool TryMarkDispatched((long Eq, long Pos) key, string localTaskId, string assignedTaskId)
    {
        while (_expectedInbound.TryGetValue(key, out var current))
        {
            if (current.IsDispatched
                && string.Equals(current.SourceTaskId, assignedTaskId, StringComparison.Ordinal))
                return true;
            if (!string.Equals(current.SourceTaskId, localTaskId, StringComparison.Ordinal))
                return false;
            var next = current with
            {
                SourceTaskId = assignedTaskId,
                IsDispatched = true
            };
            if (_expectedInbound.TryUpdate(key, next, current)) return true;
        }
        return false;
    }
}
