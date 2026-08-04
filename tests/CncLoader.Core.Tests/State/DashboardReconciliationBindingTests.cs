using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P0-3：看板对账状态绑定（DashboardViewModel 所用 ReconciliationStatusBinder）。
/// 纯逻辑、无 STA / 无真实 Window。
/// </summary>
[TestFixture]
public sealed class DashboardReconciliationBindingTests
{
    [Test]
    public void 创建时_应读取scheduler当前状态()
    {
        var scheduler = new FakeScheduler
        {
            State = ReconciliationState.WaitingForRetry,
            FailureReason = "阶段 One：首次失败",
            IsReconciled = false
        };

        using var binder = new ReconciliationStatusBinder(scheduler);

        Assert.Multiple(() =>
        {
            Assert.That(binder.Title, Is.EqualTo("启动对账失败，正在重试"));
            Assert.That(binder.SubText, Does.Contain("自动派工已锁定"));
            Assert.That(binder.SubText, Does.Contain("首次失败"));
            Assert.That(binder.BrushKey, Is.EqualTo("WarnBrush"));
            Assert.That(binder.IsGateOpen, Is.False);
        });
    }

    [Test]
    public void WaitingForRetry_副文案应含锁定与安全原因()
    {
        var scheduler = new FakeScheduler
        {
            State = ReconciliationState.WaitingForRetry,
            FailureReason = "阶段 Two：InvalidOperationException：回滚失败 Password=Hidden",
            IsReconciled = false
        };

        using var binder = new ReconciliationStatusBinder(scheduler);

        Assert.Multiple(() =>
        {
            Assert.That(binder.SubText, Does.Contain("自动派工已锁定"));
            Assert.That(binder.SubText, Does.Not.Contain("Password="));
            Assert.That(binder.SubText, Does.Not.Contain("Hidden"));
            Assert.That(binder.SubText, Does.Not.Contain("InvalidOperationException"));
            Assert.That(binder.DetailToolTip, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void Succeeded_应映射为自动派工已开启且旧原因不残留()
    {
        var scheduler = new FakeScheduler
        {
            State = ReconciliationState.WaitingForRetry,
            FailureReason = "阶段 One：旧失败",
            IsReconciled = false
        };
        using var binder = new ReconciliationStatusBinder(scheduler);

        scheduler.Publish(ReconciliationState.Succeeded, null, true);

        Assert.Multiple(() =>
        {
            Assert.That(binder.Title, Is.EqualTo("启动对账完成"));
            Assert.That(binder.SubText, Is.EqualTo("自动派工已开启"));
            Assert.That(binder.BrushKey, Is.EqualTo("OkBrush"));
            Assert.That(binder.DetailToolTip, Is.Null);
            Assert.That(binder.IsGateOpen, Is.True);
            Assert.That(binder.SubText, Does.Not.Contain("旧失败"));
        });
    }

    [Test]
    public void 后台状态事件_应触发绑定属性更新()
    {
        var scheduler = new FakeScheduler();
        var changes = 0;
        using var binder = new ReconciliationStatusBinder(scheduler);
        binder.Changed += (_, _) => changes++;

        scheduler.Publish(ReconciliationState.Reconciling, null, false);
        scheduler.Publish(ReconciliationState.WaitingForRetry, "阶段 Three：PLC 核对失败", false);

        Assert.Multiple(() =>
        {
            Assert.That(changes, Is.EqualTo(2));
            Assert.That(binder.Title, Is.EqualTo("启动对账失败，正在重试"));
            Assert.That(binder.SubText, Does.Contain("PLC 核对失败"));
        });
    }

    [Test]
    public void Dispose后_不再响应事件()
    {
        var scheduler = new FakeScheduler();
        var binder = new ReconciliationStatusBinder(scheduler);
        var changes = 0;
        binder.Changed += (_, _) => changes++;

        binder.Dispose();
        scheduler.Publish(ReconciliationState.WaitingForRetry, "不应更新", false);

        Assert.Multiple(() =>
        {
            Assert.That(changes, Is.Zero);
            Assert.That(binder.Title, Is.EqualTo("启动对账未开始"));
        });
    }

    private sealed class FakeScheduler : IPositionScheduler
    {
        public bool IsReconciled { get; set; }
        public ReconciliationState State { get; set; } = ReconciliationState.NotStarted;
        public string? FailureReason { get; set; }
        public ReconciliationState ReconciliationState => State;
        public string? ReconciliationFailureReason => FailureReason;
        public bool IsAutoDispatchPaused => false;

        public event EventHandler? Reconciled
        {
            add { }
            remove { }
        }
        public event EventHandler<ReconciliationSnapshot>? ReconciliationStateChanged;

        public void Publish(ReconciliationState state, string? reason, bool isReconciled)
        {
            State = state;
            FailureReason = reason;
            IsReconciled = isReconciled;
            ReconciliationStateChanged?.Invoke(this, new ReconciliationSnapshot(state, reason, isReconciled));
        }

        public void SetAutoDispatchPaused(bool paused) { }
        public Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public void InvalidateFrameBindingCache(long? equipmentId = null) { }
    }
}
