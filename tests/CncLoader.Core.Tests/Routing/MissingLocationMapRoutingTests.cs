using CncLoader.Communication.State;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// 安全红线「缺 LOCATION_MAP 拒发，禁止 FRAME-{id} 假码」的直接覆盖。
/// 此前派工门禁只测到 fake 路由（永远解析成功且返回 FRAME-{id} 形状），红线本身未被走到。
/// 上半部分打生产 <see cref="RouteResolver"/>，下半部分打真实调度器的拒发行为。
/// </summary>
[TestFixture]
public sealed class MissingLocationMapRoutingTests
{
    private const long Eq = 30;
    private const long Pos = 1;
    private const long FrameId = 77;

    // ─── 生产 RouteResolver：缺映射一律 null，绝不编码 ──────────────────────

    [Test]
    public async Task FrameCell_MapMissing_ReturnsNull_AndNeverFabricatesFrameCode()
    {
        var resolver = new RouteResolver(new StubLocationMap(), NullLogger<RouteResolver>.Instance);

        var cell = await resolver.ResolveFrameCellAsync(FrameId);

        Assert.That(cell, Is.Null, "缺 LOCATION_MAP 必须返回 null，禁止生成 FRAME-{id} 假码");
    }

    [Test]
    public async Task FrameCell_MapPresent_ReturnsMappedRcsCodeOnly()
    {
        var map = new StubLocationMap();
        map.SeedFrame(FrameId, "cell", "RK-A-01");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        var cell = await resolver.ResolveFrameCellAsync(FrameId);

        Assert.That(cell, Is.EqualTo("RK-A-01"));
        Assert.That(cell, Does.Not.StartWith("FRAME-"), "只能回 LOCATION_MAP 真实编码");
    }

    [Test]
    public async Task PositionCell_MapMissing_ReturnsNull()
    {
        var resolver = new RouteResolver(new StubLocationMap(), NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolvePositionCellAsync(Eq, Pos), Is.Null);
    }

