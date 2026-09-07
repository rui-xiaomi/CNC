using CncLoader.Common.Configuration;
using CncLoader.Communication.State;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Routing;

[TestFixture]
public sealed class LoadUnloadVerbDispatchTests
{
    [Test]
    public async Task Grab档_上料走excuteTask_不走transit()
    {
        var h = BuildUpload(RcsLoadUnloadVerbs.Grab);
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Tasks.DispatchGrabCount, Is.EqualTo(1));
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(h.Tasks.LastGrabArgs, Is.Not.Null);
            Assert.That(h.Tasks.LastGrabArgs!.SrcStation, Is.EqualTo("101"));
            Assert.That(h.Tasks.LastGrabArgs.DstStation, Is.EqualTo("201"));
            Assert.That(h.Tasks.LastGrabArgs.TaskType, Is.EqualTo("0"));
            Assert.That(h.Tasks.LastGrabArgs.Items, Has.Count.EqualTo(1));
            var item = h.Tasks.LastGrabArgs.Items[0];
            Assert.That(item.SrcNo, Is.EqualTo(101));
            Assert.That(item.SrcPos, Is.EqualTo(101));
            Assert.That(item.DstNo, Is.EqualTo(201));
            Assert.That(item.DstPos, Is.EqualTo(101));
            Assert.That(item.Data, Is.EqualTo("MAT-1"));
            Assert.That(h.Slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(h.Tasks.LastTaskId, Is.EqualTo(h.Slots.LastReservedTaskId));
        });
    }

    [Test]
    public async Task Transit档_上料仍走transitTask()
    {
        var h = BuildUpload(RcsLoadUnloadVerbs.Transit);
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(1));
            Assert.That(h.Tasks.DispatchGrabCount, Is.EqualTo(0));
            Assert.That(h.Tasks.LastArgs!.Kind, Is.EqualTo(RcsTaskKind.Transit));
        });
    }

    [Test]
    public async Task Grab档_缺加工位station_拒发并回滚()
    {
        var h = BuildUpload(RcsLoadUnloadVerbs.Grab, missingStation: true);
        h.Scheduler.ProbeMarkReconciled();
        h.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(h.Tasks.DispatchGrabCount, Is.EqualTo(0));
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(h.Slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Grab档_下料到料架走excuteTask()
    {
        var h = BuildUnload(RcsLoadUnloadVerbs.Grab);
        h.Scheduler.ProbeMarkReconciled();
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1");
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.True);
            Assert.That(h.Tasks.DispatchGrabCount, Is.EqualTo(1));
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(h.Tasks.LastGrabArgs!.TaskType, Is.EqualTo("1"));
            Assert.That(h.Tasks.LastGrabArgs.SrcStation, Is.EqualTo("201"));
            Assert.That(h.Tasks.LastGrabArgs.DstStation, Is.EqualTo("303"));
            var item = h.Tasks.LastGrabArgs.Items[0];
            Assert.That(item.SrcPos, Is.EqualTo(101));
            Assert.That(item.DstPos, Is.EqualTo(101));
            Assert.That(item.Data, Is.EqualTo("M-1"));
        });
    }

    private static Harness BuildUpload(string verb, bool missingStation = false)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(
            DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, craftNode: 1, DispatchGateHarness.Eq);
        store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.UploadFrame, FrameRole.Upload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, DispatchGateHarness.UploadFrame, equipment: equipment, store: store);
        var tasks = new TracingTaskService(trace);
        var plc = new TracingPlcOps(trace);
        var routes = new FixedRoutes
        {
            FrameShelf = "101",
            FrameSlotCell = "101101",
            PositionStation = missingStation ? null : "201",
            PositionCell = "201101",
            MissingPositionStation = missingStation
        };
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, routingStore: store, routes: routes, loadUnloadVerb: verb);

        return new Harness(slots, tasks, scheduler);
    }

    private static Harness BuildUnload(string verb)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(
            DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, craftNode: 1, DispatchGateHarness.Eq);
        store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.DownloadFrame, FrameRole.Unload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(
            trace, occupiedFrameId: -1, putFrameId: DispatchGateHarness.DownloadFrame,
            equipment: equipment, store: store);
        var tasks = new TracingTaskService(trace);
        var plc = new TracingPlcOps(trace);
        var routes = new FixedRoutes
        {
            FrameShelf = "303",
            FrameSlotCell = "303101",
            PositionStation = "201",
            PositionCell = "201101"
        };
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, routingStore: store, routes: routes, loadUnloadVerb: verb);

        return new Harness(slots, tasks, scheduler);
    }

    private sealed record Harness(TracingSlots Slots, TracingTaskService Tasks, PositionScheduler Scheduler);
}
