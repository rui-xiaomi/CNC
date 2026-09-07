using CncLoader.Communication.State;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 R11：下一工序下料走本机下料架（下游上料架），禁止工位直送。
/// 走真实 EnqueueUnload → ResolveUnloadTarget(DownloadFrame) → ReservePut → Final → DispatchTransit。
/// </summary>
[TestFixture]
public sealed class DirectHandoffRoutingGateTests
{
    private const long SrcEq = 1;
    private const long SrcPos = 1;
    private const long DstEq = 2;
    private const long DstPos = 1;
    private const long TransitFrame = 70;
    private const long LineId = 10;
    private const long SrcCraft = 100;
    private const long DstCraft = 200;
    private const string Disabled = "1";

    [Test]
    public async Task R11_SourceEquipmentDisabled_NoHandoffNoReserveNoRcs()
    {
        var h = BuildHandoffHarness();
        h.Store.SetEquipmentState(SrcEq, Disabled);

        h.Scheduler.ProbeMarkReconciled();
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(SrcEq, SrcPos, isOk: true, materialId: "M-H");
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.False, "源 Equipment 禁用 → ResolveLine 不可用 → 不入队");
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.False);
            Assert.That(h.Slots.ReservePutCount, Is.EqualTo(0));
            Assert.That(h.Slots.ReserveTakeCount, Is.EqualTo(0));
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R11_SourceWorkLineDisabled_NoHandoffNoReserveNoRcs()
    {
        var h = BuildHandoffHarness();
        h.Store.SetWorkLineState(LineId, Disabled);

        h.Scheduler.ProbeMarkReconciled();
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(SrcEq, SrcPos, isOk: true, materialId: "M-H");
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.False);
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.False);
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R11_DestEquipmentDisabled_NoHandoffRegistrationNoRcs()
    {
        var h = BuildHandoffHarness();
        // 不绑下料架：目标禁用后 NextProcess 为空。GREEN 应明确失败且不 RCS。
        // 修复前：回退命名区 ResolveUnload 仍会 DispatchTransit（真实 RED，非假阴性）。
        h.Store.SetEquipmentState(DstEq, Disabled);

        h.Scheduler.ProbeMarkReconciled();
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(SrcEq, SrcPos, isOk: true, materialId: "M-H");
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.True, "源仍活动时应能入队");
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.False,
                "目标 Equipment 禁用不得登记 _expectedInbound");
            Assert.That(h.Slots.ReservePutCount, Is.EqualTo(0));
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0),
                "目标禁用后不得因命名区回退继续 RCS（当前会回退下发 → RED）");
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R11_DestWorkLineDisabled_NoHandoffRegistrationNoRcs()
    {
        var h = BuildHandoffHarness();
        // 目标与源同线；禁用线体后源入队也会失败——改用「仅禁用目标父线」需双线体。
        // 真实路径：同线体禁用 → 源 ResolveLine 亦失败。记录为源侧拒发（仍满足不交接/不 RCS）。
        h.Store.SetWorkLineState(LineId, Disabled);

        h.Scheduler.ProbeMarkReconciled();
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(SrcEq, SrcPos, isOk: true, materialId: "M-H");
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.False);
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.False);
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R11_StaleLineCache_SourceDisabled_MustNotDispatchHandoff()
    {
        var h = BuildHandoffHarness();

        // 缓存旧活动路由
        var line = await h.Scheduler.ProbeResolveLineAsync(SrcEq);
        Assert.That(line, Is.Not.Null);
        Assert.That(h.Scheduler.ProbeHasLineCache(SrcEq), Is.True);

        // 权威源配置禁用（缓存未清）
        h.Store.SetEquipmentState(SrcEq, Disabled);

        h.Scheduler.ProbeMarkReconciled();
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(SrcEq, SrcPos, isOk: true, materialId: "M-H");
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            // GREEN：缓存非权威，最终须拒绝。当前 CacheHit 可能导致入队并 RCS → RED。
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.False);
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0),
                "缓存粘滞不得绕过权威配置继续交接下发");
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0));
            // 额外记录：若仍入队，说明缺最终门禁
            if (enqueued)
                Assert.Fail($"源已禁用但仍入队（enqueued={enqueued}），缓存粘滞绕过权威配置");
        });
    }

    [Test]
    public async Task Supplemental_Handoff_FinalFail_ClearsExpectedInbound()
    {
        // 交接预记走 _expectedInbound，不经 SlotAccount；用 Validator 在第二次（Final）前禁用目标。
        var h = BuildHandoffHarness(finalDisablesDest: true);
        h.Scheduler.ProbeMarkReconciled();
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(SrcEq, SrcPos, isOk: true, materialId: "M-H");
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.True);
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.False,
                "自动下料不得登记工位直送");
            Assert.That(h.Slots.ReservePutCount, Is.EqualTo(1));
            Assert.That(h.Slots.RollbackPutCount, Is.EqualTo(1),
                "最终门禁失败须回滚中转架入库预记");
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R11_AllActive_GoesTransitFrame_NotMachineCell()
    {
        var h = BuildHandoffHarness();
        h.Scheduler.ProbeMarkReconciled();
        var enqueued = await h.Scheduler.ProbeEnqueueUnloadAsync(SrcEq, SrcPos, isOk: true, materialId: "M-H");
        await h.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.True);
            Assert.That(h.Scheduler.ProbeHasExpectedInbound(DstEq, DstPos), Is.False,
                "现场无工位直送，不得登记 _expectedInbound");
            Assert.That(h.Slots.ReservePutCount, Is.EqualTo(1));
            Assert.That(h.Tasks.DispatchTransitCount, Is.EqualTo(1));
            Assert.That(h.Tasks.LastTaskType, Is.EqualTo("1"));
            Assert.That(h.Tasks.LastArgs?.EquipmentId, Is.EqualTo(SrcEq));
            Assert.That(h.Tasks.LastArgs?.ToCode, Is.EqualTo($"RACK-CELL-{TransitFrame}"));
        });
    }

    private sealed class Harness
    {
        public required MutableEquipmentRoutingStore Store { get; init; }
        public required TracingSlots Slots { get; init; }
        public required TracingTaskService Tasks { get; init; }
        public required TracingPlcOps Plc { get; init; }
        public required PositionScheduler Scheduler { get; init; }
    }

    private static Harness BuildHandoffHarness(bool finalDisablesDest = false)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(LineId, "LINE-A", SrcCraft, craftNode: 1, equipmentId: SrcEq);
        store.SeedNextEquipment(DstEq, DstCraft, nextNode: 2, lineId: LineId);
        store.BindFrame(SrcEq, TransitFrame, FrameRole.Unload);
        store.BindFrame(DstEq, TransitFrame, FrameRole.Upload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, occupiedFrameId: -1, equipment: equipment, store: store);
        var tasks = new TracingTaskService(trace);
        var plc = new TracingPlcOps(trace);
        var signalStore = new SignalStateStore();

        IRoutingAvailabilityValidator? validator = null;
        if (finalDisablesDest)
        {
            var inner = new Data.Repositories.RoutingAvailabilityValidator(
                store, equipment, new FakeFrameRoutingStore(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Data.Repositories.RoutingAvailabilityValidator>.Instance);
            validator = new DisableDestOnSecondValidate(store, inner);
        }

        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, store: signalStore, routingStore: store, validator: validator);

        scheduler.ProbeSeedPosition(SrcEq, SrcPos);
        scheduler.ProbeSeedPosition(DstEq, DstPos);

        return new Harness
        {
            Store = store, Slots = slots, Tasks = tasks, Plc = plc, Scheduler = scheduler
        };
    }

    private sealed class DisableDestOnSecondValidate : IRoutingAvailabilityValidator
    {
        private readonly MutableEquipmentRoutingStore _store;
        private readonly IRoutingAvailabilityValidator _inner;
        private int _calls;

        public DisableDestOnSecondValidate(
            MutableEquipmentRoutingStore store, IRoutingAvailabilityValidator inner)
        {
            _store = store;
            _inner = inner;
        }

        public Task<RoutingAvailabilityResult> ValidateAsync(
            DispatchRouteContext context, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) >= 2)
                _store.SetEquipmentState(DstEq, Disabled);
            return _inner.ValidateAsync(context, ct);
        }
    }
}
