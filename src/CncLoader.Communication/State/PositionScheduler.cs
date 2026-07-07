using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.State;

/// <summary>
/// 加工位状态机调度器（第四阶段⑤，<see cref="IHostedService"/>）。
/// 每加工位一个独立 <see cref="PositionContext"/> 并行驱动（双位并行），按 §7 状态机推进：
/// WAIT_LOAD→DISPATCHING→TRANSPORTING→(PLC复核)→LOADED→PROCESSING→DONE→DISPATCHING→TRANSPORTING→UNLOADED→WAIT_LOAD。
/// LOADED/UNLOADED 双条件（RCS completed 且 PLC 复核通过）；复核不过 → ALARM 不写启动（安全底线）。
/// 启动时先做 §6.3 对账（未完结任务绑定回加工位），对账完成前不自动派工。
/// </summary>
public sealed class PositionScheduler : IHostedService, IPositionScheduler
{
    private readonly ISignalStateStore _store;
    private readonly IRcsTaskService _taskSvc;
    private readonly IRcsTaskStore _taskStore;
    private readonly IPlcPointSource _points;
    private readonly IPlcOperationService _plcOps;
    private readonly IRouteResolver _routes;
    private readonly IDispatchQueue _queue;
    private readonly IAlarmEventService _alarms;
    private readonly IPlcWriteHook? _writeHook;
    private readonly IWorkRecordService _workRecords;
    private readonly IEquipmentConfigService _equipment;
    private readonly RcsOptions _options;
    private readonly ILogger<PositionScheduler> _logger;

