using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

[TestFixture]
public sealed class HasMatRecheckTrackerTests
{
    [TestCase(PositionPhase.Unload, 0)]
    [TestCase(PositionPhase.Upload, 1)]
    public void 带错误的原始值应判为未知(PositionPhase phase, int rawValue)
    {
        var tracker = new HasMatRecheckTracker();

        var result = tracker.Evaluate("task-1", phase,
            HasMatReading.From(rawValue, onValue: 1, error: "PLC 读取失败"), threshold: 6);

        Assert.That(result.Decision, Is.EqualTo(HasMatRecheckDecision.Hold));
        Assert.That(result.NextState, Is.EqualTo(PositionState.Transporting));
    }

    [Test]
    public void 阈值前连续未知应保持运输态并显示复核进度()
    {
        var tracker = new HasMatRecheckTracker();

        for (var count = 1; count <= 5; count++)
        {
            var result = tracker.Evaluate("task-1", PositionPhase.Upload, null, threshold: 6);

            Assert.Multiple(() =>
            {
                Assert.That(result.Decision, Is.EqualTo(HasMatRecheckDecision.Hold));
                Assert.That(result.NextState, Is.EqualTo(PositionState.Transporting));
                Assert.That(result.FailureCount, Is.EqualTo(count));
                Assert.That(result.StatusDetail, Is.EqualTo($"复核中（{count}/6）"));
                Assert.That(result.PermitsTestStart, Is.False);
                Assert.That(result.PermitsSlotSettlement, Is.False);
            });
        }
    }

    [Test]
    public void 第六次连续未知应只首次触发Alarm()
    {
        var tracker = new HasMatRecheckTracker();
        HasMatRecheckResult result = default;

        for (var count = 1; count <= 6; count++)
            result = tracker.Evaluate("task-1", PositionPhase.Upload, null, threshold: 6);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(HasMatRecheckDecision.Alarm));
            Assert.That(result.NextState, Is.EqualTo(PositionState.Alarm));
            Assert.That(result.AlarmRaisedNow, Is.True);
        });

        var repeated = tracker.Evaluate("task-1", PositionPhase.Upload, null, threshold: 6);
        Assert.That(repeated.AlarmRaisedNow, Is.False);
    }

    [TestCase(PositionPhase.Upload, true, PositionState.Loaded)]
    [TestCase(PositionPhase.Unload, false, PositionState.Unloaded)]
    public void 阈值内读到符合预期的明确值应清零并推进(
        PositionPhase phase, bool hasMat, PositionState expectedState)
    {
        var tracker = new HasMatRecheckTracker();
        tracker.Evaluate("task-1", phase, null, threshold: 6);
        tracker.Evaluate("task-1", phase, null, threshold: 6);

        var result = tracker.Evaluate("task-1", phase, hasMat, threshold: 6);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(HasMatRecheckDecision.Confirmed));
            Assert.That(result.NextState, Is.EqualTo(expectedState));
            Assert.That(result.FailureCount, Is.Zero);
            Assert.That(result.StatusDetail, Is.Null);
        });
    }

    [TestCase(PositionPhase.Upload, false)]
    [TestCase(PositionPhase.Unload, true)]
    public void 明确值不符应立即Alarm(PositionPhase phase, bool hasMat)
    {
        var tracker = new HasMatRecheckTracker();

        var result = tracker.Evaluate("task-1", phase, hasMat, threshold: 6);

        Assert.Multiple(() =>
        {
            Assert.That(result.Decision, Is.EqualTo(HasMatRecheckDecision.Alarm));
            Assert.That(result.NextState, Is.EqualTo(PositionState.Alarm));
            Assert.That(result.AlarmRaisedNow, Is.True);
        });
    }

    [Test]
    public void 新任务不应继承旧任务失败计数()
    {
        var tracker = new HasMatRecheckTracker();
        for (var count = 0; count < 5; count++)
            tracker.Evaluate("task-old", PositionPhase.Upload, null, threshold: 6);

        var result = tracker.Evaluate("task-new", PositionPhase.Upload, null, threshold: 6);

        Assert.That(result.FailureCount, Is.EqualTo(1));
        Assert.That(result.Decision, Is.EqualTo(HasMatRecheckDecision.Hold));
    }

    [Test]
    public void 离开运输态后旧失败计数不应影响后续任务()
    {
        var tracker = new HasMatRecheckTracker();
        for (var count = 0; count < 5; count++)
            tracker.Evaluate("task-1", PositionPhase.Upload, null, threshold: 6);

        tracker.Reset();
        var result = tracker.Evaluate("task-1", PositionPhase.Upload, null, threshold: 6);

        Assert.That(result.FailureCount, Is.EqualTo(1));
        Assert.That(result.Decision, Is.EqualTo(HasMatRecheckDecision.Hold));
    }

    [Test]
    public void 上下料使用同一阈值但成功判据相反()
    {
        var upload = new HasMatRecheckTracker();
        var unload = new HasMatRecheckTracker();

        var uploadResult = upload.Evaluate("upload", PositionPhase.Upload, true, threshold: 3);
        var unloadResult = unload.Evaluate("unload", PositionPhase.Unload, false, threshold: 3);

        Assert.Multiple(() =>
        {
            Assert.That(uploadResult.NextState, Is.EqualTo(PositionState.Loaded));
            Assert.That(unloadResult.NextState, Is.EqualTo(PositionState.Unloaded));
        });
    }

    [Test]
    public void 达到Alarm后读数恢复也不得自动推进()
    {
        var tracker = new HasMatRecheckTracker();
        tracker.Evaluate("task-1", PositionPhase.Upload, null, threshold: 2);
        tracker.Evaluate("task-1", PositionPhase.Upload, null, threshold: 2);

        var result = tracker.Evaluate("task-1", PositionPhase.Upload, true, threshold: 2);

        Assert.That(result.Decision, Is.EqualTo(HasMatRecheckDecision.Alarm));
        Assert.That(result.NextState, Is.EqualTo(PositionState.Alarm));
    }
}
