using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 审查补充（D11）：类型化受管端点 — 真实 <see cref="ManagedDispatchRouteResolver"/>。
/// </summary>
[TestFixture]
public sealed class TypedManagedRouteResolverTests
{
    private FakeLocationMapForRouting _loc = null!;
    private FakeFrameRoutingStore _frames = null!;
    private ManagedDispatchRouteResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _loc = new FakeLocationMapForRouting();
        SeedStandardAreas(_loc);
        _loc.Seed(PositionCellMap());
        _loc.Seed(FrameShelf());
        _loc.Seed(FrameCell());
        _frames = new FakeFrameRoutingStore();
        _frames.Seed(FrameIdTransit);
        _frames.Seed(FrameIdDownload);
        _resolver = new ManagedDispatchRouteResolver(
            _loc, _frames, NullLogger<ManagedDispatchRouteResolver>.Instance);
    }

    // ─── RED 1｜活动 AREA 无 EquipmentId ───────────────────────────

    [Test]
    public async Task Red1_ActiveArea_WithoutEquipmentId_Resolves_AsArea()
    {
        AssertAreaShape(_loc.SnapshotByCode(LoadAreaCode).Single(), LocLoadArea);

        var resolved = await _resolver.ResolveAsync(LoadAreaCode, PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Resolved));
            Assert.That(resolved.IsResolved, Is.True);
            Assert.That(resolved.Context, Is.Not.Null);
            Assert.That(resolved.Context!.FromCode, Is.EqualTo(LoadAreaCode));
            Assert.That(resolved.Context.ToCode, Is.EqualTo(PositionCell));
            Assert.That(resolved.Context.FromEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Area));
            Assert.That(resolved.Context.SourceEquipmentId, Is.EqualTo(0),
                "EndpointKind=AREA 时 Equipment 依赖 NotApplicable");
            Assert.That(resolved.Context.DestEquipmentId.IsApplicable, Is.True);
            Assert.That(resolved.Context.DestEquipmentId.Id, Is.EqualTo(EqId));
            Assert.That(resolved.Context.ToEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Position));
            Assert.That(resolved.Status, Is.Not.EqualTo(ManagedDispatchRouteStatus.NotFound));
            Assert.That(resolved.Status, Is.Not.EqualTo(ManagedDispatchRouteStatus.InvalidRelationship));
        });
    }

    // ─── RED 2｜活动 FRAME 无 EquipmentId ──────────────────────────

    [Test]
    public async Task Red2_ActiveFrame_WithoutEquipmentId_Resolves_AsFrame()
    {
        AssertFrameShape(_loc.SnapshotByCode(FrameCellCode).Single());

        var resolved = await _resolver.ResolveAsync(FrameCellCode, EmptyBufferCode);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Resolved));
            Assert.That(resolved.IsResolved, Is.True);
            Assert.That(resolved.Context, Is.Not.Null);
            Assert.That(resolved.Context!.FromCode, Is.EqualTo(FrameCellCode));
            Assert.That(resolved.Context.ToCode, Is.EqualTo(EmptyBufferCode));
            Assert.That(resolved.Context.FromEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Frame));
            Assert.That(resolved.Context.SourceEquipmentId, Is.EqualTo(0));
            Assert.That(resolved.Context.SourceFrameId.IsApplicable, Is.True);
            Assert.That(resolved.Context.SourceFrameId.Id, Is.EqualTo(FrameIdTransit));
            Assert.That(resolved.Context.DestEquipmentId.IsApplicable, Is.False);
            Assert.That(resolved.Context.ToEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Area));
        });

        Assert.That(typeof(ManagedDispatchRouteResolver).GetConstructors()[0].GetParameters()
                .Any(p => p.ParameterType == typeof(IFrameRoutingStore)),
            Is.True,
            "须注入 IFrameRoutingStore 读取 Frame.STATE");
        Assert.That(_frames.FindCallCount, Is.GreaterThanOrEqualTo(1));
    }

    // ─── 契约 3｜AREA/FRAME 禁用 ─────────────────────────────────

    [Test]
    public async Task Contract3_DisabledArea_IsDisabled_NotNotFound()
    {
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");

        var resolved = await _resolver.ResolveAsync(LoadAreaCode, PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.False);
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Disabled));
            Assert.That(resolved.Status, Is.Not.EqualTo(ManagedDispatchRouteStatus.NotFound));
        });
    }

    [Test]
    public async Task Contract3_DisabledFrame_IsDisabled_NotNotFound()
    {
        _loc.SetStateByCode(FrameCellCode, remove: false, state: "1");

        var resolved = await _resolver.ResolveAsync(FrameCellCode, EmptyBufferCode);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.False);
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Disabled));
            Assert.That(resolved.Status, Is.Not.EqualTo(ManagedDispatchRouteStatus.NotFound));
        });
    }

    // ─── 契约 4｜POSITION 缺 EquipmentId ─────────────────────────

    [Test]
    public async Task Contract4_PositionMissingEquipmentId_InvalidRelationship()
    {
        const string badCode = "601299";
        _loc.Seed(PositionMissingEquipment(badCode));

        var resolved = await _resolver.ResolveAsync(badCode, PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.False);
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.InvalidRelationship));
        });
    }

    // ─── 契约 5｜同码歧义 ────────────────────────────────────────

    [Test]
    public async Task Contract5_TwoActiveSameType_Ambiguous()
    {
        _loc.Seed(new LocationMapItem
        {
            Id = 80, LocType = "AREA", LocName = LocLoadArea, RcsCode = LoadAreaCode,
            RcsType = "station", EquipmentId = null, PositionId = null, FrameId = null
        });

        var a = await _resolver.ResolveAsync(LoadAreaCode, PositionCell);
        var b = await _resolver.ResolveAsync(LoadAreaCode, PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(a.Status, Is.EqualTo(ManagedDispatchRouteStatus.Ambiguous));
            Assert.That(b.Status, Is.EqualTo(a.Status));
            Assert.That(a.IsResolved, Is.False);
        });
    }

    [Test]
    public async Task Contract5_ActiveAreaAndActivePosition_SameCode_Ambiguous()
    {
        const string shared = "SHARED-CODE";
        _loc.Seed(Area(LocLoadArea, shared, 70));
        _loc.Seed(new LocationMapItem
        {
            Id = 71, LocType = "POSITION", EquipmentId = EqId, PositionId = 2,
            RcsCode = shared, RcsType = "cell"
        });

        var resolved = await _resolver.ResolveAsync(shared, PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Ambiguous));
            Assert.That(resolved.IsResolved, Is.False);
        });
    }

    [Test]
    public async Task Contract5_OneActiveOneDisabled_SameCode_UsesActiveOnly()
    {
        _loc.Seed(new LocationMapItem
        {
            Id = 81, LocType = "AREA", LocName = LocLoadArea, RcsCode = LoadAreaCode,
            RcsType = "station", EquipmentId = null
        }, state: "1");

        var resolved = await _resolver.ResolveAsync(LoadAreaCode, PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Status, Is.Not.EqualTo(ManagedDispatchRouteStatus.Ambiguous));
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Resolved));
            Assert.That(resolved.Context!.FromEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Area));
        });
    }

    [Test]
    public async Task Contract5_TwoDisabled_SameCode_Disabled()
    {
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");
        _loc.Seed(Area(LocLoadArea, LoadAreaCode, 82), state: "1");

        var resolved = await _resolver.ResolveAsync(LoadAreaCode, PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Disabled));
            Assert.That(resolved.IsResolved, Is.False);
        });
    }

    // ─── 补充：Frame 实体 / 未知 LocType / Equipment 查询隔离 ─────

    [Test]
    public async Task Supplement_FrameEntityDisabled_IsDisabled()
    {
        _frames.SetState(FrameIdTransit, "1");

        var resolved = await _resolver.ResolveAsync(FrameCellCode, EmptyBufferCode);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.False);
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Disabled));
            Assert.That(resolved.EntityKind, Is.EqualTo("Frame"));
        });
    }

    [Test]
    public async Task Supplement_FrameIdMissing_IsNotFound()
    {
        _frames.Remove(FrameIdTransit);

        var resolved = await _resolver.ResolveAsync(FrameCellCode, EmptyBufferCode);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.False);
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.NotFound));
            Assert.That(resolved.EntityKind, Is.EqualTo("Frame"));
        });
    }

    [Test]
    public async Task EquipmentStation_Resolves_AsEquipment()
    {
        _loc.Seed(EquipmentStation());
        var resolved = await _resolver.ResolveAsync(FrameShelfCode, EquipmentStationCode);
        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.True);
            Assert.That(resolved.Context!.FromEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Frame));
            Assert.That(resolved.Context.ToEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Equipment));
            Assert.That(resolved.Context.ToEndpoint.EquipmentId, Is.EqualTo(EqId));
            Assert.That(resolved.Context.DestEquipmentId.IsApplicable, Is.True);
        });
    }

    [Test]
    public async Task Supplement_UnknownLocType_InvalidRelationship()
    {
        const string code = "UNK-TYPE-1";
        _loc.Seed(new LocationMapItem
        {
            Id = 90, LocType = "NOT_A_KIND", LocName = "EQ1", RcsCode = code,
            RcsType = "station", EquipmentId = EqId
        });

        var resolved = await _resolver.ResolveAsync(code, PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.False);
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.InvalidRelationship));
        });
    }

    [Test]
    public async Task Supplement_AreaResolve_DoesNotQueryEquipmentStore()
    {
        // Resolver 本身不依赖 IEquipmentRoutingStore；AREA→AREA 路径零 Frame 以外权威机台查询
        var before = _frames.FindCallCount;
        var resolved = await _resolver.ResolveAsync(LoadAreaCode, EmptyBufferCode);
        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.True);
            Assert.That(resolved.Context!.FromEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Area));
            Assert.That(resolved.Context.ToEndpoint!.Kind, Is.EqualTo(ManagedEndpointKind.Area));
            Assert.That(resolved.Context.DestEquipmentId.IsApplicable, Is.False);
            Assert.That(_frames.FindCallCount, Is.EqualTo(before), "AREA 不得查 Frame");
        });
    }

    [Test]
    public async Task Supplement_FrameWithoutEquipment_DoesNotRequireEquipmentId()
    {
        var resolved = await _resolver.ResolveAsync(FrameCellCode, EmptyBufferCode);
        Assert.Multiple(() =>
        {
            Assert.That(resolved.IsResolved, Is.True);
            Assert.That(resolved.Context!.SourceEquipmentId, Is.EqualTo(0));
            Assert.That(resolved.Context.DestEquipmentId.IsApplicable, Is.False);
            Assert.That(_frames.FindArgs, Does.Contain(FrameIdTransit));
        });
    }

    [Test]
    public async Task Supplement_PositionStillRequiresEquipmentId()
    {
        const string bad = "601298";
        _loc.Seed(new LocationMapItem
        {
            Id = 98, LocType = "POSITION", EquipmentId = null, PositionId = 1,
            RcsCode = bad, RcsType = "cell"
        });

        var resolved = await _resolver.ResolveAsync(bad, PositionCell);
        Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.InvalidRelationship));
    }
}
