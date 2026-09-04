using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P0-3：直接实例化真实 <see cref="DashboardViewModel"/>，断言可绑定属性。
/// 无 Application 时 MarshalToUi 走同步路径；不启动真实 Window。
/// </summary>
[TestFixture]
public sealed class DashboardViewModelReconciliationTests
{
    [Test]
    public void 构造时_WaitingForRetry映射到真实绑定属性()
    {
        var scheduler = new FakeScheduler
        {
            State = ReconciliationState.WaitingForRetry,
            FailureReason = "阶段 One：查询失败",
            IsReconciled = false
        };

        using var vm = CreateVm(scheduler);

        Assert.Multiple(() =>
        {
            Assert.That(vm.ReconcileTitle, Is.EqualTo("启动对账失败，正在重试"));
            Assert.That(vm.ReconcileSubText, Does.Contain("自动派工已锁定"));
            Assert.That(vm.ReconcileSubText, Does.Contain("查询失败"));
            Assert.That(vm.ReconcileBrushKey, Is.EqualTo("WarnBrush"));
            Assert.That(vm.IsReconcileGateOpen, Is.False);
            Assert.That(vm.ReconcileDetailToolTip, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void 状态事件切换到Succeeded_真实属性更新且旧原因不残留()
    {
        var scheduler = new FakeScheduler
        {
            State = ReconciliationState.WaitingForRetry,
            FailureReason = "阶段 Two：旧失败",
            IsReconciled = false
        };
        using var vm = CreateVm(scheduler);

        Assert.That(vm.ReconcileSubText, Does.Contain("旧失败"));

        scheduler.Publish(ReconciliationState.Succeeded, null, true);

        Assert.Multiple(() =>
        {
            Assert.That(vm.ReconcileTitle, Is.EqualTo("启动对账完成"));
            Assert.That(vm.ReconcileSubText, Is.EqualTo("自动派工已开启"));
            Assert.That(vm.ReconcileBrushKey, Is.EqualTo("OkBrush"));
            Assert.That(vm.IsReconcileGateOpen, Is.True);
            Assert.That(vm.ReconcileDetailToolTip, Is.Null);
            Assert.That(vm.ReconcileSubText, Does.Not.Contain("旧失败"));
        });
    }

    [Test]
    public void Dispose后_不再响应状态事件()
    {
        var scheduler = new FakeScheduler
        {
            State = ReconciliationState.NotStarted,
            IsReconciled = false
        };
        var vm = CreateVm(scheduler);
        Assert.That(vm.ReconcileTitle, Is.EqualTo("启动对账未开始"));

        vm.Dispose();
        scheduler.Publish(ReconciliationState.WaitingForRetry, "阶段 One：不应更新", false);

        Assert.Multiple(() =>
        {
            Assert.That(vm.ReconcileTitle, Is.EqualTo("启动对账未开始"));
            Assert.That(vm.ReconcileSubText, Does.Not.Contain("不应更新"));
            Assert.That(vm.IsReconcileGateOpen, Is.False);
        });
    }

    private static DashboardViewModel CreateVm(FakeScheduler scheduler)
        => new(
            new SignalStateStore(),
            new FakeWorkRecords(),
            new FakeAlarms(),
            scheduler,
            new FakeFrames(),
            new FakeWorkLines(),
            new FakeUser(),
            new Tests.UI.RecordingNotify(),
            new Tests.UI.ImmediateUiDispatcher());

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

    private sealed class FakeUser : ICurrentUser
    {
        public string Name => "test";
    }

    private sealed class FakeWorkLines : IWorkLineService
    {
        public event EventHandler? WorkLinesChanged
        {
            add { }
            remove { }
        }
        public Task<IReadOnlyList<WorkLineListItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkLineListItem>>(Array.Empty<WorkLineListItem>());
        public Task<WorkLineEditModel?> GetByIdAsync(long id, CancellationToken ct = default) => Task.FromResult<WorkLineEditModel?>(null);
        public Task<long> SaveAsync(WorkLineEditModel model, string author, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeFrames : IFrameService
    {
        public Task<IReadOnlyList<FrameListItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameListItem>>(Array.Empty<FrameListItem>());
        public Task<FrameDetail?> GetDetailAsync(long frameId, CancellationToken ct = default) => Task.FromResult<FrameDetail?>(null);
        public Task<long> CreateFrameAsync(FrameCreateModel model, string author, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<NamedOption>> GetEquipmentOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task BindEquipmentAsync(long frameId, long equipmentId, string roleCode, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnbindAsync(long bindId, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<FrameEditModel?> GetFrameForEditAsync(long id, CancellationToken ct = default) => Task.FromResult<FrameEditModel?>(null);
        public Task UpdateFrameAsync(FrameEditModel model, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeleteCheckResult> CheckDeleteFrameAsync(long id, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteFrameAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWorkRecords : IWorkRecordService
    {
        public Task<long> RecordStartAsync(WorkRecordStartArgs args, CancellationToken ct = default) => Task.FromResult(0L);
        public Task RecordResultAsync(long recordId, string result, string? remark, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkRecordRow?> FindOpenByPositionAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<WorkRecordRow?>(null);
        public Task<IReadOnlyList<WorkRecordRow>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkRecordRow>>(Array.Empty<WorkRecordRow>());
        public Task<WorkShiftStats> GetShiftStatsAsync(CancellationToken ct = default) => Task.FromResult(new WorkShiftStats(0, 0, 0));
    }

    private sealed class FakeAlarms : IAlarmEventService
    {
        public event EventHandler<AlarmRow>? AlarmRaised
        {
            add { }
            remove { }
        }
        public event EventHandler? AlarmsChanged
        {
            add { }
            remove { }
        }
        public Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent, string? taskCode, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task MarkHandledAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> DeleteAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> GetUnhandledCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
