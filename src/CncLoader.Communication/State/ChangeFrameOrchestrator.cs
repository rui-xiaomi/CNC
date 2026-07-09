using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
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
    private readonly RcsCallbackNotifier _notifier;
    private readonly RcsOptions _options;
    private readonly ILogger<ChangeFrameOrchestrator> _logger;

    private readonly ConcurrentDictionary<string, ChangeFrameContext> _active = new();
    private static long _seq;

    public ChangeFrameOrchestrator(
        IRcsTaskService taskSvc, IRcsTaskStore taskStore, IEquipmentConfigService equipment,
        ILocationMapService locationMap, IAlarmEventService alarms, RcsCallbackNotifier notifier,
        IOptions<AppOptions> options, ILogger<ChangeFrameOrchestrator> logger)
    {
        _taskSvc = taskSvc;
        _taskStore = taskStore;
        _equipment = equipment;
        _locationMap = locationMap;
        _alarms = alarms;
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

        // 2. 解析料架站点（cell 优先，否则 shelf）+ 缓存区；缺 LOCATION_MAP 拒发，禁止假码。
        var frameLoc = await _locationMap.ResolveFrameAsync(frameId.Value, "cell", ct)
                       ?? await _locationMap.ResolveFrameAsync(frameId.Value, "shelf", ct);
        var bufferArea = role == FrameRole.Unload ? _options.FullBufferArea : _options.EmptyBufferArea;
        var buffer = await _locationMap.ResolveAreaAsync(bufferArea, ct);
        if (frameLoc is null || buffer is null || string.IsNullOrWhiteSpace(buffer.RcsCode))
        {
            var msg = frameLoc is null
                ? $"料架 {frameId.Value} 未录入 LOCATION_MAP（cell/shelf）"
                : $"缓存区 {bufferArea} 未录入 LOCATION_MAP";
            _logger.LogWarning("换架失败：{Msg}", msg);
            Raise(txnId, equipmentId, role, ChangeFrameStep.Alarm, null, null, "FAILED", msg);
            await _alarms.RaiseRcsTaskNotFoundAsync($"CHANGE-FRAME-{txnId}", ct);
            return txnId;
        }

        var line = await _equipment.GetWorkLineByEquipmentAsync(equipmentId, ct) ?? new WorkLineRef(1, "LINE");

        var ctx = new ChangeFrameContext
        {
            TxnId = txnId, EquipmentId = equipmentId, Role = role,
            FrameId = frameId.Value, FrameCell = frameLoc.RcsCode, BufferCell = buffer.RcsCode, Author = author,
            WorkLineId = line.WorkLineId, LineCode = line.LineCode
        };
        _active[txnId] = ctx;

        // 3. 下发第一发：拉走旧架（站点→缓存区）
        var pull = await _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, TaskType = "1", Priority = 9,
            FromCode = ctx.FrameCell, ToCode = ctx.BufferCell,
            EquipmentId = equipmentId, Kind = RcsTaskKind.ChangeFrame,
            TxnId = txnId, Author = author
        }, ct);

        if (!pull.Success || string.IsNullOrEmpty(pull.TaskId))
        {
            Raise(txnId, equipmentId, role, ChangeFrameStep.Alarm, null, null, "FAILED", $"第一发下发失败：{pull.Error ?? pull.Message}");
            await _alarms.RaiseRcsTaskCanceledAsync(txnId, ct);
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
                    _logger.LogInformation("换架 {Txn} 第一发完成，下发第二发 送新架", ctx.TxnId);
                    var push = await _taskSvc.DispatchTransitAsync(new TransitDispatchArgs
                    {
                        WorkLineId = ctx.WorkLineId, LineCode = ctx.LineCode, TaskType = "0", Priority = 9,
                        FromCode = ctx.BufferCell, ToCode = ctx.FrameCell,
                        EquipmentId = ctx.EquipmentId, Kind = RcsTaskKind.ChangeFrame,
                        TxnId = ctx.TxnId, Author = ctx.Author
                    });
                    if (push.Success && !string.IsNullOrEmpty(push.TaskId))
                    {
                        ctx.PushTaskId = push.TaskId;
                        Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.PushNew, ctx.PullTaskId, ctx.PushTaskId, "RUNNING", "已下发送新架");
                    }
                    else
                    {
                        Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Alarm, ctx.PullTaskId, null, "FAILED", $"第二发下发失败：{push.Error ?? push.Message}");
                        await _alarms.RaiseRcsTaskCanceledAsync(ctx.TxnId);
                    }
                }
                else if (e.TaskState == RcsTaskState.Canceled || await IsRedoExhausted(e.TaskId))
                {
                    Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Alarm, ctx.PullTaskId, null, e.TaskState, "第一发失败/取消，绑定不解除，原状保持");
                    await _alarms.RaiseRcsTaskCanceledAsync(ctx.TxnId);
                    _active.TryRemove(ctx.TxnId, out _);
                }
            }
            // 第二发（送新架）
            else if (e.TaskId == ctx.PushTaskId)
            {
                if (e.TaskState == RcsTaskState.Completed)
                {
                    Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Done, ctx.PullTaskId, ctx.PushTaskId, "COMPLETED", "换架完成");
                    _logger.LogInformation("换架 {Txn} 完成", ctx.TxnId);
                    _active.TryRemove(ctx.TxnId, out _);
                }
                else if (e.TaskState == RcsTaskState.Canceled || await IsRedoExhausted(e.TaskId))
                {
                    Raise(ctx.TxnId, ctx.EquipmentId, ctx.Role, ChangeFrameStep.Alarm, ctx.PullTaskId, ctx.PushTaskId, e.TaskState, "第二发失败/取消，站点空置，锁定工序+工单");
                    await _alarms.RaiseRcsTaskCanceledAsync(ctx.TxnId);
                    _active.TryRemove(ctx.TxnId, out _);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "换架 {Txn} 处理事件异常", ctx.TxnId); }
    }

    private async Task<bool> IsRedoExhausted(string taskId)
    {
        var row = await _taskStore.GetByTaskIdAsync(taskId);
        return row is { TaskState: RcsTaskState.Failed } && row.RedoCount >= _options.MaxAutoRedo;
    }

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
        public string? Author;
        public long WorkLineId;
        public string LineCode = "LINE";
    }
}
