using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.State;

/// <summary>
/// 换架任务对编排（第四阶段⑥b，§6.2 v2.2/v2.3）。
/// 先拉后送：①拉走旧架（站点→缓存区）→ 等 ① completed → ②送新架（缓存区→站点）。
/// TXN_ID 关联两个 taskId；失败由 RcsTaskTracker 自动 redo（≤MaxAutoRedo），耗尽后本编排告警+工单。
/// 第一发失败：绑定不解除、原状保持；第二发失败：锁定工序+工单（人工送架后界面点"新架到位"再绑定，绑定不在本编排）。
/// </summary>
public sealed class ChangeFrameOrchestrator : IChangeFrameOrchestrator
{
    private readonly IRcsTaskService _taskSvc;
    private readonly IRcsTaskStore _taskStore;
    private readonly IEquipmentConfigService _equipment;
    private readonly ILocationMapService _locationMap;
    private readonly IAlarmEventService _alarms;
    private readonly IPositionScheduler _scheduler;
    private readonly RcsCallbackNotifier _notifier;
    private readonly RcsOptions _options;
    private readonly ILogger<ChangeFrameOrchestrator> _logger;

    private readonly ConcurrentDictionary<string, ChangeFrameContext> _active = new();
    private readonly ConcurrentDictionary<(long Eq, FrameRole Role), byte> _roleLocks = new();
    private static long _seq;

    public ChangeFrameOrchestrator(
        IRcsTaskService taskSvc, IRcsTaskStore taskStore, IEquipmentConfigService equipment,
        ILocationMapService locationMap, IAlarmEventService alarms, IPositionScheduler scheduler,
        RcsCallbackNotifier notifier, IOptions<AppOptions> options, ILogger<ChangeFrameOrchestrator> logger)
    {
        _taskSvc = taskSvc;
        _taskStore = taskStore;
        _equipment = equipment;
        _locationMap = locationMap;
        _alarms = alarms;
        _scheduler = scheduler;
        _notifier = notifier;
        _options = options.Value.Rcs;
        _logger = logger;
        _notifier.TaskStatusReceived += OnTaskStatusReceived;
    }

    public event EventHandler<ChangeFrameProgressEvent>? ProgressChanged;

    public IReadOnlyList<ChangeFrameProgressEvent> GetActiveTransactions()
        => _active.Values.Select(MapToEvent).ToList();

