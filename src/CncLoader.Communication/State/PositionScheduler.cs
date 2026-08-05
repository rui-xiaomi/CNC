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
    private readonly IRoutingAvailabilityValidator _routingValidator;
    private readonly RcsOptions _options;
    private readonly ILogger<PositionScheduler> _logger;
    private readonly ReservationFirstDispatcher _reservationFirstDispatcher = new();
    private readonly StartupReconcileCoordinator _reconcileCoordinator = new();
    private readonly ReconciliationStatePublisher _reconcilePublisher = new();

    private readonly ConcurrentDictionary<(long Eq, long Pos), PositionContext> _contexts = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), (long PlcId, string RegAddr)> _testStartPoints = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), (long PlcId, string RegAddr, int OnValue, int OffValue)> _hasMatPoints = new();
    // bug#7：每加工位一把信号量，串行化主循环驱动与派工回填对同一 ctx 的读写（不同加工位仍并行）。
    private readonly ConcurrentDictionary<(long Eq, long Pos), SemaphoreSlim> _posGates = new();
    // bug#6：机台→线体反查缓存（提示用，非活动权威；命中仍须重读配置）。
    private readonly ConcurrentDictionary<long, WorkLineRef> _lineCache = new();
    // 机台→料架绑定 ID 缓存（上/下料架，避免每 tick 查库）。
    private readonly ConcurrentDictionary<long, EquipmentFrameBindingIds> _bindingCache = new();
    // 工序间直接交接的"待入库"登记：目标(机台,工位) → 交接信息（源工位空闲时置位，目标工位见料即接）。
    private readonly ConcurrentDictionary<(long Eq, long Pos), InboundHandoff> _expectedInbound = new();
    /// <summary>路由拒发 Warning 限频（按机台）。</summary>
    private readonly ConcurrentDictionary<long, DateTime> _routeWarnStamp = new();
    private static readonly TimeSpan RouteWarnThrottle = TimeSpan.FromSeconds(30);
    private List<(long Eq, long Pos, long PlcId)> _positions = new();

    /// <summary>非 COMPLETED 交接登记最长等待；超时且目标无料则清除（源 hung / 缺失）。</summary>
    private static readonly TimeSpan InboundHandoffTtl = TimeSpan.FromMinutes(10);
    /// <summary>源任务已 COMPLETED 后继续等 PLC 见料的宽限；超时只告警不清登记（防误退回中转）。</summary>
    private static readonly TimeSpan InboundHandoffCompletedGrace = TimeSpan.FromMinutes(30);
    /// <summary>运行期陈旧预记回滚间隔。</summary>
    private static readonly TimeSpan StaleReservationSweepInterval = TimeSpan.FromSeconds(60);
    private DateTime _lastStaleReservationSweep = DateTime.MinValue;
    /// <summary>COMPLETED 交接超宽限已告警的工位（防刷屏）。</summary>
    private readonly ConcurrentDictionary<(long Eq, long Pos), byte> _inboundCompletedGraceWarned = new();

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
        _logger = logger;
        _workRecords = workRecords;
        _equipment = equipment;
        _slots = slots;
        _routingValidator = routingValidator ?? throw new ArgumentNullException(nameof(routingValidator));
        _writeHook = writeHook;
        _notifier = notifier;
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

    /// <summary>
    /// 反查机台所属线体。_lineCache 仅提示/快照，命中后仍须读权威配置；
    /// 不可用则淘汰缓存并返回 null；禁止默认 LINE / 任意首条线体。
    /// </summary>
    private async Task<WorkLineRef?> ResolveLineAsync(long equipmentId, CancellationToken ct)
    {
        try
        {
            var line = await _equipment.GetWorkLineByEquipmentAsync(equipmentId, ct);
            if (line is null)
            {
                _lineCache.TryRemove(equipmentId, out _);
                LogRouteUnavailableThrottled(equipmentId,
                    $"机台 {equipmentId} 线体路由不可用（缺失或已禁用），拒绝派工");
                return null;
            }
            _lineCache[equipmentId] = line;
            return line;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _lineCache.TryRemove(equipmentId, out _);
            _logger.LogWarning(ex, "机台 {Eq} 线体路由权威查询异常，fail-closed", equipmentId);
            return null;
        }
    }

    private void InvalidateLineCache(long equipmentId) => _lineCache.TryRemove(equipmentId, out _);

    private void LogRouteUnavailableThrottled(long equipmentId, string message)
    {
        var now = DateTime.UtcNow;
        if (_routeWarnStamp.TryGetValue(equipmentId, out var last)
            && now - last < RouteWarnThrottle)
            return;
        _routeWarnStamp[equipmentId] = now;
        _logger.LogWarning("{Msg}", message);
    }

    /// <summary>测试接缝：暴露真实 <see cref="ResolveLineAsync"/>（无 LINE 回退；仅成功结果入缓存）。</summary>
    internal Task<WorkLineRef?> ProbeResolveLineAsync(long equipmentId, CancellationToken ct = default)
        => ResolveLineAsync(equipmentId, ct);

    /// <summary>测试接缝：线体缓存是否含指定机台。</summary>
    internal bool ProbeHasLineCache(long equipmentId) => _lineCache.ContainsKey(equipmentId);

    /// <summary>测试接缝：标记已对账，以便 ProbeDispatchOnce 进入上料分配。</summary>
    internal void ProbeMarkReconciled() => _isReconciled = true;

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

    /// <summary>测试接缝：播种加工位清单与 WaitLoad 上下文（供直接交接选空闲工位；不改行为）。</summary>
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
        => _expectedInbound.ContainsKey((equipmentId, positionId));

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

    /// <summary>取机台上/下料架绑定 ID（带缓存）。</summary>
    private async Task<EquipmentFrameBindingIds> ResolveBindingsAsync(long equipmentId, CancellationToken ct)
    {
        if (_bindingCache.TryGetValue(equipmentId, out var cached)) return cached;
        var ids = await _equipment.GetFrameBindingIdsAsync(equipmentId, ct);
        _bindingCache[equipmentId] = ids;
        return ids;
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
            _logger.LogInformation("加工位状态机调度器未启用（SchedulerEnabled=false）。");
            await SeedDashboardPositionsAsync(cancellationToken);
            _isReconciled = true;
            _reconciliationState = (int)ReconciliationState.Succeeded;
            _reconciliationFailureReason = null;
            PublishReconcileState();
            return;
        }

        // 1. 装载加工位与 POS_TEST_START 点位缓存（仅受启动 token 控制）
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
                    _testStartPoints[key] = (p.PlcId, p.RegisterAddress);
                if (!p.IsWrite && p.Signal == SignalKey.PosHasMat)
                    _hasMatPoints[key] = (p.PlcId, p.RegisterAddress, p.OnValue, p.OffValue);
            }
            _positions = posKeys.Select(k => (k.Item1, k.Item2, _testStartPoints.TryGetValue(k, out var tp) ? tp.PlcId : 0L)).ToList();
            _logger.LogInformation("位置调度器装载 {N} 个加工位", _positions.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "装载加工位点位失败，调度器将以空集启动"); }

        cancellationToken.ThrowIfCancellationRequested();

        // 2. §6.3 对账：原子启动单一后台 workflow 后立即返回，不阻塞后续 HostedService（P1-1 / D1）。
        // 后台生命周期使用调度器自有 CTS；启动 token 不链接进运行期（D5）。
        lock (_lifecycleLock)
        {
            if (_stopping) return;
            if (_reconcileWorkflowTask is not null) return;

            _cts = new CancellationTokenSource();
            if (_notifier is not null)
                _notifier.TaskStatusReceived += OnRcsTaskStatusForInbound;

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

    /// <summary>
    /// 单一启动对账工作流：attempt → 成功开闸结束，或失败 → interval → 下一 attempt。
    /// 同一时刻至多一个 workflow / 一个 attempt（D3/D7）。
    /// </summary>
    private async Task ReconcileWorkflowAsync(CancellationToken lifecycleToken)
    {
        while (!lifecycleToken.IsCancellationRequested)
        {
            if (_isReconciled || Volatile.Read(ref _gateOpened) != 0)
                return;

            ReconcileAttemptOutcome outcome;
            try
            {
                outcome = await TryReconcileOnceAsync(lifecycleToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifecycleToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 协调器已吞阶段异常；此处兜底防止 workflow 静默死亡，按失败重试（不开闸）。
                _logger.LogWarning(ex, "启动对账工作流未预期异常，将按失败间隔重试");
                _isReconciled = false;
                _reconciliationFailureReason = FormatFailureReason(
                    ReconcileRoundResult.Fail(ReconcilePhase.One, ex.Message ?? ex.GetType().Name, ex));
                _reconciliationState = (int)ReconciliationState.WaitingForRetry;
                PublishReconcileState();
                outcome = ReconcileAttemptOutcome.Failed;
            }

            if (outcome == ReconcileAttemptOutcome.Succeeded)
            {
                TryOpenGateAfterSuccess();
                return;
            }
            if (outcome == ReconcileAttemptOutcome.Cancelled)
                return;

            try
            {
                await DelayMsAsync(_options.ReconcileRetryIntervalMs, lifecycleToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifecycleToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<ReconcileAttemptOutcome> TryReconcileOnceAsync(CancellationToken ct)
    {
        // 进入每一轮 Reconciling：清空旧失败原因，属性与发布快照同一转换。
        _reconciliationState = (int)ReconciliationState.Reconciling;
        _reconciliationFailureReason = null;
        PublishReconcileState();
        Interlocked.Increment(ref _reconcileAttemptCount);

        ReconcileRoundResult round;
        try
        {
            round = await ReconcileAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消 ≠ 业务失败：不发布 WaitingForRetry / 不改写 FailureReason
            return ReconcileAttemptOutcome.Cancelled;
        }

        if (StartupReconcileCoordinator.DecideIsReconciled(round))
            return ReconcileAttemptOutcome.Succeeded;

        var reason = FormatFailureReason(round);
        _isReconciled = false;
        _reconciliationFailureReason = reason;
        _reconciliationState = (int)ReconciliationState.WaitingForRetry;
        PublishReconcileState();
        _logger.LogWarning(
            "启动对账失败，自动派工已锁定：阶段 {Phase}，原因 {Reason}；将在 {IntervalMs}ms 后自动重试（第 {Attempt} 次已失败）",
            round.FailedPhase, reason, _options.ReconcileRetryIntervalMs, ReconcileAttemptCount);
        return ReconcileAttemptOutcome.Failed;
    }

    private static string FormatFailureReason(ReconcileRoundResult round)
    {
        var phase = round.FailedPhase?.ToString() ?? "?";
        var detail = string.IsNullOrWhiteSpace(round.FailureReason)
            ? "对账失败"
            : round.FailureReason!;
        // FailureReason 已经过协调器 Sanitize；再包一层阶段前缀供看板/日志
        if (detail.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Pwd=", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Connection String", StringComparison.OrdinalIgnoreCase))
        {
            detail = "对账阶段执行异常（已隐藏敏感连接信息）";
        }
        return $"阶段 {phase}：{detail}";
    }

    private Task DelayMsAsync(int milliseconds, CancellationToken ct)
        => DelayOverride is not null
            ? DelayOverride(milliseconds, ct)
            : Task.Delay(milliseconds, ct);

    private enum ReconcileAttemptOutcome
    {
        Succeeded,
        Failed,
        Cancelled
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 先取得 stopping 生命周期控制权，再取消自有 CTS（禁止随后开闸；D6）。
        EnterStopping();
        if (_notifier is not null)
            _notifier.TaskStatusReceived -= OnRcsTaskStatusForInbound;
        _cts?.Cancel();
        var pending = Task.WhenAll(
            ObserveAsync(_loopTask),
            ObserveAsync(_dispatchTask),
            ObserveAsync(_reconcileWorkflowTask));
        // 下游忽略 CT 时 workflow 可能继续挂起：有界等待，不硬杀、不并发替代 attempt（D4）。
        await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
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
            // 先按方向回滚本工位绑定任务的预记，再清交接；避免恢复后假占用等 60s 扫。
            var taskId = ctx.CurrentTaskId;
            var phase = ctx.Phase;
            if (!string.IsNullOrEmpty(taskId))
            {
                if (phase == PositionPhase.Upload)
                    await _slots.RollbackTakeAsync(taskId, ct);
                else if (phase == PositionPhase.Unload)
                    await _slots.RollbackAsync(taskId, ct);
                await ClearInboundsBySourceTaskAsync(taskId, "RESET_ALARM", ct);
            }

            ctx.CurrentTaskId = null;
            ctx.Phase = null;
            ctx.MaterialId = null;
            ctx.AlarmRaised = false;
            ctx.UploadRequested = false;
            ctx.HasMatRecheck.Reset();
            ctx.StatusDetail = null;
            ctx.AlarmReason = null;
            await ClearInboundAtAsync((equipmentId, positionId), "RESET_ALARM", ct);
            SetState(ctx, PositionState.WaitLoad); // 同步刷看板
            _logger.LogInformation("人工恢复 EQ{Eq} POS{Pos} → WAIT_LOAD（已回滚预记 {Task}）",
                equipmentId, positionId, taskId ?? "—");
        }
        finally { gate.Release(); }
    }

    public void InvalidateFrameBindingCache(long? equipmentId = null)
    {
        if (equipmentId is long eq)
        {
            _bindingCache.TryRemove(eq, out _);
            _lineCache.TryRemove(eq, out _);
            _logger.LogInformation("已失效机台 {Eq} 的料架/线体缓存", eq);
        }
        else
        {
            _bindingCache.Clear();
            _lineCache.Clear();
            _logger.LogInformation("已清空全部料架/线体缓存");
        }
    }

    public async Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;

        // 清掉以该任务为源的工序间交接登记（物料回中转或告警，下游可恢复上料）
        await ClearInboundsBySourceTaskAsync(taskId, reason, ct);

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
            SetState(ctx, PositionState.Alarm);
            _logger.LogWarning("EQ{Eq} POS{Pos} 任务 {Task} 已放弃（{Reason}）→ ALARM，可点恢复", eq, pos, taskId, reason);
        }
        finally { gate.Release(); }
    }

    private void OnRcsTaskStatusForInbound(object? sender, RcsTaskStatusEvent e)
    {
        if (e.TaskState is RcsTaskState.Failed or RcsTaskState.Canceled)
            _ = ClearInboundsBySourceTaskAsync(e.TaskId, e.TaskState, CancellationToken.None);
    }

    /// <summary>按源任务清除全部交接登记，物料退回目标机中转架或告警（禁止静默丢件）。</summary>
    private async Task ClearInboundsBySourceTaskAsync(string taskId, string reason, CancellationToken ct)
    {
        var keys = _expectedInbound
            .Where(kv => string.Equals(kv.Value.SourceTaskId, taskId, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in keys)
            await ClearInboundAtAsync(key, reason, ct);
    }

    /// <summary>清除指定工位的交接登记；有物料码则入库目标机中转架，满架/无中转则告警。</summary>
    private async Task ClearInboundAtAsync((long Eq, long Pos) key, string reason, CancellationToken ct)
    {
        if (!_expectedInbound.TryRemove(key, out var handoff)) return;
        _inboundCompletedGraceWarned.TryRemove(key, out _);

        _logger.LogWarning("清除工序间交接登记 EQ{Eq} POS{Pos}（源 {Src} 物料 {El}，原因 {Reason}）",
            key.Eq, key.Pos, handoff.SourceTaskId ?? "—", handoff.MaterialId ?? "—", reason);

        if (string.IsNullOrWhiteSpace(handoff.MaterialId)) return;

        try
        {
            var transitFrameId = await _equipment.GetFrameBindingByRoleAsync(key.Eq, FrameRole.Transit, ct);
            if (transitFrameId is long tf)
            {
                var src = handoff.SourceTaskId ?? "NA";
                var returnTaskId = $"HANDOFF-RETURN-{src}";
                if (returnTaskId.Length > 50) returnTaskId = returnTaskId[..50];
                var put = await _slots.ReserveAsync(tf, returnTaskId, handoff.MaterialId, ct);
                if (put is not null)
                {
                    await _slots.ConfirmAsync(returnTaskId, ct);
                    _logger.LogInformation("交接物料 {El} 已退回 EQ{Eq} 中转架 {Frame}（原因 {Reason}）",
                        handoff.MaterialId, key.Eq, tf, reason);
                    return;
                }
                await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    $"交接废弃：物料 {handoff.MaterialId} 无法退回中转架 {tf}（满架），源 {handoff.SourceTaskId ?? "—"}，原因 {reason}",
                    handoff.SourceTaskId, ct);
            }
            else
            {
                await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    $"交接废弃：物料 {handoff.MaterialId} 目标 EQ{key.Eq} 无中转架，源 {handoff.SourceTaskId ?? "—"}，原因 {reason}",
                    handoff.SourceTaskId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "交接物料 {El} 回收失败 EQ{Eq} POS{Pos}", handoff.MaterialId, key.Eq, key.Pos);
            try
            {
                await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    $"交接废弃回收异常：物料 {handoff.MaterialId} EQ{key.Eq} POS{key.Pos}，原因 {reason}",
                    handoff.SourceTaskId, ct);
            }
            catch { /* 告警失败不二次抛 */ }
        }
    }

    /// <summary>§6.3 启动三方对账：经 <see cref="StartupReconcileCoordinator"/> fail-closed 编排 ①/①b/②/③。</summary>
    private Task<ReconcileRoundResult> ReconcileAsync(CancellationToken ct)
    {
        var unfinished = new List<string>();
        return _reconcileCoordinator.RunAsync(
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
        return ReconcilePhaseResult.Ok(ReconcilePhase.One);
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
        var n = await _slots.RollbackStaleReservationsAsync(unfinished, ct);
        if (n > 0) _logger.LogInformation("对账②：回滚陈旧槽位预记 {N} 个", n);
        var c = await SettleCompletedPendingWithPlcAsync(unfinished, ct);
        if (c > 0) _logger.LogInformation("对账②：PLC 门补落账 COMPLETED 预记 {N} 个", c);
        return ReconcilePhaseResult.Ok(ReconcilePhase.Two);
    }

    /// <summary>③ PLC 账实核对：有料无任务 → 工位 Alarm（不等于全局失败）。过程异常 → 全局失败。</summary>
    private async Task<ReconcilePhaseResult> ReconcilePhaseThreeAsync(CancellationToken ct)
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
        var queryPhase = StartupReconcileCoordinator.MapQueryResult(
            result.Success, result.Message ?? result.Error);
        if (!queryPhase.Succeeded)
        {
            _logger.LogWarning("对账①b queryTask 失败：{Msg}", result.Message ?? result.Error);
            return (queryPhase, settled);
        }

        foreach (var (taskId, rcsStatus) in ParseQueryItems(result.RawResponse))
        {
            var state = RcsStatusMapper.ToTaskState(rcsStatus);
            if (state is null || !RcsStatusMapper.IsTerminal(state)) continue;

            var row = await _taskStore.GetByTaskIdAsync(taskId, ct);
            if (row is null) continue;
            if (row.TaskState != state)
            {
                if (!await _taskStore.UpdateStateAsync(taskId, state, rcsStatus, row.ErrorMsg, ct))
                    _logger.LogWarning("对账①b：更新任务态未生效（任务不存在）{TaskId} → {State}", taskId, state);
            }

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
                ctx.MaterialId = null;
                SetState(ctx, PositionState.WaitLoad);
            }
            else if (state == RcsTaskState.Completed && hasMat is null)
            {
                // PLC 尚未可读（启动瞬间常见）：预记已保守回滚，工位回 WaitLoad，不 latch Alarm（避免误粘滞挡后续派工）
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.MaterialId = null;
                SetState(ctx, PositionState.WaitLoad);
                _logger.LogWarning("对账①b：{TaskId} COMPLETED 但 PLC HasMat 未读到（phase={Phase}），预记已回滚，工位回 WaitLoad", taskId, phase);
            }
            else if (state == RcsTaskState.Completed)
            {
                // COMPLETED 且 PLC 明确不符（上料 hasMat=false / 下料 hasMat=true）→ Alarm，预记已回滚
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.MaterialId = null;
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                    $"启动对账：RCS 已 COMPLETED 但 PLC 不符（阶段 {phase}，HasMat={hasMat}），预记已回滚，请核对后点恢复", ct);
                _logger.LogWarning("对账①b：{TaskId} COMPLETED 但 PLC 不符（phase={Phase} hasMat={Has}）→ ALARM", taskId, phase, hasMat);
            }
            else
            {
                // CANCELED / FAILED：预记已回滚，工位回 WaitLoad（FAILED 的自动 redo 由 tracker 另途处理）
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.MaterialId = null;
                SetState(ctx, PositionState.WaitLoad);
                if (state == RcsTaskState.Canceled)
                    await _alarms.RaiseRcsTaskCanceledAsync(taskId, "启动对账发现任务已取消，预记已回滚", ct);
            }

            settled.Add(taskId);
            _logger.LogInformation("对账①b：任务 {TaskId} 终态 {State} 已收口 EQ{Eq} POS{Pos}", taskId, state, ctx.EquipmentId, ctx.PositionId);
        }

        return (ReconcilePhaseResult.Ok(ReconcilePhase.OneB), settled);
    }

    /// <summary>终态槽位收口：COMPLETED 且 PLC 符合阶段预期才 Confirm，否则 Rollback（避免误清空/误入库）。</summary>
    private async Task SettleSlotForTerminalAsync(string taskId, PositionPhase phase, string state, bool? hasMat, CancellationToken ct)
    {
        if (state == RcsTaskState.Completed)
        {
            if (phase == PositionPhase.Upload)
            {
                if (hasMat == true) await _slots.ConfirmTakeAsync(taskId, ct);
                else await _slots.RollbackTakeAsync(taskId, ct); // 无料或未知：物料应仍在源架
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
            try
            {
                if (!IsReconciled)
                {
                    await Task.Delay(_options.SchedulerIntervalMs, ct);
                    continue;
                }
                await DriveAllAsync(ct);
                await SweepStaleReservationsIfDueAsync(ct);
            }
            catch (OperationCanceledException) { break; }
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

    /// <summary>运行期定期：回滚非 COMPLETED 陈旧预记；对 COMPLETED 预记按 PLC 门补 Confirm。</summary>
    private async Task SweepStaleReservationsIfDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastStaleReservationSweep < StaleReservationSweepInterval) return;
        _lastStaleReservationSweep = DateTime.UtcNow;
        try
        {
            var unfinished = await _taskStore.GetUnfinishedTaskIdsAsync(ct);
            var n = await _slots.RollbackStaleReservationsAsync(unfinished, ct);
            if (n > 0) _logger.LogInformation("运行期回滚陈旧槽位预记 {N} 个", n);
            var c = await SettleCompletedPendingWithPlcAsync(unfinished, ct);
            if (c > 0) _logger.LogInformation("运行期 PLC 门补落账 COMPLETED 预记 {N} 个", c);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "运行期陈旧预记回滚失败"); }
    }

    /// <summary>
    /// COMPLETED 但仍为预记的槽位：读源工位 PLC HasMat，符合阶段预期才 Confirm；
    /// 不符则 Rollback；HasMat 未知则跳过等下轮（避免假完成误落账）。
    /// </summary>
    private async Task<int> SettleCompletedPendingWithPlcAsync(IReadOnlyCollection<string> unfinished, CancellationToken ct)
    {
        var pending = await _slots.ListCompletedPendingConfirmAsync(unfinished, ct);
        if (pending.Count == 0) return 0;

        var settled = 0;
        foreach (var item in pending)
        {
            var row = await _taskStore.GetByTaskIdAsync(item.TaskId, ct);
            if (row is null) continue;

            // 无工位绑定（换架等）：无 PLC 可核，按方向直接补落账
            if (row.EquipmentId is not long eq || row.PositionId is not long pos)
            {
                var ok = item.IsTake
                    ? await _slots.ConfirmTakeAsync(item.TaskId, ct)
                    : await _slots.ConfirmAsync(item.TaskId, ct);
                if (ok) settled++;
                continue;
            }

            var tmp = new PositionContext { EquipmentId = eq, PositionId = pos };
            var hasMat = await ReadHasMatFreshAsync(tmp, ct);

            if (item.IsTake)
            {
                // 上料 TAKE：目标工位有料才 ConfirmTake；明确无料 → Rollback；未知跳过
                if (hasMat == true)
                {
                    if (await _slots.ConfirmTakeAsync(item.TaskId, ct)) settled++;
                }
                else if (hasMat == false)
                {
                    if (await _slots.RollbackTakeAsync(item.TaskId, ct))
                    {
                        settled++;
                        _logger.LogWarning("COMPLETED 取料预记 {Task} PLC 无料 → 回滚（假完成/未到位）", item.TaskId);
                        await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            $"任务 {item.TaskId} COMPLETED 但工位无料，取料预记已回滚，请核对", item.TaskId, ct);
                    }
                }
                else
                    _logger.LogDebug("COMPLETED 取料预记 {Task} HasMat 未知，跳过本轮", item.TaskId);
            }
            else
            {
                // 下料 PUT：源工位无料才 Confirm；明确仍有料 → Rollback；未知跳过
                if (hasMat == false)
                {
                    if (await _slots.ConfirmAsync(item.TaskId, ct)) settled++;
                }
                else if (hasMat == true)
                {
                    if (await _slots.RollbackAsync(item.TaskId, ct))
                    {
                        settled++;
                        _logger.LogWarning("COMPLETED 入库预记 {Task} PLC 仍有料 → 回滚（假完成/未取走）", item.TaskId);
                        await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                            $"任务 {item.TaskId} COMPLETED 但工位仍有料，入库预记已回滚，请核对", item.TaskId, ct);
                    }
                }
                else
                    _logger.LogDebug("COMPLETED 入库预记 {Task} HasMat 未知，跳过本轮", item.TaskId);
            }
        }
        return settled;
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
                if (hasMat == true
                    && _expectedInbound.TryGetValue((ctx.EquipmentId, ctx.PositionId), out var inbound)
                    && inbound.IsDispatched)
                {
                    _expectedInbound.TryRemove((ctx.EquipmentId, ctx.PositionId), out _);
                    _inboundCompletedGraceWarned.TryRemove((ctx.EquipmentId, ctx.PositionId), out _);
                    ctx.Phase = PositionPhase.Upload;
                    ctx.CurrentTaskId = inbound.SourceTaskId; // 溯源上游下料任务
                    ctx.MaterialId = inbound.MaterialId;    // 物料码随交接件传入
                    _logger.LogInformation("EQ{Eq} POS{Pos} 收到工序间交接件（源 {Src} 物料 {El}），转 Loaded", ctx.EquipmentId, ctx.PositionId, inbound.SourceTaskId, inbound.MaterialId ?? "—");
                    next = PositionState.Loaded;
                    break;
                }
                // 陈旧交接：FAILED/CANCELED/缺失 → 清并回收；非 COMPLETED 超 TTL → 清；
                // COMPLETED 只等 PLC，超宽限仅告警不清（防误退回中转）。
                if (hasMat != true && _expectedInbound.TryGetValue((ctx.EquipmentId, ctx.PositionId), out var pendingInbound))
                {
                    var srcTaskId = pendingInbound.SourceTaskId;
                    RcsTaskRow? srcRow = null;
                    if (!string.IsNullOrEmpty(srcTaskId))
                        srcRow = await _taskStore.GetByTaskIdAsync(srcTaskId, ct);
                    var srcState = srcRow?.TaskState;
                    var age = DateTime.UtcNow - pendingInbound.CreatedUtc;
                    var abandoned = srcState is RcsTaskState.Failed or RcsTaskState.Canceled;
                    var missing = string.IsNullOrEmpty(srcTaskId) || srcRow is null;
                    var key = (ctx.EquipmentId, ctx.PositionId);

                    if (missing || abandoned)
                    {
                        await ClearInboundAtAsync(key, $"STALE:{srcState ?? "missing"}", ct);
                    }
                    else if (srcState == RcsTaskState.Completed)
                    {
                        if (age > InboundHandoffCompletedGrace
                            && _inboundCompletedGraceWarned.TryAdd(key, 0))
                        {
                            _logger.LogWarning("EQ{Eq} POS{Pos} 直送源 {Src} 已 COMPLETED 超 {Min}min 仍无料，保留登记等人工/见料（不清账）",
                                ctx.EquipmentId, ctx.PositionId, srcTaskId, (int)InboundHandoffCompletedGrace.TotalMinutes);
                            await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                                $"工序间直送超时：EQ{ctx.EquipmentId} POS{ctx.PositionId} 源任务 {srcTaskId} 已完成但工位无料，物料 {pendingInbound.MaterialId ?? "—"}，请核对 AGV/PLC",
                                srcTaskId, ct);
                        }
                    }
                    else if (age > InboundHandoffTtl)
                    {
                        await ClearInboundAtAsync(key, "TTL", ct);
                    }
                }
                // Layer 1：工位不自己查料/选槽，只标记"请求上料"，由单一调度消费者统一决策
                // （查料→选槽→原子预记→下发都在单消费者里串行，结构上杜绝两位同时看到同一件料）。
                // 有在途直送登记时不请求自取（与中转回流互斥）。
                // 暂停自动派工时不置请求，恢复后下一 tick 再评估。
                if (!IsAutoDispatchPaused
                    && allowLoad == true && hasMat == false && string.IsNullOrEmpty(ctx.CurrentTaskId)
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
                    if (ctx.Phase is not PositionPhase phase)
                    {
                        ctx.AlarmReason = "RCS 报完成但任务阶段未知";
                        next = PositionState.Alarm;
                        break;
                    }

                    var recheck = ctx.HasMatRecheck.Evaluate(ctx.CurrentTaskId, phase, fresh,
                        _options.HasMatRecheckFailThreshold);
                    ctx.StatusDetail = recheck.StatusDetail;
                    next = recheck.NextState;

                    if (recheck.Decision == HasMatRecheckDecision.Hold)
                    {
                        _logger.LogWarning(
                            "EQ{Eq} POS{Pos} 任务 {Task} HasMat fresh 读取未知，复核中（{Count}/{Threshold}），保持 TRANSPORTING",
                            ctx.EquipmentId, ctx.PositionId, ctx.CurrentTaskId ?? "—",
                            recheck.FailureCount, _options.HasMatRecheckFailThreshold);
                    }
                    else if (recheck.Decision == HasMatRecheckDecision.Alarm)
                    {
                        ctx.AlarmReason = fresh is null
                            ? $"HasMat 连续复核未知达到阈值 {_options.HasMatRecheckFailThreshold}"
                            : phase == PositionPhase.Upload
                                ? "RCS 报完成但 PLC 明确无料（复核不过）"
                                : "RCS 报完成但 PLC 明确仍有料（复核不过）";
                    }
                }
                break;
            case PositionState.Loaded:
                if (await WriteTestStartAsync(ctx, 1, ct))
                {
                    // 上料到位落账：源料架取料落账（物料已被取走）。工序间交接件无取料预记，此调用幂等无副作用。
                    if (!string.IsNullOrEmpty(ctx.CurrentTaskId))
                        await _slots.ConfirmTakeAsync(ctx.CurrentTaskId!, ct);
                    // 加工开始：写 WORK_RECORD（关联当前上料 taskId）
                    ctx.WorkRecordId = await _workRecords.RecordStartAsync(new WorkRecordStartArgs
                    {
                        EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId,
                        PositionCode = $"POS-{ctx.PositionId}", RcsTaskId = ctx.CurrentTaskId,
                        MaterialId = ctx.MaterialId, Author = "scheduler"
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
                ctx.MaterialId = null; // 件已离开本工位，清物料码
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
                        await ClearInboundsBySourceTaskAsync(ctx.CurrentTaskId!, "ALARM", ct);
                    }
                    await ClearInboundAtAsync((ctx.EquipmentId, ctx.PositionId), "ALARM", ct);
                    var reason = ReasonForAlarm(ctx, hasMat, rcsState);
                    var alarmKey = ctx.CurrentTaskId ?? $"EQ{ctx.EquipmentId}-POS{ctx.PositionId}";
                    await _alarms.RaiseRcsTaskCanceledAsync(alarmKey,
                        $"EQ{ctx.EquipmentId} POS{ctx.PositionId}：{reason}", ct);
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
                    {
                        await _workRecords.RecordResultAsync(ctx.WorkRecordId, isOk ? "0" : "1", null, ct);
                        // 暂停自动派工时本 tick 不下料入队，清零避免每 tick 重复写结果日志。
                        if (IsAutoDispatchPaused) ctx.WorkRecordId = 0;
                    }
                    if (IsAutoDispatchPaused)
                        break; // 保持 Done*，恢复自动派工后再入下料队
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
                await _alarms.RaiseRcsTaskNotFoundAsync($"UPLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}",
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料路由未配置（LOCATION_MAP 缺 LOAD_AREA 或加工位 cell）", ct);
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
            await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}",
                $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 下料源 cell 未配置（LOCATION_MAP 缺加工位 cell）", ct);
            _logger.LogWarning("EQ{Eq} POS{Pos} 下料源 cell 未配置（LOCATION_MAP 缺加工位 cell）", ctx.EquipmentId, ctx.PositionId);
            return false;
        }
        var line = await ResolveLineAsync(ctx.EquipmentId, ct);
        if (line is null)
        {
            _logger.LogWarning("EQ{Eq} POS{Pos} 下料入队失败：线体路由不可用，不入队",
                ctx.EquipmentId, ctx.PositionId);
            return false;
        }
        _queue.Enqueue(new DispatchItem
        {
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId, Phase = PositionPhase.Unload,
            Priority = 8, FromCode = fromCell, ToCode = "", IsOk = isOk,
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, Author = "scheduler",
            MaterialId = ctx.MaterialId
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
        {
            // 配置了后续工序但全部不可用 → 拒发，禁止回退命名区伪造成功下发
            if (await _equipment.HasSubsequentProcessAsync(ctx.EquipmentId, ct))
            {
                LogRouteUnavailableThrottled(ctx.EquipmentId,
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 下一工序已配置但无活动可用机台，拒绝下料（不回退命名区）");
                return null;
            }
            return await ResolveDownloadFrameFallbackAsync(ctx, ct); // 真末道 → 下料架
        }

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
                if (cell is not null) return new UnloadDecision(cell, UnloadTarget.TransitFrame, tf, nextEq, null);
            }
        }

        // 有活动下一工序但既无空闲工位又无中转架 → 回退下料架（避免件卡在机台）
        _logger.LogWarning("EQ{Eq} POS{Pos} OK 但下一工序无空闲工位且无中转架，回退下料架", ctx.EquipmentId, ctx.PositionId);
        return await ResolveDownloadFrameFallbackAsync(ctx, ct);
    }

    /// <summary>真末道/繁忙回退：下料架(role1) cell；无绑定回退 UNLOAD_AREA。
    /// 注意：有后续工序但全部不可用时不得调用本方法（禁止命名区伪造成功）。</summary>
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

    /// <summary>在候选机台的加工位中挑一个空闲工位（在线/安全/无料/无任务/无待交接/未请求自取）。
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
                // 该工位调度上下文空闲：无当前任务、未请求自取、且处于 WaitLoad/Offline
                if (_contexts.TryGetValue((eq, pPos), out var pctx))
                {
                    if (!string.IsNullOrEmpty(pctx.CurrentTaskId)) continue;
                    if (pctx.UploadRequested) continue; // 已向消费者要料，不宜再登记直送
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
            if (r.Error is not null)
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 复核读 HasMat 失败：{Error}",
                    ctx.EquipmentId, ctx.PositionId, r.Error);
            }
            // Error 存在时 RawValue 不可信；无错误时 OnValue=有料，其余明确值按无料处理。
            return HasMatReading.From(r.RawValue, hp.OnValue, r.Error);
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
        if (state != PositionState.Transporting)
        {
            ctx.HasMatRecheck.Reset();
            ctx.StatusDetail = null;
        }
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
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId, State = state,
            MaterialId = ctx.MaterialId, StatusDetail = ctx.StatusDetail
        });
    }

    private static string ReasonForAlarm(PositionContext ctx, bool? hasMat, string? rcsState)
    {
        if (!string.IsNullOrWhiteSpace(ctx.AlarmReason)) return ctx.AlarmReason;
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
                // 对账未完成或暂停：不 dequeue / 不上料分配，避免积压请求被发出。
                if (!IsReconciled || IsAutoDispatchPaused)
                {
                    await Task.Delay(200, ct);
                    continue;
                }

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

    /// <summary>测试接缝：执行一轮派工循环体（含 IsReconciled 防御门禁）。</summary>
    internal Task ProbeDispatchOnceAsync(CancellationToken ct = default)
        => RunDispatchOnceAsync(ct);

    private async Task RunDispatchOnceAsync(CancellationToken ct)
    {
        if (!IsReconciled || IsAutoDispatchPaused) return;
        var item = _queue.Dequeue();
        if (item is not null)
        {
            await DispatchOneAsync(item, ct);
            return;
        }
        await AllocateUploadsAsync(ct);
    }

    /// <summary>Layer 1 上料分配：收集"请求上料"的工位，按"空闲最久 → 工位编号升序"确定性排序，逐个尝试下发。
    /// 串行处理保证前一位取料预记落地后，后一位再查料——料不足时后位自然看到无料而继续等待（不告警、不重试风暴）。
    /// 返回本轮是否有成功下发（用于控制空转 delay）。</summary>
    private async Task<bool> AllocateUploadsAsync(CancellationToken ct)
    {
        // 有在途直送登记的工位禁止自取（与中转回流/上料架互斥）
        var candidates = _contexts.Values
            .Where(c => c.UploadRequested && c.State == PositionState.WaitLoad
                        && string.IsNullOrEmpty(c.CurrentTaskId)
                        && !_expectedInbound.ContainsKey((c.EquipmentId, c.PositionId)))
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
    /// 返回 Queued（已下发）/ WaitMaterial（无料或有在途直送，保持等待）/ Failed（路由缺失或下发失败，已置 Alarm）。</summary>
    private async Task<UploadDecision> TryDispatchUploadAsync(PositionContext ctx, CancellationToken ct)
    {
        if (!IsReconciled || IsAutoDispatchPaused) return UploadDecision.WaitMaterial;

        // 下发前再断言：直送在途则禁止自取，勿清交接登记
        if (_expectedInbound.ContainsKey((ctx.EquipmentId, ctx.PositionId)))
            return UploadDecision.WaitMaterial;

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

        // 第一道门禁：预记前权威校验（不以 _lineCache / 仅 LineCode 为活动权威）
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
            InvalidateLineCache(ctx.EquipmentId);
            LogRouteUnavailableThrottled(ctx.EquipmentId,
                $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料预记前路由不可用：{pre.SafeMessage}");
            return UploadDecision.WaitMaterial;
        }
        var line = pre.SourceWorkLine;
        _lineCache[ctx.EquipmentId] = line;

        var taskId = RcsTaskId.Next(line.LineCode, RcsTaskKind.Transit);
        ReservedSlot? reservedTake = null;
        RcsResult result;
        if (plan.SourceFrameId is long sourceFrameId)
        {
            var prepared = await _reservationFirstDispatcher.ExecuteAsync(
                taskId,
                (id, token) => _slots.ReserveTakeAsync(sourceFrameId, id, token),
                (id, token) => _routingValidator.ValidateAsync(routeCtx, token),
                (id, token) => _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
                {
                    TaskId = id,
                    WorkLineId = line.WorkLineId, LineCode = line.LineCode, TaskType = "0",
                    Priority = 5, FromCode = plan.From!, ToCode = plan.To!,
                    EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId,
                    Kind = RcsTaskKind.Transit, Author = "scheduler"
                }, token),
                (id, token) => _slots.RollbackTakeAsync(id, token),
                ct);

            if (prepared.Status == ReservationFirstDispatchStatus.ReservationFailed)
            {
                if (prepared.Exception is not null)
                {
                    var reserveGate = GateFor((ctx.EquipmentId, ctx.PositionId));
                    await reserveGate.WaitAsync(ct);
                    try
                    {
                        ctx.AlarmRaised = true;
                        SetState(ctx, PositionState.Alarm);
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
                InvalidateLineCache(ctx.EquipmentId);
                LogRouteUnavailableThrottled(ctx.EquipmentId,
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料最终路由门禁失败（非 RCS 失败），已回滚预记：{prepared.RouteResult?.SafeMessage ?? prepared.Exception?.Message ?? "—"}");
                if (!prepared.RollbackSucceeded)
                {
                    var g = GateFor((ctx.EquipmentId, ctx.PositionId));
                    await g.WaitAsync(ct);
                    try
                    {
                        ctx.AlarmRaised = true;
                        SetState(ctx, PositionState.Alarm);
                        await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                            $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 路由拒发后预记回滚失败，需人工核账", ct);
                    }
                    finally { g.Release(); }
                    return UploadDecision.Failed;
                }
                return UploadDecision.WaitMaterial;
            }

            reservedTake = prepared.Reservation;
            result = prepared.Status == ReservationFirstDispatchStatus.Dispatched
                ? prepared.DispatchResult!
                : RcsResult.Fail("", BuildDispatchFailureMessage(prepared.Exception,
                    prepared.DispatchResult, prepared.RollbackSucceeded)) with { TaskId = taskId };
        }
        else
        {
            // 命名区没有槽位账：合成预记占位，仍走最终门禁后再 RCS。
            var prepared = await _reservationFirstDispatcher.ExecuteAsync(
                taskId,
                (_, _) => Task.FromResult<object?>(new object()),
                (_, token) => _routingValidator.ValidateAsync(routeCtx, token),
                (id, token) => _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
                {
                    TaskId = id,
                    WorkLineId = line.WorkLineId, LineCode = line.LineCode, TaskType = "0",
                    Priority = 5, FromCode = plan.From!, ToCode = plan.To!,
                    EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId,
                    Kind = RcsTaskKind.Transit, Author = "scheduler"
                }, token),
                (_, _) => Task.FromResult(true),
                ct);

            if (prepared.Status == ReservationFirstDispatchStatus.RouteUnavailable)
            {
                InvalidateLineCache(ctx.EquipmentId);
                LogRouteUnavailableThrottled(ctx.EquipmentId,
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料（命名区）最终路由门禁失败：{prepared.RouteResult?.SafeMessage ?? "—"}");
                return UploadDecision.WaitMaterial;
            }

            result = prepared.Status == ReservationFirstDispatchStatus.Dispatched
                ? prepared.DispatchResult!
                : RcsResult.Fail("", BuildDispatchFailureMessage(prepared.Exception,
                    prepared.DispatchResult, prepared.RollbackSucceeded)) with { TaskId = taskId };
        }

        var gate = GateFor((ctx.EquipmentId, ctx.PositionId));
        await gate.WaitAsync(ct);
        try
        {
            // 下发窗口内若已登记直送：不绑定自取；收口已发出的 RCS 任务，本工位继续等交接
            if (_expectedInbound.ContainsKey((ctx.EquipmentId, ctx.PositionId)))
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 上料下发窗口内出现直送登记，放弃绑定自取任务 {Task}",
                    ctx.EquipmentId, ctx.PositionId, result.TaskId ?? "—");
                if (!string.IsNullOrEmpty(result.TaskId))
                {
                    await CloseOrphanTaskAsync(result.TaskId, "UPLOAD_SUPERSEDED_BY_HANDOFF", ct);
                    if (plan.SourceFrameId is not null)
                        await _slots.RollbackTakeAsync(result.TaskId, ct);
                }
                return UploadDecision.WaitMaterial;
            }

            if (!result.Success || string.IsNullOrEmpty(result.TaskId))
            {
                // 下发失败 → 粘滞 Alarm，等人工恢复；不自动重发（防重试风暴）
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                var err = result.Error ?? result.Message ?? "未知错误";
                await _alarms.RaiseRcsTaskNotFoundAsync($"UPLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}",
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料 RCS 下发失败：{err}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 上料下发失败：{Err}", ctx.EquipmentId, ctx.PositionId, err);
                return UploadDecision.Failed;
            }

            ctx.CurrentTaskId = result.TaskId;
            ctx.Phase = PositionPhase.Upload;
            ctx.UploadRequested = false;

            // 预记已在下发前完成；这里只把被锁定槽位的物料码绑定到工位上下文。
            if (reservedTake is not null) ctx.MaterialId = reservedTake.MaterialId;
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
        if (!IsReconciled || IsAutoDispatchPaused)
        {
            // 防御：未对账/暂停期间不应出队；若竞态已出队则重新入队，避免丢掉下料请求。
            _queue.Enqueue(item);
            return;
        }

        var ctx = _contexts.GetOrAdd((item.EquipmentId, item.PositionId), k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos });

        // 终点决策（NG架/选下游空工位/中转架/下料架）在消费者内串行完成。
        var decision = await ResolveUnloadTargetAsync(ctx, item.IsOk, ct);
        if (decision is null)
        {
            // 路由/活动配置不可用：D8 Warning、不 Alarm；缺受管终点配置仍 Alarm
            if (item.IsOk && await _equipment.HasSubsequentProcessAsync(item.EquipmentId, ct))
            {
                InvalidateLineCache(item.EquipmentId);
                LogRouteUnavailableThrottled(item.EquipmentId,
                    $"EQ{item.EquipmentId} POS{item.PositionId} 下料路由不可用（后续工序无活动目标），本 tick 跳过");
                return;
            }

            var g0 = GateFor((item.EquipmentId, item.PositionId));
            await g0.WaitAsync(ct);
            try
            {
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{item.EquipmentId}-POS{item.PositionId}",
                    $"EQ{item.EquipmentId} POS{item.PositionId} 下料终点未配置（isOk={item.IsOk}，请录入 NG/中转/下料架绑定或 UNLOAD_AREA）", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 下料终点未配置（isOk={Ok}，请录入 NG/中转/下料架绑定或 UNLOAD_AREA）→ ALARM", item.EquipmentId, item.PositionId, item.IsOk);
            }
            finally { g0.Release(); }
            return;
        }
        var d = decision.Value;

        var routeCtx = BuildUnloadRouteContext(item, d);
        var pre = await _routingValidator.ValidateAsync(routeCtx, ct);
        if (!pre.IsAvailable)
        {
            InvalidateLineCache(item.EquipmentId);
            if (d.DestEquipmentId is long destEq) InvalidateLineCache(destEq);
            LogRouteUnavailableThrottled(item.EquipmentId,
                $"EQ{item.EquipmentId} POS{item.PositionId} 下料预记前路由不可用：{pre.SafeMessage}");
            return;
        }

        var lineCode = pre.SourceWorkLine?.LineCode ?? item.LineCode;
        var workLineId = pre.SourceWorkLine?.WorkLineId ?? item.WorkLineId;
        var taskId = RcsTaskId.Next(lineCode, RcsTaskKind.Transit);
        RcsResult result;
        if (RequiresUnloadReservation(d))
        {
            var prepared = await _reservationFirstDispatcher.ExecuteAsync(
                taskId,
                (id, token) => ReserveUnloadAsync(item, d, id, token),
                (id, token) => _routingValidator.ValidateAsync(routeCtx, token),
                (id, token) => _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
                {
                    TaskId = id,
                    WorkLineId = workLineId, LineCode = lineCode, TaskType = "1",
                    Priority = item.Priority, FromCode = item.FromCode, ToCode = d.ToCell,
                    EquipmentId = item.EquipmentId, PositionId = item.PositionId,
                    Kind = RcsTaskKind.Transit, Author = item.Author
                }, token),
                (id, token) => RollbackUnloadReservationAsync(d, id, token),
                ct);

            if (prepared.Status == ReservationFirstDispatchStatus.ReservationFailed)
            {
                var reserveGate = GateFor((item.EquipmentId, item.PositionId));
                await reserveGate.WaitAsync(ct);
                try
                {
                    ctx.AlarmRaised = true;
                    SetState(ctx, PositionState.Alarm);
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
                InvalidateLineCache(item.EquipmentId);
                if (d.DestEquipmentId is long de) InvalidateLineCache(de);
                LogRouteUnavailableThrottled(item.EquipmentId,
                    $"EQ{item.EquipmentId} POS{item.PositionId} 下料最终路由门禁失败（非 RCS 失败），已回滚预记/交接：{prepared.RouteResult?.SafeMessage ?? "—"}");
                if (!prepared.RollbackSucceeded)
                {
                    var g = GateFor((item.EquipmentId, item.PositionId));
                    await g.WaitAsync(ct);
                    try
                    {
                        ctx.AlarmRaised = true;
                        SetState(ctx, PositionState.Alarm);
                        await _alarms.RaiseRcsTaskNotFoundAsync(taskId,
                            $"EQ{item.EquipmentId} POS{item.PositionId} 路由拒发后预记/交接回滚失败，需人工核账", ct);
                    }
                    finally { g.Release(); }
                }
                return;
            }

            if (prepared.Status == ReservationFirstDispatchStatus.Dispatched
                && d.Target == UnloadTarget.NextMachineCell
                && !TryMarkInboundDispatched(d, taskId))
            {
                await CloseOrphanTaskAsync(taskId, "UNLOAD_HANDOFF_RESERVATION_LOST", ct);
                result = RcsResult.Fail("", "直接交接预登记在下发窗口内丢失，任务已收口") with { TaskId = taskId };
            }
            else
            {
                result = prepared.Status == ReservationFirstDispatchStatus.Dispatched
                    ? prepared.DispatchResult!
                    : RcsResult.Fail("", BuildDispatchFailureMessage(prepared.Exception,
                        prepared.DispatchResult, prepared.RollbackSucceeded)) with { TaskId = taskId };
            }
        }
        else
        {
            var prepared = await _reservationFirstDispatcher.ExecuteAsync(
                taskId,
                (_, _) => Task.FromResult<object?>(new object()),
                (_, token) => _routingValidator.ValidateAsync(routeCtx, token),
                (id, token) => _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
                {
                    TaskId = id,
                    WorkLineId = workLineId, LineCode = lineCode, TaskType = "1",
                    Priority = item.Priority, FromCode = item.FromCode, ToCode = d.ToCell,
                    EquipmentId = item.EquipmentId, PositionId = item.PositionId,
                    Kind = RcsTaskKind.Transit, Author = item.Author
                }, token),
                (_, _) => Task.FromResult(true),
                ct);

            if (prepared.Status == ReservationFirstDispatchStatus.RouteUnavailable)
            {
                InvalidateLineCache(item.EquipmentId);
                LogRouteUnavailableThrottled(item.EquipmentId,
                    $"EQ{item.EquipmentId} POS{item.PositionId} 下料（无槽位账）最终路由门禁失败：{prepared.RouteResult?.SafeMessage ?? "—"}");
                return;
            }

            result = prepared.Status == ReservationFirstDispatchStatus.Dispatched
                ? prepared.DispatchResult!
                : RcsResult.Fail("", BuildDispatchFailureMessage(prepared.Exception,
                    prepared.DispatchResult, prepared.RollbackSucceeded)) with { TaskId = taskId };
        }

        // bug#7：回填 ctx 与主循环驱动串行化（仅结果写入在锁内）
        var gate = GateFor((item.EquipmentId, item.PositionId));
        await gate.WaitAsync(ct);
        try
        {
            if (result.Success && !string.IsNullOrEmpty(result.TaskId))
            {
                ctx.CurrentTaskId = result.TaskId;
                ctx.Phase = PositionPhase.Unload;
                _logger.LogInformation("EQ{Eq} POS{Pos} 下发下料任务 {TaskId} {From}→{To}（{Target}）", item.EquipmentId, item.PositionId, result.TaskId, item.FromCode, d.ToCell, d.Target);
            }
            else
            {
                // 派工失败 → 粘滞 Alarm（经 SetState 刷看板），等人工恢复；不自动重发（防重试风暴）
                ctx.AlarmRaised = true;
                SetState(ctx, PositionState.Alarm);
                var err = result.Error ?? result.Message ?? "未知错误";
                await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{item.EquipmentId}-POS{item.PositionId}",
                    $"EQ{item.EquipmentId} POS{item.PositionId} 下料 RCS 下发失败：{err}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 下料下发失败：{Err}", item.EquipmentId, item.PositionId, err);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>RCS 已下发但本工位不能继续绑定：尽量 cancel，失败则落库 FAILED，避免 orphan 挂起。</summary>
    private async Task CloseOrphanTaskAsync(string taskId, string reason, CancellationToken ct)
    {
        try
        {
            var r = await _taskSvc.CancelAsync(taskId, ct);
            if (!r.Success)
            {
                if (!await _taskStore.UpdateStateAsync(taskId, RcsTaskState.Failed, null, reason, ct))
                    _logger.LogWarning("orphan 任务 {Task} 取消失败且落库 FAILED 未生效（任务不存在）：{Reason}",
                        taskId, reason);
                else
                    _logger.LogWarning("orphan 任务 {Task} 取消失败，已落库 FAILED（{Reason}）：{Msg}",
                        taskId, reason, r.Message ?? r.Error ?? "—");
            }
            else
                _logger.LogInformation("orphan 任务 {Task} 已取消（{Reason}）", taskId, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "收口 orphan 任务 {Task} 异常，尝试落库 FAILED", taskId);
            try
            {
                if (!await _taskStore.UpdateStateAsync(taskId, RcsTaskState.Failed, null, reason, ct))
                    _logger.LogWarning("orphan 任务 {Task} 落库 FAILED 未生效（任务不存在）", taskId);
            }
            catch (Exception ex2) { _logger.LogWarning(ex2, "orphan 任务 {Task} 落库 FAILED 仍失败", taskId); }
        }
    }

    private static bool RequiresUnloadReservation(UnloadDecision decision) =>
        decision.Target == UnloadTarget.NextMachineCell || decision.DestFrameId is not null;

    private static DispatchRouteContext BuildUnloadRouteContext(DispatchItem item, UnloadDecision d)
    {
        var destEq = d.DestEquipmentId is long de
            ? RouteDependency.Required(de)
            : RouteDependency.NotApplicable;
        var destPos = d.DestPositionId is long dp
            ? RouteDependency.Required(dp)
            : RouteDependency.NotApplicable;
        var destFrame = d.DestFrameId is long df
            ? RouteDependency.Required(df)
            : RouteDependency.NotApplicable;

        return new DispatchRouteContext
        {
            SourceEquipmentId = item.EquipmentId,
            SourcePositionId = item.PositionId,
            DestEquipmentId = destEq,
            DestPositionId = destPos,
            DestFrameId = destFrame,
            FromCode = item.FromCode,
            ToCode = d.ToCell,
            RequiresResolvedCells = true
        };
    }

    /// <summary>下料在调用 RCS 前先登记直接交接或原子预记目标料架。</summary>
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
            return _expectedInbound.TryAdd((dstEq, dstPos), handoff)
                ? new UnloadReservation()
                : null;
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
            return new UnloadReservation();
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
            && _expectedInbound.TryGetValue((dstEq, dstPos), out var handoff)
            && string.Equals(handoff.SourceTaskId, taskId, StringComparison.Ordinal))
        {
            return _expectedInbound.TryRemove((dstEq, dstPos), out _);
        }

        return false;
    }

    private bool TryMarkInboundDispatched(UnloadDecision decision, string taskId)
    {
        if (decision.DestEquipmentId is not long dstEq || decision.DestPositionId is not long dstPos)
            return false;

        var key = (dstEq, dstPos);
        while (_expectedInbound.TryGetValue(key, out var current))
        {
            if (!string.Equals(current.SourceTaskId, taskId, StringComparison.Ordinal)) return false;
            if (current.IsDispatched) return true;
            if (_expectedInbound.TryUpdate(key, current with { IsDispatched = true }, current)) return true;
        }
        return false;
    }

    private static string BuildDispatchFailureMessage(
        Exception? exception,
        RcsResult? dispatchResult,
        bool rollbackSucceeded)
    {
        var reason = exception?.Message
            ?? (dispatchResult?.Success == true
                ? "RCS 返回 taskId 与预生成 taskId 不一致"
                : dispatchResult?.Error ?? dispatchResult?.Message ?? "RCS 下发失败");
        return rollbackSucceeded ? reason : $"{reason}；预记回滚失败，需人工核账";
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
        public HasMatRecheckTracker HasMatRecheck { get; } = new();
        public string? StatusDetail { get; set; }
        public string? AlarmReason { get; set; }
        /// <summary>当前件的物料码（上料取料时捕获，随件流转至下料/交接，供落账与加工记录溯源）。</summary>
        public string? MaterialId { get; set; }
        /// <summary>Layer 1：已向单一调度消费者投递"请求上料"（去重，避免每 tick 重复投递）。</summary>
        public bool UploadRequested { get; set; }
        /// <summary>进入 WAIT_LOAD 的时刻——多工位竞争同一料源时"空闲最久优先"的确定性排序依据。</summary>
        public DateTime? WaitLoadSince { get; set; }
    }

    /// <summary>上料入队决策：入队 / 料架无料等待 / 失败告警。</summary>
    private enum UploadDecision { Queued, WaitMaterial, Failed }

    /// <summary>下料终点决策：终点 cell + 终点类型 + 目标料架/机台工位。</summary>
    private readonly record struct UnloadDecision(string ToCell, UnloadTarget Target, long? DestFrameId, long? DestEquipmentId, long? DestPositionId);

    private sealed record UnloadReservation;

    /// <summary>工序间直接交接登记：上游把 OK 件送入下游 cell 后，下游见料即接。</summary>
    private sealed record InboundHandoff(
        string? SourceTaskId,
        string? MaterialId,
        DateTime CreatedUtc,
        bool IsDispatched = true);
}
