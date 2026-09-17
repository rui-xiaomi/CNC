using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 运行期陈旧预记 sweep、COMPLETED 槽位 PLC 门复核与统一 settlement。
/// HasMat 未知时 Hold：不落账也不回滚。
/// </summary>
internal sealed class PositionSlotMaintenance
{
    private static readonly TimeSpan StaleReservationSweepInterval = TimeSpan.FromSeconds(60);

    private readonly IRcsTaskStore _taskStore;
    private readonly ISlotAccountService _slots;
    private readonly IAlarmEventService _alarms;
    private readonly Func<TimeSpan> _staleReservationGrace;
    private readonly Func<PositionContext, CancellationToken, Task<bool?>> _readHasMatFresh;
    private readonly ILogger _logger;
    private DateTime _lastStaleReservationSweep = DateTime.MinValue;

    public PositionSlotMaintenance(
        IRcsTaskStore taskStore,
        ISlotAccountService slots,
        IAlarmEventService alarms,
        Func<TimeSpan> staleReservationGrace,
        Func<PositionContext, CancellationToken, Task<bool?>> readHasMatFresh,
        ILogger logger)
    {
        _taskStore = taskStore;
        _slots = slots;
        _alarms = alarms;
        _staleReservationGrace = staleReservationGrace;
        _readHasMatFresh = readHasMatFresh;
        _logger = logger;
    }

    /// <summary>终态槽位收口：政策见 <see cref="SlotSettlement.Decide"/>。</summary>
    public async Task<SlotSettlementAction> SettleSlotForTerminalAsync(
        string taskId, PositionPhase phase, string state, bool? hasMat, bool plcCheckApplicable, CancellationToken ct)
    {
        var action = SlotSettlement.Decide(phase, state, hasMat, plcCheckApplicable);
        await ApplySlotSettlementAsync(action, taskId, ct);
        return action;
    }

    public async Task<bool> ApplySlotSettlementAsync(SlotSettlementAction action, string taskId, CancellationToken ct)
        => action switch
        {
            SlotSettlementAction.Hold => false,
            SlotSettlementAction.ConfirmTake => await _slots.ConfirmTakeAsync(taskId, ct),
            SlotSettlementAction.ConfirmPut => await _slots.ConfirmAsync(taskId, ct),
            SlotSettlementAction.RollbackTake => await _slots.RollbackTakeAsync(taskId, ct),
            SlotSettlementAction.RollbackPut => await _slots.RollbackAsync(taskId, ct),
            _ => false
        };

    /// <summary>运行期定期：回滚非 COMPLETED 陈旧预记；对 COMPLETED 预记按 PLC 门补 Confirm。</summary>
    public async Task SweepStaleReservationsIfDueAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastStaleReservationSweep < StaleReservationSweepInterval) return;
        _lastStaleReservationSweep = DateTime.UtcNow;
        try
        {
            var unfinished = await _taskStore.GetUnfinishedTaskIdsAsync(ct);
            var n = await _slots.RollbackStaleReservationsAsync(unfinished, _staleReservationGrace(), ct);
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
    public async Task<int> SettleCompletedPendingWithPlcAsync(IReadOnlyCollection<string> unfinished, CancellationToken ct)
    {
        var pending = await _slots.ListCompletedPendingConfirmAsync(unfinished, ct);
        if (pending.Count == 0) return 0;

        var settled = 0;
        foreach (var item in pending)
        {
            var row = await _taskStore.GetByTaskIdAsync(item.TaskId, ct);
            if (row is null) continue;

            var phase = item.IsTake ? PositionPhase.Upload : PositionPhase.Unload;
            var plcCheckApplicable = row.EquipmentId is long && row.PositionId is long;
            bool? hasMat = null;
            if (plcCheckApplicable)
            {
                var tmp = new PositionContext { EquipmentId = row.EquipmentId!.Value, PositionId = row.PositionId!.Value };
                hasMat = await _readHasMatFresh(tmp, ct);
            }

            var action = SlotSettlement.Decide(phase, RcsTaskState.Completed, hasMat, plcCheckApplicable);
            if (action == SlotSettlementAction.Hold)
            {
                _logger.LogDebug("COMPLETED 预记 {Task} HasMat 未知，跳过本轮", item.TaskId);
                continue;
            }

            if (!await ApplySlotSettlementAsync(action, item.TaskId, ct))
                continue;

            settled++;
            if (action is SlotSettlementAction.RollbackTake or SlotSettlementAction.RollbackPut)
            {
                var detail = item.IsTake ? "工位无料，取料预记已回滚" : "工位仍有料，入库预记已回滚";
                _logger.LogWarning("COMPLETED 预记 {Task} PLC 不符 → 回滚（假完成/未到位）", item.TaskId);
                await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    $"任务 {item.TaskId} COMPLETED 但{detail}，请核对", item.TaskId, ct);
            }
        }
        return settled;
    }
}
