using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 主状态循环、任务批量预取与单工位 Drive 核心。
/// 工位闸仍由调度器持有；本类只在闸内推进 Decide → 动作 → 定态。
/// </summary>
internal sealed class PositionStateLoop
{
    private readonly ISignalStateStore _store;
    private readonly IRcsTaskStore _taskStore;
    private readonly PositionInboundRegistry _inbound;
    private readonly PositionActionExecutor _actionExecutor;
    private readonly PositionStatePublisher _publisher;
    private readonly PositionDriveCoordinator _driveCoordinator;
    private readonly TimeSpan _signalMaxAge;
    private readonly RcsOptions _options;
    private readonly Func<bool> _isReconciled;
    private readonly Func<bool> _isAutoDispatchPaused;
    private readonly ILogger _logger;
    private Dictionary<string, RcsTaskRow>? _tickTaskRows;

    public PositionStateLoop(
        ISignalStateStore store,
        IRcsTaskStore taskStore,
        PositionInboundRegistry inbound,
        PositionActionExecutor actionExecutor,
        PositionStatePublisher publisher,
        PositionDriveCoordinator driveCoordinator,
        TimeSpan signalMaxAge,
        RcsOptions options,
        Func<bool> isReconciled,
        Func<bool> isAutoDispatchPaused,
        ILogger logger)
    {
        _store = store;
        _taskStore = taskStore;
        _inbound = inbound;
        _actionExecutor = actionExecutor;
        _publisher = publisher;
        _driveCoordinator = driveCoordinator;
        _signalMaxAge = signalMaxAge;
        _options = options;
        _isReconciled = isReconciled;
        _isAutoDispatchPaused = isAutoDispatchPaused;
        _logger = logger;
    }

    public async Task RunAsync(
        Func<CancellationToken, Task> driveAllAsync,
        Func<CancellationToken, Task> sweepAsync,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_isReconciled())
                {
                    await Task.Delay(_options.SchedulerIntervalMs, ct);
                    continue;
                }
                await driveAllAsync(ct);
                await sweepAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "调度器主循环异常"); }
            try { await Task.Delay(_options.SchedulerIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public Task DriveAllAsync(
        IReadOnlyList<(long Eq, long Pos, long PlcId)> positions,
        ConcurrentDictionary<(long Eq, long Pos), PositionContext> contexts,
        Func<PositionContext, CancellationToken, Task> drivePositionAsync,
        CancellationToken ct)
        => _driveCoordinator.DriveAllAsync(
            positions, contexts,
            ct => WarmBoundTaskRowsAsync(contexts.Values, ct),
            drivePositionAsync, () => _tickTaskRows = null, ct);

    public async Task WarmBoundTaskRowsAsync(
        IEnumerable<PositionContext> contexts, CancellationToken ct)
    {
        var ids = contexts
            .Select(c => c.CurrentTaskId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            _tickTaskRows = new Dictionary<string, RcsTaskRow>(StringComparer.Ordinal);
            return;
        }

        _tickTaskRows = new Dictionary<string, RcsTaskRow>(
            await _taskStore.GetByTaskIdsAsync(ids, ct), StringComparer.Ordinal);
    }

    public async Task<RcsTaskRow?> LookupTaskAsync(string taskId, CancellationToken ct)
    {
        if (_tickTaskRows is not null && _tickTaskRows.TryGetValue(taskId, out var cached))
            return cached;
        return await _taskStore.GetByTaskIdAsync(taskId, ct);
    }

    public async Task DriveCoreAsync(PositionContext ctx, CancellationToken ct)
    {
        var machine = _store.GetMachine(ctx.EquipmentId);
        // 机台快照过期（轮询停摆/链路断）→ 视为离线，不再派工；切勿把「未知」折成「安全」。
        var machineFresh = machine is not null && machine.IsFresh(_signalMaxAge);
        var plcOnline = machineFresh && machine!.PlcOnline;
        var safe = machine?.Safe ?? true;
        var doorOpen = machine?.DoorOpen ?? false;

        var readings = _store.GetReadings(ctx.EquipmentId);
        bool? hasMat = null, allowLoad = null, ok = null, ng = null;
        foreach (var r in readings)
        {
            if (r.PositionId != ctx.PositionId) continue;
            if (!r.IsFresh(_signalMaxAge)) continue; // 过期读值当 unknown，勿参与状态判定
            switch (r.Signal)
            {
                case SignalKey.PosHasMat: hasMat = r.On; break;
                case SignalKey.PosAllowLoad: allowLoad = r.On; break;
                case SignalKey.PosOk: ok = r.On; break;
                case SignalKey.PosNg: ng = r.On; break;
            }
        }

        // 机台级门先问一次：命中即短路，省掉后面的 RCS 状态查询
        if (PositionTransition.DecideMachineGate(plcOnline, safe, doorOpen, ctx.State) is PositionState gated)
        {
            _publisher.SetState(ctx, gated);
            return;
        }

        // 查当前绑定任务的 RCS 态（本轮 DriveAll 已批量预取）
        RcsTaskRow? rcsRow = null;
        if (!string.IsNullOrWhiteSpace(ctx.CurrentTaskId))
            rcsRow = await LookupTaskAsync(ctx.CurrentTaskId!, ct);
        var rcsState = rcsRow?.TaskState;

        var inboundKey = (ctx.EquipmentId, ctx.PositionId);
        var hasInbound = _inbound.TryGet(inboundKey, out var inbound);

        var prev = ctx.State;
        var outcome = PositionTransition.Decide(new PositionInputs
        {
            Current = ctx.State,
            PlcOnline = plcOnline,
            Safe = safe,
            DoorOpen = doorOpen,
            HasMat = hasMat,
            AllowLoad = allowLoad,
            Ok = ok,
            Ng = ng,
            RcsState = rcsState,
            Phase = ctx.Phase,
            HasCurrentTask = !string.IsNullOrEmpty(ctx.CurrentTaskId),
            AutoDispatchPaused = _isAutoDispatchPaused(),
            InboundPresent = hasInbound,
            InboundDispatched = hasInbound && inbound!.IsDispatched,
            HasOpenWorkRecord = ctx.WorkRecordId > 0,
            AlarmAlreadyRaised = ctx.AlarmRaised,
            StateAge = ctx.StateEnteredAt is DateTime entered ? DateTime.UtcNow - entered : null,
            TaskAge = ResolveTaskAge(ctx, rcsRow),
            ProcessTimeoutMs = _options.ProcessTimeoutMs,
            TaskExecutionTimeoutMs = _options.TaskExecutionTimeoutMs
        });

        var next = await _actionExecutor.ApplyOutcomeAsync(ctx, outcome, hasInbound ? inbound : null, hasMat, rcsState, ct);
        if (next != prev)
            _logger.LogInformation("EQ{Eq} POS{Pos} {Prev} → {Next}", ctx.EquipmentId, ctx.PositionId, prev, next);
        _publisher.SetState(ctx, next);
        await _publisher.PublishCancelHoldAsync(ctx, ct);
    }

    private static TimeSpan? ResolveTaskAge(PositionContext ctx, RcsTaskRow? row)
    {
        if (row?.DispatchTime is DateTime dispatched)
            return DateTime.Now - dispatched;
        if (ctx.TaskBoundAt is DateTime bound)
            return DateTime.Now - bound;
        return null;
    }
}
