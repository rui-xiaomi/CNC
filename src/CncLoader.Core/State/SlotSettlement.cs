using CncLoader.Core.Rcs;

namespace CncLoader.Core.State;

/// <summary>RCS 终态后对预记槽位的收口动作。</summary>
public enum SlotSettlementAction
{
    /// <summary>HasMat 未知：不落账、不回滚，等下一轮 PLC 可读。</summary>
    Hold,
    ConfirmTake,
    ConfirmPut,
    RollbackTake,
    RollbackPut
}

/// <summary>
/// 槽位落账决策：COMPLETED 且 PLC 符合阶段预期才 Confirm；明确不符或任务失败/取消才 Rollback；
/// HasMat 未知不得落账也不得回滚。未绑加工位（无 PLC 可核）时 COMPLETED 按方向直接落账。
/// </summary>
public static class SlotSettlement
{
    public static SlotSettlementAction Decide(
        PositionPhase phase,
        string rcsTaskState,
        bool? hasMat,
        bool plcCheckApplicable)
    {
        if (rcsTaskState is RcsTaskState.Canceled or RcsTaskState.Failed)
            return Rollback(phase);

        if (rcsTaskState != RcsTaskState.Completed)
            return SlotSettlementAction.Hold;

        if (!plcCheckApplicable)
            return Confirm(phase);

        if (hasMat is null)
            return SlotSettlementAction.Hold;

        var matches = phase == PositionPhase.Upload ? hasMat.Value : !hasMat.Value;
        return matches ? Confirm(phase) : Rollback(phase);
    }

    private static SlotSettlementAction Confirm(PositionPhase phase)
        => phase == PositionPhase.Upload ? SlotSettlementAction.ConfirmTake : SlotSettlementAction.ConfirmPut;

    private static SlotSettlementAction Rollback(PositionPhase phase)
        => phase == PositionPhase.Upload ? SlotSettlementAction.RollbackTake : SlotSettlementAction.RollbackPut;
}
