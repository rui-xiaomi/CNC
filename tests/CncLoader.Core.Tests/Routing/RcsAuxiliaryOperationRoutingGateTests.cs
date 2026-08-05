using CncLoader.Communication.Rcs;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 审查补充第二组（D13）：Grab / Identify 发送边界 Final 门禁。
/// 真实 RcsTaskService + Resolver + Validator。
/// </summary>
[TestFixture]
public sealed class RcsAuxiliaryOperationRoutingGateTests
{
    private FakeLocationMapForRouting _loc = null!;
    private FakeFrameRoutingStore _frames = null!;
    private MutableEquipmentRoutingStore _eq = null!;
    private CountingManagedRouteResolver _resolver = null!;
    private CountingRoutingValidator _validator = null!;
    private FakeRcsHttpClient _client = null!;
    private MutableRcsTaskStore _tasks = null!;
    private List<string> _order = null!;
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

        _order = new List<string>();
        var realResolver = new ManagedDispatchRouteResolver(
            _loc, _frames, NullLogger<ManagedDispatchRouteResolver>.Instance);
        _resolver = new CountingManagedRouteResolver(realResolver) { OrderSink = _order };

        var equipment = new TracingEquipmentConfigService(_eq, new CallTrace());
        var realValidator = new RoutingAvailabilityValidator(
            _eq, equipment, _frames, NullLogger<RoutingAvailabilityValidator>.Instance);
        _validator = new CountingRoutingValidator(realValidator) { OrderSink = _order };

