using System.Collections.Concurrent;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>§6.3 启动三方对账 IO：经 <see cref="StartupReconcileCoordinator"/> fail-closed 编排 ①/①b/②/③。</summary>
internal sealed class PositionStartupReconciler
{
    private readonly StartupReconcileCoordinator _coordinator = new();
    private readonly IRcsTaskStore _taskStore;
    private readonly IRcsTaskService _taskSvc;
    private readonly IRouteResolver _routes;
    private readonly ISlotAccountService _slots;
    private readonly IAlarmEventService _alarms;
    private readonly ISignalStateStore _store;
    private readonly PositionInboundRegistry _inbound;
    private readonly ConcurrentDictionary<(long Eq, long Pos), PositionContext> _contexts;
    private readonly RcsCallbackNotifier? _notifier;
    private readonly IPositionStartupReconcileHost _host;
    private readonly ILogger _logger;

    public PositionStartupReconciler(
        IRcsTaskStore taskStore,
        IRcsTaskService taskSvc,
        IRouteResolver routes,
        ISlotAccountService slots,
        IAlarmEventService alarms,
        ISignalStateStore store,
        PositionInboundRegistry inbound,
        ConcurrentDictionary<(long Eq, long Pos), PositionContext> contexts,
        RcsCallbackNotifier? notifier,
        IPositionStartupReconcileHost host,
        ILogger logger)
    {
        _taskStore = taskStore;
        _taskSvc = taskSvc;
        _routes = routes;
        _slots = slots;
        _alarms = alarms;
        _store = store;
        _inbound = inbound;
        _contexts = contexts;
        _notifier = notifier;
        _host = host;
        _logger = logger;
    }

    public Task<ReconcileRoundResult> ReconcileAsync(CancellationToken ct)
    {
        var unfinished = new List<string>();
        return _coordinator.RunAsync(
            c => ReconcilePhaseOneAsync(unfinished, c),
            c => ReconcilePhaseOneBAsync(unfinished, c),
            c => ReconcilePhaseTwoAsync(unfinished, c),
            ReconcilePhaseThreeAsync,
            ct);
    }

