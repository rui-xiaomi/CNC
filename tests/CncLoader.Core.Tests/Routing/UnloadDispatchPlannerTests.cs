using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Routing;

[TestFixture]
public sealed class UnloadDispatchPlannerTests
{
    [Test]
    public async Task NG绑定NG架_走NgFrame()
    {
        var planner = Create(ngFrame: 33);
        var d = await planner.ResolveUnloadTargetAsync(Ctx(), isOk: false, CancellationToken.None);
        Assert.That(d, Is.Not.Null);
        Assert.That(d!.Value.Target, Is.EqualTo(UnloadTarget.NgFrame));
        Assert.That(d.Value.DestFrameId, Is.EqualTo(33));
    }

    [Test]
    public async Task NG无NG架_回退下料架()
    {
        var planner = Create(ngFrame: null, downloadFrame: 44);
        var d = await planner.ResolveUnloadTargetAsync(Ctx(), isOk: false, CancellationToken.None);
        Assert.That(d, Is.Not.Null);
        Assert.That(d!.Value.Target, Is.EqualTo(UnloadTarget.DownloadFrame));
        Assert.That(d.Value.DestFrameId, Is.EqualTo(44));
    }

    [Test]
    public async Task OK有后续工序但无活动机台_拒绝不下发()
    {
        var planner = Create(hasSubsequent: true, nextEquipments: Array.Empty<long>());
        var d = await planner.ResolveUnloadTargetAsync(Ctx(), isOk: true, CancellationToken.None);
        Assert.That(d, Is.Null);
    }

    [Test]
    public async Task 入队缺加工位cell_失败()
    {
        var queue = new MemoryDispatchQueue();
        var planner = Create(routes: new FixedRoutes { MissingPositionCell = true }, queue: queue);
        var ok = await planner.EnqueueUnloadAsync(Ctx(), isOk: true, CancellationToken.None);
        Assert.That(ok, Is.False);
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public async Task 入队成功_只写下料请求()
    {
        var queue = new MemoryDispatchQueue();
        var planner = Create(queue: queue);
        var ctx = Ctx();
        ctx.MaterialId = "M-1";
        var ok = await planner.EnqueueUnloadAsync(ctx, isOk: true, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(queue.Dequeue()!.Phase, Is.EqualTo(PositionPhase.Unload));
            Assert.That(ctx.Phase, Is.EqualTo(PositionPhase.Unload));
            Assert.That(ctx.CurrentTaskId, Is.Null);
        });
    }

    private static PositionContext Ctx()
        => new() { EquipmentId = 30, PositionId = 1, State = PositionState.DoneOk };

    private static UnloadDispatchPlanner Create(
        long? ngFrame = null,
        long? downloadFrame = 44,
        bool hasSubsequent = false,
        IReadOnlyList<long>? nextEquipments = null,
        IRouteResolver? routes = null,
        IDispatchQueue? queue = null)
    {
        var equipment = new PlannerEquipment
        {
            NgFrameId = ngFrame,
            DownloadFrameId = downloadFrame,
            HasSubsequent = hasSubsequent,
            NextEquipments = nextEquipments ?? Array.Empty<long>()
        };
        return new UnloadDispatchPlanner(
            equipment,
            routes ?? new FixedRoutes(),
            new NoopAlarms(),
            queue ?? new MemoryDispatchQueue(),
            NullLogger.Instance,
            (eq, _) => Task.FromResult(new EquipmentFrameBindingIds(null, downloadFrame)),
            (_, _) => Task.FromResult<WorkLineRef?>(new WorkLineRef(10, "LINE-A")),
            (_, _) => { });
    }
}
