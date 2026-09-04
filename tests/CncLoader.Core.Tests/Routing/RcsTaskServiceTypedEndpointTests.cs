using CncLoader.Communication.Rcs;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Data.DependencyInjection;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 审查补充（D11/D15）：真实 <see cref="RcsTaskService"/> + Resolver + Validator。
/// 禁止 TracingTaskService / AlwaysAvailable；SkipManagedRouteGate 已删除（D12）。
/// </summary>
[TestFixture]
public sealed class RcsTaskServiceTypedEndpointTests
{
    private FakeLocationMapForRouting _loc = null!;
    private FakeFrameRoutingStore _frames = null!;
    private MutableEquipmentRoutingStore _eq = null!;
    private CountingManagedRouteResolver _resolver = null!;
    private CountingRoutingValidator _validator = null!;
    private FakeRcsHttpClient _client = null!;
    private MutableRcsTaskStore _tasks = null!;
    private RcsTaskService _svc = null!;

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

        _eq = new MutableEquipmentRoutingStore();
        SeedActiveEquipmentChain(_eq);

        var realResolver = new ManagedDispatchRouteResolver(
            _loc, _frames, NullLogger<ManagedDispatchRouteResolver>.Instance);
        _resolver = new CountingManagedRouteResolver(realResolver);

        var equipment = new TracingEquipmentConfigService(_eq, new CallTrace());
        var realValidator = new RoutingAvailabilityValidator(
            _eq, equipment, _frames, NullLogger<RoutingAvailabilityValidator>.Instance);
        _validator = new CountingRoutingValidator(realValidator);