    [TestCase(true, false, TestName = "上料缺 LOAD_AREA 命名点")]
    [TestCase(false, true, TestName = "上料缺加工位 cell")]
    [TestCase(true, true, TestName = "上料两端全缺")]
    public async Task Upload_AnyEndpointMissing_ReturnsNull(bool missingArea, bool missingCell)
    {
        var map = new StubLocationMap();
        if (!missingArea) map.SeedArea(RouteResolver.LoadAreaName, "LOAD-01");
        if (!missingCell) map.SeedPosition(Eq, Pos, "cell", "CELL-01");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolveUploadAsync(Eq, Pos), Is.Null,
            "任一端缺映射即整条路由不可用，不得半路编码");
    }

    [TestCase(true, false, TestName = "下料缺加工位 cell")]
    [TestCase(false, true, TestName = "下料缺 UNLOAD_AREA 命名点")]
    public async Task Unload_AnyEndpointMissing_ReturnsNull(bool missingCell, bool missingArea)
    {
        var map = new StubLocationMap();
        if (!missingCell) map.SeedPosition(Eq, Pos, "cell", "CELL-01");
        if (!missingArea) map.SeedArea(RouteResolver.UnloadAreaName, "UNLOAD-01");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolveUnloadAsync(Eq, Pos), Is.Null);
    }

    [Test]
    public async Task Upload_BothEndpointsMapped_ReturnsMappedCodes()
    {
        var map = new StubLocationMap();
        map.SeedArea(RouteResolver.LoadAreaName, "LOAD-01");
        map.SeedPosition(Eq, Pos, "cell", "CELL-01");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolveUploadAsync(Eq, Pos), Is.EqualTo(("LOAD-01", "CELL-01")));
    }

    // ─── 真实调度器：路由解析失败即拒发 ────────────────────────────────────

    [Test]
    public async Task Dispatch_UploadRouteUnmapped_MustNotReserveOrSendRcs()
    {
        var (slots, tasks, plc, alarms, scheduler) = BuildUploadHarness(
            new FixedRoutes { MissingFrameCell = true });

        scheduler.ProbeMarkReconciled();
        scheduler.ProbeSeedUploadCandidate(Eq, Pos);
        await scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(slots.ReserveTakeCount, Is.EqualTo(0), "路由未配置不得预记");
            Assert.That(tasks.DispatchTransitCount, Is.EqualTo(0), "路由未配置不得下发 RCS");
            Assert.That(plc.WriteCount, Is.EqualTo(0), "拒发不得写 POS_TEST_START");
            Assert.That(alarms.NotFoundCount, Is.EqualTo(1), "须落一条人工介入告警");
            Assert.That(alarms.LastNotFoundReason, Does.Contain("LOCATION_MAP"),
                "告警须指明缺 LOCATION_MAP，便于现场直接补录");
        });
    }

    [Test]
    public async Task Dispatch_UploadRouteMapped_StillDispatchesNormally()
    {
        var (slots, tasks, _, alarms, scheduler) = BuildUploadHarness(new FixedRoutes());

        scheduler.ProbeMarkReconciled();
        scheduler.ProbeSeedUploadCandidate(Eq, Pos);
        await scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(tasks.DispatchTransitCount, Is.EqualTo(1));
            Assert.That(alarms.NotFoundCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Unload_PositionCellUnmapped_MustNotEnqueue()
    {
        var (slots, tasks, _, alarms, scheduler) = BuildUploadHarness(
            new FixedRoutes { MissingPositionCell = true });

        scheduler.ProbeMarkReconciled();
        var enqueued = await scheduler.ProbeEnqueueUnloadAsync(Eq, Pos, isOk: true, materialId: "M-1");
        await scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.False, "下料源 cell 未配置不得入队");
            Assert.That(scheduler.ProbeQueueCount, Is.EqualTo(0));
            Assert.That(slots.ReservePutCount, Is.EqualTo(0));
            Assert.That(tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(alarms.NotFoundCount, Is.EqualTo(1));
            Assert.That(alarms.LastNotFoundReason, Does.Contain("LOCATION_MAP"));
        });
    }

    private static (TracingSlots Slots, TracingTaskService Tasks, TracingPlcOps Plc,
        NoopAlarms Alarms, PositionScheduler Scheduler) BuildUploadHarness(IRouteResolver routes)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, 1, Eq);
        store.BindFrame(Eq, DispatchGateHarness.UploadFrame, FrameRole.Upload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, DispatchGateHarness.UploadFrame,
            equipment: equipment, store: store);
        var tasks = new TracingTaskService(trace);
        var plc = new TracingPlcOps(trace);
        var alarms = new NoopAlarms();
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, alarms: alarms, routingStore: store, routes: routes);
        return (slots, tasks, plc, alarms, scheduler);
    }

    /// <summary>只读位置映射桩：未 Seed 的查询一律 null，用来复现「LOCATION_MAP 未录入」。</summary>
    private sealed class StubLocationMap : ILocationMapService
    {
        private readonly List<LocationMapItem> _items = new();

        public void SeedArea(string locName, string rcsCode)
            => _items.Add(new LocationMapItem
            {
                LocType = "AREA", LocName = locName, RcsCode = rcsCode, RcsType = "station"
            });

        public void SeedPosition(long equipmentId, long positionId, string rcsType, string rcsCode)
            => _items.Add(new LocationMapItem
            {
                LocType = "POSITION", EquipmentId = equipmentId, PositionId = positionId,
                RcsCode = rcsCode, RcsType = rcsType
            });

        public void SeedFrame(long frameId, string rcsType, string rcsCode)
            => _items.Add(new LocationMapItem
            {
                LocType = "FRAME", FrameId = frameId, RcsCode = rcsCode, RcsType = rcsType
            });

        public Task<IReadOnlyList<LocationMapItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LocationMapItem>>(_items);

        public Task<long> SaveAsync(LocationMapItem item, string? author = null, CancellationToken ct = default)
            => throw new NotSupportedException("只读桩");

        public Task DeleteAsync(long id, CancellationToken ct = default)
            => throw new NotSupportedException("只读桩");

        public Task<LocationMapItem?> ResolvePositionAsync(long equipmentId, long? positionId,
            string rcsType, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(i =>
                i.EquipmentId == equipmentId && i.PositionId == positionId && i.RcsType == rcsType));

        public Task<LocationMapItem?> ResolveFrameAsync(long frameId, string rcsType, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(i => i.FrameId == frameId && i.RcsType == rcsType));

        public Task<LocationMapItem?> ResolveAreaAsync(string locName, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(i => i.LocType == "AREA" && i.LocName == locName));

        public Task<LocationMapItem?> ResolveByRcsCodeAsync(string rcsCode, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(i => i.RcsCode == rcsCode));
    }
}