        _client = new FakeRcsHttpClient { OrderSink = _order };
        _tasks = new MutableRcsTaskStore { OrderSink = _order };
        _svc = new RcsTaskService(
            _client, _tasks, new AuxNoopMsgLog(), new TrackingCallbackProcessor(),
            _resolver, _validator, NullLogger<RcsTaskService>.Instance);
    }

    // ─── 文档化：真实参数与种子形状 ───────────────────────────────

    [Test]
    public void Chain_Documents_GrabIdentify_SeedShapes()
    {
        AssertAreaShape(_loc.SnapshotByCode(LoadAreaCode).Single(), LocLoadArea);
        AssertAreaShape(_loc.SnapshotByCode(UnloadAreaCode).Single(), LocUnloadArea);
        AssertFrameShape(_loc.SnapshotByCode(FrameShelfCode).Single());
        Assert.Multiple(() =>
        {
            Assert.That(_loc.SnapshotByCode(FrameShelfCode).Single().RcsType, Is.EqualTo("shelf"));
            Assert.That(_loc.SnapshotByCode(LoadAreaCode).Single().EquipmentId, Is.Null);
            Assert.That(_loc.SnapshotByCode(FrameShelfCode).Single().EquipmentId, Is.Null);
            Assert.That(typeof(GrabDispatchArgs).GetProperty(nameof(GrabDispatchArgs.SrcStation)), Is.Not.Null);
            Assert.That(typeof(GrabDispatchArgs).GetProperty(nameof(GrabDispatchArgs.DstStation)), Is.Not.Null);
            Assert.That(typeof(IdentifyDispatchArgs).GetProperty(nameof(IdentifyDispatchArgs.Station)), Is.Not.Null);
        });
    }

    // ─── Grab：合法基线 PASS（业务成功）+ 门禁缺口 RED ───────────

    [Test]
    public async Task Grab_AllActive_AreaToArea_Baseline_Succeeds()
    {
        Assert.That(_loc.SnapshotByCode(LoadAreaCode).Single().EquipmentId, Is.Null);
        var result = await DispatchGrabAsync(LoadAreaCode, UnloadAreaCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, "合法 AREA(station)→AREA(station) 基线须成功");
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.ExcuteCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Grab_AllActive_MustRunFinalGate_BeforeCreate()
    {
        await DispatchGrabAsync(LoadAreaCode, UnloadAreaCode);

        Assert.Multiple(() =>
        {
            Assert.That(_resolver.CallCount, Is.GreaterThanOrEqualTo(1),
                $"Grab 须经 Resolver Final；顺序={string.Join("→", _order)}");
            Assert.That(_validator.CallCount, Is.GreaterThanOrEqualTo(1),
                "Grab 须经 Validator Final");
            Assert.That(_order, Does.Contain("Resolve"));
            Assert.That(_order.IndexOf("Resolve"), Is.LessThan(_order.IndexOf("CreateTask")),
                "Resolve 须在 CreateTask 前");
        });
    }

    [Test]
    public async Task Grab_SrcDisabled_MustReject_NoCreate_NoExcute()
    {
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");

        var result = await DispatchGrabAsync(LoadAreaCode, UnloadAreaCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "Src AREA 禁用须拒");
            Assert.That(result.FailureKind, Is.EqualTo(RcsFailureKind.RouteUnavailable)
                .Or.EqualTo(RcsFailureKind.ConfigurationUnavailable));
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Grab_UnknownSrc_MustReject_NoCreate_NoExcute()
    {
        var result = await DispatchGrabAsync("NO-SUCH-STATION", UnloadAreaCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "未知 Src 须 fail-closed");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Grab_AmbiguousSrc_MustReject_NoCreate_NoExcute()
    {
        _loc.Seed(new LocationMapItem
        {
            Id = 80, LocType = "AREA", LocName = LocLoadArea,
            RcsCode = LoadAreaCode, RcsType = "station",
            EquipmentId = null, PositionId = null, FrameId = null
        });

        var result = await DispatchGrabAsync(LoadAreaCode, UnloadAreaCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "同码双活动 Ambiguous 须拒");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Grab_DisableAfterFindWouldHaveFired_StillCreates_MissingFinal()
    {
        // Final-only：第 1 次 Find 快照前禁用，单次 Resolve 即拒
        _loc.DisableBeforeReturnOnFindCount(LoadAreaCode, findCount: 1);

        var result = await DispatchGrabAsync(LoadAreaCode, UnloadAreaCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "Final 前禁用须拒");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
            Assert.That(_resolver.CallCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public async Task Grab_CancelledToken_MustNotCreateOrExcute()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await DispatchGrabAsync(LoadAreaCode, UnloadAreaCode, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 允许取消异常；关键是不得有外部副作用
        }

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.CreateCount, Is.EqualTo(0), "Final 取消不得 Create");
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    // ─── Identify：合法 FRAME shelf 基线 + 门禁缺口 ───────────────

    [Test]
    public async Task Identify_AllActive_FrameShelf_Baseline_Succeeds()
    {
        AssertFrameShape(_loc.SnapshotByCode(FrameShelfCode).Single());
        Assert.That(_loc.SnapshotByCode(FrameShelfCode).Single().EquipmentId, Is.Null);
        var result = await DispatchIdentifyAsync(FrameShelfCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, "合法 FRAME(shelf) 识别基线须成功");
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.ExcuteCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Identify_AllActive_MustRunFinalGate_BeforeCreate()
    {
        await DispatchIdentifyAsync(FrameShelfCode);

        Assert.Multiple(() =>
        {
            Assert.That(_resolver.CallCount, Is.GreaterThanOrEqualTo(1),
                $"Identify 须经 Resolver；顺序={string.Join("→", _order)}");
            Assert.That(_validator.CallCount, Is.GreaterThanOrEqualTo(1),
                "Identify 须经 Validator");
            Assert.That(_frames.FindCallCount, Is.GreaterThanOrEqualTo(1),
                "须校验 Frame 实体活动");
        });
    }

    [Test]
    public async Task Identify_FrameMapDisabled_MustReject_NoCreate_NoExcute()
    {
        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");

        var result = await DispatchIdentifyAsync(FrameShelfCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "FRAME Map 禁用须拒");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Identify_FrameEntityDisabled_MustReject_NoCreate()
    {
        _frames.SetState(FrameIdTransit, "1");

        var result = await DispatchIdentifyAsync(FrameShelfCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "Frame 实体禁用须拒");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Identify_FrameEntityMissing_MustReject_NoCreate()
    {
        _frames.Remove(FrameIdTransit);

        var result = await DispatchIdentifyAsync(FrameShelfCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "Frame 不存在须拒");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Identify_UnknownStation_MustReject_NoCreate()
    {
        var result = await DispatchIdentifyAsync("NO-SUCH-SHELF");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "未知 station 须拒");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Identify_AmbiguousStation_MustReject_NoCreate()
    {
        _loc.Seed(new LocationMapItem
        {
            Id = 81, LocType = "FRAME", FrameId = FrameIdTransit,
            LocName = "dup-shelf", RcsCode = FrameShelfCode, RcsType = "shelf",
            EquipmentId = null, PositionId = null
        });

        var result = await DispatchIdentifyAsync(FrameShelfCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "同码歧义须拒");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Identify_AreaStation_MustReject_RoleNotFrame()
    {
        // 操作矩阵：手动识别允许 FRAME(shelf/station)；任意 AREA 不得作为 Identify 端点
        var result = await DispatchIdentifyAsync(LoadAreaCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "AREA 不得作为 Identify 端点");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Identify_SameInstance_SecondAfterDisable_MustNotExtraExcute()
    {
        var first = await DispatchIdentifyAsync(FrameShelfCode);
        Assert.That(first.Success, Is.True);
        Assert.That(_client.ExcuteCount, Is.EqualTo(1));
        var firstCreate = _tasks.CreateCount;

        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");
        _order.Clear();
        _resolver.Reset();
        _validator.Reset();

        var second = await DispatchIdentifyAsync(FrameShelfCode);

        Assert.Multiple(() =>
        {
            Assert.That(second.Success, Is.False, "同实例禁用后第二次须拒");
            Assert.That(_client.ExcuteCount, Is.EqualTo(1), "总 Excute 应仍为 1");
            Assert.That(_tasks.CreateCount, Is.EqualTo(firstCreate));
        });
    }

    [Test]
    public async Task Identify_DisableAfterFind_StillCreates_MissingFinal()
    {
        _loc.DisableBeforeReturnOnFindCount(FrameShelfCode, findCount: 1);

        var result = await DispatchIdentifyAsync(FrameShelfCode);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "Final 前禁用须拒");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
        });
    }

    private Task<RcsResult> DispatchGrabAsync(string src, string dst, CancellationToken ct = default)
        => _svc.DispatchGrabAsync(new GrabDispatchArgs
        {
            WorkLineId = LineId,
            LineCode = LineCode,
            Priority = 5,
            SrcStation = src,
            DstStation = dst,
            Items = new[]
            {
                new GrabItem { SrcNo = 1, SrcPos = 101, DstNo = 1, DstPos = 201, Data = "M1" }
            },
            Author = "aux-red"
        }, ct);

    private Task<RcsResult> DispatchIdentifyAsync(string station, CancellationToken ct = default)
        => _svc.DispatchIdentifyAsync(new IdentifyDispatchArgs
        {
            WorkLineId = LineId,
            LineCode = LineCode,
            Priority = 8,
            Station = station,
            PosStart = 101,
            Count = 3,
            FrameId = FrameIdTransit,
            Author = "aux-red"
        }, ct);

    private sealed class AuxNoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    }
}
