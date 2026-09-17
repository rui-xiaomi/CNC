using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 动作执行器：只把 <see cref="PositionTransition"/> 的动作清单翻译成 IO，不自己决定状态。
/// 唯二由 IO 结果定态的地方：可失败动作落 <see cref="TransitionOutcome.OnActionFailure"/>；
/// HasMat 复核由 <see cref="HasMatRecheckTracker"/> 定态。
/// </summary>
internal sealed class PositionActionExecutor
{
    private readonly ISlotAccountService _slots;
    private readonly IWorkRecordService _workRecords;
    private readonly PositionInboundRegistry _inbound;
    private readonly IPositionActionHost _host;
    private readonly ILogger _logger;

    public PositionActionExecutor(
        ISlotAccountService slots,
        IWorkRecordService workRecords,
        PositionInboundRegistry inbound,
        IPositionActionHost host,
        ILogger logger)
    {
        _slots = slots;
        _workRecords = workRecords;
        _inbound = inbound;
        _host = host;
        _logger = logger;
    }

    public async Task<PositionState> ApplyOutcomeAsync(
        PositionContext ctx, TransitionOutcome outcome,
        InboundHandoff? inbound, bool? hasMat, string? rcsState, CancellationToken ct)
    {
        if (outcome.AlarmReason is not null) ctx.AlarmReason = outcome.AlarmReason;

        var next = outcome.Target;
        foreach (var action in outcome.Actions)
        {
            var result = await ApplyActionAsync(ctx, action, inbound, hasMat, rcsState, ct);
            if (result.NextOverride is PositionState overridden) next = overridden;
            if (!result.Succeeded)
            {
                next = outcome.OnActionFailure ?? next;
                break; // 可失败动作失败即中止后续动作
            }
        }

        // 只在回到 WaitLoad 时清报警标记；Alarm 期间保持标记，避免每 tick 重复告警
        if (next == PositionState.WaitLoad)
            ctx.AlarmRaised = false;
        return next;
    }

    public async Task<ActionResult> ApplyActionAsync(
        PositionContext ctx, PositionAction action,
        InboundHandoff? inbound, bool? hasMat, string? rcsState, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case PositionActionKind.ConsumeInboundHandoff:
                ConsumeInboundHandoff(ctx, inbound);
                return ActionResult.Ok;

            case PositionActionKind.ReclaimStaleInboundHandoff:
                await _host.ReclaimStaleInboundHandoffAsync(ctx, inbound, ct);
                return ActionResult.Ok;

            case PositionActionKind.RequestUploadIfNoInbound:
                // 上一动作可能刚回收掉陈旧登记，故此刻现查（与在途直送互斥）
                if (!_inbound.Contains((ctx.EquipmentId, ctx.PositionId)))
                    ctx.UploadRequested = true;
                return ActionResult.Ok;

            case PositionActionKind.RecheckHasMatFresh:
                return ActionResult.MoveTo(await _host.RecheckHasMatAsync(ctx, ct));

            case PositionActionKind.WriteTestStart:
                return await _host.WriteTestStartAsync(ctx, action.TestStartValue, ct)
                    ? ActionResult.Ok
                    : ActionResult.Failed;

            case PositionActionKind.ConfirmTake:
                // 源料架取料落账（物料已被取走）。工序间交接件无取料预记，此调用幂等无副作用。
                await _slots.ConfirmTakeAsync(ctx.CurrentTaskId!, ct);
                return ActionResult.Ok;

            case PositionActionKind.ConfirmPut:
                // 入库料架落账（下料/中转/NG 架）。直接交接到下一台机无入库预记，幂等无副作用。
                await _slots.ConfirmAsync(ctx.CurrentTaskId!, ct);
                return ActionResult.Ok;

            case PositionActionKind.RecordWorkStart:
                // 加工开始：写 WORK_RECORD（关联当前上料 taskId）
                ctx.WorkRecordId = await _workRecords.RecordStartAsync(new WorkRecordStartArgs
                {
                    EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId,
                    PositionCode = $"POS-{ctx.PositionId}", RcsTaskId = ctx.CurrentTaskId,
                    MaterialId = ctx.MaterialId, Author = "scheduler"
                }, ct);
                return ActionResult.Ok;

            case PositionActionKind.RecordWorkResult:
                ctx.LastTestOk = action.IsOk;
                await _workRecords.RecordResultAsync(ctx.WorkRecordId, action.IsOk ? "0" : "1", null, ct);
                return ActionResult.Ok;

            case PositionActionKind.ClearWorkRecord:
                ctx.WorkRecordId = 0; // 暂停派工时避免每 tick 重复写结果日志
                return ActionResult.Ok;

            case PositionActionKind.ClearCurrentItem:
                ctx.CurrentTaskId = null;
                ctx.Phase = null;
                ctx.MaterialId = null; // 件已离开本工位，清物料码
                ctx.LastTestOk = null;
                ctx.TaskBoundAt = null;
                return ActionResult.Ok;

            case PositionActionKind.EnqueueUnload:
                ctx.LastTestOk = action.IsOk;
                return await _host.EnqueueUnloadAsync(ctx, action.IsOk, ct)
                    ? ActionResult.Ok
                    : ActionResult.Failed;

            case PositionActionKind.RaiseAlarm:
                // 加工中未出结果就进 Alarm（超时等）：按 NG 记下，ResetAlarm 才能再入下料队，避免启动电平仍为 1。
                if (ctx.State == PositionState.Processing && ctx.LastTestOk is null)
                    ctx.LastTestOk = false;
                await _host.RaiseAlarmPackageAsync(ctx, hasMat, rcsState, ct);
                return ActionResult.Ok;

            default:
                return ActionResult.Ok;
        }
    }

    /// <summary>取用已下发的直送登记：清登记、绑定源任务与物料码，阶段置上料。</summary>
    public void ConsumeInboundHandoff(PositionContext ctx, InboundHandoff? inbound)
    {
        var key = (ctx.EquipmentId, ctx.PositionId);
        _inbound.TryRemove(key, out var removed);
        _inbound.ForgetWarned(key);

        if ((removed ?? inbound) is not { } handoff) return;
        ctx.Phase = PositionPhase.Upload;
        ctx.CurrentTaskId = handoff.SourceTaskId; // 溯源上游下料任务
        ctx.MaterialId = handoff.MaterialId;      // 物料码随交接件传入
        _logger.LogInformation("EQ{Eq} POS{Pos} 收到工序间交接件（源 {Src} 物料 {El}），转 Loaded",
            ctx.EquipmentId, ctx.PositionId, handoff.SourceTaskId, handoff.MaterialId ?? "—");
    }
}

/// <summary>动作执行器对调度器的接缝：复核、写启动、入队与告警仍由宿主持有。</summary>
internal interface IPositionActionHost
{
    Task<PositionState> RecheckHasMatAsync(PositionContext ctx, CancellationToken ct);
    Task<bool> WriteTestStartAsync(PositionContext ctx, int value, CancellationToken ct);
    Task<bool> EnqueueUnloadAsync(PositionContext ctx, bool isOk, CancellationToken ct);
    Task RaiseAlarmPackageAsync(PositionContext ctx, bool? hasMat, string? rcsState, CancellationToken ct);
    Task ReclaimStaleInboundHandoffAsync(PositionContext ctx, InboundHandoff? inbound, CancellationToken ct);
}
