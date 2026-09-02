using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

[TestFixture]
public sealed class SlotSettlementTests
{
    [Test]
    public void 上料完成且有料_取料落账()
    {
        var action = SlotSettlement.Decide(PositionPhase.Upload, RcsTaskState.Completed, hasMat: true, plcCheckApplicable: true);
        Assert.That(action, Is.EqualTo(SlotSettlementAction.ConfirmTake));
    }

    [Test]
    public void 下料完成且无料_入库落账()
    {
        var action = SlotSettlement.Decide(PositionPhase.Unload, RcsTaskState.Completed, hasMat: false, plcCheckApplicable: true);
        Assert.That(action, Is.EqualTo(SlotSettlementAction.ConfirmPut));
    }

    [Test]
    public void 上料完成但明确无料_回滚取料预记()
    {
        var action = SlotSettlement.Decide(PositionPhase.Upload, RcsTaskState.Completed, hasMat: false, plcCheckApplicable: true);
        Assert.That(action, Is.EqualTo(SlotSettlementAction.RollbackTake));
    }

    [Test]
    public void 下料完成但明确仍有料_回滚入库预记()
    {
        var action = SlotSettlement.Decide(PositionPhase.Unload, RcsTaskState.Completed, hasMat: true, plcCheckApplicable: true);
        Assert.That(action, Is.EqualTo(SlotSettlementAction.RollbackPut));
    }

    [TestCase(PositionPhase.Upload)]
    [TestCase(PositionPhase.Unload)]
    public void 完成但HasMat未知_不动账(PositionPhase phase)
    {
        var action = SlotSettlement.Decide(phase, RcsTaskState.Completed, hasMat: null, plcCheckApplicable: true);
        Assert.That(action, Is.EqualTo(SlotSettlementAction.Hold));
    }

    [Test]
    public void 无加工位可核_完成则按方向落账()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                SlotSettlement.Decide(PositionPhase.Upload, RcsTaskState.Completed, hasMat: null, plcCheckApplicable: false),
                Is.EqualTo(SlotSettlementAction.ConfirmTake));
            Assert.That(
                SlotSettlement.Decide(PositionPhase.Unload, RcsTaskState.Completed, hasMat: null, plcCheckApplicable: false),
                Is.EqualTo(SlotSettlementAction.ConfirmPut));
        });
    }

    [TestCase(RcsTaskState.Canceled, PositionPhase.Upload, SlotSettlementAction.RollbackTake)]
    [TestCase(RcsTaskState.Failed, PositionPhase.Upload, SlotSettlementAction.RollbackTake)]
    [TestCase(RcsTaskState.Canceled, PositionPhase.Unload, SlotSettlementAction.RollbackPut)]
    [TestCase(RcsTaskState.Failed, PositionPhase.Unload, SlotSettlementAction.RollbackPut)]
    public void 失败或取消_按方向回滚(string state, PositionPhase phase, SlotSettlementAction expected)
    {
        var action = SlotSettlement.Decide(phase, state, hasMat: null, plcCheckApplicable: true);
        Assert.That(action, Is.EqualTo(expected));
    }

    [Test]
    public void 非终态_不动账()
    {
        var action = SlotSettlement.Decide(PositionPhase.Upload, RcsTaskState.Executing, hasMat: true, plcCheckApplicable: true);
        Assert.That(action, Is.EqualTo(SlotSettlementAction.Hold));
    }
}