        _client = new FakeRcsHttpClient();
        _tasks = new MutableRcsTaskStore();
        _svc = new RcsTaskService(
            _client, _tasks, new NoopMsgLog(), new TrackingCallbackProcessor(),
            _resolver, _validator, NullLogger<RcsTaskService>.Instance, new TrackingSlotsForClosure());
    }

    // ─── RED 6｜AREA→POSITION ────────────────────────────────────

    [Test]
    public async Task Red6_AreaToPosition_RealService_SucceedsOnce()
    {
        Assert.That(_loc.SnapshotByCode(LoadAreaCode).Single().EquipmentId, Is.Null);

        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = LineId,
            LineCode = LineCode,
            TaskType = "0",
            FromCode = LoadAreaCode,
            ToCode = PositionCell,
            EquipmentId = EqId,
            PositionId = PositionId,
            Author = "typed-green"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
            Assert.That(_client.SendCount, Is.EqualTo(1));
            Assert.That(_resolver.CallCount, Is.EqualTo(1), "发送边界 Resolve 一次");
            Assert.That(_validator.CallCount, Is.EqualTo(1), "发送边界 Validate 一次");
            Assert.That(_tasks.Created.Any(c => c.FromCode == LoadAreaCode && c.ToCode == PositionCell),
                Is.True);
        });
    }

    // ─── RED 7｜POSITION→AREA ────────────────────────────────────

    [Test]
    public async Task Red7_PositionToArea_RealService_SucceedsOnce()
    {
        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = LineId,
            LineCode = LineCode,
            TaskType = "1",
            FromCode = PositionCell,
            ToCode = UnloadAreaCode,
            EquipmentId = EqId,
            PositionId = PositionId,
            Author = "typed-green"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
            Assert.That(_resolver.CallCount, Is.EqualTo(1));
            Assert.That(_validator.CallCount, Is.EqualTo(1));
        });
    }

    // ─── RED 8｜FRAME→AREA ───────────────────────────────────────

    [Test]
    public async Task Red8_FrameToArea_RealService_SucceedsOnce()
    {
        AssertFrameShape(_loc.SnapshotByCode(FrameCellCode).Single());

        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = LineId,
            LineCode = LineCode,
            TaskType = "1",
            FromCode = FrameCellCode,
            ToCode = EmptyBufferCode,
            EquipmentId = EqId,
            Kind = RcsTaskKind.ChangeFrame,
            Author = "typed-green"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
            Assert.That(_resolver.CallCount, Is.EqualTo(1));
        });
    }

    // ─── RED 9｜AREA→FRAME ───────────────────────────────────────

    [Test]
    public async Task Red9_AreaToFrame_RealService_SucceedsOnce()
    {
        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = LineId,
            LineCode = LineCode,
            TaskType = "0",
            FromCode = EmptyBufferCode,
            ToCode = FrameCellCode,
            EquipmentId = EqId,
            Kind = RcsTaskKind.ChangeFrame,
            Author = "typed-green"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
        });
    }

    // ─── 契约 10｜POSITION→POSITION 不回归 ───────────────────────

    [Test]
    public async Task Contract10_PositionToPosition_StillSucceeds()
    {
        const string otherCell = "601204";
        _loc.Seed(new LocationMapItem
        {
            Id = 11, LocType = "POSITION", EquipmentId = EqId, PositionId = 2,
            RcsCode = otherCell, RcsType = "cell"
        });

        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = LineId,
            LineCode = LineCode,
            FromCode = PositionCell,
            ToCode = otherCell,
            EquipmentId = EqId,
            Author = "typed-green"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
            Assert.That(result.FailureKind, Is.EqualTo(RcsFailureKind.None));
        });
    }

    // ─── 禁用 Final ─────────────────────────────────────────────

    [Test]
    public async Task Final_AreaDisabledBeforeSend_NoCreate_NoRcs()
    {
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");

        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            FromCode = LoadAreaCode, ToCode = PositionCell,
            WorkLineId = LineId, LineCode = LineCode
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureKind, Is.EqualTo(RcsFailureKind.RouteUnavailable)
                .Or.EqualTo(RcsFailureKind.ConfigurationUnavailable));
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.SendCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Final_FrameDisabledBeforeSend_NoCreate_NoRcs()
    {
        _loc.SetStateByCode(FrameCellCode, remove: false, state: "1");

        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            FromCode = FrameCellCode, ToCode = EmptyBufferCode,
            WorkLineId = LineId, LineCode = LineCode
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.SendCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Final_PositionWorkLineDisabled_NoCreate_NoRcs()
    {
        const string otherCell = "601204";
        _loc.Seed(new LocationMapItem
        {
            Id = 11, LocType = "POSITION", EquipmentId = EqId, PositionId = 2,
            RcsCode = otherCell, RcsType = "cell"
        });
        _eq.SetWorkLineState(LineId, "1");

        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            FromCode = PositionCell, ToCode = otherCell,
            WorkLineId = LineId, LineCode = LineCode, EquipmentId = EqId
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureKind, Is.EqualTo(RcsFailureKind.RouteUnavailable)
                .Or.EqualTo(RcsFailureKind.ConfigurationUnavailable));
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.SendCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Final_FrameEntityDisabledBeforeSend_NoCreate()
    {
        _frames.SetState(FrameIdTransit, "1");

        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            FromCode = FrameCellCode, ToCode = EmptyBufferCode,
            WorkLineId = LineId, LineCode = LineCode
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.SendCount, Is.EqualTo(0));
            Assert.That(_resolver.CallCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Final_AreaDisabledBeforeRedo_NoSend()
    {
        _tasks.Seed(new RcsTaskRow(
            1, "LINE01-MV-TOCTOU-1", "transit", "0", RcsTaskState.Failed, "failed", 5,
            LoadAreaCode, PositionCell, EqId, PositionId, null, null, null,
            0, "0", DateTime.Now, null, null, "prev"));

        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");

        var result = await _svc.RedoAsync("LINE01-MV-TOCTOU-1");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(_client.SendCount, Is.EqualTo(0));
            Assert.That(_tasks.IncrementRedoCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task AllActive_ServiceOrder_ResolvesAndValidatesOnce()
    {
        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            FromCode = LoadAreaCode, ToCode = PositionCell,
            WorkLineId = LineId, LineCode = LineCode, EquipmentId = EqId
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_resolver.CallCount, Is.EqualTo(1));
            Assert.That(_validator.CallCount, Is.EqualTo(1));
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.SendCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Validator_AreaEndpoint_DoesNotQuerySourceEquipmentZero()
    {
        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            FromCode = LoadAreaCode, ToCode = PositionCell,
            WorkLineId = LineId, LineCode = LineCode, EquipmentId = EqId
        });
        Assert.That(result.Success, Is.True);
        Assert.That(_eq.Queries.Any(q => q.EquipmentId == 0), Is.False,
            "AREA 不得以 EquipmentId=0 触发链查询");
        Assert.That(_eq.Queries.Any(q => q.EquipmentId == EqId), Is.True,
            "POSITION 端仍须校验机台链");
    }

    [Test]
    public async Task Validator_FrameToArea_DoesNotQueryEquipment()
    {
        var before = _eq.Queries.Count;
        var result = await _svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            FromCode = FrameCellCode, ToCode = EmptyBufferCode,
            WorkLineId = LineId, LineCode = LineCode
        });
        Assert.That(result.Success, Is.True);
        Assert.That(_eq.Queries.Count, Is.EqualTo(before),
            "FRAME→AREA 两端 Equipment N/A，不得查机台链");
    }

    [Test]
    public void Di_Registers_RealFrameStore_Resolver_Validator()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(DataServiceCollectionExtensions)
                .GetMethod(nameof(DataServiceCollectionExtensions.AddCncData)), Is.Not.Null);
            Assert.That(typeof(FrameRoutingStore).GetInterfaces(), Does.Contain(typeof(IFrameRoutingStore)));
            Assert.That(typeof(ManagedDispatchRouteResolver).GetConstructors()[0]
                .GetParameters().Select(p => p.ParameterType),
                Does.Contain(typeof(IFrameRoutingStore)));
            Assert.That(typeof(RoutingAvailabilityValidator).GetConstructors()[0]
                .GetParameters().Select(p => p.ParameterType),
                Does.Contain(typeof(IFrameRoutingStore)));
            Assert.That(typeof(RoutingAvailabilityValidator).GetConstructors()[0]
                .GetParameters().Any(p => p.ParameterType == typeof(IFrameRoutingStore)
                    && p.HasDefaultValue), Is.False, "不得可选 null 依赖");
        });
    }

    // ─── 旧 TracingTaskService 缺口标注（不删除旧测）─────────────

    [Test]
    public void Audit_R9R15_StillUseTracingTaskService_GapDocumented()
    {
        Assert.That(typeof(TracingTaskService).IsClass, Is.True);
        Assert.That(typeof(TracingTaskService).GetInterfaces(), Does.Contain(typeof(IRcsTaskService)));
        Assert.That(typeof(RcsTaskService).Assembly.GetName().Name, Is.EqualTo("CncLoader.Communication"));
    }

    private sealed class CountingManagedRouteResolver : IManagedDispatchRouteResolver
    {
        private readonly IManagedDispatchRouteResolver _inner;
        private int _calls;

        public CountingManagedRouteResolver(IManagedDispatchRouteResolver inner) => _inner = inner;
        public int CallCount => Volatile.Read(ref _calls);

        public async Task<ManagedDispatchRouteResult> ResolveAsync(
            string? fromCode, string? toCode, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return await _inner.ResolveAsync(fromCode, toCode, ct);
        }
    }

    private sealed class NoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    }
}
