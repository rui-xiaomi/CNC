using System.Collections.Concurrent;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Signals;
using CncLoader.Core.Tests.Routing;

namespace CncLoader.Core.Tests.State;

/// <summary>P2-4：主循环按 PLC 分组并发、组内串行——一台 PLC 卡住不拖其他 PLC 的工位。</summary>
[TestFixture]
public sealed class DriveAllGroupingTests
{
    [Test]
    public async Task 一台PLC的工位驱动卡住_其他PLC工位照常驱动_同PLC工位组内串行()
    {
        var scheduler = Build(new[]
        {
            TestStartPoint(plcId: 1, eq: 1, pos: 1, "D1100"),
            TestStartPoint(plcId: 1, eq: 1, pos: 2, "D1102"),
            TestStartPoint(plcId: 2, eq: 2, pos: 3, "D1100")
        });
        await scheduler.ProbeLoadPositionCacheAsync();

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plc2Driven = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driven = new ConcurrentQueue<(long Eq, long Pos)>();
        var firstPlc1 = 0;
        scheduler.DrivePositionOverride = async (eq, pos, ct) =>
        {
            driven.Enqueue((eq, pos));
            if (eq == 2) plc2Driven.TrySetResult();
            // PLC1 组内第一个被驱动的工位卡住（模拟该 PLC 读写超时）
            if (eq == 1 && Interlocked.Exchange(ref firstPlc1, 1) == 0)
                await release.Task.WaitAsync(ct);
        };

        var round = scheduler.ProbeDriveAllAsync();
        await plc2Driven.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var drivenWhileBlocked = driven.ToArray();
        release.TrySetResult();
        await round.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(drivenWhileBlocked, Does.Contain((2L, 3L)), "PLC1 卡住期间 PLC2 工位须照常驱动");
            Assert.That(drivenWhileBlocked.Count(d => d.Eq == 1), Is.EqualTo(1), "同 PLC 工位组内串行，须排在卡住的工位之后");
            Assert.That(driven.Distinct().Count(), Is.EqualTo(3), "放行后本轮全部工位驱动完成");
        });
    }

    private static PositionScheduler Build(IReadOnlyList<PlcPointDefinition> points)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, 1, 1);
        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, DispatchGateHarness.UploadFrame, equipment: equipment, store: store);
        return DispatchGateHarness.CreateScheduler(
            equipment, slots, new TracingTaskService(trace), new TracingPlcOps(trace),
            routingStore: store, points: new FixedPoints(points));
    }

    private static PlcPointDefinition TestStartPoint(long plcId, long eq, long pos, string address)
        => new()
        {
            PlcId = plcId, EquipmentId = eq, PositionId = pos, Signal = SignalKey.PosTestStart,
            RegisterAddress = address, IsWrite = true
        };

    private sealed class FixedPoints(IReadOnlyList<PlcPointDefinition> points) : IPlcPointSource
    {
        public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(points);
        public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default) => GetAllAsync(ct);
        public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default) => GetAllAsync(ct);
        public void Invalidate() { }
    }
}
