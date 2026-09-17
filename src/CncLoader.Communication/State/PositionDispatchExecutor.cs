using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 自动上下料执行：预记先于 RCS、下发窗口回填、失败粘滞 Alarm。
/// 消费顺序仍由 <see cref="PositionDispatchConsumer"/> 独占。
/// </summary>
internal sealed class PositionDispatchExecutor
{
    private readonly IPositionDispatchRuntime _rt;
    private readonly IRcsTaskService _taskSvc;
    private readonly IRcsTaskStore _taskStore;
    private readonly ISlotAccountService _slots;
    private readonly IEquipmentConfigService _equipment;
    private readonly IRouteResolver _routes;
    private readonly IRoutingAvailabilityValidator _routingValidator;
    private readonly IDispatchQueue _queue;
    private readonly IAlarmEventService _alarms;
    private readonly UploadDispatchPlanner _uploadPlanner;
    private readonly UnloadDispatchPlanner _unloadPlanner;
    private readonly ReservationFirstDispatcher _reservationFirst = new();
    private readonly RcsOptions _options;
    private readonly ILogger _logger;

    public PositionDispatchExecutor(
        IPositionDispatchRuntime runtime,
        IRcsTaskService taskSvc,
        IRcsTaskStore taskStore,
        ISlotAccountService slots,
        IEquipmentConfigService equipment,
        IRouteResolver routes,
        IRoutingAvailabilityValidator routingValidator,
        IDispatchQueue queue,
        IAlarmEventService alarms,
        UploadDispatchPlanner uploadPlanner,
        UnloadDispatchPlanner unloadPlanner,
        RcsOptions options,
        ILogger logger)
    {
        _rt = runtime;
        _taskSvc = taskSvc;
        _taskStore = taskStore;
        _slots = slots;
        _equipment = equipment;
        _routes = routes;
        _routingValidator = routingValidator;
        _queue = queue;
        _alarms = alarms;
        _uploadPlanner = uploadPlanner;
        _unloadPlanner = unloadPlanner;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> AllocateUploadsAsync(CancellationToken ct)
    {
        var candidates = _rt.Contexts
            .Where(c => c.UploadRequested && c.State == PositionState.WaitLoad
                        && string.IsNullOrEmpty(c.CurrentTaskId)
                        && !_rt.IsEquipmentDispatchHeld(c.EquipmentId)
                        && !_rt.HasInbound((c.EquipmentId, c.PositionId)))
            .OrderBy(c => c.WaitLoadSince ?? DateTime.MaxValue)
            .ThenBy(c => c.PositionId)
            .ToList();
        if (candidates.Count == 0) return false;

        var any = false;
        foreach (var ctx in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await TryDispatchUploadAsync(ctx, ct);
            if (outcome == UploadDecision.Queued) any = true;
        }
        return any;
    }

    public async Task<UploadDecision> TryDispatchUploadAsync(PositionContext ctx, CancellationToken ct)
    {
        if (!_rt.CanAutoDispatch || _rt.IsEquipmentDispatchHeld(ctx.EquipmentId))
            return UploadDecision.WaitMaterial;
        if (await _taskStore.HasUnconfirmedCanceledAsync(ctx.EquipmentId, ctx.PositionId, ct))
        {
            _rt.LogRouteUnavailableThrottled(ctx.EquipmentId,
                $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 存在未确认取消任务，上料等待（RCS 页「确认取消已处理」）");
            return UploadDecision.WaitMaterial;
        }

        if (_rt.HasInbound((ctx.EquipmentId, ctx.PositionId)))
            return UploadDecision.WaitMaterial;

        var expectedState = ctx.State;
        var expectedTaskId = ctx.CurrentTaskId;

        var plan = await _uploadPlanner.ResolveUploadPlanAsync(ctx, ct);
        if (plan.Decision == UploadDecision.WaitMaterial) return UploadDecision.WaitMaterial;
        if (plan.Decision == UploadDecision.Failed)
        {
            var g = _rt.GateFor((ctx.EquipmentId, ctx.PositionId));
            await g.WaitAsync(ct);
            try { ctx.AlarmRaised = true; _rt.SetState(ctx, PositionState.Alarm); }
            finally { g.Release(); }
            return UploadDecision.Failed;
        }

        var routeCtx = new DispatchRouteContext
        {
            SourceEquipmentId = ctx.EquipmentId,
            SourcePositionId = ctx.PositionId,
            SourceFrameId = plan.SourceFrameId is long sf
                ? RouteDependency.Required(sf)
                : RouteDependency.NotApplicable,
            FromCode = plan.From,
            ToCode = plan.To,
            RequiresResolvedCells = true
        };
        var pre = await _routingValidator.ValidateAsync(routeCtx, ct);
        if (!pre.IsAvailable || pre.SourceWorkLine is null)
        {
            _rt.InvalidateLineCache(ctx.EquipmentId);
            _rt.LogRouteUnavailableThrottled(ctx.EquipmentId,
                $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料预记前路由不可用：{pre.SafeMessage}");
            return UploadDecision.WaitMaterial;
        }
        var line = pre.SourceWorkLine;
        _rt.CacheLine(ctx.EquipmentId, line);

        var useGrab = UsesGrabLoadUnload && plan.SourceFrameId is not null;
        var taskId = RcsTaskId.Next(line.LineCode, useGrab ? RcsTaskKind.Grab : RcsTaskKind.Transit);
        ReservedSlot? reservedTake = null;
        var fromCode = plan.From;
        GrabDispatchArgs? grabArgs = null;
        RcsResult result;
        if (plan.SourceFrameId is long sourceFrameId)
        {
            var prepared = await _reservationFirst.ExecuteAsync(
                taskId,
                async (id, token) =>
                {
                    reservedTake = await _slots.ReserveTakeAsync(sourceFrameId, id, token);
                    return reservedTake;
                },
                async (_, token) =>
                {
                    if (reservedTake is { } slot)
                    {
                        var slotCell = await _routes.ResolveFrameSlotCellAsync(
                            slot.FrameId, slot.LayerNo, slot.PosInLayer, token);
                        if (slotCell is null)
                        {
                            return RoutingAvailabilityResult.Unavailable(
                                RoutingUnavailableReason.NotFound, "LocationMap", sourceFrameId,
                                $"上料槽位 cell 未录入 LOCATION_MAP（架 {slot.FrameId} 层{slot.LayerNo}位{slot.PosInLayer}）");
                        }
                        if (useGrab)
                        {
                            grabArgs = await _uploadPlanner.TryBuildUploadGrabArgsAsync(
                                taskId, line.WorkLineId, line.LineCode,
                                plan.From!, slot, ctx.EquipmentId, ctx.PositionId, token);
                            if (grabArgs is null)
                            {
                                return RoutingAvailabilityResult.Unavailable(
                                    RoutingUnavailableReason.NotFound, "LocationMap", sourceFrameId,
                                    $"上料抓取站或加工位孔未录入 LOCATION_MAP（EQ{ctx.EquipmentId} POS{ctx.PositionId}）");
                            }
                            return await _routingValidator.ValidateAsync(
                                routeCtx with { FromCode = grabArgs.SrcStation, ToCode = grabArgs.DstStation },
                                token);
                        }
                        fromCode = slotCell;
                    }
                    return await _routingValidator.ValidateAsync(routeCtx with { FromCode = fromCode }, token);
                },
                (id, token) => DispatchLoadUnloadAsync(
                    id, line.WorkLineId, line.LineCode, "0", 5,
                    fromCode!, plan.To!, ctx.EquipmentId, ctx.PositionId,
                    reservedTake?.MaterialId, "scheduler", grabArgs, token),
                (id, token) => _slots.RollbackTakeAsync(id, token),
                ct);

            if (prepared.Status == ReservationFirstDispatchStatus.ReservationFailed)
            {
                if (prepared.Exception is not null)
                {
                    var reserveGate = _rt.GateFor((ctx.EquipmentId, ctx.PositionId));
                    await reserveGate.WaitAsync(ct);
                    try
                    {
                        ctx.AlarmRaised = true;
                        _rt.SetState(ctx, PositionState.Alarm);
                        await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                            $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料预记异常，未调用 RCS：{prepared.Exception.Message}", ct);
                        _logger.LogWarning(prepared.Exception,
                            "EQ{Eq} POS{Pos} 上料预记异常，未调用 RCS → ALARM", ctx.EquipmentId, ctx.PositionId);
                    }
                    finally { reserveGate.Release(); }
                    return UploadDecision.Failed;
                }

                _logger.LogDebug("EQ{Eq} POS{Pos} 上料预记未抢到料架 {Frame} 的可取槽，保持 WAIT_LOAD",
                    ctx.EquipmentId, ctx.PositionId, sourceFrameId);
                return UploadDecision.WaitMaterial;
            }

            if (prepared.Status == ReservationFirstDispatchStatus.RouteUnavailable)
            {
                _rt.InvalidateLineCache(ctx.EquipmentId);
                _rt.LogRouteUnavailableThrottled(ctx.EquipmentId,
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料最终路由门禁失败（非 RCS 失败），已回滚预记：{prepared.RouteResult?.SafeMessage ?? prepared.Exception?.Message ?? "—"}");
                if (!prepared.RollbackSucceeded)
                {
                    var g = _rt.GateFor((ctx.EquipmentId, ctx.PositionId));
                    await g.WaitAsync(ct);
                    try
                    {
                        ctx.AlarmRaised = true;
                        _rt.SetState(ctx, PositionState.Alarm);
                        await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                            $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 路由拒发后预记回滚失败，需人工核账", ct);
                    }
                    finally { g.Release(); }
                    return UploadDecision.Failed;
                }
                if (prepared.RouteResult?.EntityKind == "LocationMap")
                {
                    var g = _rt.GateFor((ctx.EquipmentId, ctx.PositionId));
                    await g.WaitAsync(ct);
                    try
                    {
                        ctx.AlarmRaised = true;
                        _rt.SetState(ctx, PositionState.Alarm);
                        await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                            $"EQ{ctx.EquipmentId} POS{ctx.PositionId} {prepared.RouteResult.SafeMessage}", ct);
                    }
                    finally { g.Release(); }
                    return UploadDecision.Failed;
                }
                return UploadDecision.WaitMaterial;
            }

            reservedTake = prepared.Reservation;
            result = PositionDispatchBindSupport.BindableResult(prepared, taskId, _logger);
        }
        else
        {
            var prepared = await _reservationFirst.ExecuteAsync(
                taskId,
                (_, _) => Task.FromResult<object?>(new object()),
                (_, token) => _routingValidator.ValidateAsync(routeCtx, token),
                (id, token) => DispatchLoadUnloadAsync(
                    id, line.WorkLineId, line.LineCode, "0", 5,
                    plan.From!, plan.To!, ctx.EquipmentId, ctx.PositionId,
                    null, "scheduler", grab: null, token),
                (_, _) => Task.FromResult(true),
                ct);

            if (prepared.Status == ReservationFirstDispatchStatus.RouteUnavailable)
            {
                _rt.InvalidateLineCache(ctx.EquipmentId);
                _rt.LogRouteUnavailableThrottled(ctx.EquipmentId,
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料（命名区）最终路由门禁失败：{prepared.RouteResult?.SafeMessage ?? "—"}");
                return UploadDecision.WaitMaterial;
            }

            result = PositionDispatchBindSupport.BindableResult(prepared, taskId, _logger);
        }

        var gate = _rt.GateFor((ctx.EquipmentId, ctx.PositionId));
        await gate.WaitAsync(ct);
        try
        {
            if (_rt.HasInbound((ctx.EquipmentId, ctx.PositionId)))
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 上料下发窗口内出现直送登记，放弃绑定自取任务 {Task}",
                    ctx.EquipmentId, ctx.PositionId, result.TaskId ?? "—");
                if (!string.IsNullOrEmpty(result.TaskId)
                    && await _rt.CloseOrphanTaskAsync(result.TaskId, "UPLOAD_SUPERSEDED_BY_HANDOFF", ct)
                    && plan.SourceFrameId is not null)
                    await _slots.RollbackTakeAsync(result.TaskId, ct);
                return UploadDecision.WaitMaterial;
            }

            if (ctx.State != expectedState || ctx.CurrentTaskId != expectedTaskId)
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 上料下发窗口内状态漂移（{From}→{To}），放弃绑定任务 {Task}",
                    ctx.EquipmentId, ctx.PositionId, expectedState, ctx.State, result.TaskId ?? "—");
                if (result.Success && !string.IsNullOrEmpty(result.TaskId)
                    && await _rt.CloseOrphanTaskAsync(result.TaskId, "UPLOAD_SUPERSEDED_BY_STATE_DRIFT", ct)
                    && plan.SourceFrameId is not null)
                    await _slots.RollbackTakeAsync(result.TaskId, ct);
                return UploadDecision.WaitMaterial;
            }

            if (!result.Success || string.IsNullOrEmpty(result.TaskId))
            {
                ctx.AlarmRaised = true;
                _rt.SetState(ctx, PositionState.Alarm);
                var err = result.Error ?? result.Message ?? "未知错误";
                await _alarms.RaiseRcsTaskNotFoundAsync($"UPLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}",
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料 RCS 下发失败：{err}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 上料下发失败：{Err}", ctx.EquipmentId, ctx.PositionId, err);
                return UploadDecision.Failed;
            }

            ctx.CurrentTaskId = result.TaskId;
            ctx.TaskBoundAt = DateTime.Now;
            ctx.Phase = PositionPhase.Upload;
            ctx.UploadRequested = false;

            if (reservedTake is not null) ctx.MaterialId = reservedTake.MaterialId;
            _rt.SetState(ctx, PositionState.Dispatching);
            _logger.LogInformation("EQ{Eq} POS{Pos} 下发上料任务 {TaskId} {From}→{To}", ctx.EquipmentId, ctx.PositionId, result.TaskId, fromCode, plan.To);
            return UploadDecision.Queued;
        }
        finally { gate.Release(); }
    }

    public async Task DispatchOneAsync(DispatchItem item, CancellationToken ct)
    {
        if (!_rt.CanAutoDispatch || _rt.IsEquipmentDispatchHeld(item.EquipmentId))
        {
            _queue.Enqueue(item);
            return;
        }

        if (await _taskStore.HasUnconfirmedCanceledAsync(item.EquipmentId, item.PositionId, ct))
        {
            _queue.Enqueue(item);
            _rt.LogRouteUnavailableThrottled(item.EquipmentId,
                $"EQ{item.EquipmentId} POS{item.PositionId} 存在未确认取消任务，下料回队");
            return;
        }

        var ctx = _rt.GetOrAddContext(item.EquipmentId, item.PositionId);

        var expectedState = ctx.State;
        var expectedTaskId = ctx.CurrentTaskId;

        var decision = await _unloadPlanner.ResolveUnloadTargetAsync(ctx, item.IsOk, ct);
        if (decision is null)
        {
            if (item.IsOk && await _equipment.HasSubsequentProcessAsync(item.EquipmentId, ct))
            {
                _rt.InvalidateLineCache(item.EquipmentId);
                await _rt.DeferUnloadAsync(item, ctx,
                    "下料路由不可用（后续工序无活动目标）", ct);
                return;
            }

            var g0 = _rt.GateFor((item.EquipmentId, item.PositionId));
            await g0.WaitAsync(ct);
            try
            {
                ctx.AlarmRaised = true;
                _rt.SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{item.EquipmentId}-POS{item.PositionId}",
                    $"EQ{item.EquipmentId} POS{item.PositionId} 下料终点未配置（isOk={item.IsOk}，请录入 NG/中转/下料架绑定或 UNLOAD_AREA）", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 下料终点未配置（isOk={Ok}，请录入 NG/中转/下料架绑定或 UNLOAD_AREA）→ ALARM", item.EquipmentId, item.PositionId, item.IsOk);
            }
            finally { g0.Release(); }
            return;
        }
        var d = decision.Value;
        var toCell = d.ToCell;

        var routeCtx = UnloadDispatchPlanner.BuildUnloadRouteContext(item, d);
        var pre = await _routingValidator.ValidateAsync(routeCtx, ct);
        if (!pre.IsAvailable)
        {
            _rt.InvalidateLineCache(item.EquipmentId);
            if (d.DestEquipmentId is long destEq) _rt.InvalidateLineCache(destEq);
            await _rt.DeferUnloadAsync(item, ctx, $"下料预记前路由不可用：{pre.SafeMessage}", ct);
            return;
        }

        var lineCode = pre.SourceWorkLine?.LineCode ?? item.LineCode;
        var workLineId = pre.SourceWorkLine?.WorkLineId ?? item.WorkLineId;
        var useGrab = UnloadDispatchPlanner.PreferGrabForUnload(d, UsesGrabLoadUnload);
        var taskId = RcsTaskId.Next(lineCode, useGrab ? RcsTaskKind.Grab : RcsTaskKind.Transit);
        GrabDispatchArgs? grabArgs = null;
        UnloadReservation? reservedHold = null;
        RcsResult result;
        if (UnloadDispatchPlanner.RequiresUnloadReservation(d))
        {
            var prepared = await _reservationFirst.ExecuteAsync(
                taskId,
                async (id, token) =>
                {
                    var reserved = await ReserveUnloadAsync(item, d, id, token);
                    reservedHold = reserved;
                    if (reserved?.SlotCell is string slotCell)
                        toCell = slotCell;
                    else if (d.DestFrameId is not null)
                        return null;
                    return reserved;
                },
                async (_, token) =>
                {
                    if (useGrab)
                    {
                        grabArgs = await _unloadPlanner.TryBuildUnloadGrabArgsAsync(
                            taskId, workLineId, lineCode, item, d, reservedHold, token);
                        if (grabArgs is null)
                        {
                            return RoutingAvailabilityResult.Unavailable(
                                RoutingUnavailableReason.NotFound, "LocationMap", d.DestFrameId,
                                $"下料抓取站或加工位孔未录入 LOCATION_MAP（EQ{item.EquipmentId} POS{item.PositionId}）");
                        }
                        return await _routingValidator.ValidateAsync(
                            routeCtx with { FromCode = grabArgs.SrcStation, ToCode = grabArgs.DstStation },
                            token);
                    }
                    return await _routingValidator.ValidateAsync(routeCtx with { ToCode = toCell }, token);
                },
                (id, token) => DispatchLoadUnloadAsync(
                    id, workLineId, lineCode, "1", item.Priority,
                    item.FromCode, toCell, item.EquipmentId, item.PositionId,
                    item.MaterialId, item.Author, grabArgs, token),
                (id, token) => RollbackUnloadReservationAsync(d, id, token),
                ct);

            if (prepared.Status == ReservationFirstDispatchStatus.ReservationFailed)
            {
                var reserveGate = _rt.GateFor((item.EquipmentId, item.PositionId));
                await reserveGate.WaitAsync(ct);
                try
                {
                    ctx.AlarmRaised = true;
                    _rt.SetState(ctx, PositionState.Alarm);
                    await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                        $"EQ{item.EquipmentId} POS{item.PositionId} 下料预记失败（目标 {d.Target}，物料 {item.MaterialId ?? "—"}），未调用 RCS", ct);
                    _logger.LogWarning("EQ{Eq} POS{Pos} 下料预记失败，未调用 RCS → ALARM（目标 {Target}，物料 {El}）",
                        item.EquipmentId, item.PositionId, d.Target, item.MaterialId ?? "—");
                }
                finally { reserveGate.Release(); }
                return;
            }

            if (prepared.Status == ReservationFirstDispatchStatus.RouteUnavailable)
            {
                _rt.InvalidateLineCache(item.EquipmentId);
                if (d.DestEquipmentId is long de) _rt.InvalidateLineCache(de);
                if (!prepared.RollbackSucceeded)
                {
                    var g = _rt.GateFor((item.EquipmentId, item.PositionId));
                    await g.WaitAsync(ct);
                    try
                    {
                        ctx.AlarmRaised = true;
                        _rt.SetState(ctx, PositionState.Alarm);
                        await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                            $"EQ{item.EquipmentId} POS{item.PositionId} 路由拒发后预记/交接回滚失败，需人工核账", ct);
                    }
                    finally { g.Release(); }
                    return;
                }

                await _rt.DeferUnloadAsync(item, ctx,
                    $"下料最终路由门禁失败，已回滚预记/交接：{prepared.RouteResult?.SafeMessage ?? "—"}", ct);
                return;
            }

            if (prepared.Status is ReservationFirstDispatchStatus.Dispatched or ReservationFirstDispatchStatus.DispatchUnknown
                && d.Target == UnloadTarget.NextMachineCell
                && !TryMarkInboundDispatched(d, taskId, prepared.DispatchResult?.TaskId ?? taskId))
            {
                var orphanId = prepared.DispatchResult?.TaskId ?? taskId;
                await _rt.CloseOrphanTaskAsync(orphanId, "UNLOAD_HANDOFF_RESERVATION_LOST", ct);
                result = RcsResult.Fail("", "直接交接预登记在下发窗口内丢失，任务已收口") with { TaskId = orphanId };
            }
            else
            {
                result = PositionDispatchBindSupport.BindableResult(prepared, taskId, _logger);
            }
        }
        else
        {
            var prepared = await _reservationFirst.ExecuteAsync(
                taskId,
                (_, _) => Task.FromResult<object?>(new object()),
                (_, token) => _routingValidator.ValidateAsync(routeCtx, token),
                (id, token) => DispatchLoadUnloadAsync(
                    id, workLineId, lineCode, "1", item.Priority,
                    item.FromCode, toCell, item.EquipmentId, item.PositionId,
                    item.MaterialId, item.Author, grab: null, token),
                (_, _) => Task.FromResult(true),
                ct);

            if (prepared.Status == ReservationFirstDispatchStatus.RouteUnavailable)
            {
                _rt.InvalidateLineCache(item.EquipmentId);
                await _rt.DeferUnloadAsync(item, ctx,
                    $"下料（无槽位账）最终路由门禁失败：{prepared.RouteResult?.SafeMessage ?? "—"}", ct);
                return;
            }

            result = PositionDispatchBindSupport.BindableResult(prepared, taskId, _logger);
        }

