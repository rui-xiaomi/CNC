using CncLoader.Communication.Rcs;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 审查补充第二组（D13）：Inventory → 真实 Identify Service Final 门禁。
/// 生产 Inventory 仅发起 identifyQR（不调用 Grab）。
/// </summary>
[TestFixture]
public sealed class InventoryRoutingGateTests
{
    private FakeLocationMapForRouting _loc = null!;
    private FakeFrameRoutingStore _frames = null!;
    private MutableEquipmentRoutingStore _eq = null!;
    private CountingManagedRouteResolver _resolver = null!;
    private CountingRoutingValidator _validator = null!;
    private FakeRcsHttpClient _client = null!;
    private MutableRcsTaskStore _tasks = null!;
    private List<string> _order = null!;
    private NoopAlarms _alarms = null!;
    private RcsCallbackNotifier _notifier = null!;
    private InventoryService _inventory = null!;
    private List<InventoryResultEvent> _completed = null!;

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
        _resolver = new CountingManagedRouteResolver(
            new ManagedDispatchRouteResolver(
                _loc, _frames, NullLogger<ManagedDispatchRouteResolver>.Instance))
        {
            OrderSink = _order
        };
        var equipmentForValidator = new TracingEquipmentConfigService(_eq, new CallTrace());
        _validator = new CountingRoutingValidator(
            new RoutingAvailabilityValidator(
                _eq, equipmentForValidator, _frames,
                NullLogger<RoutingAvailabilityValidator>.Instance))
        {
            OrderSink = _order
        };

        _client = new FakeRcsHttpClient { OrderSink = _order };
        _tasks = new MutableRcsTaskStore { OrderSink = _order };
        var taskSvc = new RcsTaskService(
            _client, _tasks, new InvNoopMsgLog(), new TrackingCallbackProcessor(),
            _resolver, _validator, NullLogger<RcsTaskService>.Instance);

        var equipment = new StoreBackedFrameBindEquipment(_eq);
        _alarms = new NoopAlarms();
        _notifier = new RcsCallbackNotifier();
        _completed = new List<InventoryResultEvent>();

