using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Communication.State;

/// <summary>
/// 派工执行对调度器的窄接缝：工位闸、定态、交接登记、线体缓存与孤儿收口。
/// 主循环与对账不经过此接口。
/// </summary>
internal interface IPositionDispatchRuntime
{
    bool CanAutoDispatch { get; }
    IEnumerable<PositionContext> Contexts { get; }
    bool IsEquipmentDispatchHeld(long equipmentId);
    bool HasInbound((long Eq, long Pos) key);
    PositionContext GetOrAddContext(long equipmentId, long positionId);
    SemaphoreSlim GateFor((long Eq, long Pos) key);
    void SetState(PositionContext ctx, PositionState state);
    void InvalidateLineCache(long equipmentId);
    void CacheLine(long equipmentId, WorkLineRef line);
    void LogRouteUnavailableThrottled(long equipmentId, string message);
    Task DeferUnloadAsync(DispatchItem item, PositionContext ctx, string reason, CancellationToken ct);
    Task<bool> CloseOrphanTaskAsync(string taskId, string reason, CancellationToken ct);
    bool TryAddInbound((long Eq, long Pos) key, InboundHandoff handoff);
    bool TryRemoveInboundIfSource((long Eq, long Pos) key, string taskId);
    bool TryMarkInboundDispatched((long Eq, long Pos) key, string localTaskId, string assignedTaskId);
}
