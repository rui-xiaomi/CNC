using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>交接登记清理与陈旧回收。调用方须持有目标工位闸。</summary>
internal sealed class PositionInboundCoordinator
{
    private readonly PositionInboundRegistry _inbound;
    private readonly IRcsTaskStore _taskStore;
    private readonly IAlarmEventService _alarms;
    private readonly ILogger _logger;

    public PositionInboundCoordinator(
        PositionInboundRegistry inbound,
        IRcsTaskStore taskStore,
        IAlarmEventService alarms,
        ILogger logger)
    {
        _inbound = inbound;
        _taskStore = taskStore;
        _alarms = alarms;
        _logger = logger;
    }

    public void OnRcsTaskStatus(object? sender, RcsTaskStatusEvent e)
    {
        if (e.TaskState == RcsTaskState.Canceled)
            RequestClearBySourceTask(e.TaskId, e.TaskState);
    }

    public void RequestClearBySourceTask(string taskId, string reason)
        => _inbound.RequestClearBySourceTask(taskId, reason);

    public Task ApplyPendingClearAsync((long Eq, long Pos) key, CancellationToken ct)
        => _inbound.TryTakePendingClear(key, out var request)
            ? ClearAtAsync(key, request.Reason, ct, request.SourceTaskId)
            : Task.CompletedTask;

    public async Task ReclaimStaleAsync(PositionContext ctx, InboundHandoff? inbound, CancellationToken ct)
    {
        if (inbound is not { } pending) return;
        var key = (ctx.EquipmentId, ctx.PositionId);

        var srcTaskId = pending.SourceTaskId;
        RcsTaskRow? srcRow = null;
        if (!string.IsNullOrEmpty(srcTaskId))
            srcRow = await _taskStore.GetByTaskIdAsync(srcTaskId, ct);

        var reclaim = PositionTransition.DecideInboundReclaim(
            string.IsNullOrEmpty(srcTaskId) || srcRow is null,
            srcRow?.TaskState,
            DateTime.UtcNow - pending.CreatedUtc);

        switch (reclaim.Kind)
        {
            case InboundReclaimKind.Clear:
                await ClearAtAsync(key, reclaim.Reason!, ct);
                break;

            case InboundReclaimKind.WarnOverdue when _inbound.TryWarnOverdueOnce(key):
                _logger.LogWarning("EQ{Eq} POS{Pos} 直送源 {Src} 已 COMPLETED 超 {Min}min 仍无料，保留登记等人工/见料（不清账）",
                    ctx.EquipmentId, ctx.PositionId, srcTaskId,
                    (int)PositionTransition.InboundHandoffCompletedGrace.TotalMinutes);
                await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    $"工序间直送超时：EQ{ctx.EquipmentId} POS{ctx.PositionId} 源任务 {srcTaskId} 已完成但工位无料，物料 {pending.MaterialId ?? "—"}，请核对 AGV/PLC",
                    srcTaskId, ct);
                break;
        }
    }

    public async Task ClearAtAsync((long Eq, long Pos) key, string reason, CancellationToken ct,
        string? expectedSourceTaskId = null)
    {
        InboundHandoff? handoff;
        if (expectedSourceTaskId is null)
        {
            if (!_inbound.TryRemove(key, out handoff)) return;
        }
        else if (!_inbound.TryGet(key, out handoff)
                 || !string.Equals(handoff!.SourceTaskId, expectedSourceTaskId, StringComparison.Ordinal)
                 || !_inbound.TryRemoveExact(key, handoff))
        {
            return;
        }

        _logger.LogWarning("清除工序间交接登记 EQ{Eq} POS{Pos}（源 {Src} 物料 {El}，原因 {Reason}）",
            key.Eq, key.Pos, handoff.SourceTaskId ?? "—", handoff.MaterialId ?? "—", reason);
        try
        {
            await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                $"交接废弃：物料 {handoff.MaterialId ?? "—"} 源任务 {handoff.SourceTaskId ?? "—"} 目标 EQ{key.Eq} POS{key.Pos}，原因 {reason}；未记账，请人工确认实物位置后在料架页校正",
                handoff.SourceTaskId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "交接废弃告警落库失败 EQ{Eq} POS{Pos} 物料 {El}",
                key.Eq, key.Pos, handoff.MaterialId ?? "—");
        }
    }
}
