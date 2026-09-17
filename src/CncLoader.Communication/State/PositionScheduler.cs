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
/// 每加工位一个独立 <see cref="PositionContext"/>，主循环按 PLC 分组并发、组内逐个驱动（DriveAllAsync，P2-4），按 §7 状态机推进：
/// WAIT_LOAD→DISPATCHING→TRANSPORTING→(PLC复核)→LOADED→PROCESSING→DONE→DISPATCHING→TRANSPORTING→UNLOADED→WAIT_LOAD。
/// LOADED/UNLOADED 双条件（RCS completed 且 PLC 复核通过）；复核不过 → ALARM 不写启动（安全底线）。
/// 启动时先做 §6.3 对账（未完结任务绑定回加工位），对账完成前不自动派工。
/// </summary>
public sealed partial class PositionScheduler : IHostedService, IPositionScheduler, IPositionDispatchHost,
    IPositionActionHost, IPositionStartupReconcileHost, IPositionDispatchRuntime
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
    private readonly IEquipmentConfigService _equipment;
    private readonly ISlotAccountService _slots;
    private readonly IRoutingAvailabilityValidator _routingValidator;
    private readonly RcsOptions _options;
    private readonly TimeSpan _signalMaxAge;
    /// <summary>陈旧预记回滚宽限：未满宽限的预记可能是尚未落库的在途派工（P0-1）。</summary>
    private TimeSpan _staleReservationGrace => StaleReservationPolicy.ComputeGrace(_options.RequestTimeoutMs, _options.MaxRetries);
    private readonly ILogger<PositionScheduler> _logger;
    private readonly ReconciliationStatePublisher _reconcilePublisher = new();
    private readonly UploadDispatchPlanner _uploadPlanner;
    private readonly UnloadDispatchPlanner _unloadPlanner;
    private readonly PositionDispatchConsumer _dispatchConsumer;
    private readonly PositionDispatchExecutor _dispatchExecutor;
    private readonly PositionDriveCoordinator _driveCoordinator;
    private readonly PositionActionExecutor _actionExecutor;
    private readonly PositionStartupReconciler _startupReconciler;
    private readonly PositionPlcSignalIo _plcIo;
    private readonly PositionInboundCoordinator _inboundCoord;
    private readonly PositionAlarmRecovery _alarmRecovery;
    private readonly PositionRouteCache _routeCache;
    private readonly PositionSlotMaintenance _slotMaintenance;
    private readonly PositionStatePublisher _statePublisher;
    private readonly PositionStateLoop _stateLoop;

    private readonly ConcurrentDictionary<(long Eq, long Pos), PositionContext> _contexts = new();
    // bug#7：每加工位一把信号量，串行化主循环驱动与派工回填对同一 ctx 的读写（不同加工位仍并行）。
    private readonly ConcurrentDictionary<(long Eq, long Pos), SemaphoreSlim> _posGates = new();
    private readonly PositionInboundRegistry _inbound = new();
    private List<(long Eq, long Pos, long PlcId)> _positions = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _dispatchTask;
    /// <summary>统一启动对账后台工作流（single-flight；由 StopAsync 观察）。</summary>
    private Task? _reconcileWorkflowTask;
    private readonly RcsCallbackNotifier? _notifier;
    /// <summary>0=自动派工开，1=暂停新自动上下料派工（进程内，volatile 供 UI/调度循环可见）。</summary>
    private volatile int _autoDispatchPaused;
    private volatile bool _isReconciled;
    private volatile int _reconciliationState;
    private string? _reconciliationFailureReason;
    /// <summary>开闸门闩：0=未开，1=已开（防重复启循环/事件）。</summary>
    private int _gateOpened;
    private int _reconcileAttemptCount;
    /// <summary>统一 reconciliation workflow 启动次数（0/1）；兼容旧名 ReconcileRetryLoopStartCount。</summary>
    private int _reconcileRetryLoopStartCount;
    /// <summary>生命周期锁：Stop 与开闸的线性化点（先取得锁者决定能否开闸）。</summary>
    private readonly object _lifecycleLock = new();
    /// <summary>已进入停止：取得后禁止任何成功开闸副作用。</summary>
    private bool _stopping;

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
        IRoutingAvailabilityValidator routingValidator,
        IPlcWriteHook? writeHook = null,
        RcsCallbackNotifier? notifier = null)
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
        _signalMaxAge = TimeSpan.FromMilliseconds(options.Value.Plc.EffectiveSignalMaxAgeMs);
        _logger = logger;
        _equipment = equipment;
        _slots = slots;
        _routingValidator = routingValidator ?? throw new ArgumentNullException(nameof(routingValidator));
        _writeHook = writeHook;
        _notifier = notifier;
        _statePublisher = new PositionStatePublisher(_store, _taskStore);
        _routeCache = new PositionRouteCache(
            _equipment, _alarms, _queue, _logger, GateFor, _statePublisher.SetState);
        _slotMaintenance = new PositionSlotMaintenance(
            _taskStore, _slots, _alarms, () => _staleReservationGrace, ReadHasMatFreshAsync, _logger);
        _uploadPlanner = new UploadDispatchPlanner(
            _equipment, _slots, _routes, _alarms, _logger, _routeCache.ResolveBindingsAsync);
        _unloadPlanner = new UnloadDispatchPlanner(
            _equipment, _routes, _alarms, _queue, _logger,
            _routeCache.ResolveBindingsAsync, _routeCache.ResolveLineAsync, _routeCache.LogRouteUnavailableThrottled);
        _plcIo = new PositionPlcSignalIo(_plcOps, _logger, _writeHook);
        _inboundCoord = new PositionInboundCoordinator(_inbound, _taskStore, _alarms, _logger);
        _alarmRecovery = new PositionAlarmRecovery(_taskStore, _alarms, _logger);
        _dispatchConsumer = new PositionDispatchConsumer(this, _queue, _logger);
        _dispatchExecutor = new PositionDispatchExecutor(
            this, _taskSvc, _taskStore, _slots, _equipment, _routes, _routingValidator,
            _queue, _alarms, _uploadPlanner, _unloadPlanner, _options, _logger);
        _driveCoordinator = new PositionDriveCoordinator(_logger);
        _actionExecutor = new PositionActionExecutor(_slots, workRecords, _inbound, this, _logger);
        _stateLoop = new PositionStateLoop(
            _store, _taskStore, _inbound, _actionExecutor, _statePublisher, _driveCoordinator,
            _signalMaxAge, _options, () => IsReconciled, () => IsAutoDispatchPaused, _logger);
        _startupReconciler = new PositionStartupReconciler(
            _taskStore, _taskSvc, _routes, _slots, _alarms, _store, _inbound, _contexts, _notifier, this, _logger);
    }

    bool IPositionDispatchRuntime.CanAutoDispatch => IsReconciled && !IsAutoDispatchPaused;
    IEnumerable<PositionContext> IPositionDispatchRuntime.Contexts => _contexts.Values;
    PositionContext IPositionDispatchRuntime.GetOrAddContext(long equipmentId, long positionId)
        => _contexts.GetOrAdd((equipmentId, positionId), k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });
    SemaphoreSlim IPositionDispatchRuntime.GateFor((long Eq, long Pos) key) => GateFor(key);
    void IPositionDispatchRuntime.SetState(PositionContext ctx, PositionState state) => _statePublisher.SetState(ctx, state);
    void IPositionDispatchRuntime.InvalidateLineCache(long equipmentId) => _routeCache.InvalidateLine(equipmentId);
    void IPositionDispatchRuntime.CacheLine(long equipmentId, WorkLineRef line) => _routeCache.CacheLine(equipmentId, line);
    void IPositionDispatchRuntime.LogRouteUnavailableThrottled(long equipmentId, string message)
        => _routeCache.LogRouteUnavailableThrottled(equipmentId, message);
    Task IPositionDispatchRuntime.DeferUnloadAsync(DispatchItem item, PositionContext ctx, string reason, CancellationToken ct)
        => _routeCache.DeferUnloadAsync(item, ctx, reason, ct);
    Task<bool> IPositionDispatchRuntime.CloseOrphanTaskAsync(string taskId, string reason, CancellationToken ct)
        => CloseOrphanTaskAsync(taskId, reason, ct);
    bool IPositionDispatchRuntime.HasInbound((long Eq, long Pos) key) => _inbound.Contains(key);
    bool IPositionDispatchRuntime.TryAddInbound((long Eq, long Pos) key, InboundHandoff handoff)
        => _inbound.TryAdd(key, handoff);
    bool IPositionDispatchRuntime.TryRemoveInboundIfSource((long Eq, long Pos) key, string taskId)
        => _inbound.TryRemoveIfSource(key, taskId);
    bool IPositionDispatchRuntime.TryMarkInboundDispatched((long Eq, long Pos) key, string localTaskId, string assignedTaskId)
        => _inbound.TryMarkDispatched(key, localTaskId, assignedTaskId);

    bool IPositionDispatchHost.CanAutoDispatch => IsReconciled && !IsAutoDispatchPaused;
    Task IPositionDispatchHost.DispatchOneAsync(DispatchItem item, CancellationToken ct)
        => _dispatchExecutor.DispatchOneAsync(item, ct);
    Task<bool> IPositionDispatchHost.AllocateUploadsAsync(CancellationToken ct)
        => _dispatchExecutor.AllocateUploadsAsync(ct);

    Task<PositionState> IPositionActionHost.RecheckHasMatAsync(PositionContext ctx, CancellationToken ct)
        => RecheckHasMatAsync(ctx, ct);
    Task<bool> IPositionActionHost.WriteTestStartAsync(PositionContext ctx, int value, CancellationToken ct)
        => WriteTestStartAsync(ctx, value, ct);
    Task<bool> IPositionActionHost.EnqueueUnloadAsync(PositionContext ctx, bool isOk, CancellationToken ct)
        => EnqueueUnloadAsync(ctx, isOk, ct);
    Task IPositionActionHost.RaiseAlarmPackageAsync(PositionContext ctx, bool? hasMat, string? rcsState, CancellationToken ct)
        => RaiseAlarmPackageAsync(ctx, hasMat, rcsState, ct);
    Task IPositionActionHost.ReclaimStaleInboundHandoffAsync(PositionContext ctx, InboundHandoff? inbound, CancellationToken ct)
        => ReclaimStaleInboundHandoffAsync(ctx, inbound, ct);

    IReadOnlyList<(long Eq, long Pos, long PlcId)> IPositionStartupReconcileHost.Positions => _positions;
    TimeSpan IPositionStartupReconcileHost.StaleReservationGrace => _staleReservationGrace;
    TimeSpan IPositionStartupReconcileHost.SignalMaxAge => _signalMaxAge;
    void IPositionStartupReconcileHost.SetState(PositionContext ctx, PositionState state) => _statePublisher.SetState(ctx, state);
    Task<bool?> IPositionStartupReconcileHost.ReadHasMatFreshAsync(PositionContext ctx, CancellationToken ct)
        => ReadHasMatFreshAsync(ctx, ct);
    Task<bool> IPositionStartupReconcileHost.WriteTestStartAsync(PositionContext ctx, int value, CancellationToken ct)
        => WriteTestStartAsync(ctx, value, ct);
    Task<int> IPositionStartupReconcileHost.SettleCompletedPendingWithPlcAsync(IReadOnlyCollection<string> unfinished, CancellationToken ct)
        => _slotMaintenance.SettleCompletedPendingWithPlcAsync(unfinished, ct);
    Task<SlotSettlementAction> IPositionStartupReconcileHost.SettleSlotForTerminalAsync(
        string taskId, PositionPhase phase, string state, bool? hasMat, bool plcCheckApplicable, CancellationToken ct)
        => _slotMaintenance.SettleSlotForTerminalAsync(taskId, phase, state, hasMat, plcCheckApplicable, ct);

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

    public bool IsReconciled => _isReconciled;
    public ReconciliationState ReconciliationState => (ReconciliationState)_reconciliationState;
    public string? ReconciliationFailureReason => _reconciliationFailureReason;
    public bool IsAutoDispatchPaused => _autoDispatchPaused != 0;
    public event EventHandler? Reconciled;
    public event EventHandler<ReconciliationSnapshot>? ReconciliationStateChanged
    {
        add => _reconcilePublisher.Changed += value;
        remove => _reconcilePublisher.Changed -= value;
    }

    /// <summary>测试接缝：状态推进循环已启动次数（成功开闸后应为 1）。</summary>
    internal int StateLoopStartCount { get; private set; }
    /// <summary>测试接缝：自动派工循环已启动次数（成功开闸后应为 1）。</summary>
    internal int DispatchLoopStartCount { get; private set; }
    /// <summary>测试接缝：<see cref="Reconciled"/> 已触发次数。</summary>
    internal int ReconciledRaiseCount { get; private set; }
    /// <summary>
    /// 测试接缝：统一 reconciliation workflow 启动次数（应为 0 或 1）。
    /// 兼容旧名；不再表示「失败后另开的 retry loop」。
    /// </summary>
    internal int ReconcileRetryLoopStartCount => Volatile.Read(ref _reconcileRetryLoopStartCount);
    /// <summary>测试接缝：对账尝试次数（含首次）。</summary>
    internal int ReconcileAttemptCount => Volatile.Read(ref _reconcileAttemptCount);
    /// <summary>测试接缝：可替换延迟（避免单测真实等待 5s）；生产默认 <see cref="Task.Delay(int, CancellationToken)"/>。</summary>
    internal Func<int, CancellationToken, Task>? DelayOverride { get; set; }
    /// <summary>测试接缝：开闸声明前同步回调（用于注入「停止已先取得生命周期」时序；禁止在回调内 await StopAsync）。</summary>
    internal Action? BeforeOpenGateClaim { get; set; }

    /// <summary>进入 stopping（StopAsync 首步；可幂等重复调用）。</summary>
    internal void EnterStopping()
    {
        lock (_lifecycleLock)
            _stopping = true;
    }

    public void SetAutoDispatchPaused(bool paused)
    {
        var next = paused ? 1 : 0;
        var prev = Interlocked.Exchange(ref _autoDispatchPaused, next);
        if (prev == next) return;
        if (paused)
            _logger.LogWarning("已开启「暂停自动派工 / 仅手动测试」：不再产生或下发新的自动上料/下料 RCS 任务；已下发任务跟踪与 PLC 复核继续。");
        else
            _logger.LogInformation("已关闭「暂停自动派工 / 仅手动测试」：恢复 PositionScheduler 自动上料/下料派工。");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 入口已取消：不启动 workflow、不进入对账、不开闸（D5）。
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.SchedulerEnabled)
        {
            _logger.LogInformation("加工位状态机调度器未启用（SchedulerEnabled=false）：不对账、不开闸。");
            await SeedDashboardPositionsAsync(cancellationToken);
            _isReconciled = false;
            _reconciliationState = (int)ReconciliationState.Disabled;
            _reconciliationFailureReason = null;
            PublishReconcileState();
            return;
        }

        // 1. 装载加工位与 POS_TEST_START 点位缓存（仅受启动 token 控制）
        await LoadPositionCacheAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        // 2. §6.3 对账：原子启动单一后台 workflow 后立即返回，不阻塞后续 HostedService（P1-1 / D1）。
        // 后台生命周期使用调度器自有 CTS；启动 token 不链接进运行期（D5）。
        lock (_lifecycleLock)
        {
            if (_stopping) return;
            if (_reconcileWorkflowTask is not null) return;

            _cts = new CancellationTokenSource();
            if (_notifier is not null)
                _notifier.TaskStatusReceived += _inboundCoord.OnRcsTaskStatus;

            _reconcileRetryLoopStartCount = 1;
            var lifecycleToken = _cts.Token;
            _reconcileWorkflowTask = Task.Run(
                () => ReconcileWorkflowAsync(lifecycleToken),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// 成功开闸（幂等）：IsReconciled / 双循环 / Reconciled 事件各至多一次。
    /// 线性化点：<_lifecycleLock> 内完成 stopping 检查 + 门闩 + 状态字段 + 双循环 Task 创建；
    /// 事件/日志在锁外触发，避免订阅方重入死锁。
    /// </summary>
    internal bool TryOpenGateAfterSuccess()
    {
        BeforeOpenGateClaim?.Invoke();

        lock (_lifecycleLock)
        {
            // 线性化点：与 EnterStopping 共用锁；stopping 已成立则整段开闸放弃（无半开闸）。
            if (_stopping) return false;
            if (_gateOpened != 0) return false;

            _gateOpened = 1;
            _isReconciled = true;
            _reconciliationState = (int)ReconciliationState.Succeeded;
            _reconciliationFailureReason = null;
            var loopToken = _cts?.Token ?? CancellationToken.None;
            StateLoopStartCount++;
            _loopTask = Task.Run(() => LoopAsync(loopToken));
            DispatchLoopStartCount++;
            _dispatchTask = Task.Run(() => DispatchLoopAsync(loopToken));
        }

        // 事件/日志在锁外：避免订阅方重入 Stop/开闸导致死锁。
        PublishReconcileState();
        ReconciledRaiseCount++;
        try { Reconciled?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { _logger.LogWarning(ex, "Reconciled 订阅方异常"); }
        _logger.LogInformation("启动对账完成，开始自动派工");
        return true;
    }

    private void PublishReconcileState()
    {
        try
        {
            _reconcilePublisher.TryPublish(
                (ReconciliationState)_reconciliationState,
                _reconciliationFailureReason,
                _isReconciled);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "发布 ReconciliationStateChanged 失败");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 先取得 stopping 生命周期控制权，再取消自有 CTS（禁止随后开闸；D6）。
        EnterStopping();
        if (_notifier is not null)
            _notifier.TaskStatusReceived -= _inboundCoord.OnRcsTaskStatus;
        _cts?.Cancel();
        var pending = Task.WhenAll(
            ObserveAsync(_loopTask),
            ObserveAsync(_dispatchTask),
            ObserveAsync(_reconcileWorkflowTask));
        // 有界等待覆盖最坏 PLC 写（FINS 3s + 250ms 重试 + 3s）：确保循环真正退出后再返回，
        // 避免宿主随后 Dispose 连接管理器时撞在途读写（P1-7）。下游忽略 CT 时由宿主 5s CTS 兜底。
        await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 循环内已记日志；此处仅避免未观察异常 */ }
    }

    public async Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        if (!_contexts.TryGetValue((equipmentId, positionId), out var ctx)) return;
        var gate = GateFor((equipmentId, positionId));
        await gate.WaitAsync(ct);
        try
        {
            await _alarmRecovery.ResetAlarmAsync(
                ctx,
                _plcIo.HasHasMatPoint((equipmentId, positionId)),
                ReadHasMatFreshAsync,
                _slotMaintenance.ApplySlotSettlementAsync,
                RequestInboundClearBySourceTask,
                (key, reason, token) => ClearInboundAtAsync(key, reason, token),
                WriteTestStartAsync,
                EnqueueUnloadAsync,
                _statePublisher.SetState,
                ct);
            await _statePublisher.PublishCancelHoldAsync(ctx, ct);
        }
        finally { gate.Release(); }
    }

    public async Task AcknowledgeCancelHoldAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        if (!_contexts.TryGetValue((equipmentId, positionId), out var ctx)) return;
        var gate = GateFor((equipmentId, positionId));
        await gate.WaitAsync(ct);
        try
        {
            var ids = await _taskStore.ListUnconfirmedCanceledTaskIdsAsync(equipmentId, positionId, ct);
            if (ids.Count == 0)
                throw new InvalidOperationException("该工位没有待确认取消任务");
            await _alarmRecovery.ConfirmUnconfirmedCancelsAsync(equipmentId, positionId, ct);
            await _statePublisher.PublishCancelHoldAsync(ctx, ct);
        }
        finally { gate.Release(); }
    }

    public void SetEquipmentDispatchHold(long equipmentId, bool held, string? reason = null)
        => _routeCache.SetHold(equipmentId, held, reason);

    public bool IsEquipmentDispatchHeld(long equipmentId) => _routeCache.IsHeld(equipmentId);

    public void InvalidateFrameBindingCache(long? equipmentId = null)
        => _routeCache.InvalidateBindings(equipmentId);

    public async Task ClearStaleDisplayMaterialAsync(string? materialId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(materialId)) return;

        foreach (var kv in _contexts)
        {
            var ctx = kv.Value;
            if (!string.Equals(ctx.MaterialId, materialId, StringComparison.Ordinal)) continue;
            if (ctx.State is not (PositionState.Alarm or PositionState.WaitLoad or PositionState.Offline))
                continue;

            var gate = GateFor(kv.Key);
            await gate.WaitAsync(ct);
            try
            {
                if (!string.Equals(ctx.MaterialId, materialId, StringComparison.Ordinal)) continue;
                if (ctx.State is not (PositionState.Alarm or PositionState.WaitLoad or PositionState.Offline))
                    continue;

                var hasMat = await ReadHasMatFreshAsync(ctx, ct);
                if (hasMat != false) continue;

                ctx.MaterialId = null;
                _statePublisher.PublishPosition(ctx);
                _logger.LogInformation(
                    "EQ{Eq} POS{Pos} 料架置空后清除看板物料 {Mat}（PLC 确认无料）",
                    ctx.EquipmentId, ctx.PositionId, materialId);
            }
            finally { gate.Release(); }
        }
    }

    public async Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        // 清掉以该任务为源的工序间交接登记（告警，下游可恢复上料）；由目标工位在其闸内执行（P1-6）
        RequestInboundClearBySourceTask(taskId, reason);

        var row = await _taskStore.GetByTaskIdAsync(taskId, ct);
        if (row?.EquipmentId is not long eq || row.PositionId is not long pos) return;
        if (!_contexts.TryGetValue((eq, pos), out var ctx)) return;

        var gate = GateFor((eq, pos));
        await gate.WaitAsync(ct);
        try
        {
            // 仅当工位仍绑定该任务时收口，避免误伤已换新任务的工位
            if (!string.Equals(ctx.CurrentTaskId, taskId, StringComparison.Ordinal)) return;

            if (ctx.Phase == PositionPhase.Upload)
                await _slots.RollbackTakeAsync(taskId, ct);
            else if (ctx.Phase == PositionPhase.Unload)
                await _slots.RollbackAsync(taskId, ct);

            ctx.CurrentTaskId = null;
            ctx.Phase = null;
            ctx.MaterialId = null;
            ctx.AlarmRaised = true;
            _statePublisher.SetState(ctx, PositionState.Alarm);
            _logger.LogWarning("EQ{Eq} POS{Pos} 任务 {Task} 已放弃（{Reason}）→ ALARM，可点恢复", eq, pos, taskId, reason);
        }
        finally { gate.Release(); }
    }

    private void RequestInboundClearBySourceTask(string taskId, string reason)
        => _inboundCoord.RequestClearBySourceTask(taskId, reason);

    private Task ApplyPendingInboundClearAsync((long Eq, long Pos) key, CancellationToken ct)
        => _inboundCoord.ApplyPendingClearAsync(key, ct);

    private Task ClearInboundAtAsync((long Eq, long Pos) key, string reason, CancellationToken ct,
        string? expectedSourceTaskId = null)
        => _inboundCoord.ClearAtAsync(key, reason, ct, expectedSourceTaskId);

    private async Task LoadPositionCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            var allPoints = await _points.GetAllAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var posKeys = new HashSet<(long, long)>();
            foreach (var p in allPoints)
            {
                if (p.PositionId is null) continue;
                var key = (p.EquipmentId, p.PositionId.Value);
                posKeys.Add(key);
                if (p.IsWrite && p.Signal == SignalKey.PosTestStart)
                    _plcIo.RememberTestStart(key, p.PlcId, p.RegisterAddress);
                if (!p.IsWrite && p.Signal == SignalKey.PosHasMat)
                    _plcIo.RememberHasMat(key, p.PlcId, p.RegisterAddress, p.OnValue, p.OffValue);
            }
            _positions = posKeys.Select(k => (k.Item1, k.Item2, _plcIo.TryGetTestStartPlc(k, out var plcId) ? plcId : 0L)).ToList();
            _logger.LogInformation("位置调度器装载 {N} 个加工位", _positions.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "装载加工位点位失败，调度器将以空集启动"); }
    }

    /// <summary>§6.3 启动三方对账：委托 <see cref="PositionStartupReconciler"/> fail-closed 编排 ①/①b/②/③。</summary>
    private Task<ReconcileRoundResult> ReconcileAsync(CancellationToken ct)
        => _startupReconciler.ReconcileAsync(ct);

    private Task LoopAsync(CancellationToken ct)
        => _stateLoop.RunAsync(DriveAllAsync, _slotMaintenance.SweepStaleReservationsIfDueAsync, ct);

    private Task DriveAllAsync(CancellationToken ct)
        => _stateLoop.DriveAllAsync(_positions, _contexts, DriveOneForGroupAsync, ct);

    private Task DriveOneForGroupAsync(PositionContext ctx, CancellationToken ct)
        => DrivePositionOverride is { } drive
            ? drive(ctx.EquipmentId, ctx.PositionId, ct)
            : DrivePositionAsync(ctx, ct);

    private async Task DrivePositionAsync(PositionContext ctx, CancellationToken ct)
    {
        // bug#7：与派工回填串行化对同一 ctx 的读写
        var gate = GateFor((ctx.EquipmentId, ctx.PositionId));
        await gate.WaitAsync(ct);
        try
        {
            // P1-6：他处按源任务发起的交接清理在本工位闸内执行，与见料消费 / 自取判定串行
            await ApplyPendingInboundClearAsync((ctx.EquipmentId, ctx.PositionId), ct);
            await _stateLoop.DriveCoreAsync(ctx, ct);
        }
        finally { gate.Release(); }
    }

    /// <summary>现读源任务状态，按 <see cref="PositionTransition.DecideInboundReclaim"/> 回收陈旧登记或告警。</summary>
    private Task ReclaimStaleInboundHandoffAsync(PositionContext ctx, InboundHandoff? inbound,
        CancellationToken ct)
        => _inboundCoord.ReclaimStaleAsync(ctx, inbound, ct);

    private Task<PositionState> RecheckHasMatAsync(PositionContext ctx, CancellationToken ct)
        => _plcIo.RecheckHasMatAsync(ctx, _options.HasMatRecheckFailThreshold, ct);

    private Task RaiseAlarmPackageAsync(PositionContext ctx, bool? hasMat, string? rcsState,
        CancellationToken ct)
        => _alarmRecovery.RaisePackageAsync(
            ctx, hasMat, rcsState,
            _plcIo.HasHasMatPoint((ctx.EquipmentId, ctx.PositionId)),
            ReadHasMatFreshAsync,
            _slotMaintenance.SettleSlotForTerminalAsync,
            RequestInboundClearBySourceTask,
            (key, reason, token) => ClearInboundAtAsync(key, reason, token),
            ct);

    private Task<bool> EnqueueUnloadAsync(PositionContext ctx, bool isOk, CancellationToken ct)
        => _unloadPlanner.EnqueueUnloadAsync(ctx, isOk, ct);

    private Task<bool> WriteTestStartAsync(PositionContext ctx, int value, CancellationToken ct)
        => _plcIo.WriteTestStartAsync(ctx, value, ct);

    private Task<bool?> ReadHasMatFreshAsync(PositionContext ctx, CancellationToken ct)
        => _plcIo.ReadHasMatFreshAsync(ctx, ct);

    /// <summary>看板工位卡：未确认取消占用，须带任务号供看板就地确认。</summary>
    internal const string CancelHoldPrefix = CancelHoldDisplay.Prefix;

    internal static string FormatCancelHoldDetail(IReadOnlyList<string> ids)
        => CancelHoldDisplay.Format(ids);

    /// <summary>单一调度消费者（Layer 1）：先派下料（优先级高、无料源争用），队列空时再统一分配上料。
    /// 所有"查料源→选槽→原子预记→下发"都在此单线程串行完成——两个空工位不可能同时看到并取走同一件料。</summary>
    private Task DispatchLoopAsync(CancellationToken ct)
        => _dispatchConsumer.RunLoopAsync(ct);

    private Task RunDispatchOnceAsync(CancellationToken ct)
        => _dispatchConsumer.RunOnceAsync(ct);

    private async Task<bool> CloseOrphanTaskAsync(string taskId, string reason, CancellationToken ct)
    {
        string error;
        try
        {
            var r = await _taskSvc.CancelAsync(taskId, ct);
            if (r.Success)
            {
                _logger.LogInformation("orphan 任务 {Task} 已取消（{Reason}）", taskId, reason);
                return true;
            }
            error = r.Message ?? r.Error ?? "—";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "收口 orphan 任务 {Task} 取消异常", taskId);
            error = ex.Message;
        }

        _logger.LogWarning("orphan 任务 {Task} 取消失败（{Reason}）：{Msg}；保留预记与任务态，交跟踪器收敛", taskId, reason, error);
        try
        {
            await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                $"孤儿任务 {taskId} 取消失败（{reason}）：{error}；小车可能仍在执行，预记已保留，请在 RCS 页核对",
                taskId, CancellationToken.None);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "orphan 任务 {Task} 取消失败告警落库失败", taskId); }
        return false;
    }

}
