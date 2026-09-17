using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>Alarm 粘滞包与人工恢复。在途任务不回滚预记。</summary>
internal sealed class PositionAlarmRecovery
{
    private readonly IRcsTaskStore _taskStore;
    private readonly IAlarmEventService _alarms;
    private readonly ILogger _logger;

    public PositionAlarmRecovery(IRcsTaskStore taskStore, IAlarmEventService alarms, ILogger logger)
    {
        _taskStore = taskStore;
        _alarms = alarms;
        _logger = logger;
    }

    public async Task RaisePackageAsync(
        PositionContext ctx,
        bool? hasMat,
        string? rcsState,
        bool hasMatPointConfigured,
        Func<PositionContext, CancellationToken, Task<bool?>> readHasMatFreshAsync,
        Func<string, PositionPhase, string, bool?, bool, CancellationToken, Task<SlotSettlementAction>> settleSlotAsync,
        Action<string, string> requestInboundClear,
        Func<(long Eq, long Pos), string, CancellationToken, Task> clearInboundAsync,
        CancellationToken ct)
    {
        ctx.AlarmRaised = true;
        if (!string.IsNullOrEmpty(ctx.CurrentTaskId))
        {
            var taskId = ctx.CurrentTaskId!;
            var row = await _taskStore.GetByTaskIdAsync(taskId, ct);
            if (row is not null && !RcsStatusMapper.IsTerminal(row.TaskState))
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 告警时任务 {Task} 仍为 {State}，保留预记与交接登记",
                    ctx.EquipmentId, ctx.PositionId, taskId, row.TaskState);
            }
            else
            {
                if (ctx.Phase is PositionPhase phase)
                {
                    var state = row?.TaskState ?? RcsTaskState.Failed;
                    bool? freshHasMat = state == RcsTaskState.Completed ? await readHasMatFreshAsync(ctx, ct) : null;
                    await settleSlotAsync(taskId, phase, state, freshHasMat, hasMatPointConfigured, ct);
                }
                requestInboundClear(taskId, "ALARM");
            }
        }
        await clearInboundAsync((ctx.EquipmentId, ctx.PositionId), "ALARM", ct);

        var reason = ReasonForAlarm(ctx, hasMat, rcsState);
        var alarmKey = ctx.CurrentTaskId ?? $"EQ{ctx.EquipmentId}-POS{ctx.PositionId}";
        await _alarms.RaiseRcsTaskCanceledAsync(alarmKey,
            $"EQ{ctx.EquipmentId} POS{ctx.PositionId}：{reason}", ct);
        _logger.LogWarning("EQ{Eq} POS{Pos} ALARM：{Reason}", ctx.EquipmentId, ctx.PositionId, reason);
    }

    public static string ReasonForAlarm(PositionContext ctx, bool? hasMat, string? rcsState)
    {
        if (!string.IsNullOrWhiteSpace(ctx.AlarmReason)) return ctx.AlarmReason;
        if (rcsState == RcsTaskState.Canceled) return "RCS 任务被取消";
        if (rcsState == RcsTaskState.Failed) return "RCS 任务失败";
        if (ctx.Phase == PositionPhase.Upload && hasMat != true) return "RCS 报完成但 PLC 无料（复核不过）";
        if (ctx.Phase == PositionPhase.Unload && hasMat != false) return "RCS 报完成但 PLC 仍有料（复核不过）";
        return "未知异常";
    }

    public async Task ResetAlarmAsync(
        PositionContext ctx,
        bool hasMatPointConfigured,
        Func<PositionContext, CancellationToken, Task<bool?>> readHasMatFreshAsync,
        Func<SlotSettlementAction, string, CancellationToken, Task<bool>> applySettlementAsync,
        Action<string, string> requestInboundClear,
        Func<(long Eq, long Pos), string, CancellationToken, Task> clearInboundAsync,
        Func<PositionContext, int, CancellationToken, Task<bool>> writeTestStartAsync,
        Func<PositionContext, bool, CancellationToken, Task<bool>> enqueueUnloadAsync,
        Action<PositionContext, PositionState> setState,
        CancellationToken ct)
    {
        var taskId = ctx.CurrentTaskId;
        var phase = ctx.Phase;
        if (!string.IsNullOrEmpty(taskId))
        {
            var row = await _taskStore.GetByTaskIdAsync(taskId, ct);
            if (row is not null && !RcsStatusMapper.IsTerminal(row.TaskState))
                throw new InvalidOperationException(
                    $"工位绑定的 RCS 任务 {taskId} 仍为 {row.TaskState}，请先在 RCS 页取消该任务或等待其结束，再恢复告警");
            if (row is { TaskState: RcsTaskState.Canceled } && row.CancelManualFlag != "1")
                throw new InvalidOperationException(
                    $"工位绑定的 RCS 任务 {taskId} 已取消，请先在 RCS 页「确认人工处理」后再恢复告警");

            if (phase is PositionPhase phaseValue)
            {
                var state = row?.TaskState ?? RcsTaskState.Failed;
                bool? hasMat = state == RcsTaskState.Completed ? await readHasMatFreshAsync(ctx, ct) : null;
                var action = SlotSettlement.Decide(phaseValue, state, hasMat, hasMatPointConfigured);
                if (action == SlotSettlementAction.Hold)
                    throw new InvalidOperationException(
                        $"工位绑定的 RCS 任务 {taskId} 已完成，但 PLC 有料信号读不到，无法核账；请恢复 PLC 通信后再恢复告警");
                await applySettlementAsync(action, taskId, ct);
            }
            requestInboundClear(taskId, "RESET_ALARM");
        }

        var lastTestOk = ctx.LastTestOk;
        var materialId = ctx.MaterialId;
        ctx.CurrentTaskId = null;
        ctx.Phase = null;
        ctx.MaterialId = materialId;
        ctx.AlarmRaised = false;
        ctx.UploadRequested = false;
        ctx.HasMatRecheck.Reset();
        ctx.StatusDetail = null;
        ctx.AlarmReason = null;
        await clearInboundAsync((ctx.EquipmentId, ctx.PositionId), "RESET_ALARM", ct);
        if (!await writeTestStartAsync(ctx, 2, ct))
            _logger.LogWarning("人工恢复 EQ{Eq} POS{Pos} 写 POS_TEST_START=2 未确认，请在 PLC 页复核启动电平",
                ctx.EquipmentId, ctx.PositionId);
        setState(ctx, PositionState.WaitLoad);
        if (lastTestOk is bool isOk && await readHasMatFreshAsync(ctx, ct) == true)
        {
            await enqueueUnloadAsync(ctx, isOk, ct);
            _logger.LogInformation("人工恢复 EQ{Eq} POS{Pos} 后件仍在机台，已再入下料队 isOk={Ok}",
                ctx.EquipmentId, ctx.PositionId, isOk);
        }
        else
        {
            _logger.LogInformation("人工恢复 EQ{Eq} POS{Pos} → WAIT_LOAD（已回滚预记 {Task}）",
                ctx.EquipmentId, ctx.PositionId, taskId ?? "—");
        }
    }
}
