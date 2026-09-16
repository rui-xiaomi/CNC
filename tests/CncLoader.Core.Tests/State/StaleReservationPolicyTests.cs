using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

[TestFixture]
public sealed class StaleReservationPolicyTests
{
    [Test]
    public void 默认10秒3次_宽限覆盖整段下发耗时()
    {
        // 10s×3 + 退避(300+600) + 30s 余量 = 60.9s，低于下限取 2min
        var grace = StaleReservationPolicy.ComputeGrace(10_000, 3);
        Assert.That(grace, Is.EqualTo(StaleReservationPolicy.MinimumGrace));
    }

    [Test]
    public void 超时加大_宽限随之增大()
    {
        // 60s×3 + 900ms + 30s = 210.9s
        var grace = StaleReservationPolicy.ComputeGrace(60_000, 3);
        Assert.That(grace, Is.EqualTo(TimeSpan.FromMilliseconds(210_900)));
    }

    [Test]
    public void 非法参数按RcsClient同口径钳制_不产生零或负宽限()
    {
        Assert.Multiple(() =>
        {
            Assert.That(StaleReservationPolicy.ComputeGrace(0, 0), Is.EqualTo(StaleReservationPolicy.MinimumGrace));
            Assert.That(StaleReservationPolicy.ComputeGrace(-1, -5), Is.EqualTo(StaleReservationPolicy.MinimumGrace));
            Assert.That(StaleReservationPolicy.ComputeGrace(1000, int.MaxValue), Is.GreaterThan(StaleReservationPolicy.MinimumGrace));
        });
    }

    [Test]
    public void 查无_Claim后旧DispatchTime不得按可见延迟立即到期()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0);
        var send = now.AddSeconds(-10);
        var oldDispatch = now.AddMinutes(-20);
        var visibility = TimeSpan.FromSeconds(63);
        var grace = TimeSpan.FromMinutes(2);

        Assert.That(
            StaleReservationPolicy.IsQueryNotFoundDue(now, send, oldDispatch, visibility, grace),
            Is.False,
            "SEND_TIME 已刷新、DISPATCH_TIME 仍是首发：须走下发宽限，不能用旧 DISPATCH_TIME 判查无");
    }

    [Test]
    public void 查无_重发已回写DispatchTime且超可见延迟_到期()
    {
        var now = new DateTime(2026, 9, 16, 12, 0, 0);
        var send = now.AddSeconds(-80);
        var dispatch = now.AddSeconds(-70);
        var visibility = TimeSpan.FromSeconds(63);
        var grace = TimeSpan.FromMinutes(2);

        Assert.That(
            StaleReservationPolicy.IsQueryNotFoundDue(now, send, dispatch, visibility, grace),
            Is.True);
    }
}
