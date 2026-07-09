using System.Collections.Concurrent;
using System.Text.Json;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
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
    private readonly ISlotAccountService _slots;
    private readonly RcsOptions _options;
    private readonly ILogger<PositionScheduler> _logger;

    private readonly ConcurrentDictionary<(long Eq, long Pos), PositionContext> _contexts = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), (long PlcId, string RegAddr)> _testStartPoints = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), (long PlcId, string RegAddr, int OnValue, int OffValue)> _hasMatPoints = new();
    // bug#7：每加工位一把信号量，串行化主循环驱动与派工回填对同一 ctx 的读写（不同加工位仍并行）。
    private readonly ConcurrentDictionary<(long Eq, long Pos), SemaphoreSlim> _posGates = new();
    // bug#6：机台→线体反查缓存（避免每次入队查库）。
    private readonly ConcurrentDictionary<long, WorkLineRef> _lineCache = new();
    // 机台→料架绑定 ID 缓存（上/下料架，避免每 tick 查库）。
    private readonly ConcurrentDictionary<long, EquipmentFrameBindingIds> _bindingCache = new();
    // 工序间直接交接的"待入库"登记：目标(机台,工位) → 交接信息（源工位空闲时置位，目标工位见料即接）。
    private readonly ConcurrentDictionary<(long Eq, long Pos), InboundHandoff> _expectedInbound = new();
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
        ISlotAccountService slots,
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
        _slots = slots;
        _writeHook = writeHook;
    }

    private SemaphoreSlim GateFor((long Eq, long Pos) key) => _posGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>调度器关闭时仍向看板播种加工位卡片（Offline），避免监控页空白。</summary>
    private async Task SeedDashboardPositionsAsync(CancellationToken ct)
    {
        try
        {
            var allPoints = await _points.GetAllAsync(ct);
            var posKeys = new HashSet<(long Eq, long Pos)>();
            foreach (var p in allPoints)
            {
                if (p.PositionId is null) continue;
                posKeys.Add((p.EquipmentId, p.PositionId.Value));
            }
            foreach (var (eq, pos) in posKeys.OrderBy(k => k.Eq).ThenBy(k => k.Pos))
            {
                _store.UpdatePosition(new PositionStatus
                {
                    EquipmentId = eq, PositionId = pos, State = PositionState.Offline
                });
            }
            _logger.LogInformation("调度器关闭：已向看板播种 {N} 个加工位（Offline）", posKeys.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "调度器关闭时播种看板加工位失败"); }
    }

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

    /// <summary>取机台上/下料架绑定 ID（带缓存）。</summary>
    private async Task<EquipmentFrameBindingIds> ResolveBindingsAsync(long equipmentId, CancellationToken ct)
    {
        if (_bindingCache.TryGetValue(equipmentId, out var cached)) return cached;
        var ids = await _equipment.GetFrameBindingIdsAsync(equipmentId, ct);
        _bindingCache[equipmentId] = ids;
        return ids;
    }

    public bool IsReconciled { get; private set; }
    public event EventHandler? Reconciled;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.SchedulerEnabled)
        {
            _logger.LogInformation("加工位状态机调度器未启用（SchedulerEnabled=false）。");
            await SeedDashboardPositionsAsync(cancellationToken);
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

    /// <summary>§6.3 启动三方对账：①RCS 未完结任务绑定回工位；①b query 终态收口（落账/回滚预记）；②槽位账陈旧预记按方向回滚；③PLC 账实核对（有料无任务 → 报警等人工）。</summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        var unfinished = new List<string>();
        // ① RCS 任务对账
        try
        {
            unfinished = (await _taskStore.GetUnfinishedTaskIdsAsync(ct)).ToList();
            foreach (var taskId in unfinished)
            {
                var row = await _taskStore.GetByTaskIdAsync(taskId, ct);
                if (row is null || row.PositionId is null || row.EquipmentId is null) continue;
                var key = (Eq: row.EquipmentId.Value, Pos: row.PositionId.Value);
                var ctx = _contexts.GetOrAdd(key, k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
                ctx.CurrentTaskId = taskId;
                ctx.Phase = row.TaskType == "1" ? PositionPhase.Unload : PositionPhase.Upload;
                ctx.State = PositionState.Dispatching; // 等 RCS 状态明确后由 loop / ①b 推进
                _logger.LogInformation("对账①：任务 {TaskId} 绑定回 EQ{Eq} POS{Pos} 阶段 {Phase}", taskId, key.Eq, key.Pos, ctx.Phase);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "启动对账①查询未完结任务失败"); }

        // ①b 向 RCS query 未完结任务：已终态的立刻收口槽位账 + 工位态，并从 active 集合剔除（供②兜底）
        try
        {
            var settled = await SettleTerminalTasksOnReconcileAsync(unfinished, ct);
            if (settled.Count > 0)
            {
                unfinished = unfinished.Where(id => !settled.Contains(id)).ToList();
                _logger.LogInformation("对账①b：收口终态任务 {N} 个", settled.Count);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "启动对账①b 终态收口失败"); }

        // ② 槽位账对账：回滚未完结任务集之外的陈旧预记（按 BindSource 方向）
        try
        {
            var n = await _slots.RollbackStaleReservationsAsync(unfinished, ct);
            if (n > 0) _logger.LogInformation("对账②：回滚陈旧槽位预记 {N} 个", n);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "启动对账②槽位账回滚失败"); }

        // ③ PLC 账实核对：工位有料但无绑定任务/无待交接 → 账实不符，置 ALARM 等人工（三方对完账才开闸）
        try
        {
            foreach (var (eq, pos, _) in _positions)
            {
                _contexts.TryGetValue((eq, pos), out var ctx);
                if (ctx is not null && !string.IsNullOrEmpty(ctx.CurrentTaskId)) continue; // 已有在途任务，正常
                if (_expectedInbound.ContainsKey((eq, pos))) continue;

                var machine = _store.GetMachine(eq);
                if (machine is null || !machine.PlcOnline) continue; // PLC 未上线，交给运行态离线处理
                var tmp = new PositionContext { EquipmentId = eq, PositionId = pos };
                var hasMat = await ReadHasMatFreshAsync(tmp, ct);
                if (hasMat == true)
                {
                    var ctxAlarm = _contexts.GetOrAdd((eq, pos), k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
                    ctxAlarm.AlarmRaised = true;
                    SetState(ctxAlarm, PositionState.Alarm);
                    await _alarms.RaiseRcsTaskNotFoundAsync($"RECONCILE-EQ{eq}-POS{pos}", ct);
                    _logger.LogWarning("对账③：EQ{Eq} POS{Pos} PLC 有料但无任务（账实不符）→ ALARM 等人工确认", eq, pos);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "启动对账③ PLC 账实核对失败"); }
    }

    /// <summary>
    /// 对账①b：批量 queryTask，对已终态任务立刻收口——先 PLC 复核再 Confirm/Rollback，避免「RCS 报完成但料未到」误清空槽位。
    /// 返回已收口的 taskId 集合（应从 active/unfinished 剔除）。
    /// </summary>
    private async Task<HashSet<string>> SettleTerminalTasksOnReconcileAsync(IReadOnlyList<string> unfinished, CancellationToken ct)
    {
        var settled = new HashSet<string>(StringComparer.Ordinal);
        if (unfinished.Count == 0) return settled;

        var req = new QueryTaskRequest
        {
            Condition = new QueryCondition
            {
                Relation = "AND",
                Conditions =
                {
                    new QueryConditionItem { Key = "taskId", Value = string.Join(",", unfinished), Operator = "IN", Order = "None" }
                }
            },
            PageIndex = 1,
            PageSize = Math.Max(10, unfinished.Count)
        };
        var result = await _taskSvc.QueryAsync(req, ct);
        if (!result.Success)
        {
            _logger.LogWarning("对账①b queryTask 失败：{Msg}，跳过终态收口（②仍按库内未完结保护预记）", result.Message ?? result.Error);
            return settled;
        }

        foreach (var (taskId, rcsStatus) in ParseQueryItems(result.RawResponse))
        {
            var state = RcsStatusMapper.ToTaskState(rcsStatus);
            if (state is null || !RcsStatusMapper.IsTerminal(state)) continue;

            var row = await _taskStore.GetByTaskIdAsync(taskId, ct);
            if (row is null) continue;
            if (row.TaskState != state)
                await _taskStore.UpdateStateAsync(taskId, state, rcsStatus, row.ErrorMsg, ct);

            PositionContext? ctx = null;
            if (row.EquipmentId is long eq && row.PositionId is long pos)
                _contexts.TryGetValue((eq, pos), out ctx);

            var phase = ctx?.Phase ?? (row.TaskType == "1" ? PositionPhase.Unload : PositionPhase.Upload);
            if (ctx is null)
            {
                // 无工位上下文（任务未绑加工位，如换架）：仅按方向收口槽位账
                await SettleSlotForTerminalAsync(taskId, phase, state, hasMat: null, ct);
                settled.Add(taskId);
                _logger.LogInformation("对账①b：无工位任务 {TaskId} 终态 {State}，已收口槽位账", taskId, state);
                continue;
            }

            bool? hasMat = null;
            if (state == RcsTaskState.Completed)
                hasMat = await ReadHasMatFreshAsync(ctx, ct);

            await SettleSlotForTerminalAsync(taskId, phase, state, hasMat, ct);

            if (state == RcsTaskState.Completed && phase == PositionPhase.Upload && hasMat == true)
            {
                // 上料完成且 PLC 有料 → 进 Loaded，由主循环写启动/加工记录
                ctx.CurrentTaskId = taskId;
                ctx.Phase = PositionPhase.Upload;
                SetState(ctx, PositionState.Loaded);
            }
            else if (state == RcsTaskState.Completed && phase == PositionPhase.Unload && hasMat == false)
            {
                // 下料完成且 PLC 无料 → 复位检测启动后清任务回 WaitLoad（跳过 Unloaded 态，补写 POS_TEST_START=2）
                await WriteTestStartAsync(ctx, 2, ct);
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.ElectrodeId = null;
                SetState(ctx, PositionState.WaitLoad);
            }
            else if (state == RcsTaskState.Completed && hasMat is null)
            {
                // PLC 尚未可读（启动瞬间常见）：预记已保守回滚，工位回 WaitLoad，不 latch Alarm（避免误粘滞挡后续派工）
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.ElectrodeId = null;
                SetState(ctx, PositionState.WaitLoad);
                _logger.LogWarning("对账①b：{TaskId} COMPLETED 但 PLC HasMat 未读到（phase={Phase}），预记已回滚，工位回 WaitLoad", taskId, phase);
            }
            else if (state == RcsTaskState.Completed)
            {
                // COMPLETED 且 PLC 明确不符（上料 hasMat=false / 下料 hasMat=true）→ Alarm，预记已回滚
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.ElectrodeId = null;
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync($"RECONCILE-{taskId}", ct);
                _logger.LogWarning("对账①b：{TaskId} COMPLETED 但 PLC 不符（phase={Phase} hasMat={Has}）→ ALARM", taskId, phase, hasMat);
            }
            else
            {
                // CANCELED / FAILED：预记已回滚，工位回 WaitLoad（FAILED 的自动 redo 由 tracker 另途处理）
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.ElectrodeId = null;
                SetState(ctx, PositionState.WaitLoad);
                if (state == RcsTaskState.Canceled)
                    await _alarms.RaiseRcsTaskCanceledAsync(taskId, ct);
            }

            settled.Add(taskId);
            _logger.LogInformation("对账①b：任务 {TaskId} 终态 {State} 已收口 EQ{Eq} POS{Pos}", taskId, state, ctx.EquipmentId, ctx.PositionId);
        }

        return settled;
    }

    /// <summary>终态槽位收口：COMPLETED 且 PLC 符合阶段预期才 Confirm，否则 Rollback（避免误清空/误入库）。</summary>
    private async Task SettleSlotForTerminalAsync(string taskId, PositionPhase phase, string state, bool? hasMat, CancellationToken ct)
    {
        if (state == RcsTaskState.Completed)
        {
            if (phase == PositionPhase.Upload)
            {
                if (hasMat == true) await _slots.ConfirmTakeAsync(taskId, ct);
                else await _slots.RollbackTakeAsync(taskId, ct); // 无料或未知：电极应仍在源架
            }
            else if (phase == PositionPhase.Unload)
            {
                if (hasMat == false) await _slots.ConfirmAsync(taskId, ct);
                else await _slots.RollbackAsync(taskId, ct); // 仍有料或未知：入库未真正完成
            }
            return;
        }

        // CANCELED / FAILED
        if (phase == PositionPhase.Upload) await _slots.RollbackTakeAsync(taskId, ct);
        else if (phase == PositionPhase.Unload) await _slots.RollbackAsync(taskId, ct);
    }

    /// <summary>解析 queryTask 应答 items[] → (taskId, status)。</summary>
    private static IReadOnlyList<(string taskId, string status)> ParseQueryItems(string? raw)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return list;
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var id = it.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                var st = it.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(st))
                    list.Add((id!, st!));
            }
        }
        catch { /* 解析失败：本轮跳过 */ }
        return list;
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
        // 不安全/开门：
        //   已投入运行（非 Offline）→ 报警（安全底线：运行中安全掉线必须停下）；
        //   仍处 Offline（启动/未就绪，机台尚未上报安全信号）→ 保持 Offline 等就绪，不误 latch 粘滞告警。
        if (safe == false || doorOpen)
        {
            if (ctx.State != PositionState.Offline) { SetState(ctx, PositionState.Alarm); return; }
            SetState(ctx, PositionState.Offline);
            return;
        }

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
            _logger.LogInformation("EQ{Eq} POS{Pos} {Prev} → {Next}", ctx.EquipmentId, ctx.PositionId, prev, next);
        SetState(ctx, next); // SetState 内统一写 ctx.State（含 WAIT_LOAD 进/出标记维护）
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
                // 工序间直接交接：上游 OK 件已送入本工位 cell，PLC 见料 + 有待入库登记 → 直接进 Loaded（视同已上料到位）。
                if (hasMat == true && _expectedInbound.TryGetValue((ctx.EquipmentId, ctx.PositionId), out var inbound))
                {
                    _expectedInbound.TryRemove((ctx.EquipmentId, ctx.PositionId), out _);
                    ctx.Phase = PositionPhase.Upload;
                    ctx.CurrentTaskId = inbound.SourceTaskId; // 溯源上游下料任务
                    ctx.ElectrodeId = inbound.ElectrodeId;    // 电极码随交接件传入
                    _logger.LogInformation("EQ{Eq} POS{Pos} 收到工序间交接件（源 {Src} 电极 {El}），转 Loaded", ctx.EquipmentId, ctx.PositionId, inbound.SourceTaskId, inbound.ElectrodeId ?? "—");
                    next = PositionState.Loaded;
                    break;
                }
                // Layer 1：工位不自己查料/选槽，只标记"请求上料"，由单一调度消费者统一决策
                // （查料→选槽→原子预记→下发都在单消费者里串行，结构上杜绝两位同时看到同一件料）。
                // 转 Dispatching 由消费者下发成功后设置；抢不到料则保持 WAIT_LOAD 等待（不告警）。
                if (allowLoad == true && hasMat == false && string.IsNullOrEmpty(ctx.CurrentTaskId)
                    && !_expectedInbound.ContainsKey((ctx.EquipmentId, ctx.PositionId)))
                {
                    ctx.UploadRequested = true;
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
                    // 上料到位落账：源料架取料落账（电极已被取走）。工序间交接件无取料预记，此调用幂等无副作用。
                    if (!string.IsNullOrEmpty(ctx.CurrentTaskId))
                        await _slots.ConfirmTakeAsync(ctx.CurrentTaskId!, ct);
                    // 加工开始：写 WORK_RECORD（关联当前上料 taskId）
                    ctx.WorkRecordId = await _workRecords.RecordStartAsync(new WorkRecordStartArgs
                    {
                        EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId,
                        PositionCode = $"POS-{ctx.PositionId}", RcsTaskId = ctx.CurrentTaskId,
                        ElectrodeId = ctx.ElectrodeId, Author = "scheduler"
                    }, ct);
                    next = PositionState.Processing;
                }
                else next = PositionState.Alarm;
                break;
            case PositionState.Unloaded:
                if (await WriteTestStartAsync(ctx, 2, ct))
                {
                    // 下料到位落账：入库料架落账（下料/中转/NG 架）。直接交接到下一台机无入库预记，幂等无副作用。
                if (!string.IsNullOrEmpty(ctx.CurrentTaskId))
                    await _slots.ConfirmAsync(ctx.CurrentTaskId!, ct);
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.ElectrodeId = null; // 件已离开本工位，清电极码
                next = PositionState.WaitLoad;
                }
                else next = PositionState.Alarm;
                break;
            case PositionState.Alarm:
                if (!ctx.AlarmRaised)
                {
                    ctx.AlarmRaised = true;
                    // 回滚未完成的槽位预记（按方向）：上料取料回滚为占用，下料入库回滚为空
                    if (!string.IsNullOrEmpty(ctx.CurrentTaskId))
                    {
                        if (ctx.Phase == PositionPhase.Upload) await _slots.RollbackTakeAsync(ctx.CurrentTaskId!, ct);
                        else if (ctx.Phase == PositionPhase.Unload) await _slots.RollbackAsync(ctx.CurrentTaskId!, ct);
                    }
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
                    var isOk = next == PositionState.DoneOk;
                    if (ctx.WorkRecordId > 0)
                        await _workRecords.RecordResultAsync(ctx.WorkRecordId, isOk ? "0" : "1", null, ct);
                    if (await EnqueueUnloadAsync(ctx, isOk, ct)) next = PositionState.Dispatching;
                    else next = PositionState.Alarm;
                }
                break;
        }

        // 只在回到 WaitLoad 时清报警标记；Alarm 期间保持标记，避免每 tick 重复告警
        if (next == PositionState.WaitLoad)
            ctx.AlarmRaised = false;
        return next;
    }

    /// <summary>上料料源+路由解析结果。Decision=Queued 时 From/To 必有值、SourceFrameId 为取料料架。</summary>
    private readonly record struct UploadPlan(UploadDecision Decision, long? SourceFrameId, string? From, string? To);

    /// <summary>解析上料料源与起终点 cell（不下发、不预记）：本机中转架(role2)有件 → 回流取；否则上料架/LOAD_AREA。
    /// 无料源占用 → WaitMaterial；路由未配置 → Failed（已告警）。占用校验即"料源确认有料"的前置门（配合单消费者串行，杜绝两位并发抢同一件）。</summary>
    private async Task<UploadPlan> ResolveUploadPlanAsync(PositionContext ctx, CancellationToken ct)
    {
        // 上料源优先级：本机中转架(role2)有件 → 从中转架取（工序间流转回流）；否则从上料架/LOAD_AREA 取。
        var binds = await ResolveBindingsAsync(ctx.EquipmentId, ct);
        var transitFrameId = await _equipment.GetFrameBindingByRoleAsync(ctx.EquipmentId, FrameRole.Transit, ct);
        long? sourceFrameId = null;
        string? from = null, to = null;

        if (transitFrameId is long tf && (await _slots.GetOccupancyAsync(tf, ct)).Occupied > 0)
        {
            // 中转架回流：from=中转架 cell，to=本加工位 cell
            var transitCell = await _routes.ResolveFrameCellAsync(tf, ct);
            var posCell = await _routes.ResolvePositionCellAsync(ctx.EquipmentId, ctx.PositionId, ct);
            if (transitCell is not null && posCell is not null)
            {
                sourceFrameId = tf; from = transitCell; to = posCell;
                _logger.LogInformation("EQ{Eq} POS{Pos} 从中转架 {Frame} 回流取件", ctx.EquipmentId, ctx.PositionId, tf);
            }
        }

        if (from is null)
        {
            // 无上料架(role0)绑定 = 纯下游机台：只接收上游工序间交接 / 中转架回流，不从 LOAD_AREA 自取原料。
            if (binds.UploadFrameId is not long upFrame)
            {
                _logger.LogDebug("EQ{Eq} POS{Pos} 无上料架绑定（纯下游机台），等待上游交接/中转回流", ctx.EquipmentId, ctx.PositionId);
                return new UploadPlan(UploadDecision.WaitMaterial, null, null, null);
            }
            // 上料架有料校验：账面无占用 → 等料（非告警，由水位/人工补料）
            var occ = await _slots.GetOccupancyAsync(upFrame, ct);
            if (occ.Occupied == 0)
            {
                _logger.LogDebug("EQ{Eq} POS{Pos} 上料架 {Frame} 无料（账面 occupied=0），保持等料", ctx.EquipmentId, ctx.PositionId, upFrame);
                return new UploadPlan(UploadDecision.WaitMaterial, null, null, null);
            }
            sourceFrameId = upFrame;
            var route = await _routes.ResolveUploadAsync(ctx.EquipmentId, ctx.PositionId, ct);
            if (route is null)
            {
                await _alarms.RaiseRcsTaskNotFoundAsync($"UPLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 上料路由未配置（LOCATION_MAP 缺 LOAD_AREA/加工位 cell）", ctx.EquipmentId, ctx.PositionId);
                return new UploadPlan(UploadDecision.Failed, null, null, null);
            }
            (from, to) = route.Value;
        }

        return new UploadPlan(UploadDecision.Queued, sourceFrameId, from, to);
    }

    /// <summary>下料入队：仅解析下料源 cell + 结果，入"下料请求"队。
    /// 终点决策（NG架/选下游空工位/中转架/下料架）推迟到单消费者出队时统一做——把"选下游空工位"与"登记待交接"收进同一串行步骤，
    /// 避免两件下料抢到同一个下游空工位（与上料竞态同构）。</summary>
    private async Task<bool> EnqueueUnloadAsync(PositionContext ctx, bool isOk, CancellationToken ct)
    {
        var fromCell = await _routes.ResolvePositionCellAsync(ctx.EquipmentId, ctx.PositionId, ct);
        if (fromCell is null)
        {
            await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}", ct);
            _logger.LogWarning("EQ{Eq} POS{Pos} 下料源 cell 未配置（LOCATION_MAP 缺加工位 cell）", ctx.EquipmentId, ctx.PositionId);
            return false;
        }
        var line = await ResolveLineAsync(ctx.EquipmentId, ct);
        _queue.Enqueue(new DispatchItem
        {
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId, Phase = PositionPhase.Unload,
            Priority = 8, FromCode = fromCell, ToCode = "", IsOk = isOk,
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, Author = "scheduler",
            ElectrodeId = ctx.ElectrodeId
        });
        ctx.Phase = PositionPhase.Unload;
        ctx.CurrentTaskId = null; // 上料任务已完结，清掉；下料任务由消费者绑定新 taskId
        _logger.LogInformation("EQ{Eq} POS{Pos} 入下料队 from={From} isOk={Ok}（终点由消费者决策）", ctx.EquipmentId, ctx.PositionId, fromCell, isOk);
        return true;
    }

    /// <summary>决策下料终点（在单消费者内调用）。返回 null 表示无可用终点（告警人工）。</summary>
    private async Task<UnloadDecision?> ResolveUnloadTargetAsync(PositionContext ctx, bool isOk, CancellationToken ct)
    {
        if (!isOk)
        {
            // NG → NG 专用架（role3）；未绑定则回退下料架/UNLOAD_AREA（并告警提示）
            var ngFrame = await _equipment.GetFrameBindingByRoleAsync(ctx.EquipmentId, FrameRole.NgFrame, ct);
            if (ngFrame is long ng)
            {
                var cell = await _routes.ResolveFrameCellAsync(ng, ct);
                if (cell is not null) return new UnloadDecision(cell, UnloadTarget.NgFrame, ng, null, null);
            }
            _logger.LogWarning("EQ{Eq} POS{Pos} NG 但未绑定 NG 架（role3），回退下料架", ctx.EquipmentId, ctx.PositionId);
            return await ResolveDownloadFrameFallbackAsync(ctx, ct);
        }

        // OK → 下一道工序流转
        var nextEqs = await _equipment.GetNextProcessEquipmentsAsync(ctx.EquipmentId, ct);
        if (nextEqs.Count == 0)
            return await ResolveDownloadFrameFallbackAsync(ctx, ct); // 末道工序 → 下料架

        // 下一工序有空闲工位 → 直接交接
        var idle = FindIdlePositionAmong(nextEqs);
        if (idle is not null)
        {
            var cell = await _routes.ResolvePositionCellAsync(idle.Value.Eq, idle.Value.Pos, ct);
            if (cell is not null)
                return new UnloadDecision(cell, UnloadTarget.NextMachineCell, null, idle.Value.Eq, idle.Value.Pos);
        }

        // 下一工序全忙 → 入下一工序某机的中转架（role2）排队
        foreach (var nextEq in nextEqs)
        {
            var transit = await _equipment.GetFrameBindingByRoleAsync(nextEq, FrameRole.Transit, ct);
            if (transit is long tf)
            {
                var cell = await _routes.ResolveFrameCellAsync(tf, ct);
                if (cell is not null) return new UnloadDecision(cell, UnloadTarget.TransitFrame, tf, null, null);
            }
        }

        // 有下一工序但既无空闲工位又无中转架 → 回退下料架（避免件卡在机台）
        _logger.LogWarning("EQ{Eq} POS{Pos} OK 但下一工序无空闲工位且无中转架，回退下料架", ctx.EquipmentId, ctx.PositionId);
        return await ResolveDownloadFrameFallbackAsync(ctx, ct);
    }

    /// <summary>末道/回退：下料架(role1) cell；无绑定回退 UNLOAD_AREA。</summary>
    private async Task<UnloadDecision?> ResolveDownloadFrameFallbackAsync(PositionContext ctx, CancellationToken ct)
    {
        var binds = await ResolveBindingsAsync(ctx.EquipmentId, ct);
        if (binds.DownloadFrameId is long df)
        {
            var cell = await _routes.ResolveFrameCellAsync(df, ct);
            if (cell is not null) return new UnloadDecision(cell, UnloadTarget.DownloadFrame, df, null, null);
        }
        var route = await _routes.ResolveUnloadAsync(ctx.EquipmentId, ctx.PositionId, ct);
        if (route is not null) return new UnloadDecision(route.Value.to, UnloadTarget.DownloadFrame, null, null, null);
        return null;
    }

    /// <summary>在候选机台的加工位中挑一个空闲工位（在线/安全/无料/无任务/无待交接）。
    /// 多候选按"空闲最久（WaitLoadSince 最早）→ 工位编号升序"确定性排序（防饿死、均衡、可复现）。
    /// 仅在单消费者内调用：选中后随即由调用方登记 _expectedInbound，串行保证两件下料不会抢到同一工位。</summary>
    private (long Eq, long Pos)? FindIdlePositionAmong(IReadOnlyList<long> equipmentIds)
    {
        var candidates = new List<(long Eq, long Pos, DateTime Since)>();
        foreach (var eq in equipmentIds)
        {
            var machine = _store.GetMachine(eq);
            if (machine is null || !machine.PlcOnline || machine.Safe == false || machine.DoorOpen == true) continue;
            var readings = _store.GetReadings(eq);
            foreach (var (pEq, pPos, _) in _positions)
            {
                if (pEq != eq) continue;
                if (_expectedInbound.ContainsKey((eq, pPos))) continue; // 已有件在途
                var since = DateTime.MaxValue;
                // 该工位调度上下文空闲：无当前任务且处于 WaitLoad/Offline
                if (_contexts.TryGetValue((eq, pPos), out var pctx))
                {
                    if (!string.IsNullOrEmpty(pctx.CurrentTaskId)) continue;
                    if (pctx.State != PositionState.WaitLoad && pctx.State != PositionState.Offline) continue;
                    since = pctx.WaitLoadSince ?? DateTime.MaxValue;
                }
                // PLC 无料（当前工位空）
                var hasMat = readings.FirstOrDefault(r => r.PositionId == pPos && r.Signal == SignalKey.PosHasMat)?.On;
                if (hasMat == true) continue;
                candidates.Add((eq, pPos, since));
            }
        }
        if (candidates.Count == 0) return null;
        var best = candidates.OrderBy(c => c.Since).ThenBy(c => c.Pos).First();
        return (best.Eq, best.Pos);
    }

    private async Task<bool> WriteTestStartAsync(PositionContext ctx, int value, CancellationToken ct)
    {
        if (!_testStartPoints.TryGetValue((ctx.EquipmentId, ctx.PositionId), out var tp))
        {
            _logger.LogWarning("EQ{Eq} POS{Pos} 未配置 POS_TEST_START 写点位", ctx.EquipmentId, ctx.PositionId);
            return false;
        }

        // 现场偶发 FINS/Modbus 写超时：失败后短间隔重试 1 次，仍失败再走 Alarm。
        var r = await _plcOps.WriteWithConfirmAsync(tp.PlcId, tp.RegAddr, value, "scheduler", ct);
        if (!r.Verified)
        {
            _logger.LogWarning("EQ{Eq} POS{Pos} 写 POS_TEST_START={V} 首次失败，250ms 后重试：{Err}",
                ctx.EquipmentId, ctx.PositionId, value, r.Error);
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return false; }
            r = await _plcOps.WriteWithConfirmAsync(tp.PlcId, tp.RegAddr, value, "scheduler", ct);
            if (!r.Verified)
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 写 POS_TEST_START={V} 重试仍失败：{Err}",
                    ctx.EquipmentId, ctx.PositionId, value, r.Error);
                return false;
            }
            _logger.LogInformation("EQ{Eq} POS{Pos} 写 POS_TEST_START={V} 重试成功", ctx.EquipmentId, ctx.PositionId, value);
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
        var prev = ctx.State;
        ctx.State = state;
        // Layer 1：进入 WAIT_LOAD 记空闲起点（供竞争排序）；离开 WAIT_LOAD 清"请求上料"标记与空闲计时。
        if (state == PositionState.WaitLoad)
        {
            if (prev != PositionState.WaitLoad) ctx.WaitLoadSince = DateTime.Now;
        }
        else
        {
            ctx.UploadRequested = false;
            ctx.WaitLoadSince = null;
        }
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

    /// <summary>单一调度消费者（Layer 1）：先派下料（优先级高、无料源争用），队列空时再统一分配上料。
    /// 所有"查料源→选槽→原子预记→下发"都在此单线程串行完成——两个空工位不可能同时看到并取走同一件料。</summary>
    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var item = _queue.Dequeue();
                if (item is not null)
                {
                    await DispatchOneAsync(item, ct); // 下料（DONE→下料队）
                    continue;                          // 尽快清空下料队列后再处理上料
                }
                var dispatched = await AllocateUploadsAsync(ct);
                if (!dispatched) await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "派工循环异常"); await Task.Delay(500, ct); }
        }
    }

    /// <summary>Layer 1 上料分配：收集"请求上料"的工位，按"空闲最久 → 工位编号升序"确定性排序，逐个尝试下发。
    /// 串行处理保证前一位取料预记落地后，后一位再查料——料不足时后位自然看到无料而继续等待（不告警、不重试风暴）。
    /// 返回本轮是否有成功下发（用于控制空转 delay）。</summary>
    private async Task<bool> AllocateUploadsAsync(CancellationToken ct)
    {
        var candidates = _contexts.Values
            .Where(c => c.UploadRequested && c.State == PositionState.WaitLoad && string.IsNullOrEmpty(c.CurrentTaskId))
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
            // WaitMaterial：保留 UploadRequested，下一轮或新料到位时再评估；Failed：TryDispatch 内已置 Alarm。
        }
        return any;
    }

    /// <summary>解析上料料源+路由 → 下发 RCS → 原子取料预记 → 绑定回工位（成功转 Dispatching）。
    /// 返回 Queued（已下发）/ WaitMaterial（无料，保持等待）/ Failed（路由缺失或下发失败，已置 Alarm）。</summary>
    private async Task<UploadDecision> TryDispatchUploadAsync(PositionContext ctx, CancellationToken ct)
    {
        var plan = await ResolveUploadPlanAsync(ctx, ct);
        if (plan.Decision == UploadDecision.WaitMaterial) return UploadDecision.WaitMaterial;
        if (plan.Decision == UploadDecision.Failed)
        {
            var g = GateFor((ctx.EquipmentId, ctx.PositionId));
            await g.WaitAsync(ct);
            try { ctx.AlarmRaised = true; SetState(ctx, PositionState.Alarm); }
            finally { g.Release(); }
            return UploadDecision.Failed;
        }

        // 料源已确认有料（占用槽存在）→ 下发 RCS（网络调用在锁外）。
        var line = await ResolveLineAsync(ctx.EquipmentId, ct);
        var result = await _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, TaskType = "0",
            Priority = 5, FromCode = plan.From!, ToCode = plan.To!,
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId,
            Kind = RcsTaskKind.Transit, Author = "scheduler"
        }, ct);

        var gate = GateFor((ctx.EquipmentId, ctx.PositionId));
        await gate.WaitAsync(ct);
        try
        {
            if (!result.Success || string.IsNullOrEmpty(result.TaskId))
            {
                // 下发失败 → 粘滞 Alarm，等人工恢复；不自动重发（防重试风暴）
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync($"UPLOAD-POS{ctx.PositionId}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 上料下发失败：{Err}", ctx.EquipmentId, ctx.PositionId, result.Error ?? result.Message);
                return UploadDecision.Failed;
            }

            ctx.CurrentTaskId = result.TaskId;
            ctx.Phase = PositionPhase.Upload;
            ctx.UploadRequested = false;

            // 原子取料预记（Layer 2）：源料架取一件、捕获电极码随件流转。
            if (plan.SourceFrameId is long src)
            {
                var taken = await _slots.ReserveTakeAsync(src, result.TaskId!, ct);
                if (taken is not null) ctx.ElectrodeId = taken.ElectrodeId;
                else
                {
                    // 已下发但源料架无可取料——单消费者串行下常规竞争不会命中此处；命中即外部写入者/盘点在下发窗口内取走了最后一件（账实异常）→ 告警人工。
                    ctx.AlarmRaised = true;
                    SetState(ctx, PositionState.Alarm);
                    await _alarms.RaiseRcsTaskNotFoundAsync($"UPLOAD-NOSTOCK-POS{ctx.PositionId}", ct);
                    _logger.LogWarning("EQ{Eq} POS{Pos} 上料已下发但源料架 {Frame} 无可取料（并发/账实异常）→ ALARM", ctx.EquipmentId, ctx.PositionId, src);
                    return UploadDecision.Failed;
                }
            }
            SetState(ctx, PositionState.Dispatching);
            _logger.LogInformation("EQ{Eq} POS{Pos} 下发上料任务 {TaskId} {From}→{To}", ctx.EquipmentId, ctx.PositionId, result.TaskId, plan.From, plan.To);
            return UploadDecision.Queued;
        }
        finally { gate.Release(); }
    }

    /// <summary>单消费者出队处理下料请求：先决策终点（含"选下游空工位"）→ 下发 RCS → 绑定 + 登记/入库预记。
    /// 选位与登记同在此串行完成，前一件登记落地后后一件才选位，两件下料不会抢到同一下游空工位。</summary>
    private async Task DispatchOneAsync(DispatchItem item, CancellationToken ct)
    {
        var ctx = _contexts.GetOrAdd((item.EquipmentId, item.PositionId), k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });

        // 终点决策（NG架/选下游空工位/中转架/下料架）在消费者内串行完成。
        var decision = await ResolveUnloadTargetAsync(ctx, item.IsOk, ct);
        if (decision is null)
        {
            var g0 = GateFor((item.EquipmentId, item.PositionId));
            await g0.WaitAsync(ct);
            try
            {
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{item.EquipmentId}-POS{item.PositionId}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 下料终点未配置（isOk={Ok}，请录入 NG/中转/下料架绑定或 UNLOAD_AREA）→ ALARM", item.EquipmentId, item.PositionId, item.IsOk);
            }
            finally { g0.Release(); }
            return;
        }
        var d = decision.Value;

        // 网络下发在锁外
        var result = await _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = item.WorkLineId, LineCode = item.LineCode, TaskType = "1",
            Priority = item.Priority, FromCode = item.FromCode, ToCode = d.ToCell,
            EquipmentId = item.EquipmentId, PositionId = item.PositionId,
            Kind = RcsTaskKind.Transit, Author = item.Author
        }, ct);

        // bug#7：回填 ctx 与主循环驱动串行化（仅结果写入在锁内）
        var gate = GateFor((item.EquipmentId, item.PositionId));
        await gate.WaitAsync(ct);
        try
        {
            if (result.Success && !string.IsNullOrEmpty(result.TaskId))
            {
                ctx.CurrentTaskId = result.TaskId;
                ctx.Phase = PositionPhase.Unload;
                await ApplyUnloadReservationAsync(item, d, result.TaskId!, ct);
                _logger.LogInformation("EQ{Eq} POS{Pos} 下发下料任务 {TaskId} {From}→{To}（{Target}）", item.EquipmentId, item.PositionId, result.TaskId, item.FromCode, d.ToCell, d.Target);
            }
            else
            {
                // 派工失败 → 粘滞 Alarm（经 SetState 刷看板），等人工恢复；不自动重发（防重试风暴）
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-POS{item.PositionId}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 下料下发失败：{Err}", item.EquipmentId, item.PositionId, result.Error ?? result.Message);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>下发成功后按下料终点做登记（直接交接）/ 入库预记（料架）+ 电极码流转。</summary>
    private async Task ApplyUnloadReservationAsync(DispatchItem item, UnloadDecision d, string taskId, CancellationToken ct)
    {
        try
        {
            if (d.Target == UnloadTarget.NextMachineCell && d.DestEquipmentId is long dstEq && d.DestPositionId is long dstPos)
            {
                // 直接交接：登记目标工位待入库（不记料架账，件进机台），电极码随交接传给下游
                _expectedInbound[(dstEq, dstPos)] = new InboundHandoff(taskId, item.ElectrodeId);
            }
            else if (d.DestFrameId is long destFrame)
            {
                // 入下料/中转/NG 架：入库预记；满架 → 告警人工（不静默丢件）
                var put = await _slots.ReserveAsync(destFrame, taskId, item.ElectrodeId, ct);
                if (put is null)
                {
                    _logger.LogWarning("EQ{Eq} POS{Pos} 料架 {Frame} 已满，件 {Task}（电极 {El}）无法入库预记，需人工换架/清架",
                        item.EquipmentId, item.PositionId, destFrame, taskId, item.ElectrodeId ?? "—");
                    await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        $"料架 {destFrame} 已满，件 {taskId}（电极 {item.ElectrodeId ?? "—"}）无法入库，请人工换架/清架", taskId, ct);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "下料槽位账预记异常 task={Task}", taskId); }
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
        /// <summary>当前件的电极码（上料取料时捕获，随件流转至下料/交接，供落账与加工记录溯源）。</summary>
        public string? ElectrodeId { get; set; }
        /// <summary>Layer 1：已向单一调度消费者投递"请求上料"（去重，避免每 tick 重复投递）。</summary>
        public bool UploadRequested { get; set; }
        /// <summary>进入 WAIT_LOAD 的时刻——多工位竞争同一料源时"空闲最久优先"的确定性排序依据。</summary>
        public DateTime? WaitLoadSince { get; set; }
    }

    /// <summary>上料入队决策：入队 / 料架无料等待 / 失败告警。</summary>
    private enum UploadDecision { Queued, WaitMaterial, Failed }

    /// <summary>下料终点决策：终点 cell + 终点类型 + 目标料架/机台工位。</summary>
    private readonly record struct UnloadDecision(string ToCell, UnloadTarget Target, long? DestFrameId, long? DestEquipmentId, long? DestPositionId);

    /// <summary>工序间直接交接登记：上游把 OK 件送入下游 cell 后，下游见料即接。</summary>
    private sealed record InboundHandoff(string? SourceTaskId, string? ElectrodeId);
}