    /// <summary>① RCS 未完结任务绑定回工位。</summary>
    private async Task<ReconcilePhaseResult> ReconcilePhaseOneAsync(List<string> unfinished, CancellationToken ct)
    {
        unfinished.Clear();
        unfinished.AddRange(await _taskStore.GetUnfinishedTaskIdsAsync(ct));
        var rows = await _taskStore.GetByTaskIdsAsync(unfinished, ct);
        foreach (var taskId in unfinished)
        {
            if (!rows.TryGetValue(taskId, out var row)
                || row.PositionId is null || row.EquipmentId is null) continue;
            var key = (Eq: row.EquipmentId.Value, Pos: row.PositionId.Value);
            var ctx = _contexts.GetOrAdd(key, k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
            ctx.CurrentTaskId = taskId;
            ctx.Phase = row.TaskType == "1" ? PositionPhase.Unload : PositionPhase.Upload;
            ctx.State = PositionState.Dispatching; // 等 RCS 状态明确后由 loop / ①b 推进
            _logger.LogInformation("对账①：任务 {TaskId} 绑定回 EQ{Eq} POS{Pos} 阶段 {Phase}", taskId, key.Eq, key.Pos, ctx.Phase);
            if (row.TaskType == "1")
                await RestoreInboundHandoffAsync(row, taskId, key, ct);
        }
        return ReconcilePhaseResult.Ok(ReconcilePhase.One);
    }

    /// <summary>
    /// 重建工序间交接登记（P1-4）：登记只在内存，重启即丢；未完结下料任务终点是下游加工位时补回，
    /// 否则件送到后目标工位被对账③/运行期判「有料无任务」告警，物料码丢失。
    /// </summary>
    private async Task RestoreInboundHandoffAsync(RcsTaskRow row, string taskId, (long Eq, long Pos) source, CancellationToken ct)
    {
        if (await _routes.ResolveHandoffDestinationAsync(row.ToCode, row.ReqParam, ct) is not { } dest) return;
        if (dest.EquipmentId == source.Eq && dest.PositionId == source.Pos) return;

        var key = (dest.EquipmentId, dest.PositionId);
        var handoff = new InboundHandoff(taskId, row.MaterialId, (row.DispatchTime ?? row.SendTime).ToUniversalTime());
        if (_inbound.TryAdd(key, handoff))
        {
            _logger.LogInformation("对账①：重建交接登记 源任务 {TaskId} 物料 {El} → EQ{Eq} POS{Pos}",
                taskId, row.MaterialId ?? "—", dest.EquipmentId, dest.PositionId);
        }
        else if (_inbound.TryGet(key, out var existing)
                 && !string.Equals(existing.SourceTaskId, taskId, StringComparison.Ordinal))
        {
            _logger.LogWarning("对账①：EQ{Eq} POS{Pos} 已有交接登记（源 {Existing}），未完结任务 {TaskId} 的登记未重建",
                dest.EquipmentId, dest.PositionId, existing.SourceTaskId ?? "—", taskId);
        }
    }

    /// <summary>①b query 终态收口；Query.Success=false 记失败。</summary>
    private async Task<ReconcilePhaseResult> ReconcilePhaseOneBAsync(List<string> unfinished, CancellationToken ct)
    {
        var (phase, settled) = await SettleTerminalTasksOnReconcileAsync(unfinished, ct);
        if (!phase.Succeeded) return phase;
        if (settled.Count > 0)
        {
            unfinished.RemoveAll(id => settled.Contains(id));
            _logger.LogInformation("对账①b：收口终态任务 {N} 个", settled.Count);
        }
        return ReconcilePhaseResult.Ok(ReconcilePhase.OneB);
    }

    /// <summary>② 陈旧预记回滚 + COMPLETED 预记 PLC 门补落账。</summary>
    private async Task<ReconcilePhaseResult> ReconcilePhaseTwoAsync(List<string> unfinished, CancellationToken ct)
    {
        var n = await _slots.RollbackStaleReservationsAsync(unfinished, _host.StaleReservationGrace, ct);
        if (n > 0) _logger.LogInformation("对账②：回滚陈旧槽位预记 {N} 个", n);
        var c = await _host.SettleCompletedPendingWithPlcAsync(unfinished, ct);
        if (c > 0) _logger.LogInformation("对账②：PLC 门补落账 COMPLETED 预记 {N} 个", c);
        return ReconcilePhaseResult.Ok(ReconcilePhase.Two);
    }

    /// <summary>③ PLC 账实核对：有料无任务 → 工位 Alarm（不等于全局失败）。过程异常 → 全局失败。</summary>
    private async Task<ReconcilePhaseResult> ReconcilePhaseThreeAsync(CancellationToken ct)
    {
        foreach (var (eq, pos, _) in _host.Positions)
        {
            _contexts.TryGetValue((eq, pos), out var ctx);
            if (ctx is not null && !string.IsNullOrEmpty(ctx.CurrentTaskId)) continue; // 已有在途任务，正常
            if (_inbound.Contains((eq, pos))) continue;

            var machine = _store.GetMachine(eq);
            if (machine is null || !machine.IsFresh(_host.SignalMaxAge) || !machine.PlcOnline) continue; // PLC 未上线/快照过期，交给运行态离线处理
            var tmp = new PositionContext { EquipmentId = eq, PositionId = pos };
            var hasMat = await _host.ReadHasMatFreshAsync(tmp, ct);
            if (hasMat == true)
            {
                var ctxAlarm = _contexts.GetOrAdd((eq, pos), k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
                ctxAlarm.AlarmRaised = true;
                _host.SetState(ctxAlarm, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync($"RECONCILE-EQ{eq}-POS{pos}",
                    $"启动对账：EQ{eq} POS{pos} PLC 有料但无绑定任务/无待交接（账实不符），请人工确认后点恢复", ct);
                _logger.LogWarning("对账③：EQ{Eq} POS{Pos} PLC 有料但无任务（账实不符）→ ALARM 等人工确认", eq, pos);
            }
        }
        return ReconcilePhaseResult.Ok(ReconcilePhase.Three);
    }

    /// <summary>
    /// 对账①b：批量 queryTask，对已终态任务立刻收口——先 PLC 复核再 Confirm/Rollback，避免「RCS 报完成但料未到」误清空槽位。
    /// Query.Success=false / 响应无法解析时返回阶段失败（fail-closed）。
    /// </summary>
    private async Task<(ReconcilePhaseResult Phase, HashSet<string> Settled)> SettleTerminalTasksOnReconcileAsync(
        IReadOnlyList<string> unfinished, CancellationToken ct)
    {
        var settled = new HashSet<string>(StringComparer.Ordinal);
        if (unfinished.Count == 0)
            return (ReconcilePhaseResult.Ok(ReconcilePhase.OneB), settled);

        var queryIds = unfinished.Where(id => !RcsTaskId.IsAssignedRemoteId(id)).ToList();
        if (queryIds.Count == 0)
            return (ReconcilePhaseResult.Ok(ReconcilePhase.OneB), settled);

        var req = QueryTaskRequest.ForLocalIds(queryIds);
        var result = await _taskSvc.QueryAsync(req, ct);
        var queryPhase = StartupReconcileCoordinator.MapQueryResult(
            result.Success, result.Message ?? result.Error);
        if (!queryPhase.Succeeded)
        {
            _logger.LogWarning("对账①b queryTask 失败：{Msg}", result.Message ?? result.Error);
            return (queryPhase, settled);
        }

        foreach (var (rawId, rcsStatus) in RcsAckParser.ParseQueryItems(result.RawResponse))
        {
            var state = RcsStatusMapper.ToTaskState(rcsStatus);
            if (state is null || !RcsStatusMapper.IsTerminal(state)) continue;

            var row = await _taskStore.GetByTaskIdAsync(rawId, ct);
            if (row is null) continue;
            var taskId = row.RcsTaskId ?? rawId;
            var changed = row.TaskState != state;
            if (changed)
            {
                if (!await _taskStore.UpdateStateAsync(taskId, state, rcsStatus, row.ErrorMsg, ct))
                    _logger.LogWarning("对账①b：更新任务态未生效（任务不存在）{TaskId} → {State}", taskId, state);
            }

            PositionContext? ctx = null;
            if (row.EquipmentId is long eq && row.PositionId is long pos)
                _contexts.TryGetValue((eq, pos), out ctx);

            var phase = ctx?.Phase ?? (row.TaskType == "1" ? PositionPhase.Unload : PositionPhase.Upload);
            var plcCheckApplicable = ctx is not null;
            bool? hasMat = null;
            if (plcCheckApplicable && state == RcsTaskState.Completed)
                hasMat = await _host.ReadHasMatFreshAsync(ctx!, ct);

            var action = await _host.SettleSlotForTerminalAsync(taskId, phase, state, hasMat, plcCheckApplicable, ct);

            if (ctx is null)
            {
                if (action != SlotSettlementAction.Hold)
                    settled.Add(taskId);
                _logger.LogInformation("对账①b：无工位任务 {TaskId} 终态 {State} 槽位动作 {Action}", taskId, state, action);
                // P1-4：换架/盘点等无工位任务停机期间已终态——直接落库不通知订阅方，须补发事件，
                // 否则已接续的换架事务等不到第一发完成、第二发永不下发（与跟踪器轮询发现终态同一路径）。
                if (changed)
                {
                    var errorCode = state == RcsTaskState.Failed ? RcsErrorCode.Error
                        : state == RcsTaskState.Canceled ? RcsErrorCode.Cancel
                        : RcsErrorCode.Success;
                    _notifier?.RaiseTaskStatus(new RcsTaskStatusEvent(taskId, errorCode, null, state) { Source = "poll" });
                }
                continue;
            }

            if (action == SlotSettlementAction.Hold)
            {
                // HasMat 未知：预记与工位绑定都留着，等 PLC 可读后再收口；不回 WaitLoad（避免同槽再派工）
                ctx.CurrentTaskId = taskId;
                ctx.Phase = phase;
                _logger.LogWarning("对账①b：{TaskId} COMPLETED 但 PLC HasMat 未读到（phase={Phase}），预记保留，工位保持绑定", taskId, phase);
                continue;
            }

            if (action == SlotSettlementAction.ConfirmTake)
            {
                ctx.CurrentTaskId = taskId;
                ctx.Phase = PositionPhase.Upload;
                _host.SetState(ctx, PositionState.Loaded);
            }
            else if (action == SlotSettlementAction.ConfirmPut)
            {
                await _host.WriteTestStartAsync(ctx, 2, ct);
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.MaterialId = null;
                _host.SetState(ctx, PositionState.WaitLoad);
            }
            else if (state == RcsTaskState.Completed)
            {
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.MaterialId = null;
                ctx.AlarmRaised = true;
                _host.SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                    $"启动对账：RCS 已 COMPLETED 但 PLC 不符（阶段 {phase}，HasMat={hasMat}），预记已回滚，请核对后点恢复", ct);
                _logger.LogWarning("对账①b：{TaskId} COMPLETED 但 PLC 不符（phase={Phase} hasMat={Has}）→ ALARM", taskId, phase, hasMat);
            }
            else
            {
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.MaterialId = null;
                _host.SetState(ctx, PositionState.WaitLoad);
                if (state == RcsTaskState.Canceled)
                    await _alarms.RaiseRcsTaskCanceledAsync(taskId, "启动对账发现任务已取消，预记已回滚", ct);
            }

            settled.Add(taskId);
            _logger.LogInformation("对账①b：任务 {TaskId} 终态 {State} 已收口 EQ{Eq} POS{Pos}", taskId, state, ctx.EquipmentId, ctx.PositionId);
        }

        return (ReconcilePhaseResult.Ok(ReconcilePhase.OneB), settled);
    }
}

/// <summary>启动对账对调度器的接缝：工位集合、定态与 PLC/槽位 IO 仍由宿主持有。</summary>
internal interface IPositionStartupReconcileHost
{
    IReadOnlyList<(long Eq, long Pos, long PlcId)> Positions { get; }
    TimeSpan StaleReservationGrace { get; }
    TimeSpan SignalMaxAge { get; }
    void SetState(PositionContext ctx, PositionState state);
    Task<bool?> ReadHasMatFreshAsync(PositionContext ctx, CancellationToken ct);
    Task<bool> WriteTestStartAsync(PositionContext ctx, int value, CancellationToken ct);
    Task<int> SettleCompletedPendingWithPlcAsync(IReadOnlyCollection<string> unfinished, CancellationToken ct);
    Task<SlotSettlementAction> SettleSlotForTerminalAsync(
        string taskId, PositionPhase phase, string state, bool? hasMat, bool plcCheckApplicable, CancellationToken ct);
}
