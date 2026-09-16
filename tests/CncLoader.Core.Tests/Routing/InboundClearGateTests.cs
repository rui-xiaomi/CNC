using CncLoader.Communication.State;

namespace CncLoader.Core.Tests.Routing;

/// <summary>P1-6：按源任务清除交接登记须在目标工位闸内执行，且只清仍属该源任务的登记。</summary>
[TestFixture]
public sealed class InboundClearGateTests
{
    private static readonly long DstEq = DispatchGateHarness.Eq;
    private static readonly long DstPos = DispatchGateHarness.Pos;

    [Test]
    public async Task 源任务放弃_闸外只登记请求_目标工位驱动时闸内清除()
    {
        var scheduler = Build();
        scheduler.ProbeSeedExpectedInbound(DstEq, DstPos, "T-SRC", "M-1");

        await scheduler.NotifyTaskAbandonedAsync("T-SRC", "REDO_LIMIT");
        var stillRegistered = scheduler.ProbeHasExpectedInbound(DstEq, DstPos);
        var requested = scheduler.ProbeHasPendingInboundClear(DstEq, DstPos);

        await scheduler.ProbeDrivePositionOnceAsync(DstEq, DstPos);

        Assert.Multiple(() =>
        {
            Assert.That(stillRegistered, Is.True, "不得在目标工位闸外直接删登记");
            Assert.That(requested, Is.True, "须登记清理请求");
            Assert.That(scheduler.ProbeHasPendingInboundClear(DstEq, DstPos), Is.False, "目标工位驱动须消费清理请求");
            Assert.That(scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.False);
        });
    }

    [Test]
    public async Task 清理执行前登记已换成新源任务_不得误删新登记()
    {
        var scheduler = Build();
        scheduler.ProbeSeedExpectedInbound(DstEq, DstPos, "T-OLD");
        await scheduler.NotifyTaskAbandonedAsync("T-OLD", "REDO_LIMIT");
        scheduler.ProbeSeedExpectedInbound(DstEq, DstPos, "T-NEW");

        await scheduler.ProbeApplyPendingInboundClearAsync(DstEq, DstPos);

        Assert.Multiple(() =>
        {
            Assert.That(scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.True, "旧源任务的请求不得删掉新登记");
            Assert.That(scheduler.ProbeHasPendingInboundClear(DstEq, DstPos), Is.False);
        });
    }

    private static PositionScheduler Build()
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, 1, DstEq);
        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, DispatchGateHarness.UploadFrame, equipment: equipment, store: store);
        return DispatchGateHarness.CreateScheduler(
            equipment, slots, new TracingTaskService(trace), new TracingPlcOps(trace),
            alarms: new NoopAlarms(), routingStore: store);
    }
}