        _inventory = new InventoryService(
            taskSvc, _loc, new TrackingSlotsForClosure(), equipment, _alarms,
            new EmptyDispatchQueue(), new IdleChangeFrameOrchestrator(),
            _notifier, NullLogger<InventoryService>.Instance);
        _inventory.InventoryCompleted += (_, e) => _completed.Add(e);
    }

    [Test]
    public void Chain_Documents_Inventory_OnlyIdentify_NoGrab()
    {
        // 生产 StartInventoryAsync：ResolveFrame(station|shelf) → DispatchIdentifyAsync；无 Grab
        Assert.Multiple(() =>
        {
            Assert.That(typeof(InventoryService).GetMethod(nameof(InventoryService.StartInventoryAsync)),
                Is.Not.Null);
            Assert.That(typeof(IRcsTaskService).GetMethod(nameof(IRcsTaskService.DispatchIdentifyAsync)),
                Is.Not.Null);
            Assert.That(_loc.SnapshotByCode(FrameShelfCode).Single().RcsType, Is.EqualTo("shelf"));
            Assert.That(_loc.SnapshotByCode(FrameShelfCode).Single().EquipmentId, Is.Null,
                "FRAME shelf EquipmentId=null 合法");
            Assert.That(_loc.SnapshotByCode(FrameShelfCode).Single().FrameId, Is.EqualTo(FrameIdTransit));
        });
    }

    [Test]
    public async Task Inventory_AllActive_Baseline_StartsIdentify_ViaRealService()
    {
        var taskId = await _inventory.StartInventoryAsync(FrameIdTransit, 101, 3, "inv-red");

        Assert.Multiple(() =>
        {
            Assert.That(taskId, Is.Not.Null.And.Not.Empty, "合法盘点应返回 taskId（基线）");
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.ExcuteCount, Is.EqualTo(1), "须经真实 Excute（identifyQR）");
            Assert.That(_client.TransitCount, Is.EqualTo(0), "盘点不走 transit");
            Assert.That(_tasks.Created.Single().Kind, Is.EqualTo(RcsTaskKind.Identify));
            Assert.That(_tasks.Created.Single().FromCode, Is.EqualTo(FrameShelfCode));
            Assert.That(_loc.ResolveFrameCallCount, Is.GreaterThanOrEqualTo(1),
                "业务层 ResolveFrame 作 Pre（station 未命中后 shelf）");
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0), "成功路径不告警");
            Assert.That(_completed, Is.Empty, "成功发起不立刻 InventoryCompleted");
        });
    }

    [Test]
    public async Task Inventory_AllActive_MustRunServiceFinalGate()
    {
        await _inventory.StartInventoryAsync(FrameIdTransit, 101, 3, "inv-red");

        Assert.Multiple(() =>
        {
            Assert.That(_resolver.CallCount, Is.GreaterThanOrEqualTo(1),
                $"Inventory→Identify 须经 Service Resolver Final；顺序={string.Join("→", _order)}");
            Assert.That(_validator.CallCount, Is.GreaterThanOrEqualTo(1),
                "Inventory→Identify 须经 Service Validator Final");
        });
    }

    [Test]
    public async Task Inventory_FrameMapDisabledBeforeStart_NoCreate_NoExcute()
    {
        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");
        // cell 同行 FrameId 一并禁用，避免误命中其他类型
        _loc.SetStateByCode(FrameCellCode, remove: false, state: "1");

        var taskId = await _inventory.StartInventoryAsync(FrameIdTransit, 101, 3, "inv-red");

        Assert.Multiple(() =>
        {
            Assert.That(taskId, Is.EqualTo(""));
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
            Assert.That(_completed.Count, Is.EqualTo(1));
            Assert.That(_completed[0].State, Is.EqualTo("FAILED"));
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0),
                "路由配置不可用不置工位 Alarm（D8）");
        });
    }

    [Test]
    public async Task Inventory_SameInstance_SecondAfterDisable_NoExtraExcute()
    {
        var first = await _inventory.StartInventoryAsync(FrameIdTransit, 101, 3, "inv-red");
        Assert.That(first, Is.Not.Empty);
        Assert.That(_client.ExcuteCount, Is.EqualTo(1));
        var firstCreate = _tasks.CreateCount;

        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");
        _loc.SetStateByCode(FrameCellCode, remove: false, state: "1");
        _completed.Clear();
        _resolver.Reset();
        _validator.Reset();

        var second = await _inventory.StartInventoryAsync(FrameIdTransit, 101, 3, "inv-red");

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(""));
            Assert.That(_client.ExcuteCount, Is.EqualTo(1), "第二次不得再 Excute");
            Assert.That(_tasks.CreateCount, Is.EqualTo(firstCreate));
            Assert.That(_completed.Single().State, Is.EqualTo("FAILED"));
        });
    }

    [Test]
    public async Task Inventory_AfterBusinessResolve_MapDisabled_IdentifyStillSends_MissingFinal()
    {
        // ResolveFrame 第 1 次（station 未命中计次？）——按 frameId 计数：station 尝试 + shelf 尝试各 +1
        // Inventory 先 station 再 shelf：两次 ResolveFrameAsync(frameId,*)
        // 设 after=2：第二次（shelf）返回活动后禁用 → 随后 Identify 无 Final 仍 Create
        _loc.DisableFrameMapAfterResolveCount(FrameIdTransit, resolveCount: 2);

        var taskId = await _inventory.StartInventoryAsync(FrameIdTransit, 101, 3, "inv-red");

        Assert.Multiple(() =>
        {
            Assert.That(_loc.ResolveFrameCallCount, Is.GreaterThanOrEqualTo(2));
            Assert.That(_loc.SnapshotByCode(FrameShelfCode).All(r => r.State == "1"), Is.True,
                "业务 Pre 返回后 Map 已禁用");
            Assert.That(taskId, Is.EqualTo(""),
                $"Service Final 须拒发；Actual taskId={taskId} Create={_tasks.CreateCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
            Assert.That(_resolver.CallCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0), "Final 路由拒发不置 Alarm");
        });
    }

    [Test]
    public async Task Inventory_DoesNotDispatchGrab()
    {
        await _inventory.StartInventoryAsync(FrameIdTransit, 101, 3, "inv-red");

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.Created.All(c => c.Kind == RcsTaskKind.Identify), Is.True);
            Assert.That(_tasks.Created.Any(c => c.Kind == RcsTaskKind.Grab), Is.False,
                "当前生产盘点形态：仅 Identify；Grab 分阶段不存在");
        });
    }

    [Test]
    public async Task Inventory_DirectIdentifyEntry_DisabledStation_StillSends_ProvesServiceGap()
    {
        // 绕过 Inventory Pre，直接打真实 Service：证明发送边界自身无门禁
        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");
        var taskSvc = new RcsTaskService(
            _client, _tasks, new InvNoopMsgLog(), new TrackingCallbackProcessor(),
            _resolver, _validator, NullLogger<RcsTaskService>.Instance);

        var result = await taskSvc.DispatchIdentifyAsync(new IdentifyDispatchArgs
        {
            WorkLineId = LineId, LineCode = LineCode, Priority = 8,
            Station = FrameShelfCode, PosStart = 101, Count = 1,
            FrameId = FrameIdTransit, Author = "direct-red"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False,
                "直接 Service Identify 禁用码须拒（Inventory Pre 挡不住直调）");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0));
            Assert.That(_resolver.CallCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    private sealed class InvNoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    }
}
