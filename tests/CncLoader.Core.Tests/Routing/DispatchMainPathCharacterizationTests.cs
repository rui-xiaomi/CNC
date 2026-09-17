using CncLoader.Communication.State;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// 派工主路径特征测试：对账门禁、暂停/机台 Hold、取消阻断、预记失败、RCS 失败回滚、
/// 直送竞争、成功绑定、单消费者顺序。供后续拆分 PositionScheduler 时锁行为。
/// </summary>
[TestFixture]
public sealed class DispatchMainPathCharacterizationTests
{
    [Test]
    public async Task 未对账_不上料也不下发()
    {
        var h = BuildUpload();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1");

        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Tracing.DispatchTransitCount, Is.Zero);
            Assert.That(h.Slots.ReserveTakeCount, Is.Zero);
            Assert.That(h.Slots.ReservePutCount, Is.Zero);
            Assert.That(h.Scheduler.ProbeUploadRequested(DispatchGateHarness.Eq, DispatchGateHarness.Pos), Is.True);
            if (enqueued)
                Assert.That(h.Scheduler.ProbeQueueCount, Is.EqualTo(1), "未对账不得消费下料队");
        });
    }

    [Test]
    public async Task 暂停自动派工_不消费队列也不上料()
    {
        var h = BuildBoth();
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.SetAutoDispatchPaused(true);
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1");
        Assert.That(enqueued, Is.True);

        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Tracing.DispatchTransitCount, Is.Zero);
            Assert.That(h.Slots.ReserveTakeCount, Is.Zero);
            Assert.That(h.Scheduler.ProbeQueueCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task 机台Hold_上料等待且下料回队()
    {
        var h = BuildBoth();
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.SetEquipmentDispatchHold(DispatchGateHarness.Eq, true, "换架");
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.That(await h.Scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1"), Is.True);

        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Tracing.DispatchTransitCount, Is.Zero);
            Assert.That(h.Scheduler.ProbeQueueCount, Is.EqualTo(1), "Hold 已出队须回队");
            Assert.That(h.Scheduler.ProbeUploadRequested(DispatchGateHarness.Eq, DispatchGateHarness.Pos), Is.True);
            var ctx = h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
            Assert.That(ctx.CurrentTaskId, Is.Null);
            Assert.That(ctx.AlarmRaised, Is.False);
        });
    }

    [Test]
    public async Task 未确认取消任务_上料等待且下料回队()
    {
        var store = new NoopTaskStore { UnconfirmedCanceled = true };
        var h = BuildBoth(taskStore: store);
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.That(await h.Scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1"), Is.True);

        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Tracing.DispatchTransitCount, Is.Zero);
            Assert.That(h.Slots.ReserveTakeCount, Is.Zero);
            Assert.That(h.Scheduler.ProbeQueueCount, Is.EqualTo(1));
            var ctx = h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
            Assert.That(ctx.AlarmRaised, Is.False);
            Assert.That(ctx.CurrentTaskId, Is.Null);
        });
    }

    [Test]
    public async Task 上料预记未抢到槽_保持WaitLoad不Alarm不下发()
    {
        var h = BuildUpload();
        h.Slots.ReserveTakeReturnsNull = true;
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);

        await h.Scheduler.ProbeDispatchOnceAsync();

        var ctx = h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(h.Slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(h.Tracing.DispatchTransitCount, Is.Zero);
            Assert.That(ctx.AlarmRaised, Is.False);
            Assert.That(ctx.State, Is.EqualTo(PositionState.WaitLoad));
            Assert.That(h.Scheduler.ProbeUploadRequested(DispatchGateHarness.Eq, DispatchGateHarness.Pos), Is.True);
        });
    }

    [Test]
    public async Task 上料预记异常_置Alarm且不调用RCS()
    {
        var h = BuildUpload();
        h.Slots.ReserveTakeThrows = new InvalidOperationException("预记死锁");
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);

        await h.Scheduler.ProbeDispatchOnceAsync();

        var ctx = h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(h.Tracing.DispatchTransitCount, Is.Zero);
            Assert.That(ctx.AlarmRaised, Is.True);
            Assert.That(ctx.State, Is.EqualTo(PositionState.Alarm));
        });
    }

    [Test]
    public async Task 上料RCS失败_回滚预记并置Alarm()
    {
        var h = BuildUpload();
        h.Tracing.DispatchFails = true;
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);

        await h.Scheduler.ProbeDispatchOnceAsync();

        var ctx = h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(h.Slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(h.Tracing.DispatchTransitCount, Is.EqualTo(1));
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(1));
            Assert.That(ctx.AlarmRaised, Is.True);
            Assert.That(ctx.State, Is.EqualTo(PositionState.Alarm));
            Assert.That(ctx.CurrentTaskId, Is.Null);
        });
    }

    [Test]
    public async Task 已有直送登记_禁止自取上料()
    {
        var h = BuildUpload();
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedExpectedInbound(DispatchGateHarness.Eq, DispatchGateHarness.Pos, "T-IN", "M-IN");
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);

        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Tracing.DispatchTransitCount, Is.Zero);
            Assert.That(h.Slots.ReserveTakeCount, Is.Zero);
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DispatchGateHarness.Eq, DispatchGateHarness.Pos), Is.True);
            Assert.That(h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos).CurrentTaskId, Is.Null);
        });
    }

    [Test]
    public async Task 上料下发窗口内出现直送登记_放弃绑定并收口回滚()
    {
        var h = BuildUpload(tasks: new HangingTaskService());
        var hanging = (HangingTaskService)h.Tasks;
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);

        var dispatch = h.Scheduler.ProbeDispatchOnceAsync();
        await hanging.DispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        h.Scheduler.ProbeSeedExpectedInbound(DispatchGateHarness.Eq, DispatchGateHarness.Pos, "T-IN", "M-IN");
        hanging.ReleaseSuccess();
        await dispatch;

        var ctx = h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(ctx.CurrentTaskId, Is.Null, "直送窗口内不得绑定自取任务");
            Assert.That(ctx.State, Is.Not.EqualTo(PositionState.Dispatching));
            Assert.That(hanging.DispatchTransitCount, Is.EqualTo(1));
            Assert.That(hanging.CancelCount, Is.EqualTo(1));
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(1));
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DispatchGateHarness.Eq, DispatchGateHarness.Pos), Is.True);
        });
    }

    [Test]
    public async Task 上料成功_绑定任务并进入Dispatching()
    {
        var h = BuildUpload();
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);

        await h.Scheduler.ProbeDispatchOnceAsync();

        var ctx = h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(h.Slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(h.Tracing.DispatchTransitCount, Is.EqualTo(1));
            Assert.That(h.Slots.RollbackTakeCount, Is.Zero);
            Assert.That(ctx.State, Is.EqualTo(PositionState.Dispatching));
            Assert.That(ctx.CurrentTaskId, Is.EqualTo(h.Slots.LastReservedTaskId));
            Assert.That(h.Scheduler.ProbeUploadRequested(DispatchGateHarness.Eq, DispatchGateHarness.Pos), Is.False);
        });
    }

    [Test]
    public async Task 单消费者_先清空下料再上料()
    {
        var h = BuildBoth();
        h.Scheduler.ProbeMarkReconciled();
        Assert.That(await h.Scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1"), Is.True);
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, 2);

        await h.Scheduler.ProbeDispatchOnceAsync();
        Assert.Multiple(() =>
        {
            Assert.That(h.Tracing.DispatchTransitCount, Is.EqualTo(1), "第一轮只消费下料");
            Assert.That(h.Tracing.LastTaskType, Is.EqualTo("1"));
            Assert.That(h.Scheduler.ProbeUploadRequested(DispatchGateHarness.Eq, 2), Is.True);
            Assert.That(h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, 2).CurrentTaskId, Is.Null);
        });

        await h.Scheduler.ProbeDispatchOnceAsync();
        Assert.Multiple(() =>
        {
            Assert.That(h.Tracing.DispatchTransitCount, Is.EqualTo(2));
            Assert.That(h.Tracing.LastTaskType, Is.EqualTo("0"));
            Assert.That(h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, 2).State,
                Is.EqualTo(PositionState.Dispatching));
        });
    }

    [Test]
    public async Task 两工位上料_预记串行不得并行抢槽()
    {
        var h = BuildUpload();
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, 2);

        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Slots.ReserveTakeCount, Is.EqualTo(2));
            Assert.That(h.Slots.ReserveTakeMaxInFlight, Is.EqualTo(1), "两工位不得并行预记");
            Assert.That(h.Tracing.DispatchTransitCount, Is.EqualTo(2));
            Assert.That(h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos).State,
                Is.EqualTo(PositionState.Dispatching));
            Assert.That(h.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, 2).State,
                Is.EqualTo(PositionState.Dispatching));
        });
    }

    private static Harness BuildUpload(IRcsTaskService? tasks = null, IRcsTaskStore? taskStore = null)
        => Build(bindUpload: true, bindDownload: false, tasks, taskStore);

    private static Harness BuildBoth(IRcsTaskService? tasks = null, IRcsTaskStore? taskStore = null)
        => Build(bindUpload: true, bindDownload: true, tasks, taskStore);

    private static Harness Build(bool bindUpload, bool bindDownload,
        IRcsTaskService? tasks, IRcsTaskStore? taskStore)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, 1, DispatchGateHarness.Eq);
        if (bindUpload)
            store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.UploadFrame, FrameRole.Upload);
        if (bindDownload)
            store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.DownloadFrame, FrameRole.Unload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, DispatchGateHarness.UploadFrame,
            putFrameId: bindDownload ? DispatchGateHarness.DownloadFrame : null,
            equipment: equipment, store: store);
        IRcsTaskService taskSvc = tasks ?? new TracingTaskService(trace);
        var plc = new TracingPlcOps(trace);
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, taskSvc, plc, routingStore: store, taskStore: taskStore ?? new NoopTaskStore());
        return new Harness
        {
            Slots = slots,
            Tracing = taskSvc as TracingTaskService ?? new TracingTaskService(trace),
            Tasks = taskSvc,
            Scheduler = scheduler
        };
    }

    private sealed class Harness
    {
        public required TracingSlots Slots { get; init; }
        public required TracingTaskService Tracing { get; init; }
        public required IRcsTaskService Tasks { get; init; }
        public required PositionScheduler Scheduler { get; init; }
    }

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