    public async Task<string> ChangeFrameAsync(long equipmentId, FrameRole role, string author, CancellationToken ct = default)
    {
        var txnId = $"CF-{DateTime.Now:yyyyMMddHHmmss}-{Interlocked.Increment(ref _seq):D3}";

        // 1. 取该角色当前绑定料架
        var bindingIds = await _equipment.GetFrameBindingIdsAsync(equipmentId, ct);
        long? frameId = role == FrameRole.Upload ? bindingIds.UploadFrameId : bindingIds.DownloadFrameId;
        if (frameId is null)
        {
            _logger.LogWarning("换架失败：机台 {Eq} 无 {Role} 角色绑定料架", equipmentId, role);
            Raise(txnId, equipmentId, role, ChangeFrameStep.Alarm, null, null, "FAILED", $"无 {role} 角色绑定料架");
            return txnId;
        }

        // 2. 整架搬运用 shelf/station（一架多 cell 时取第一条 cell 会错位）；缺 LOCATION_MAP 拒发，禁止假码。
        var frameLoc = await _locationMap.ResolveFrameAsync(frameId.Value, "shelf", ct)
                       ?? await _locationMap.ResolveFrameAsync(frameId.Value, "station", ct);
        var bufferArea = role == FrameRole.Unload ? _options.FullBufferArea : _options.EmptyBufferArea;
        var buffer = await _locationMap.ResolveAreaAsync(bufferArea, ct);
        if (frameLoc is null || buffer is null || string.IsNullOrWhiteSpace(buffer.RcsCode))
        {
            var msg = frameLoc is null
                ? $"料架 {frameId.Value} 未录入 LOCATION_MAP（shelf/station）"
                : $"缓存区 {bufferArea} 未录入 LOCATION_MAP";
            // D8：路由配置不可用 → Warning，不 Raise 业务 Alarm
            _logger.LogWarning("换架路由不可用：机台 {Eq} 角色 {Role} Frame={Frame}：{Msg}",
                equipmentId, role, frameId.Value, msg);
            Raise(txnId, equipmentId, role, ChangeFrameStep.Alarm, null, null, "FAILED", msg);
            return txnId;
        }

        var line = await _equipment.GetWorkLineByEquipmentAsync(equipmentId, ct);
        if (line is null)
        {
            const string msg = "机台线体路由不可用（缺失或已禁用）";
            _logger.LogWarning("换架路由不可用：机台 {Eq} 角色 {Role}：{Msg}", equipmentId, role, msg);
            Raise(txnId, equipmentId, role, ChangeFrameStep.Alarm, null, null, "FAILED", msg);
            return txnId;
        }

        var ctx = new ChangeFrameContext
        {
            TxnId = txnId, EquipmentId = equipmentId, Role = role,
            FrameId = frameId.Value, FrameCell = frameLoc.RcsCode, BufferCell = buffer.RcsCode, Author = author,
            WorkLineId = line.WorkLineId, LineCode = line.LineCode
        };
        _active[txnId] = ctx;

        // 3. 下发第一发：拉走旧架（站点→缓存区）；Operation=ChangeFrame 注入机台上下文供 Final Bind
        var pull = await _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, TaskType = "1", Priority = 9,
            FromCode = ctx.FrameCell, ToCode = ctx.BufferCell,
            EquipmentId = equipmentId, Kind = RcsTaskKind.ChangeFrame,
            Operation = DispatchOperationKind.ChangeFrame,
            TxnId = txnId, Author = author
        }, ct);

        if (pull.FailureKind == RcsFailureKind.OutcomeUnknown && !string.IsNullOrEmpty(pull.TaskId))
        {
            // 请求已发出未拿到应答：RCS 可能已建任务、车可能在拉架——保留事务交跟踪器按 queryTask 确认，
            // 完成事件照常推进第二发；RCS 实无此任务时由 NotifyTaskAbandonedAsync 收口。
            ctx.PullTaskId = pull.TaskId;
            Raise(txnId, equipmentId, role, ChangeFrameStep.PullOld, ctx.PullTaskId, null, "RUNNING",
                $"第一发下发结果未知，等待 RCS 确认：{pull.Error ?? pull.Message ?? "—"}");
            _logger.LogWarning("换架 {Txn} 第一发 {Pull} 下发结果未知，保留事务等待 RCS 确认", txnId, ctx.PullTaskId);
            return txnId;
        }

        if (!pull.Success || string.IsNullOrEmpty(pull.TaskId))
        {
            var pullErr = pull.Error ?? pull.Message ?? "未知错误";
            Raise(txnId, equipmentId, role, ChangeFrameStep.Alarm, null, null, "FAILED", $"第一发下发失败：{pullErr}");
            if (IsRoutingFailure(pull))
            {
                _logger.LogWarning(
                    "换架第一发路由拒发：机台 {Eq} 角色 {Role} Frame={Frame} Kind={Kind}：{Msg}",
                    equipmentId, role, ctx.FrameId, pull.FailureKind, pullErr);
            }
            else
            {
                await _alarms.RaiseRcsTaskCanceledAsync(txnId, $"换架第一发（拉旧架）下发失败：{pullErr}", ct);
            }
            _active.TryRemove(txnId, out _);
            return txnId;
        }

        ctx.PullTaskId = pull.TaskId;
        Raise(txnId, equipmentId, role, ChangeFrameStep.PullOld, ctx.PullTaskId, null, "RUNNING", "已下发拉旧架");
        _logger.LogInformation("换架 {Txn} 第一发 拉旧架 {Pull}（{Cell}→{Buf}）", txnId, ctx.PullTaskId, ctx.FrameCell, ctx.BufferCell);
        return txnId;
    }

    private void OnTaskStatusReceived(object? sender, RcsTaskStatusEvent e)
    {
        // 找属于某活动换架事务的 pull/push taskId
        var ctx = _active.Values.FirstOrDefault(c => c.PullTaskId == e.TaskId || c.PushTaskId == e.TaskId);
        if (ctx is null) return;
        _ = HandleAsync(ctx, e);
    }

    private async Task HandleAsync(ChangeFrameContext ctx, RcsTaskStatusEvent e)
    {
        try
        {
            // 第一发（拉旧架）
            if (e.TaskId == ctx.PullTaskId)
            {
                if (e.TaskState == RcsTaskState.Completed)
                {
                    // 回调+轮询可能各触发一次 Completed；只允许一次下发第二发。
                    if (Interlocked.CompareExchange(ref ctx.PushDispatched, 1, 0) != 0)
                        return;

                    _logger.LogInformation("换架 {Txn} 第一发完成，下发第二发 送新架", ctx.TxnId);
                    var push = await _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
                    {
                        WorkLineId = ctx.WorkLineId, LineCode = ctx.LineCode, TaskType = "0", Priority = 9,
                        FromCode = ctx.BufferCell, ToCode = ctx.FrameCell,
                        EquipmentId = ctx.EquipmentId, Kind = RcsTaskKind.ChangeFrame,
                        Operation = DispatchOperationKind.ChangeFrame,
                        TxnId = ctx.TxnId, Author = ctx.Author
                    });
                    if (push.Success && !string.IsNullOrEmpty(push.TaskId))
                    {
                        ctx.PushTaskId = push.TaskId;
                        Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.PushNew, ctx.PullTaskId, ctx.PushTaskId, "RUNNING", "已下发送新架");
                    }
                    else if (push.FailureKind == RcsFailureKind.OutcomeUnknown && !string.IsNullOrEmpty(push.TaskId))
                    {
                        // 同第一发：可能已在送新架，不得按失败锁工序；保留事务等 RCS 确认
                        ctx.PushTaskId = push.TaskId;
                        Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.PushNew, ctx.PullTaskId, ctx.PushTaskId, "RUNNING",
                            $"第二发下发结果未知，等待 RCS 确认：{push.Error ?? push.Message ?? "—"}");
                        _logger.LogWarning("换架 {Txn} 第二发 {Push} 下发结果未知，保留事务等待 RCS 确认", ctx.TxnId, ctx.PushTaskId);
                    }
                    else
                    {
                        if (!TryBeginTerminal(ctx)) return;
                        var pushErr = push.Error ?? push.Message ?? "未知错误";
                        Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Alarm, ctx.PullTaskId, null, "FAILED", $"第二发下发失败：{pushErr}");
                        if (IsRoutingFailure(push))
                        {
                            _logger.LogWarning(
                                "换架第二发路由拒发：机台 {Eq} 角色 {Role} Frame={Frame} Kind={Kind}：{Msg}",
                                ctx.EquipmentId, ctx.Role, ctx.FrameId, push.FailureKind, pushErr);
                        }
                        else
                        {
                            await _alarms.RaiseRcsTaskCanceledAsync(ctx.TxnId, $"换架第二发（送新架）下发失败：{pushErr}");
                        }
                        LockDispatch(ctx, $"换架第二发下发失败：{pushErr}");
                        _active.TryRemove(ctx.TxnId, out _);
                    }
                }
                else if (e.TaskState == RcsTaskState.Canceled || await IsRedoExhausted(e.TaskId))
                {
                    if (!TryBeginTerminal(ctx)) return;
                    Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Alarm, ctx.PullTaskId, null, e.TaskState, "第一发失败/取消，绑定不解除，原状保持");
                    await _alarms.RaiseRcsTaskCanceledAsync(ctx.TxnId,
                        $"换架第一发（拉旧架）{e.TaskState}，绑定不解除、原状保持");
                    _active.TryRemove(ctx.TxnId, out _);
                }
            }
            // 第二发（送新架）
            else if (e.TaskId == ctx.PushTaskId)
            {
                if (e.TaskState == RcsTaskState.Completed)
                {
                    if (!TryBeginTerminal(ctx)) return;
                    Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Done, ctx.PullTaskId, ctx.PushTaskId, "COMPLETED", "换架完成");
                    _scheduler.InvalidateFrameBindingCache(ctx.EquipmentId);
                    _logger.LogInformation("换架 {Txn} 完成", ctx.TxnId);
                    _active.TryRemove(ctx.TxnId, out _);
                }
                else if (e.TaskState == RcsTaskState.Canceled || await IsRedoExhausted(e.TaskId))
                {
                    if (!TryBeginTerminal(ctx)) return;
                    Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Alarm, ctx.PullTaskId, ctx.PushTaskId, e.TaskState, "第二发失败/取消，站点空置，锁定工序+工单");
                    await _alarms.RaiseRcsTaskCanceledAsync(ctx.TxnId,
                        $"换架第二发（送新架）{e.TaskState}，站点空置，需锁定工序并人工处理");
                    LockDispatch(ctx, $"换架第二发（送新架）{e.TaskState}，站点空置");
                    _active.TryRemove(ctx.TxnId, out _);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "换架 {Txn} 处理事件异常", ctx.TxnId); }
    }

    public async Task<int> RecoverInFlightAsync(IReadOnlyList<RcsTaskRow> unfinished, CancellationToken ct = default)
    {
        var kind = RcsTaskKindNames.ToDbKind(RcsTaskKind.ChangeFrame);
        var groups = unfinished
            .Where(r => r.Kind == kind && !string.IsNullOrEmpty(r.TxnId) && !string.IsNullOrEmpty(r.RcsTaskId))
            .GroupBy(r => r.TxnId!, StringComparer.Ordinal)
            .ToList();
        if (groups.Count == 0) return 0;

        var emptyBuffer = await _locationMap.ResolveAreaAsync(_options.EmptyBufferArea, ct);
        var recovered = 0;
        foreach (var group in groups)
        {
            var txnId = group.Key;
            if (_active.ContainsKey(txnId)) continue;
            var pull = group.FirstOrDefault(r => r.TaskType == "1");
            var push = group.FirstOrDefault(r => r.TaskType == "0");
            var anchor = push ?? pull;
            if (anchor?.EquipmentId is not long equipmentId) continue;

            var line = await _equipment.GetWorkLineByEquipmentAsync(equipmentId, ct);
            if (line is null)
            {
                _logger.LogWarning("换架 {Txn} 重启后无法接续：机台 {Eq} 线体路由不可用", txnId, equipmentId);
                await _alarms.RaiseRcsTaskCanceledAsync(txnId, "重启后换架事务无法接续（机台线体路由不可用），请人工核对料架位置并处理", ct);
                continue;
            }

            // 拉旧架 = 站点→缓存区；送新架 = 缓存区→站点
            var frameCell = push?.ToCode ?? pull!.FromCode;
            var bufferCell = push?.FromCode ?? pull!.ToCode;
            var frame = await _locationMap.ResolveByRcsCodeAsync(frameCell, ct);
            var ctx = new ChangeFrameContext
            {
                TxnId = txnId, EquipmentId = equipmentId,
                // 角色未落库，按缓存区反推（上料架换架用空架缓存区）；仅影响进度展示与水位去重
                Role = string.Equals(bufferCell, emptyBuffer?.RcsCode, StringComparison.Ordinal) ? FrameRole.Upload : FrameRole.Unload,
                FrameId = frame?.FrameId ?? 0, FrameCell = frameCell, BufferCell = bufferCell,
                PullTaskId = pull?.RcsTaskId, PushTaskId = push?.RcsTaskId, PushDispatched = push is null ? 0 : 1,
                Author = "recovery", WorkLineId = line.WorkLineId, LineCode = line.LineCode
            };
            _active[txnId] = ctx;
            recovered++;
            _logger.LogInformation("换架 {Txn} 重启接续：拉旧架 {Pull} 送新架 {Push}", txnId, ctx.PullTaskId ?? "—", ctx.PushTaskId ?? "—");
        }
        return recovered;
    }

    public async Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default)
    {
        var ctx = _active.Values.FirstOrDefault(c =>
            string.Equals(c.PullTaskId, taskId, StringComparison.Ordinal)
            || string.Equals(c.PushTaskId, taskId, StringComparison.Ordinal));
        if (ctx is null || !TryBeginTerminal(ctx) || !_active.TryRemove(ctx.TxnId, out _)) return;

        var isPush = string.Equals(ctx.PushTaskId, taskId, StringComparison.Ordinal);
        var msg = isPush
            ? $"换架第二发（送新架）已放弃（{reason}），站点可能空置，需锁定工序并人工处理"
            : $"换架第一发（拉旧架）已放弃（{reason}），绑定不解除、原状保持，请人工核对料架位置";
        Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Alarm, ctx.PullTaskId, ctx.PushTaskId, "FAILED", msg);
        _logger.LogWarning("换架 {Txn} 任务 {Task} 已放弃（{Reason}），事务结束", ctx.TxnId, taskId, reason);
        if (isPush) LockDispatch(ctx, msg);
        await _alarms.RaiseRcsTaskCanceledAsync(ctx.TxnId, msg, ct);
    }

    public Task ConfirmNewFrameInPlaceAsync(long equipmentId, FrameRole role, string author, CancellationToken ct = default)
    {
        _roleLocks.TryRemove((equipmentId, role), out _);
        _scheduler.SetEquipmentDispatchHold(equipmentId, false);
        _scheduler.InvalidateFrameBindingCache(equipmentId);
        _logger.LogInformation("换架新架到位确认：机台 {Eq} 角色 {Role} 操作人 {Author}，已解锁派工",
            equipmentId, role, author);
        Raise($"CF-UNLOCK-{equipmentId}-{role}", equipmentId, role, ChangeFrameStep.Done, null, null, "COMPLETED",
            "人工确认新架到位，已解锁派工");
        return Task.CompletedTask;
    }

    public bool IsRoleLocked(long equipmentId, FrameRole role) => _roleLocks.ContainsKey((equipmentId, role));

    private static bool TryBeginTerminal(ChangeFrameContext ctx)
        => Interlocked.CompareExchange(ref ctx.TerminalHandled, 1, 0) == 0;

    private void LockDispatch(ChangeFrameContext ctx, string reason)
    {
        _roleLocks[(ctx.EquipmentId, ctx.Role)] = 0;
        _scheduler.SetEquipmentDispatchHold(ctx.EquipmentId, true, reason);
    }

    private async Task<bool> IsRedoExhausted(string taskId)
    {
        var row = await _taskStore.GetByTaskIdAsync(taskId);
        return row is { TaskState: RcsTaskState.Failed } && row.RedoCount >= _options.MaxAutoRedo;
    }

    /// <summary>D8：路由/配置不可用只 Warning，不 Raise 业务 Alarm。</summary>
    private static bool IsRoutingFailure(RcsResult result)
        => result.FailureKind is RcsFailureKind.RouteUnavailable
            or RcsFailureKind.ConfigurationUnavailable;

    private void Raise(string txnId, long eq, FrameRole role, ChangeFrameStep step, string? pull, string? push, string state, string msg)
    {
        var evt = new ChangeFrameProgressEvent(txnId, eq, role, step, pull, push, state, msg);
        _logger.LogDebug("换架进度 {Txn} {Step} {State} {Msg}", txnId, step, state, msg);
        ProgressChanged?.Invoke(this, evt);
    }

    private static ChangeFrameProgressEvent MapToEvent(ChangeFrameContext c) =>
        new(c.TxnId, c.EquipmentId, c.Role, c.PushTaskId is null ? ChangeFrameStep.PullOld : ChangeFrameStep.PushNew,
            c.PullTaskId, c.PushTaskId, "RUNNING", null);

    private sealed class ChangeFrameContext
    {
        public string TxnId = "";
        public long EquipmentId;
        public FrameRole Role;
        public long FrameId;
        public string FrameCell = "";
        public string BufferCell = "";
        public string? PullTaskId;
        public string? PushTaskId;
        public int PushDispatched;
        public int TerminalHandled;
        public string? Author;
        public long WorkLineId;
        public string LineCode = "LINE";
    }
}
