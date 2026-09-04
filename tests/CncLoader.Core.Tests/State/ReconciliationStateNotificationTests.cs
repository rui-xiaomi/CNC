using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

/// <summary>P0-3：对账状态变化通知契约（纯逻辑）。</summary>
[TestFixture]
public sealed class ReconciliationStateNotificationTests
{
    [Test]
    public void TryPublish_状态序列应依次通知()
    {
        var pub = new ReconciliationStatePublisher();
        var states = new List<ReconciliationState>();
        pub.Changed += (_, s) => states.Add(s.State);

        Assert.That(pub.TryPublish(ReconciliationState.Reconciling, null, false), Is.True);
        Assert.That(pub.TryPublish(ReconciliationState.WaitingForRetry, "阶段 One：失败A", false), Is.True);
        Assert.That(pub.TryPublish(ReconciliationState.Reconciling, "阶段 One：失败A", false), Is.True);
        Assert.That(pub.TryPublish(ReconciliationState.Succeeded, null, true), Is.True);

        Assert.That(states, Is.EqualTo(new[]
        {
            ReconciliationState.Reconciling,
            ReconciliationState.WaitingForRetry,
            ReconciliationState.Reconciling,
            ReconciliationState.Succeeded
        }));
    }

    [Test]
    public void TryPublish_相同快照不重复通知()
    {
        var pub = new ReconciliationStatePublisher();
        var count = 0;
        pub.Changed += (_, _) => count++;

        Assert.That(pub.TryPublish(ReconciliationState.WaitingForRetry, "原因X", false), Is.True);
        Assert.That(pub.TryPublish(ReconciliationState.WaitingForRetry, "原因X", false), Is.False);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void TryPublish_连续失败原因更新时应通知()
    {
        var pub = new ReconciliationStatePublisher();
        var reasons = new List<string?>();
        pub.Changed += (_, s) => reasons.Add(s.FailureReason);

        pub.TryPublish(ReconciliationState.WaitingForRetry, "阶段 One：A", false);
        Assert.That(pub.TryPublish(ReconciliationState.WaitingForRetry, "阶段 One：B", false), Is.True);

        Assert.That(reasons, Is.EqualTo(new[] { "阶段 One：A", "阶段 One：B" }));
    }

    [Test]
    public void TryPublish_成功后应清空失败原因并通知()
    {
        var pub = new ReconciliationStatePublisher();
        ReconciliationSnapshot? last = null;
        pub.Changed += (_, s) => last = s;

        pub.TryPublish(ReconciliationState.WaitingForRetry, "阶段 Two：失败", false);
        pub.TryPublish(ReconciliationState.Succeeded, null, true);

        Assert.Multiple(() =>
        {
            Assert.That(last!.State, Is.EqualTo(ReconciliationState.Succeeded));
            Assert.That(last.FailureReason, Is.Null);
            Assert.That(last.IsReconciled, Is.True);
            Assert.That(pub.Current.FailureReason, Is.Null);
        });
    }

    [Test]
    public void TryPublish_取消路径不发布失败快照()
    {
        // 取消由调度器不调用 TryPublish(WaitingForRetry) 保证；此处断言：仅 Reconciling 后无失败发布
        var pub = new ReconciliationStatePublisher();
        var snapshots = new List<ReconciliationSnapshot>();
        pub.Changed += (_, s) => snapshots.Add(s);

        pub.TryPublish(ReconciliationState.Reconciling, null, false);
        // 模拟取消：不再 Publish WaitingForRetry

        Assert.Multiple(() =>
        {
            Assert.That(snapshots, Has.Count.EqualTo(1));
            Assert.That(snapshots[0].State, Is.EqualTo(ReconciliationState.Reconciling));
            Assert.That(snapshots.Any(s => s.State == ReconciliationState.WaitingForRetry), Is.False);
            Assert.That(pub.Current.FailureReason, Is.Null);
        });
    }

    [Test]
    public void TryPublish_订阅方异常不得阻断后续通知()
    {
        var pub = new ReconciliationStatePublisher();
        var good = 0;
        pub.Changed += (_, _) => throw new InvalidOperationException("坏订阅方");
        pub.Changed += (_, _) => good++;

        Assert.DoesNotThrow(() => pub.TryPublish(ReconciliationState.Reconciling, null, false));
        Assert.That(good, Is.EqualTo(1));
        Assert.DoesNotThrow(() => pub.TryPublish(ReconciliationState.Succeeded, null, true));
        Assert.That(good, Is.EqualTo(2));
    }

    [Test]
    public void Map_WaitingForRetry_应含锁定文案与Warn语义()
    {
        var mapped = ReconciliationStatusPresentation.Map(
            new ReconciliationSnapshot(ReconciliationState.WaitingForRetry, "阶段 One：查询失败", false));

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Title, Is.EqualTo("启动对账失败，正在重试"));
            Assert.That(mapped.SubText, Does.Contain("自动派工已锁定"));
            Assert.That(mapped.SubText, Does.Contain("查询失败"));
            Assert.That(mapped.BrushKey, Is.EqualTo("WarnBrush"));
            Assert.That(mapped.IsGateOpen, Is.False);
        });
    }

    [Test]
    public void Map_Succeeded_应开启派工且不残留失败原因()
    {
        var mapped = ReconciliationStatusPresentation.Map(
            new ReconciliationSnapshot(ReconciliationState.Succeeded, null, true));

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Title, Is.EqualTo("启动对账完成"));
            Assert.That(mapped.SubText, Is.EqualTo("自动派工已开启"));
            Assert.That(mapped.BrushKey, Is.EqualTo("OkBrush"));
            Assert.That(mapped.DetailToolTip, Is.Null);
            Assert.That(mapped.IsGateOpen, Is.True);
        });
    }

    [Test]
    public void Map_Disabled_应明示调度未启用且不开闸()
    {
        var mapped = ReconciliationStatusPresentation.Map(
            new ReconciliationSnapshot(ReconciliationState.Disabled, null, false));

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Title, Is.EqualTo("调度器未启用"));
            Assert.That(mapped.SubText, Does.Contain("SchedulerEnabled=false"));
            Assert.That(mapped.BrushKey, Is.EqualTo("IdleBrush"));
            Assert.That(mapped.IsGateOpen, Is.False);
        });
    }

    [Test]
    public void SanitizeDisplayReason_应去掉异常类型名与敏感连接信息()
    {
        var cleaned = ReconciliationStatusPresentation.SanitizeDisplayReason(
            "阶段 One：InvalidOperationException：DB fail Password=Secret");

        Assert.Multiple(() =>
        {
            Assert.That(cleaned, Does.Not.Contain("InvalidOperationException"));
            Assert.That(cleaned, Does.Not.Contain("Secret"));
            Assert.That(cleaned, Does.Not.Contain("Password="));
        });
    }
}