        var gate = _rt.GateFor((item.EquipmentId, item.PositionId));
        await gate.WaitAsync(ct);
        try
        {
            if (ctx.State != expectedState || ctx.CurrentTaskId != expectedTaskId)
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 下料下发窗口内状态漂移（{From}→{To}），放弃绑定任务 {Task}",
                    item.EquipmentId, item.PositionId, expectedState, ctx.State, result.TaskId ?? "—");
                if (result.Success && !string.IsNullOrEmpty(result.TaskId)
                    && await _rt.CloseOrphanTaskAsync(result.TaskId, "UNLOAD_SUPERSEDED_BY_STATE_DRIFT", ct))
                    await RollbackUnloadReservationAsync(d, result.TaskId, ct);
                return;
            }

            if (result.Success && !string.IsNullOrEmpty(result.TaskId))
            {
                ctx.CurrentTaskId = result.TaskId;
                ctx.TaskBoundAt = DateTime.Now;
                ctx.Phase = PositionPhase.Unload;
                _logger.LogInformation("EQ{Eq} POS{Pos} 下发下料任务 {TaskId} {From}→{To}（{Target}）", item.EquipmentId, item.PositionId, result.TaskId, item.FromCode, toCell, d.Target);
            }
            else
            {
                ctx.AlarmRaised = true;
                _rt.SetState(ctx, PositionState.Alarm);
                var err = result.Error ?? result.Message ?? "未知错误";
                await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{item.EquipmentId}-POS{item.PositionId}",
                    $"EQ{item.EquipmentId} POS{item.PositionId} 下料 RCS 下发失败：{err}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 下料下发失败：{Err}", item.EquipmentId, item.PositionId, err);
            }
        }
        finally { gate.Release(); }
    }

    private async Task<UnloadReservation?> ReserveUnloadAsync(
        DispatchItem item,
        UnloadDecision decision,
        string taskId,
        CancellationToken ct)
    {
        if (decision.Target == UnloadTarget.NextMachineCell
            && decision.DestEquipmentId is long dstEq
            && decision.DestPositionId is long dstPos)
        {
            var handoff = new InboundHandoff(taskId, item.MaterialId, DateTime.UtcNow, IsDispatched: false);
            var destKey = (dstEq, dstPos);
            var destGate = _rt.GateFor(destKey);
            await destGate.WaitAsync(ct);
            try
            {
                return _rt.TryAddInbound(destKey, handoff)
                    ? new UnloadReservation()
                    : null;
            }
            finally { destGate.Release(); }
        }

        if (decision.DestFrameId is long destFrame)
        {
            var put = await _slots.ReserveAsync(destFrame, taskId, item.MaterialId, ct);
            if (put is null)
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 料架 {Frame} 已满，件 {Task}（物料 {El}）无法入库预记，未调用 RCS",
                    item.EquipmentId, item.PositionId, destFrame, taskId, item.MaterialId ?? "—");
                await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    $"料架 {destFrame} 已满，件 {taskId}（物料 {item.MaterialId ?? "—"}）无法入库，请人工换架/清架", taskId, ct);
                return null;
            }
            var slotCell = await _routes.ResolveFrameSlotCellAsync(destFrame, put.LayerNo, put.PosInLayer, ct);
            if (slotCell is null)
            {
                await _slots.RollbackAsync(taskId, ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 料架 {Frame} 槽 层{L}位{P} 未录入 LOCATION_MAP cell，已回滚预记",
                    item.EquipmentId, item.PositionId, destFrame, put.LayerNo, put.PosInLayer);
                return null;
            }
            return new UnloadReservation(put, slotCell);
        }

        return null;
    }

    private async Task<bool> RollbackUnloadReservationAsync(
        UnloadDecision decision,
        string taskId,
        CancellationToken ct)
    {
        if (decision.DestFrameId is not null)
            return await _slots.RollbackAsync(taskId, ct);

        if (decision.Target == UnloadTarget.NextMachineCell
            && decision.DestEquipmentId is long dstEq
            && decision.DestPositionId is long dstPos
            && _rt.TryRemoveInboundIfSource((dstEq, dstPos), taskId))
        {
            return true;
        }

        return false;
    }

    private bool TryMarkInboundDispatched(UnloadDecision decision, string localTaskId, string assignedTaskId)
    {
        if (decision.DestEquipmentId is not long dstEq || decision.DestPositionId is not long dstPos)
            return false;
        return _rt.TryMarkInboundDispatched((dstEq, dstPos), localTaskId, assignedTaskId);
    }

    private bool UsesGrabLoadUnload => RcsLoadUnloadVerbs.IsGrab(_options.LoadUnloadVerb);

    private Task<RcsResult> DispatchLoadUnloadAsync(
        string taskId,
        long workLineId,
        string lineCode,
        string taskType,
        int priority,
        string fromCode,
        string toCode,
        long equipmentId,
        long positionId,
        string? materialId,
        string? author,
        GrabDispatchArgs? grab,
        CancellationToken ct)
        => PositionDispatchBindSupport.DispatchLoadUnloadAsync(
            _taskSvc, _logger, taskId, workLineId, lineCode, taskType, priority,
            fromCode, toCode, equipmentId, positionId, materialId, author, grab, ct);
}
