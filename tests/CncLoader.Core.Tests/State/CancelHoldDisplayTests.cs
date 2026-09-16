using CncLoader.Core.State;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.State;

[TestFixture]
public sealed class CancelHoldDisplayTests
{
    [Test]
    public void Format_单张带任务号_多张带等N单()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CancelHoldDisplay.Format(Array.Empty<string>()), Is.EqualTo("待确认取消"));
            Assert.That(CancelHoldDisplay.Format(new[] { "T-1" }), Is.EqualTo("待确认取消 T-1"));
            Assert.That(CancelHoldDisplay.Format(new[] { "T-2", "T-1" }), Is.EqualTo("待确认取消 T-2 等2单"));
        });
    }

    [Test]
    public void TryParseTaskId_取出第一张号()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CancelHoldDisplay.TryParseTaskId(null), Is.Null);
            Assert.That(CancelHoldDisplay.TryParseTaskId("等待上料"), Is.Null);
            Assert.That(CancelHoldDisplay.TryParseTaskId("待确认取消"), Is.Null);
            Assert.That(CancelHoldDisplay.TryParseTaskId("待确认取消 LINE01-GR-1"),
                Is.EqualTo("LINE01-GR-1"));
            Assert.That(CancelHoldDisplay.TryParseTaskId("待确认取消 LINE01-GR-1 等2单"),
                Is.EqualTo("LINE01-GR-1"));
        });
    }

    [Test]
    public void 工位卡_未确认取消时HasCancelHold()
    {
        var hold = new PositionCardVm(1, 2, PositionState.WaitLoad, null,
            CancelHoldDisplay.Format(new[] { "T-1" }), true, true, false);
        var idle = new PositionCardVm(1, 1, PositionState.WaitLoad, null, null, true, true, false);

        Assert.Multiple(() =>
        {
            Assert.That(hold.HasCancelHold, Is.True);
            Assert.That(idle.HasCancelHold, Is.False);
        });
    }
}
