using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>上/下料共用的 RCS 绑定与失败文案。不含预记策略。</summary>
internal static class PositionDispatchBindSupport
{
    public static string BuildDispatchFailureMessage(
        Exception? exception,
        RcsResult? dispatchResult,
        bool rollbackSucceeded)
    {
        var reason = exception?.Message
            ?? (dispatchResult?.Success == true
                ? "RCS 下发成功但未带回任务号"
                : dispatchResult?.Error ?? dispatchResult?.Message ?? "RCS 下发失败");
        return rollbackSucceeded ? reason : $"{reason}；预记回滚失败，需人工核账";
    }

    public static RcsResult BindableResult<TReservation>(
        ReservationFirstDispatchResult<TReservation> prepared, string taskId, ILogger logger)
        where TReservation : class
    {
        if (prepared.Status == ReservationFirstDispatchStatus.Dispatched)
            return prepared.DispatchResult!;
        if (prepared.Status == ReservationFirstDispatchStatus.DispatchUnknown)
        {
            logger.LogWarning("任务 {Task} RCS 下发结果未知（{Err}），保留预记并绑定工位，交跟踪器确认",
                taskId, prepared.DispatchResult?.Error ?? "—");
            return prepared.DispatchResult! with
            {
                Ok = true, Success = true, FailureKind = RcsFailureKind.None, TaskId = taskId
            };
        }
        return RcsResult.Fail("", BuildDispatchFailureMessage(prepared.Exception,
            prepared.DispatchResult, prepared.RollbackSucceeded)) with { TaskId = taskId };
    }

    public static Task<RcsResult> DispatchLoadUnloadAsync(
        IRcsTaskService taskSvc,
        ILogger logger,
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
    {
        if (grab is not null)
        {
            var item = grab.Items.Count > 0 ? grab.Items[0] : null;
            logger.LogInformation(
                "EQ{Eq} POS{Pos} 自动上下料走抓取 {Src}→{Dst} srcNo={SrcNo} srcPos={SrcPos} dstNo={DstNo} dstPos={DstPos} data={Data} task={Task}",
                equipmentId, positionId, grab.SrcStation, grab.DstStation,
                item?.SrcNo, item?.SrcPos, item?.DstNo, item?.DstPos, item?.Data ?? "", taskId);
            return taskSvc.DispatchGrabAsync(grab with { TaskId = taskId }, ct);
        }

        return taskSvc.DispatchTransitAsync(new TransitDispatchArgs
        {
            TaskId = taskId,
            WorkLineId = workLineId,
            LineCode = lineCode,
            TaskType = taskType,
            Priority = priority,
            FromCode = fromCode,
            ToCode = toCode,
            EquipmentId = equipmentId,
            PositionId = positionId,
            MaterialId = materialId,
            Kind = RcsTaskKind.Transit,
            Author = author
        }, ct);
    }
}
