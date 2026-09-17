using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

[TestFixture]
public sealed class SignalStateStoreTests
{
    [Test]
    public void UpdatePosition_MaterialIdChange_RaisesPositionChanged()
    {
        var store = new SignalStateStore();
        var raised = 0;
        store.PositionChanged += (_, _) => raised++;

        store.UpdatePosition(new PositionStatus
        {
            EquipmentId = 1, PositionId = 1, State = PositionState.Alarm, MaterialId = "M-1"
        });
        store.UpdatePosition(new PositionStatus
        {
            EquipmentId = 1, PositionId = 1, State = PositionState.Alarm, MaterialId = null
        });

        Assert.That(raised, Is.EqualTo(2),
            "物料码变化必须通知看板；只比 State 会导致置空后工位卡仍显示旧码");
    }

    [Test]
    public void UpdatePosition_SameStateAndMaterial_DoesNotRaise()
    {
        var store = new SignalStateStore();
        var raised = 0;
        store.PositionChanged += (_, _) => raised++;

        store.UpdatePosition(new PositionStatus
        {
            EquipmentId = 1, PositionId = 1, State = PositionState.WaitLoad, MaterialId = null
        });
        store.UpdatePosition(new PositionStatus
        {
            EquipmentId = 1, PositionId = 1, State = PositionState.WaitLoad, MaterialId = null
        });

        Assert.That(raised, Is.EqualTo(1));
    }
}
