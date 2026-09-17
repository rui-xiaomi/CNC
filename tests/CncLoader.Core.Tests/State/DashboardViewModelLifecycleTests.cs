using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.State;

[TestFixture]
public sealed class DashboardViewModelLifecycleTests
{
    [Test]
    public async Task Dispose后_AlarmsChanged不再刷新列表()
    {
        var alarms = new RecordingAlarms();
        var workLines = new RecordingWorkLines();
        using var vm = CreateVm(alarms, workLines);
        await Task.Delay(50);
        alarms.ResetCalls();
        workLines.ResetCalls();

        vm.Dispose();
        alarms.RaiseChanged();
        alarms.RaiseAlarm();
        workLines.RaiseChanged();
        await Task.Delay(50);

        Assert.Multiple(() =>
        {
            Assert.That(alarms.GetAlarmsCalls, Is.Zero, "Dispose 后不得再拉告警");
            Assert.That(workLines.GetAllCalls, Is.Zero, "Dispose 后不得再刷线体");
        });
    }

    private static DashboardViewModel CreateVm(IAlarmEventService alarms, IWorkLineService workLines)
        => new(
            new SignalStateStore(),
            new IdleWorkRecords(),
            alarms,
            new IdleScheduler(),
            new IdleFrames(),
            workLines,
            new IdleUser(),
            new Tests.UI.RecordingNotify(),
            new Tests.UI.ImmediateUiDispatcher());

    private sealed class RecordingAlarms : IAlarmEventService
    {
#pragma warning disable CS0067
        public event EventHandler<AlarmRow>? AlarmRaised;
#pragma warning restore CS0067
        public event EventHandler? AlarmsChanged;
        public int GetAlarmsCalls { get; private set; }

        public void RaiseChanged() => AlarmsChanged?.Invoke(this, EventArgs.Empty);
        public void RaiseAlarm() => AlarmRaised?.Invoke(this, new AlarmRow(1, DateTime.Now, "2", "x", "0"));
        public void ResetCalls() => GetAlarmsCalls = 0;

        public Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default)
        {
            GetAlarmsCalls++;
            return Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        }
        public Task<int> GetUnhandledCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent, string? taskCode, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task MarkHandledAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> DeleteAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class RecordingWorkLines : IWorkLineService
    {
        public event EventHandler? WorkLinesChanged;
        public int GetAllCalls { get; private set; }
        public void RaiseChanged() => WorkLinesChanged?.Invoke(this, EventArgs.Empty);
        public void ResetCalls() => GetAllCalls = 0;
        public Task<IReadOnlyList<WorkLineListItem>> GetAllAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            return Task.FromResult<IReadOnlyList<WorkLineListItem>>(Array.Empty<WorkLineListItem>());
        }
        public Task<WorkLineEditModel?> GetByIdAsync(long id, CancellationToken ct = default) => Task.FromResult<WorkLineEditModel?>(null);
        public Task<long> SaveAsync(WorkLineEditModel model, string author, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class IdleUser : ICurrentUser { public string Name => "test"; }
    private sealed class IdleFrames : IFrameService
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
    private sealed class IdleWorkRecords : IWorkRecordService
    {
        public Task<long> RecordStartAsync(WorkRecordStartArgs args, CancellationToken ct = default) => Task.FromResult(0L);
        public Task RecordResultAsync(long recordId, string result, string? remark, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkRecordRow?> FindOpenByPositionAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<WorkRecordRow?>(null);
        public Task<IReadOnlyList<WorkRecordRow>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkRecordRow>>(Array.Empty<WorkRecordRow>());
        public Task<WorkShiftStats> GetShiftStatsAsync(CancellationToken ct = default) => Task.FromResult(new WorkShiftStats(0, 0, 0));
    }
    private sealed class IdleScheduler : IPositionScheduler
    {
        public bool IsReconciled => true;
        public ReconciliationState ReconciliationState => ReconciliationState.Succeeded;
        public string? ReconciliationFailureReason => null;
        public bool IsAutoDispatchPaused => false;
        public event EventHandler? Reconciled { add { } remove { } }
        public event EventHandler<ReconciliationSnapshot>? ReconciliationStateChanged { add { } remove { } }
        public void SetAutoDispatchPaused(bool paused) { }
        public Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public void InvalidateFrameBindingCache(long? equipmentId = null) { }
    }
}
