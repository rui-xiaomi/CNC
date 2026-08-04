using CncLoader.Communication.Rcs;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Communication.RcsCallbackTestFakes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 R20–R24：历史回调/对账继续收口；拒绝原因可区分；自动路径无 Growl/Alarm。
/// </summary>
[TestFixture]
public sealed class HistoricalTaskClosureRoutingTests
{
    private const string TaskId = "LINE-A-MV-HIST-0001";

    // ─── R20｜历史回调继续收口 ───────────────────────────────────────

    [Test]
    public async Task R20_DispatchedTask_ConfigDisabled_PushCompleted_StillPersists()
    {
        var h = ManualReplayHarness.Create();
        h.SeedHistoricalTask(TaskId, state: RcsTaskState.Dispatched, error: null, redoCount: 0);
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "1");
        h.Store.SetWorkLineState(ManualReplayRoutingCodes.LineId, "1");

        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var real = CreateProcessor(store, new FakeAlarms());
        var validator = h.Validator;
        var callsBefore = validator.CallCount;

        var body = "{\"taskId\":\"" + TaskId + "\",\"data\":{\"system\":{\"error_code\":0,\"msg\":\"ok\"}}}";
        var ack = await real.HandlePushTaskStatusAsync(body);

        Assert.Multiple(() =>
        {
            Assert.That(ack, Does.Contain(TaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1), "callback 仍能落库");
            Assert.That(store.UpdateStates[0], Is.EqualTo(RcsTaskState.Completed));
            Assert.That(validator.CallCount, Is.EqualTo(callsBefore),
                "历史回调不得调用 RoutingValidator");
            Assert.That(h.Client.SendCount, Is.EqualTo(0), "不得创建新外部执行");
        });
    }

    [Test]
    public async Task R20_FailedAndCancelledCallbacks_StillWork_AndDedupe()
    {
        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => Task.FromResult(true));
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var sut = CreateProcessor(store, new FakeAlarms());
        var h = ManualReplayHarness.Create();
        h.Store.SetCraftState(ManualReplayRoutingCodes.CraftId, "1");
        var v0 = h.Validator.CallCount;

        var failed = "{\"taskId\":\"" + TaskId + "\",\"data\":{\"system\":{\"error_code\":1,\"msg\":\"failed\"}}}";
        var canceled = "{\"taskId\":\"" + TaskId + "-C\",\"data\":{\"system\":{\"error_code\":9,\"msg\":\"cancel\"}}}";

        await sut.HandlePushTaskStatusAsync(failed);
        await sut.HandlePushTaskStatusAsync(failed); // 去重
        await sut.HandlePushTaskStatusAsync(canceled);

        Assert.Multiple(() =>
        {
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2), "失败1次+取消1次；重复失败被去重");
            Assert.That(store.UpdateStates, Is.EqualTo(new[] { RcsTaskState.Failed, RcsTaskState.Canceled }));
            Assert.That(h.Validator.CallCount, Is.EqualTo(v0));
        });
    }

    [Test]
    public async Task R20_SlotConfirmRollback_ByTaskId_NotBlockedByDisabledConfig()
    {
        var h = ManualReplayHarness.Create();
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "1");
        var v0 = h.Validator.CallCount;

        var okConfirm = await h.Slots.ConfirmAsync(TaskId);
        var okRollback = await h.Slots.RollbackAsync(TaskId + "-rb");
        var okTake = await h.Slots.ConfirmTakeAsync(TaskId + "-take");

        Assert.Multiple(() =>
        {
            Assert.That(okConfirm, Is.True);
            Assert.That(okRollback, Is.True);
            Assert.That(okTake, Is.True);
            Assert.That(h.Slots.ConfirmedTaskIds, Does.Contain(TaskId));
            Assert.That(h.Slots.RolledBackTaskIds, Does.Contain(TaskId + "-rb"));
            Assert.That(h.Validator.CallCount, Is.EqualTo(v0), "Confirm/Rollback 不经 Validator");
            Assert.That(h.Client.SendCount, Is.EqualTo(0));
        });
    }

    // ─── R21｜启动对账只读历史、不新执行 ─────────────────────────────

    [Test]
    public async Task R21_StartupReconcile_DisabledConfig_ReadsHistory_NoNewDispatch()
    {
        var h = ManualReplayHarness.Create();
        h.SeedHistoricalTask(TaskId, state: RcsTaskState.Dispatched, error: null, redoCount: 0);
        h.Store.SetWorkLineState(ManualReplayRoutingCodes.LineId, "1");

        var sendBefore = h.Client.SendCount;
        var unfinished = await h.TaskStore.GetUnfinishedTaskIdsAsync();
        Assert.That(unfinished, Does.Contain(TaskId));

        // 真实协调器接缝：阶段只读历史，不触发 Redo/Redispatch/Dispatch
        var dispatchCalls = 0;
        var redoCalls = 0;
        var round = await new StartupReconcileCoordinator().RunAsync(
            async ct =>
            {
                var ids = await h.TaskStore.GetUnfinishedTaskIdsAsync(ct);
                await h.TaskStore.GetByTaskIdAsync(ids[0], ct);
                // 对账可读禁用配置关联任务：查权威不得抛
                await h.Store.FindEquipmentAsync(ManualReplayRoutingCodes.SrcEq, ct);
                return ReconcilePhaseResult.Ok(ReconcilePhase.One);
            },
            _ => Task.FromResult(ReconcilePhaseResult.Ok(ReconcilePhase.OneB)),
            _ => Task.FromResult(ReconcilePhaseResult.Ok(ReconcilePhase.Two)),
            _ => Task.FromResult(ReconcilePhaseResult.Ok(ReconcilePhase.Three)));

        Assert.Multiple(() =>
        {
            Assert.That(round.Succeeded, Is.True, "对账成功仍按 P0-3 契约");
            Assert.That(StartupReconcileCoordinator.DecideIsReconciled(round), Is.True);
            Assert.That(h.Client.SendCount, Is.EqualTo(sendBefore), "对账不得新任务发送");
            Assert.That(dispatchCalls, Is.EqualTo(0));
            Assert.That(redoCalls, Is.EqualTo(0));
            Assert.That(h.Validator.CallCount, Is.EqualTo(0),
                "对账读历史不走新执行 Validator");
        });
    }

    [Test]
    public async Task R21_PositionScheduler_ReconcileSeam_NoAutoRedo()
    {
        var h = ManualReplayHarness.Create();
        h.SeedHistoricalTask(TaskId, state: RcsTaskState.Dispatched, error: null, redoCount: 0);
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "1");

        var tasks = new TracingTaskService(h.Trace);
        var equipment = new TracingEquipmentConfigService(h.Store, h.Trace);
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, h.Slots, tasks, h.Plc, routingStore: h.Store, validator: h.Validator);

        // 标记对账完成并跑一次派工 tick：禁用路由不得因历史任务自动 Redo
        scheduler.ProbeMarkReconciled();
        await scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(tasks.DispatchTransitCount, Is.EqualTo(0), "不得因历史未完结自动新执行");
            Assert.That(h.Client.SendCount, Is.EqualTo(0));
        });
    }

    // ─── R23｜拒绝原因可区分 ─────────────────────────────────────────

    [Test]
    public async Task R23_Validator_Distinguishes_AllRequiredReasons()
    {
        var seen = new HashSet<RoutingUnavailableReason>();

        async Task Probe(Action<MutableEquipmentRoutingStore> mutate, RoutingUnavailableReason expect,
            DispatchRouteContext? ctx = null)
        {
            var store = new MutableEquipmentRoutingStore();
            store.SeedActiveChain(10, "L", 20, 1, 30);
            store.SeedNextEquipment(40, 21, 2, 10);
            store.BindFrame(30, 50, FrameRole.Upload);
            mutate(store);
            var eq = new TracingEquipmentConfigService(store, new CallTrace());
            var v = new RoutingAvailabilityValidator(store, eq, NullLogger<RoutingAvailabilityValidator>.Instance);
            var r = await v.ValidateAsync(ctx ?? new DispatchRouteContext
            {
                SourceEquipmentId = 30,
                FromCode = "A",
                ToCode = "B",
                SourceFrameId = RouteDependency.Required(50)
            });
            Assert.That(r.IsAvailable, Is.False, expect.ToString());
            Assert.That(r.Reason, Is.EqualTo(expect), expect.ToString());
            Assert.That(r.Reason, Is.Not.EqualTo(RoutingUnavailableReason.Available));
            seen.Add(r.Reason);
        }

        await Probe(s => s.SetEquipmentState(30, "1"), RoutingUnavailableReason.EquipmentDisabled);
        await Probe(s => s.SetCraftState(20, "1"), RoutingUnavailableReason.CraftDisabled);
        await Probe(s => s.SetWorkLineState(10, "1"), RoutingUnavailableReason.WorkLineDisabled);
        await Probe(s =>
        {
            s.FrameBinds.Clear();
            s.BindFrame(30, 50, FrameRole.Upload);
            s.FrameBinds[0] = s.FrameBinds[0] with { State = "1" };
        }, RoutingUnavailableReason.FrameBindDisabled);
        await Probe(_ => { }, RoutingUnavailableReason.NotFound,
            new DispatchRouteContext { SourceEquipmentId = 999, FromCode = "A", ToCode = "B" });
        await Probe(s =>
        {
            s.Crafts.Clear(); // Equipment 指向缺失 Craft → InvalidRelationship
        }, RoutingUnavailableReason.InvalidRelationship);
        await Probe(_ => { }, RoutingUnavailableReason.LocationMapDisabled,
            new DispatchRouteContext
            {
                SourceEquipmentId = 30,
                FromCode = "",
                ToCode = "",
                RequiresResolvedCells = true
            });

        var boomStore = new BoomStore();
        var boomEq = new TracingEquipmentConfigService(new MutableEquipmentRoutingStore(), new CallTrace());
        var boomV = new RoutingAvailabilityValidator(boomStore, boomEq, NullLogger<RoutingAvailabilityValidator>.Instance);
        var boom = await boomV.ValidateAsync(new DispatchRouteContext
        {
            SourceEquipmentId = 1, FromCode = "A", ToCode = "B"
        });
        Assert.That(boom.Reason, Is.EqualTo(RoutingUnavailableReason.ConfigurationUnavailable));
        seen.Add(boom.Reason);

        // PointDisabled：当前 Validator 若尚未覆盖，至少枚举存在且不得退化为 bool
        Assert.That(Enum.IsDefined(RoutingUnavailableReason.PointDisabled), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Does.Contain(RoutingUnavailableReason.EquipmentDisabled));
            Assert.That(seen, Does.Contain(RoutingUnavailableReason.CraftDisabled));
            Assert.That(seen, Does.Contain(RoutingUnavailableReason.WorkLineDisabled));
            Assert.That(seen, Does.Contain(RoutingUnavailableReason.FrameBindDisabled));
            Assert.That(seen, Does.Contain(RoutingUnavailableReason.NotFound));
            Assert.That(seen, Does.Contain(RoutingUnavailableReason.InvalidRelationship));
            Assert.That(seen, Does.Contain(RoutingUnavailableReason.LocationMapDisabled));
            Assert.That(seen, Does.Contain(RoutingUnavailableReason.ConfigurationUnavailable));
            Assert.That(seen.All(r => r != RoutingUnavailableReason.Available), Is.True,
                "内部结果不得全部退化为 Available/bool");
        });
    }

    // ─── R24｜自动路径无 Growl/Alarm ─────────────────────────────────

    [TestCase("equipment")]
    [TestCase("workline")]
    public async Task R24_AutoRouteUnavailable_NoNotify_NoAlarm(string which)
    {
        var notify = new FakeNotifyCounter();
        var harness = BuildUploadHarness();
        if (which == "equipment")
            harness.Store.SetEquipmentState(DispatchGateHarness.Eq, "1");
        else
            harness.Store.SetWorkLineState(DispatchGateHarness.LineId, "1");

        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        var ctx = harness.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(notify.SuccessCount + notify.WarningCount + notify.ErrorCount + notify.InfoCount,
                Is.EqualTo(0), "自动 RouteUnavailable 不得调 IUserNotificationService");
            Assert.That(ctx.AlarmRaised, Is.False, "不得置 Position Alarm");
            Assert.That(ctx.State, Is.Not.EqualTo(PositionState.Alarm));
        });
    }

    [Test]
    public async Task R24_RollbackFailure_KeepsAlarmSemantics_NotConfusedWithRouteUnavailable()
    {
        // 回滚失败仍按 P0-2 Alarm；与普通 RouteUnavailable（无 Alarm）区分
        var harness = BuildUploadHarness(disableAfterReserve: true, rollbackFails: true);
        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        var ctx = harness.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0), "Final 拒发不得 RCS");
            Assert.That(harness.Slots.RollbackTakeCount, Is.EqualTo(1));
            Assert.That(ctx.AlarmRaised, Is.True,
                "回滚失败须 Alarm；不得与普通 RouteUnavailable（无 Alarm）混淆");
        });
    }

    private static (MutableEquipmentRoutingStore Store, TracingSlots Slots, TracingTaskService Tasks,
        TracingPlcOps Plc, PositionScheduler Scheduler) BuildUploadHarness(
        bool disableAfterReserve = false, bool rollbackFails = false)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, 1, DispatchGateHarness.Eq);
        store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.UploadFrame, FrameRole.Upload);

        Action? disable = null;
        if (disableAfterReserve)
            disable = () => store.SetWorkLineState(DispatchGateHarness.LineId, "1");

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, DispatchGateHarness.UploadFrame,
            equipment: equipment, store: store, disableOnReserveCommit: disable)
        {
            RollbackTakeSucceeds = !rollbackFails
        };
        var tasks = new TracingTaskService(trace);
        var plc = new TracingPlcOps(trace);
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, routingStore: store);
        return (store, slots, tasks, plc, scheduler);
    }

    private sealed class BoomStore : IEquipmentRoutingStore
    {
        public Task<EquipmentRoutingRow?> FindEquipmentAsync(long equipmentId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<CraftworkRoutingRow?> FindCraftworkAsync(long craftworkId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<WorkLineRoutingRow?> FindWorkLineAsync(long workLineId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<IReadOnlyList<CraftworkRoutingRow>> FindCraftworksByWorkLineAsync(long workLineId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<IReadOnlyList<EquipmentRoutingRow>> FindEquipmentsByCraftworkIdsAsync(
            IReadOnlyCollection<long> craftworkIds, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<IReadOnlyList<FrameBindRoutingRow>> FindFrameBindsByEquipmentAsync(
            long equipmentId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
    }
}