    private readonly ConcurrentDictionary<(long Eq, long Pos), PositionContext> _contexts = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), (long PlcId, string RegAddr)> _testStartPoints = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), (long PlcId, string RegAddr, int OnValue, int OffValue)> _hasMatPoints = new();
    // bug#7：每加工位一把信号量，串行化主循环驱动与派工回填对同一 ctx 的读写（不同加工位仍并行）。
    private readonly ConcurrentDictionary<(long Eq, long Pos), SemaphoreSlim> _posGates = new();
    // bug#6：机台→线体反查缓存（避免每次入队查库）。
    private readonly ConcurrentDictionary<long, WorkLineRef> _lineCache = new();
    private List<(long Eq, long Pos, long PlcId)> _positions = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _dispatchTask;

    public PositionScheduler(
        ISignalStateStore store,
        IRcsTaskService taskSvc,
        IRcsTaskStore taskStore,
        IPlcPointSource points,
        IPlcOperationService plcOps,
        IRouteResolver routes,
        IDispatchQueue queue,
        IAlarmEventService alarms,
        IOptions<AppOptions> options,
        ILogger<PositionScheduler> logger,
        IWorkRecordService workRecords,
        IEquipmentConfigService equipment,
        IPlcWriteHook? writeHook = null)
    {
        _store = store;
        _taskSvc = taskSvc;
        _taskStore = taskStore;
        _points = points;
        _plcOps = plcOps;
        _routes = routes;
        _queue = queue;
        _alarms = alarms;
        _options = options.Value.Rcs;
        _logger = logger;
        _workRecords = workRecords;
        _equipment = equipment;
        _writeHook = writeHook;
    }

    private SemaphoreSlim GateFor((long Eq, long Pos) key) => _posGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>反查机台所属线体（带缓存）；查不到回退 (1,"LINE") 保证不 NPE。</summary>
    private async Task<WorkLineRef> ResolveLineAsync(long equipmentId, CancellationToken ct)
    {
        if (_lineCache.TryGetValue(equipmentId, out var cached)) return cached;
        var line = await _equipment.GetWorkLineByEquipmentAsync(equipmentId, ct);
        if (line is null)
        {
            _logger.LogWarning("机台 {Eq} 线体反查失败，回退 LINE", equipmentId);
            line = new WorkLineRef(1, "LINE");
        }
        _lineCache[equipmentId] = line;
        return line;
    }

    public bool IsReconciled { get; private set; }
    public event EventHandler? Reconciled;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.SchedulerEnabled)
        {
            _logger.LogInformation("加工位状态机调度器未启用（SchedulerEnabled=false）。");
            IsReconciled = true;
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 1. 装载加工位与 POS_TEST_START 点位缓存
        try
        {
            var allPoints = await _points.GetAllAsync(cancellationToken);
            var posKeys = new HashSet<(long, long)>();
            foreach (var p in allPoints)
            {
                if (p.PositionId is null) continue;
                var key = (p.EquipmentId, p.PositionId.Value);
                posKeys.Add(key);
                if (p.IsWrite && p.Signal == SignalKey.PosTestStart)
                    _testStartPoints[key] = (p.PlcId, p.RegisterAddress);
                if (!p.IsWrite && p.Signal == SignalKey.PosHasMat)
                    _hasMatPoints[key] = (p.PlcId, p.RegisterAddress, p.OnValue, p.OffValue);
            }
            _positions = posKeys.Select(k => (k.Item1, k.Item2, _testStartPoints.TryGetValue(k, out var tp) ? tp.PlcId : 0L)).ToList();
            _logger.LogInformation("位置调度器装载 {N} 个加工位", _positions.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "装载加工位点位失败，调度器将以空集启动"); }

        // 2. §6.3 启动对账：未完结任务绑定回加工位
        await ReconcileAsync(cancellationToken);

        IsReconciled = true;
        Reconciled?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("启动对账完成，开始自动派工");

        // 3. 启动主循环 + 派工循环
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        _dispatchTask = Task.Run(() => DispatchLoopAsync(_cts.Token));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        await Task.WhenAny(
            Task.WhenAll(_loopTask ?? Task.CompletedTask, _dispatchTask ?? Task.CompletedTask),
            Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
    }

    public async Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        if (!_contexts.TryGetValue((equipmentId, positionId), out var ctx)) return;
        var gate = GateFor((equipmentId, positionId));
        await gate.WaitAsync(ct);
        try
        {
            ctx.CurrentTaskId = null;
            ctx.Phase = null;
            ctx.AlarmRaised = false;
            SetState(ctx, PositionState.WaitLoad); // 同步刷看板
            _logger.LogInformation("人工恢复 EQ{Eq} POS{Pos} → WAIT_LOAD", equipmentId, positionId);
        }
        finally { gate.Release(); }
    }

    /// <summary>§6.3 启动对账：把未完结 RCS 任务绑定回其加工位上下文。</summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            var unfinished = await _taskStore.GetUnfinishedTaskIdsAsync(ct);
            foreach (var taskId in unfinished)
            {
                var row = await _taskStore.GetByTaskIdAsync(taskId, ct);
                if (row is null || row.PositionId is null || row.EquipmentId is null) continue;
                var key = (Eq: row.EquipmentId.Value, Pos: row.PositionId.Value);
                var ctx = _contexts.GetOrAdd(key, k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
                ctx.CurrentTaskId = taskId;
                ctx.Phase = row.TaskType == "1" ? PositionPhase.Unload : PositionPhase.Upload;
                ctx.State = PositionState.Dispatching; // 等 RCS 状态明确后由 loop 推进
                _logger.LogInformation("对账：任务 {TaskId} 绑定回 EQ{Eq} POS{Pos} 阶段 {Phase}", taskId, key.Eq, key.Pos, ctx.Phase);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "启动对账查询未完结任务失败"); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await DriveAllAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "调度器主循环异常"); }
            try { await Task.Delay(_options.SchedulerIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DriveAllAsync(CancellationToken ct)
    {
        // 确保每个已知加工位都有上下文（首次出现置 Offline/WaitLoad）
        foreach (var (eq, pos, _) in _positions)
            _contexts.GetOrAdd((eq, pos), k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos, State = PositionState.Offline });

        foreach (var ctx in _contexts.Values)
        {
            try { await DrivePositionAsync(ctx, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "驱动 EQ{Eq} POS{Pos} 异常", ctx.EquipmentId, ctx.PositionId); }
        }
    }

    private async Task DrivePositionAsync(PositionContext ctx, CancellationToken ct)
    {
        // bug#7：与派工回填串行化对同一 ctx 的读写
        var gate = GateFor((ctx.EquipmentId, ctx.PositionId));
        await gate.WaitAsync(ct);
        try { await DrivePositionCoreAsync(ctx, ct); }
        finally { gate.Release(); }
    }

    private async Task DrivePositionCoreAsync(PositionContext ctx, CancellationToken ct)
    {
        var machine = _store.GetMachine(ctx.EquipmentId);
        var plcOnline = machine?.PlcOnline ?? false;
        var safe = machine?.Safe ?? true;
        var doorOpen = machine?.DoorOpen ?? false;

        var readings = _store.GetReadings(ctx.EquipmentId);
        bool? hasMat = null, allowLoad = null, ok = null, ng = null;
        foreach (var r in readings)
        {
            if (r.PositionId != ctx.PositionId) continue;
            switch (r.Signal)
            {
                case SignalKey.PosHasMat: hasMat = r.On; break;
                case SignalKey.PosAllowLoad: allowLoad = r.On; break;
                case SignalKey.PosOk: ok = r.On; break;
                case SignalKey.PosNg: ng = r.On; break;
            }
        }

        // 通信断 → 离线（最高优先）
        if (!plcOnline) { SetState(ctx, PositionState.Offline); return; }
        // 不安全/开门 → 报警
        if (safe == false || doorOpen) { SetState(ctx, PositionState.Alarm); return; }

        // 查当前绑定任务的 RCS 态
        RcsTaskRow? rcsRow = null;
        if (!string.IsNullOrWhiteSpace(ctx.CurrentTaskId))
            rcsRow = await _taskStore.GetByTaskIdAsync(ctx.CurrentTaskId!, ct);
        var rcsState = rcsRow?.TaskState;

        var prev = ctx.State;
        var next = ComputeNextState(ctx, plcOnline, safe, doorOpen, hasMat, allowLoad, ok, ng, rcsState);
        // 执行动作；动作可能改写 next（如 WaitLoad→入队→Dispatching、Done→入下料队→Dispatching、Loaded→写启动→Processing）
        next = await ExecuteActionsAsync(ctx, next, hasMat, allowLoad, ok, ng, rcsState, ct);
        if (next != prev)
        {
            ctx.State = next;
            _logger.LogInformation("EQ{Eq} POS{Pos} {Prev} → {Next}", ctx.EquipmentId, ctx.PositionId, prev, next);
        }
        SetState(ctx, next);
    }

    private PositionState ComputeNextState(PositionContext ctx, bool online, bool? safe, bool? door,
        bool? hasMat, bool? allowLoad, bool? ok, bool? ng, string? rcsState)
    {
        switch (ctx.State)
        {
            case PositionState.Offline:
                return PositionState.WaitLoad;
            case PositionState.Alarm:
                // Alarm 粘滞：只能经 ResetAlarmAsync 人工恢复退出，状态机不自动离开（安全底线）
                return PositionState.Alarm;
            case PositionState.WaitLoad:
                // 有料且未启动 → 可能是重启后 PLC 有料但无任务（§6.3 账实不符），保持 WaitLoad 等对账/人工
                // 无料且允许上料 → 触发上料（由 ExecuteActions 入队后转 Dispatching）
                if (hasMat == true) return PositionState.WaitLoad;
                return PositionState.WaitLoad; // 实际转 Dispatching 由 ExecuteActions 设置
            case PositionState.Dispatching:
                if (rcsState == RcsTaskState.Canceled) return PositionState.Alarm;
                if (rcsState == RcsTaskState.Failed) return PositionState.Dispatching; // tracker 自动 redo
                if (rcsState == RcsTaskState.Completed) return PositionState.Transporting; // 复核交 ExecuteActions
                if (rcsState == RcsTaskState.Dispatched) return PositionState.Transporting;
                return PositionState.Dispatching;
            case PositionState.Transporting:
                if (rcsState == RcsTaskState.Canceled) return PositionState.Alarm;
                // COMPLETED → 复核交 ExecuteActions（需 fresh PLC 读，避免信号仓滞后）
                if (rcsState == RcsTaskState.Failed) return PositionState.Transporting; // redo
                return PositionState.Transporting;
            case PositionState.Loaded:
                return PositionState.Loaded; // 由 ExecuteActions 写启动后转 Processing
            case PositionState.Processing:
                if (ok == true) return PositionState.DoneOk;
                if (ng == true) return PositionState.DoneNg;
                return PositionState.Processing;
            case PositionState.DoneOk:
            case PositionState.DoneNg:
                return ctx.State; // 由 ExecuteActions 入下料队后转 Dispatching
            case PositionState.Unloaded:
                return PositionState.Unloaded; // 由 ExecuteActions 写复位后转 WaitLoad
            default:
                return PositionState.WaitLoad;
        }
    }

    private async Task<PositionState> ExecuteActionsAsync(PositionContext ctx, PositionState next, bool? hasMat, bool? allowLoad, bool? ok, bool? ng, string? rcsState, CancellationToken ct)
    {
        switch (next)
        {
            case PositionState.WaitLoad:
                // 触发上料：允许上料=ON 且 有料=OFF 且 无未完结任务
                if (allowLoad == true && hasMat == false && string.IsNullOrEmpty(ctx.CurrentTaskId))
                {
                    if (await EnqueueUploadAsync(ctx, ct)) next = PositionState.Dispatching;
                    else next = PositionState.Alarm;
                }
                break;
            case PositionState.Transporting:
                // RCS 报完成 → fresh PLC 读 HasMat 复核（不依赖信号仓，避免轮询滞后致误判）
                if (rcsState == RcsTaskState.Completed)
                {
                    var fresh = await ReadHasMatFreshAsync(ctx, ct);
                    if (ctx.Phase == PositionPhase.Upload)
                        next = fresh == true ? PositionState.Loaded : PositionState.Alarm;
                    else if (ctx.Phase == PositionPhase.Unload)
                        next = fresh == false ? PositionState.Unloaded : PositionState.Alarm;
                    else
                        next = PositionState.Alarm;
                }
                break;
            case PositionState.Loaded:
                if (await WriteTestStartAsync(ctx, 1, ct))
                {
                    // 加工开始：写 WORK_RECORD（关联当前上料 taskId）
                    ctx.WorkRecordId = await _workRecords.RecordStartAsync(new WorkRecordStartArgs
                    {
                        EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId,
                        PositionCode = $"POS-{ctx.PositionId}", RcsTaskId = ctx.CurrentTaskId, Author = "scheduler"
                    }, ct);
                    next = PositionState.Processing;
                }
                else next = PositionState.Alarm;
                break;
            case PositionState.Unloaded:
                if (await WriteTestStartAsync(ctx, 2, ct))
                {
                    ctx.CurrentTaskId = null;
                    ctx.Phase = null;
                    next = PositionState.WaitLoad;
                }
                else next = PositionState.Alarm;
                break;
            case PositionState.Alarm:
                if (!ctx.AlarmRaised)
                {
                    ctx.AlarmRaised = true;
                    var reason = ReasonForAlarm(ctx, hasMat, rcsState);
                    await _alarms.RaiseRcsTaskCanceledAsync(ctx.CurrentTaskId ?? $"POS-{ctx.PositionId}");
                    _logger.LogWarning("EQ{Eq} POS{Pos} ALARM：{Reason}", ctx.EquipmentId, ctx.PositionId, reason);
                }
                break;
            case PositionState.DoneOk:
            case PositionState.DoneNg:
                // 出结果 → 下料（仅一次：阶段还是 Upload 时触发）+ 写加工结果
                if (ctx.Phase == PositionPhase.Upload)
                {
                    if (ctx.WorkRecordId > 0)
                        await _workRecords.RecordResultAsync(ctx.WorkRecordId, next == PositionState.DoneOk ? "0" : "1", null, ct);
                    if (await EnqueueUnloadAsync(ctx, ct)) next = PositionState.Dispatching;
                    else next = PositionState.Alarm;
                }
                break;
        }

        // 只在回到 WaitLoad 时清报警标记；Alarm 期间保持标记，避免每 tick 重复告警
        if (next == PositionState.WaitLoad)
            ctx.AlarmRaised = false;
        return next;
    }

    private async Task<bool> EnqueueUploadAsync(PositionContext ctx, CancellationToken ct)
    {
        var route = await _routes.ResolveUploadAsync(ctx.EquipmentId, ctx.PositionId, ct);
        if (route is null)
        {
            await _alarms.RaiseRcsTaskNotFoundAsync($"UPLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}", ct);
            _logger.LogWarning("EQ{Eq} POS{Pos} 上料路由未配置（LOCATION_MAP 缺 LOAD_AREA/加工位 cell）", ctx.EquipmentId, ctx.PositionId);
            return false;
        }
        var (from, to) = route.Value;
        var line = await ResolveLineAsync(ctx.EquipmentId, ct);
        _queue.Enqueue(new DispatchItem
        {
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId, Phase = PositionPhase.Upload,
            Priority = 5, FromCode = from, ToCode = to,
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, Author = "scheduler"
        });
        ctx.Phase = PositionPhase.Upload;
        _logger.LogInformation("EQ{Eq} POS{Pos} 入上料队 {From}→{To}", ctx.EquipmentId, ctx.PositionId, from, to);
        return true;
    }

    private async Task<bool> EnqueueUnloadAsync(PositionContext ctx, CancellationToken ct)
    {
        var route = await _routes.ResolveUnloadAsync(ctx.EquipmentId, ctx.PositionId, ct);
        if (route is null)
        {
            await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}", ct);
            _logger.LogWarning("EQ{Eq} POS{Pos} 下料路由未配置（LOCATION_MAP 缺 UNLOAD_AREA/加工位 cell）", ctx.EquipmentId, ctx.PositionId);
            return false;
        }
        var (from, to) = route.Value;
        var line = await ResolveLineAsync(ctx.EquipmentId, ct);
        _queue.Enqueue(new DispatchItem
        {
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId, Phase = PositionPhase.Unload,
            Priority = 8, FromCode = from, ToCode = to,
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, Author = "scheduler"
        });
        ctx.Phase = PositionPhase.Unload;
        ctx.CurrentTaskId = null; // 上料任务已完结，清掉；下料任务由 dispatcher 绑定新 taskId
        _logger.LogInformation("EQ{Eq} POS{Pos} 入下料队 {From}→{To}", ctx.EquipmentId, ctx.PositionId, from, to);
        return true;
    }

    private async Task<bool> WriteTestStartAsync(PositionContext ctx, int value, CancellationToken ct)
    {
        if (!_testStartPoints.TryGetValue((ctx.EquipmentId, ctx.PositionId), out var tp))
        {
            _logger.LogWarning("EQ{Eq} POS{Pos} 未配置 POS_TEST_START 写点位", ctx.EquipmentId, ctx.PositionId);
            return false;
        }
        var r = await _plcOps.WriteWithConfirmAsync(tp.PlcId, tp.RegAddr, value, "scheduler", ct);
        if (!r.Verified)
        {
            _logger.LogWarning("EQ{Eq} POS{Pos} 写 POS_TEST_START={V} 复核失败：{Err}", ctx.EquipmentId, ctx.PositionId, value, r.Error);
            return false;
        }
        _writeHook?.OnTestStartWritten(ctx.EquipmentId, ctx.PositionId, value);
        return true;
    }

    private async Task<bool?> ReadHasMatFreshAsync(PositionContext ctx, CancellationToken ct)
    {
        if (!_hasMatPoints.TryGetValue((ctx.EquipmentId, ctx.PositionId), out var hp))
            return null;
        try
        {
            var r = await _plcOps.ReadRegisterAsync(hp.PlcId, hp.RegAddr, 1, ct);
            // OnValue=有料；OffValue=无料。按 OnValue 判定。
            return r.RawValue == hp.OnValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EQ{Eq} POS{Pos} 复核读 HasMat 失败", ctx.EquipmentId, ctx.PositionId);
            return null;
        }
    }

    private void SetState(PositionContext ctx, PositionState state)
    {
        ctx.State = state;
        _store.UpdatePosition(new PositionStatus
        {
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId, State = state
        });
    }

    private static string ReasonForAlarm(PositionContext ctx, bool? hasMat, string? rcsState)
    {
        if (rcsState == RcsTaskState.Canceled) return "RCS 任务被取消";
        if (rcsState == RcsTaskState.Failed) return "RCS 任务失败";
        if (ctx.Phase == PositionPhase.Upload && hasMat != true) return "RCS 报完成但 PLC 无料（复核不过）";
        if (ctx.Phase == PositionPhase.Unload && hasMat != false) return "RCS 报完成但 PLC 仍有料（复核不过）";
        return "未知异常";
    }

    /// <summary>派工循环：按优先级出队 → 下发 RCS → 绑定 taskId 回加工位上下文。</summary>
    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var item = _queue.Dequeue();
                if (item is null) { await Task.Delay(200, ct); continue; }
                await DispatchOneAsync(item, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "派工循环异常"); await Task.Delay(500, ct); }
        }
    }

    private async Task DispatchOneAsync(DispatchItem item, CancellationToken ct)
    {
        var result = await _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = item.WorkLineId, LineCode = item.LineCode,
            TaskType = item.Phase == PositionPhase.Unload ? "1" : "0",
            Priority = item.Priority, FromCode = item.FromCode, ToCode = item.ToCode,
            EquipmentId = item.EquipmentId, PositionId = item.PositionId,
            Kind = RcsTaskKind.Transit, Author = item.Author
        }, ct);

        var ctx = _contexts.GetOrAdd((item.EquipmentId, item.PositionId), k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
        // bug#7：回填 ctx 与主循环驱动串行化（网络下发在锁外，仅结果写入在锁内）
        var gate = GateFor((item.EquipmentId, item.PositionId));
        await gate.WaitAsync(ct);
        try
        {
            if (result.Success && !string.IsNullOrEmpty(result.TaskId))
            {
                ctx.CurrentTaskId = result.TaskId;
                ctx.Phase = item.Phase;
                _logger.LogInformation("EQ{Eq} POS{Pos} 下发 {Phase} 任务 {TaskId}", item.EquipmentId, item.PositionId, item.Phase, result.TaskId);
            }
            else
            {
                // 派工失败 → 粘滞 Alarm（经 SetState 刷看板），等人工恢复；不自动重发（防重试风暴）
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync(item.Phase == PositionPhase.Unload ? $"UNLOAD-POS{item.PositionId}" : $"UPLOAD-POS{item.PositionId}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 下发失败：{Err}", item.EquipmentId, item.PositionId, result.Error ?? result.Message);
            }
        }
        finally { gate.Release(); }
    }

    private sealed class PositionContext
    {
        public long EquipmentId { get; init; }
        public long PositionId { get; init; }
        public PositionState State { get; set; } = PositionState.Offline;
        public string? CurrentTaskId { get; set; }
        public PositionPhase? Phase { get; set; }
        public long WorkRecordId { get; set; }
        public bool AlarmRaised { get; set; }
    }
}
