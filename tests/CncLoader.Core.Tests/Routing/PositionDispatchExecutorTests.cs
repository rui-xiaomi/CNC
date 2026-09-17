using CncLoader.Common.Configuration;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Routing;

[TestFixture]
public sealed class PositionDispatchExecutorTests
{
    [Test]
    public async Task 无上料候选_AllocateUploads返回false()
    {
        var equipment = new PlannerEquipment();
        var slots = new PlannerSlots();
        var routes = new FixedRoutes();
        var alarms = new NoopAlarms();
        var queue = new MemoryDispatchQueue();
        var upload = new UploadDispatchPlanner(
            equipment, slots, routes, alarms, NullLogger.Instance,
            (id, _) => Task.FromResult(new EquipmentFrameBindingIds(1, 2)));
        var unload = new UnloadDispatchPlanner(
            equipment, routes, alarms, queue, NullLogger.Instance,
            (id, _) => Task.FromResult(new EquipmentFrameBindingIds(1, 2)),
            (_, _) => Task.FromResult<WorkLineRef?>(new WorkLineRef(10, "LINE-A")),
            (_, _) => { });
        var executor = new PositionDispatchExecutor(
            new EmptyRuntime(),
            new TracingTaskService(new CallTrace()),
            new NoopTaskStore(),
            slots,
            equipment,
            routes,
            new WorkLineOnlyRoutingValidator(equipment),
            queue,
            alarms,
            upload,
            unload,
            new RcsOptions(),
            NullLogger.Instance);

        Assert.That(await executor.AllocateUploadsAsync(CancellationToken.None), Is.False);
    }

    private sealed class EmptyRuntime : IPositionDispatchRuntime
    {
        public bool CanAutoDispatch => true;
        public IEnumerable<PositionContext> Contexts => Array.Empty<PositionContext>();
        public bool IsEquipmentDispatchHeld(long equipmentId) => false;
        public bool HasInbound((long Eq, long Pos) key) => false;
        public PositionContext GetOrAddContext(long equipmentId, long positionId)
            => new() { EquipmentId = equipmentId, PositionId = positionId };
        public SemaphoreSlim GateFor((long Eq, long Pos) key) => new(1, 1);
        public void SetState(PositionContext ctx, PositionState state) => ctx.State = state;
        public void InvalidateLineCache(long equipmentId) { }
        public void CacheLine(long equipmentId, WorkLineRef line) { }
        public void LogRouteUnavailableThrottled(long equipmentId, string message) { }
        public Task DeferUnloadAsync(DispatchItem item, PositionContext ctx, string reason, CancellationToken ct)
            => Task.CompletedTask;
        public Task<bool> CloseOrphanTaskAsync(string taskId, string reason, CancellationToken ct)
            => Task.FromResult(true);
        public bool TryAddInbound((long Eq, long Pos) key, InboundHandoff handoff) => true;
        public bool TryRemoveInboundIfSource((long Eq, long Pos) key, string taskId) => true;
        public bool TryMarkInboundDispatched((long Eq, long Pos) key, string localTaskId, string assignedTaskId) => true;
    }
}
