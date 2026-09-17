using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Communication.State;

public sealed partial class PositionScheduler
{
    /// <summary>测试接缝：替换单工位驱动（参数为机台、工位；验证分组并发/组内串行时序）；生产为 null。</summary>
    internal Func<long, long, CancellationToken, Task>? DrivePositionOverride { get; set; }

    /// <summary>测试接缝：暴露真实线体权威重读（无 LINE 回退；仅成功结果入缓存）。</summary>
    internal Task<WorkLineRef?> ProbeResolveLineAsync(long equipmentId, CancellationToken ct = default)
        => _routeCache.ResolveLineAsync(equipmentId, ct);

    /// <summary>测试接缝：线体缓存是否含指定机台。</summary>
    internal bool ProbeHasLineCache(long equipmentId) => _routeCache.HasLine(equipmentId);

    /// <summary>测试接缝：标记已对账，以便 ProbeDispatchOnce 进入上料分配。</summary>
    internal void ProbeMarkReconciled() => _isReconciled = true;

    /// <summary>测试接缝：装载点位缓存后跑一轮真实启动对账（①/①b/②/③），不启 HostedService 循环。</summary>
    internal async Task<ReconcileRoundResult> ProbeReconcileAsync(CancellationToken ct = default)
    {
        await LoadPositionCacheAsync(ct);
        return await ReconcileAsync(ct);
    }

    /// <summary>测试接缝：播种 WaitLoad+UploadRequested 候选（不经 PLC 循环）。</summary>
    internal void ProbeSeedUploadCandidate(long equipmentId, long positionId)
    {
        var ctx = _contexts.GetOrAdd((equipmentId, positionId),
            k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
        ctx.State = PositionState.WaitLoad;
        ctx.UploadRequested = true;
        ctx.CurrentTaskId = null;
        ctx.WaitLoadSince = DateTime.Now;
    }

    /// <summary>测试接缝：播种加工位清单与 WaitLoad 上下文。</summary>
    internal void ProbeSeedPosition(long equipmentId, long positionId, long plcId = 0)
    {
        if (!_positions.Exists(p => p.Eq == equipmentId && p.Pos == positionId))
            _positions.Add((equipmentId, positionId, plcId));
        var ctx = _contexts.GetOrAdd((equipmentId, positionId),
            k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
        if (ctx.State is PositionState.Offline)
        {
            ctx.State = PositionState.WaitLoad;
            ctx.WaitLoadSince = DateTime.Now;
        }
    }

    /// <summary>测试接缝：调用真实 <see cref="EnqueueUnloadAsync"/>。</summary>
    internal Task<bool> ProbeEnqueueUnloadAsync(
        long equipmentId, long positionId, bool isOk, string? materialId = null, CancellationToken ct = default)
    {
        var ctx = _contexts.GetOrAdd((equipmentId, positionId),
            k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
        if (materialId is not null) ctx.MaterialId = materialId;
        return EnqueueUnloadAsync(ctx, isOk, ct);
    }

    /// <summary>测试接缝：是否存在直接交接预登记。</summary>
    internal bool ProbeHasExpectedInbound(long equipmentId, long positionId)
        => _inbound.Contains((equipmentId, positionId));

    /// <summary>测试接缝：直接登记一条工序间交接（模拟上游下料已下发）。</summary>
    internal void ProbeSeedExpectedInbound(long equipmentId, long positionId, string sourceTaskId, string? materialId = null)
        => _inbound.Seed((equipmentId, positionId), new InboundHandoff(sourceTaskId, materialId, DateTime.UtcNow));

    /// <summary>测试接缝：是否有待目标工位闸内执行的交接清理请求。</summary>
    internal bool ProbeHasPendingInboundClear(long equipmentId, long positionId)
        => _inbound.HasPendingClear((equipmentId, positionId));

    /// <summary>测试接缝：持工位闸执行一次待清理请求（不跑状态机）。</summary>
    internal async Task ProbeApplyPendingInboundClearAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        var gate = GateFor((equipmentId, positionId));
        await gate.WaitAsync(ct);
        try { await ApplyPendingInboundClearAsync((equipmentId, positionId), ct); }
        finally { gate.Release(); }
    }

    /// <summary>测试接缝：派工队列长度。</summary>
    internal int ProbeQueueCount => _queue.Count;

    /// <summary>测试接缝：读取加工位上下文（状态 / Alarm / 当前 taskId）。</summary>
    internal (PositionState State, bool AlarmRaised, string? CurrentTaskId) ProbeGetContext(
        long equipmentId, long positionId)
    {
        if (!_contexts.TryGetValue((equipmentId, positionId), out var ctx))
            return (PositionState.Offline, false, null);
        return (ctx.State, ctx.AlarmRaised, ctx.CurrentTaskId);
    }

    /// <summary>测试接缝：加工超时进 Alarm 后是否已记下待下料结果。</summary>
    internal bool? ProbeLastTestOk(long equipmentId, long positionId)
        => _contexts.TryGetValue((equipmentId, positionId), out var ctx) ? ctx.LastTestOk : null;

    internal string? ProbeStatusDetail(long equipmentId, long positionId)
        => _contexts.TryGetValue((equipmentId, positionId), out var ctx) ? ctx.StatusDetail : null;

    /// <summary>测试接缝：置加工位上下文，供 <see cref="ProbeDrivePositionOnceAsync"/> 从指定状态起步。</summary>
    internal void ProbeSetContext(long equipmentId, long positionId, PositionState state,
        string? currentTaskId = null, PositionPhase? phase = null,
        long workRecordId = 0, string? materialId = null, bool? lastTestOk = null,
        DateTime? stateEnteredAt = null)
    {
        var ctx = _contexts.GetOrAdd((equipmentId, positionId),
            k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
        ctx.State = state;
        ctx.CurrentTaskId = currentTaskId;
        ctx.Phase = phase;
        ctx.WorkRecordId = workRecordId;
        ctx.MaterialId = materialId;
        ctx.LastTestOk = lastTestOk;
        ctx.AlarmRaised = false;
        ctx.UploadRequested = false;
        if (stateEnteredAt is DateTime entered)
            ctx.StateEnteredAt = entered;
    }

    /// <summary>测试接缝：跑一轮真实状态推进（机台门 → Decide → 动作执行 → SetState），固定副作用顺序。</summary>
    internal async Task<PositionState> ProbeDrivePositionOnceAsync(
        long equipmentId, long positionId, CancellationToken ct = default)
    {
        var ctx = _contexts.GetOrAdd((equipmentId, positionId),
            k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
        await DrivePositionAsync(ctx, ct);
        return ctx.State;
    }

    /// <summary>测试接缝：跑一轮真实 DriveAllAsync（按 PLC 分组并发）。</summary>
    internal Task ProbeDriveAllAsync(CancellationToken ct = default) => DriveAllAsync(ct);

    /// <summary>测试接缝：是否已置"请求上料"标记。</summary>
    internal bool ProbeUploadRequested(long equipmentId, long positionId)
        => _contexts.TryGetValue((equipmentId, positionId), out var ctx) && ctx.UploadRequested;

    /// <summary>测试接缝：装载点位缓存（写点位 / HasMat 点位），供动作执行器测试驱动真实 PLC 写读。</summary>
    internal Task ProbeLoadPositionCacheAsync(CancellationToken ct = default)
        => LoadPositionCacheAsync(ct);

    /// <summary>测试接缝：执行一轮派工循环体（含 IsReconciled 防御门禁）。</summary>
    internal Task ProbeDispatchOnceAsync(CancellationToken ct = default)
        => RunDispatchOnceAsync(ct);
}
