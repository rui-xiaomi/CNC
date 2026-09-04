using CncLoader.Communication.State;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using NUnit.Framework;

namespace CncLoader.Core.Tests.Routing;

/// <summary>P0-2：派工下发窗口内工位被主循环推进为 Alarm 时，回填不得覆盖粘滞告警，须收口 orphan 并按方向回滚预记。</summary>
[TestFixture]
public sealed class DispatchBackfillAlarmGuardTests
{
    [Test]
    public async Task 上料下发窗口内工位被推进为Alarm_回填不绑定并收口回滚()
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, craftNode: 1, DispatchGateHarness.Eq);
        store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.UploadFrame, FrameRole.Upload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, DispatchGateHarness.UploadFrame,
            equipment: equipment, store: store);
        var tasks = new HangingTaskService();
        var plc = new TracingPlcOps(trace);
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, routingStore: store);

        scheduler.ProbeMarkReconciled();
        scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);

        // 启动派工；RCS 下发挂起，模拟最长 31s 的下发窗口
        var dispatchTask = scheduler.ProbeDispatchOnceAsync();
        await tasks.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 窗口内主循环把工位推进为 Alarm（模拟开门/安全信号掉）
        scheduler.ProbeSetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos, PositionState.Alarm);

        tasks.ReleaseSuccess();
        await dispatchTask;

        var ctx = scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(ctx.State, Is.EqualTo(PositionState.Alarm), "不得把粘滞 Alarm 抹成 Dispatching");
            Assert.That(ctx.CurrentTaskId, Is.Null, "状态漂移后不得绑定任务");
            Assert.That(tasks.DispatchTransitCount, Is.EqualTo(1), "RCS 确已下发");
            Assert.That(tasks.CancelCount, Is.EqualTo(1), "须收口已发出的 orphan 任务");
            Assert.That(slots.RollbackTakeCount, Is.EqualTo(1), "须按方向回滚取料预记");
        });
    }

    [Test]
    public async Task 下料下发窗口内工位被推进为Alarm_回填不绑定并收口回滚()
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, craftNode: 1, DispatchGateHarness.Eq);
        store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.DownloadFrame, FrameRole.Unload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, occupiedFrameId: -1, putFrameId: DispatchGateHarness.DownloadFrame,
            equipment: equipment, store: store);
        var tasks = new HangingTaskService();
        var plc = new TracingPlcOps(trace);
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, routingStore: store);

        scheduler.ProbeMarkReconciled();
        var enqueued = await scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1");
        Assert.That(enqueued, Is.True, "下料应能入队");

        var dispatchTask = scheduler.ProbeDispatchOnceAsync();
        await tasks.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        scheduler.ProbeSetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos, PositionState.Alarm);

        tasks.ReleaseSuccess();
        await dispatchTask;

        var ctx = scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(ctx.State, Is.EqualTo(PositionState.Alarm), "不得把粘滞 Alarm 抹成 Dispatching");
            Assert.That(ctx.CurrentTaskId, Is.Null, "状态漂移后不得绑定任务");
            Assert.That(tasks.DispatchTransitCount, Is.EqualTo(1), "RCS 确已下发");
            Assert.That(tasks.CancelCount, Is.EqualTo(1), "须收口已发出的 orphan 任务");
            Assert.That(slots.RollbackPutCount, Is.EqualTo(1), "须按方向回滚入库预记");
        });
    }

    /// <summary>RCS 下发挂起至显式释放，模拟真实下发耗时窗口。</summary>
    private sealed class HangingTaskService : IRcsTaskService
    {
        public TaskCompletionSource DispatchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RcsResult> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _capturedTaskId;
        public int DispatchTransitCount { get; private set; }
        public int CancelCount { get; private set; }

        public async Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
        {
            DispatchTransitCount++;
            _capturedTaskId = args.TaskId;
            DispatchEntered.TrySetResult();
            return await _release.Task.WaitAsync(ct);
        }

        public void ReleaseSuccess()
            => _release.TrySetResult(new RcsResult(true, 200, true, "ok", "", "{}", null, 1)
            {
                TaskId = _capturedTaskId
            });

        public Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default)
        {
            CancelCount++;
            return Task.FromResult(new RcsResult(true, 200, true, "ok", "", "{}", null, 1) { TaskId = rcsTaskId });
        }

        public Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default) => Fail();
        public Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default) => Fail();
        public Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> AutoRedispatchAsync(string rcsTaskId, int maxRedoCount, CancellationToken ct = default) => Fail();
        public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default) => Fail();
        public Task<RcsResult> DispatchPalletReturnAsync(long equipmentId, long? positionId, string fromCode, string toCode,
            long workLineId, string lineCode, string author, CancellationToken ct = default) => Fail();
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentTasksAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentMessagesAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryMessagesAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;

        private static Task<RcsResult> Fail() => Task.FromResult(RcsResult.Fail("", "stub"));
    }
}
