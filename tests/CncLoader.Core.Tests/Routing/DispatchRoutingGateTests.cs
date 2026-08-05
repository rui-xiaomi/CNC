using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 R9–R15：派工双门禁 GREEN。
/// 打真实 PositionScheduler / ReservationFirstDispatcher 生产路径 + IRoutingAvailabilityValidator。
/// </summary>
[TestFixture]
public sealed class DispatchRoutingGateTests
{
    private const string Disabled = "1";

    // ─── R9｜自动上料预记前拒绝 ───────────────────────────────────────────

    [TestCase("equipment")]
    [TestCase("craft")]
    [TestCase("workline")]
    public async Task R9_Upload_PreReserve_DisabledRoute_RejectsWithoutReserveOrRcs(string which)
    {
        var harness = BuildUploadHarness(allActive: true);
        Disable(harness.Store, which);

        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        var ctx = harness.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Slots.ReserveTakeCount, Is.EqualTo(0), "预记前拒发不得 ReserveTake");
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0), "预记前拒发不得 RCS");
            Assert.That(harness.Plc.WriteCount, Is.EqualTo(0), "拒发不得写 POS_TEST_START");
            Assert.That(ctx.AlarmRaised, Is.False, "自动路由不可用不得置 Alarm");
            Assert.That(ctx.State, Is.Not.EqualTo(PositionState.Alarm));
            Assert.That(ctx.CurrentTaskId, Is.Null);
            Assert.That(harness.Trace.Events, Does.Not.Contain(DispatchGateEvents.RcsDispatch));
            Assert.That(harness.Trace.Events, Does.Not.Contain(DispatchGateEvents.ReserveCommit));
        });
    }

    // ─── R10｜自动下料预记前拒绝 ───────────────────────────────────────────

    [TestCase("equipment")]
    [TestCase("craft")]
    [TestCase("workline")]
    public async Task R10_Unload_PreReserve_DisabledRoute_RejectsWithoutReserveOrRcs(string which)
    {
        var harness = BuildUnloadDownloadHarness(allActive: true);
        Disable(harness.Store, which);

        harness.Scheduler.ProbeMarkReconciled();
        var enqueued = await harness.Scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1");
        await harness.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enqueued, Is.False, "线体不可用时不得入下料队");
            Assert.That(harness.Scheduler.ProbeQueueCount, Is.EqualTo(0));
            Assert.That(harness.Slots.ReservePutCount, Is.EqualTo(0), "下料预记前拒发不得 ReservePut");
            Assert.That(harness.Slots.ReserveTakeCount, Is.EqualTo(0));
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(harness.Plc.WriteCount, Is.EqualTo(0));
            Assert.That(harness.Tasks.LastTaskType, Is.Null);
        });
    }

    // ─── R12｜预记成功后路由失效 ───────────────────────────────────────────

    [Test]
    public async Task R12_Upload_AfterReserve_RouteDisabled_MustNotRcsAndMustRollback()
    {
        var harness = BuildUploadHarness(allActive: true, disableAfterReserve: true);

        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        var events = harness.Trace.Events;
        Assert.Multiple(() =>
        {
            Assert.That(events, Does.Contain(DispatchGateEvents.ReserveCommit));
            Assert.That(events, Does.Contain(DispatchGateEvents.DisableRoute));
            // GREEN：Reserve 后、RCS 前须有第二次权威校验
            Assert.That(events, Does.Contain(DispatchGateEvents.RouteQueryFinal),
                "预记后须再读权威配置（RouteQueryFinal）；当前缺第二道门禁");
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0),
                "第二道门禁失败不得调用 RCS");
            Assert.That(harness.Slots.RollbackTakeCount, Is.EqualTo(1),
                "须走 P0-2 RollbackTake 补偿");
            Assert.That(harness.Slots.HasActiveReservation(harness.Slots.LastReservedTaskId ?? ""), Is.False);
            Assert.That(harness.Plc.WriteCount, Is.EqualTo(0));
            Assert.That(harness.Equipment.AuthorityQueryCount, Is.GreaterThanOrEqualTo(2),
                "至少两次权威查询（Pre + Final）");
        });
    }

    [Test]
    public async Task R12_Unload_AfterReserve_RouteDisabled_MustNotRcsAndMustRollback()
    {
        var harness = BuildUnloadDownloadHarness(allActive: true, disableAfterReserve: true);

        harness.Scheduler.ProbeMarkReconciled();
        var enqueued = await harness.Scheduler.ProbeEnqueueUnloadAsync(
            DispatchGateHarness.Eq, DispatchGateHarness.Pos, isOk: true, materialId: "M-1");
        Assert.That(enqueued, Is.True, "预记前配置活动时应能入队");
        await harness.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(harness.Slots.ReservePutCount, Is.EqualTo(1), "下料须先 ReservePut");
            Assert.That(harness.Trace.Events, Does.Contain(DispatchGateEvents.DisableRoute));
            Assert.That(harness.Trace.Events, Does.Contain(DispatchGateEvents.RouteQueryFinal),
                "下料预记后须再读权威配置");
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(harness.Slots.RollbackPutCount, Is.EqualTo(1));
            Assert.That(harness.Tasks.LastTaskType, Is.Null.Or.EqualTo("1"));
            // 方向接线：下料走 PUT 预记，不是 TAKE
            Assert.That(harness.Slots.ReserveTakeCount, Is.EqualTo(0));
            Assert.That(harness.Plc.WriteCount, Is.EqualTo(0));
        });
    }

    // ─── R13｜全活动正常顺序 ───────────────────────────────────────────────

    [Test]
    public async Task R13_Upload_AllActive_Order_PreValidate_Reserve_FinalValidate_Rcs()
    {
        var harness = BuildUploadHarness(allActive: true);

        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        var events = harness.Trace.Events;
        var compact = CompactOrder(events);

        Assert.Multiple(() =>
        {
            // P0-2 保持：预记先于 RCS、taskId 一致、成功不回滚
            Assert.That(harness.Slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(1));
            Assert.That(harness.Slots.RollbackCount, Is.EqualTo(0));
            Assert.That(harness.Tasks.LastTaskId, Is.EqualTo(harness.Slots.LastReservedTaskId));
            var eventList = events.ToList();
            Assert.That(
                eventList.IndexOf(DispatchGateEvents.ReserveCommit),
                Is.LessThan(eventList.IndexOf(DispatchGateEvents.RcsDispatch)));

            // D6 双门禁顺序（缺 Final → RED）
            Assert.That(compact, Is.EqualTo(new[]
            {
                DispatchGateEvents.RouteValidatePre,
                DispatchGateEvents.ReserveCommit,
                DispatchGateEvents.RouteValidateFinal,
                DispatchGateEvents.RcsDispatch
            }), $"当前真实顺序={string.Join("→", compact)}");
        });
    }

    // ─── R14｜拒发不写 PLC ─────────────────────────────────────────────────

    [Test]
    public async Task R14_PreReserveReject_DoesNotWritePlc()
    {
        var harness = BuildUploadHarness(allActive: true);
        harness.Store.SetWorkLineState(DispatchGateHarness.LineId, Disabled);

        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        var ctx = harness.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Plc.WriteCount, Is.EqualTo(0));
            Assert.That(ctx.State, Is.Not.EqualTo(PositionState.Loaded));
            Assert.That(ctx.State, Is.Not.EqualTo(PositionState.Unloaded));
            Assert.That(ctx.AlarmRaised, Is.False);
        });
    }

    [Test]
    public async Task R14_PostReserveReject_DoesNotWritePlc()
    {
        var harness = BuildUploadHarness(allActive: true, disableAfterReserve: true);

        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        // POS_TEST_START 仅在 Loaded/Unloaded 收口写入；派工 tick 本身不得写 PLC（防回归）。
        // 预记后拒发的 RCS/回滚契约由 R12 锁定，此处不重复断言以免与第二道门禁 RED 混叠。
        Assert.That(harness.Plc.WriteCount, Is.EqualTo(0), "派工路径不得写 POS_TEST_START");
        var ctx = harness.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.That(ctx.State, Is.Not.EqualTo(PositionState.Loaded));
        Assert.That(ctx.State, Is.Not.EqualTo(PositionState.Unloaded));
    }

    // ─── R15｜缓存粘滞 ─────────────────────────────────────────────────────

    [Test]
    public async Task R15A_Resolver_CacheHit_MustNotReturnStaleActiveRoute()
    {
        var harness = BuildUploadHarness(allActive: true);

        var first = await harness.Scheduler.ProbeResolveLineAsync(DispatchGateHarness.Eq);
        Assert.That(first, Is.Not.Null);
        Assert.That(harness.Scheduler.ProbeHasLineCache(DispatchGateHarness.Eq), Is.True);
        var queriesAfterFirst = harness.Store.Queries.Count;

        harness.Store.SetWorkLineState(DispatchGateHarness.LineId, Disabled);

        var second = await harness.Scheduler.ProbeResolveLineAsync(DispatchGateHarness.Eq);
        var queriesAfterSecond = harness.Store.Queries.Count;

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Null,
                "权威 WorkLine 已禁用时，不得仅凭 _lineCache 返回活动路由");
            Assert.That(queriesAfterSecond, Is.GreaterThan(queriesAfterFirst),
                "须重新验证权威配置，不能静默 CacheHit（当前 queries 未增加即粘滞）");
        });
    }

    [Test]
    public async Task R15B_Dispatch_StaleCache_MustStillFailClosed()
    {
        var harness = BuildUploadHarness(allActive: true);

        // 1) 缓存已有活动 Route（不清缓存——真实风险）
        var cached = await harness.Scheduler.ProbeResolveLineAsync(DispatchGateHarness.Eq);
        Assert.That(cached, Is.Not.Null);
        Assert.That(harness.Scheduler.ProbeHasLineCache(DispatchGateHarness.Eq), Is.True);

        // 2) 权威配置禁用
        harness.Store.SetWorkLineState(DispatchGateHarness.LineId, Disabled);

        // 3) 真实自动上料
        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(
                harness.Slots.ReserveTakeCount == 0
                || (harness.Slots.ReserveTakeCount == 1 && harness.Slots.RollbackTakeCount == 1),
                "缓存粘滞时：预记前拒 或 预记后完整回滚");
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0),
                "最终门禁须读权威配置并拒绝 RCS");
        });
    }

    // ─── 补充：双门禁边角 ─────────────────────────────────────────────────

    [Test]
    public async Task Supplemental_AllActive_ValidatorCalledTwice_PreAndFinal()
    {
        var harness = BuildUploadHarness(allActive: true);
        var counter = new CountingValidator(harness.Store, harness.Equipment);
        var scheduler = DispatchGateHarness.CreateScheduler(
            harness.Equipment, harness.Slots, harness.Tasks, harness.Plc,
            routingStore: harness.Store, validator: counter);

        scheduler.ProbeMarkReconciled();
        scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(counter.CallCount, Is.EqualTo(2), "全活动 Pre+Final 各一次");
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(1));
            Assert.That(counter.UsedLineCache, Is.False, "Final 不得读 _lineCache");
        });
    }

    [Test]
    public async Task Supplemental_FinalFail_RollbackFail_StillNoRcs_AndAlarm()
    {
        var harness = BuildUploadHarness(allActive: true, disableAfterReserve: true);
        harness.Slots.RollbackTakeSucceeds = false;

        harness.Scheduler.ProbeMarkReconciled();
        harness.Scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await harness.Scheduler.ProbeDispatchOnceAsync();

        var ctx = harness.Scheduler.ProbeGetContext(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(harness.Slots.RollbackTakeCount, Is.EqualTo(1));
            Assert.That(ctx.AlarmRaised, Is.True, "回滚失败保持 P0-2 Alarm 语义");
        });
    }

    [Test]
    public async Task Supplemental_CacheMissAfterDisable_EvictsLineCache()
    {
        var harness = BuildUploadHarness(allActive: true);
        Assert.That(await harness.Scheduler.ProbeResolveLineAsync(DispatchGateHarness.Eq), Is.Not.Null);
        Assert.That(harness.Scheduler.ProbeHasLineCache(DispatchGateHarness.Eq), Is.True);

        harness.Store.SetWorkLineState(DispatchGateHarness.LineId, Disabled);
        Assert.That(await harness.Scheduler.ProbeResolveLineAsync(DispatchGateHarness.Eq), Is.Null);
        Assert.That(harness.Scheduler.ProbeHasLineCache(DispatchGateHarness.Eq), Is.False,
            "权威失败后须淘汰 _lineCache 条目");
    }

    [Test]
    public async Task Supplemental_FinalValidatorThrows_FailClosedRollbackNoRcs()
    {
        var harness = BuildUploadHarness(allActive: true);
        var boom = new BoomFinalValidator();
        var scheduler = DispatchGateHarness.CreateScheduler(
            harness.Equipment, harness.Slots, harness.Tasks, harness.Plc,
            routingStore: harness.Store, validator: boom);

        scheduler.ProbeMarkReconciled();
        scheduler.ProbeSeedUploadCandidate(DispatchGateHarness.Eq, DispatchGateHarness.Pos);
        await scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(harness.Slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(harness.Slots.RollbackTakeCount, Is.EqualTo(1));
            Assert.That(harness.Tasks.DispatchTransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Supplemental_FinalValidatorCancel_PropagatesAndRollsBack()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var rolledBack = false;
        using var cts = new CancellationTokenSource();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await dispatcher.ExecuteAsync(
                "task-1",
                (_, _) => Task.FromResult<object?>(new object()),
                (_, token) =>
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(RoutingAvailabilityResult.Available(new WorkLineRef(1, "L")));
                },
                (_, _) => Task.FromResult(new RcsResult(true, 200, true, "ok", "", "{}", null, 1) { TaskId = "task-1" }),
                (_, _) => { rolledBack = true; return Task.FromResult(true); },
                cts.Token));

        Assert.That(rolledBack, Is.True);
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static void Disable(MutableEquipmentRoutingStore store, string which)
    {
        switch (which)
        {
            case "equipment":
                store.SetEquipmentState(DispatchGateHarness.Eq, Disabled);
                break;
            case "craft":
                store.SetCraftState(DispatchGateHarness.CraftId, Disabled);
                break;
            case "workline":
                store.SetWorkLineState(DispatchGateHarness.LineId, Disabled);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(which), which, null);
        }
    }

    private static string[] CompactOrder(IReadOnlyList<string> events)
    {
        var wanted = new HashSet<string>
        {
            DispatchGateEvents.RouteValidatePre,
            DispatchGateEvents.ReserveCommit,
            DispatchGateEvents.RouteValidateFinal,
            DispatchGateEvents.RcsDispatch
        };
        return events.Where(wanted.Contains).ToArray();
    }

    private sealed class Harness
    {
        public required MutableEquipmentRoutingStore Store { get; init; }
        public required TracingEquipmentConfigService Equipment { get; init; }
        public required TracingSlots Slots { get; init; }
        public required TracingTaskService Tasks { get; init; }
        public required TracingPlcOps Plc { get; init; }
        public required CallTrace Trace { get; init; }
        public required PositionScheduler Scheduler { get; init; }
    }

    private sealed class CountingValidator : IRoutingAvailabilityValidator
    {
        private readonly RoutingAvailabilityValidator _inner;
        public int CallCount { get; private set; }
        public bool UsedLineCache => false;

        public CountingValidator(MutableEquipmentRoutingStore store, IEquipmentConfigService equipment)
        {
            _inner = new RoutingAvailabilityValidator(
                store, equipment, new FakeFrameRoutingStore(),
                NullLogger<RoutingAvailabilityValidator>.Instance);
        }

        public async Task<RoutingAvailabilityResult> ValidateAsync(
            DispatchRouteContext context, CancellationToken ct = default)
        {
            CallCount++;
            return await _inner.ValidateAsync(context, ct);
        }
    }

    /// <summary>Pre 通过，Final 抛异常 → 触发 ConfigurationUnavailable 回滚路径。</summary>
    private sealed class BoomFinalValidator : IRoutingAvailabilityValidator
    {
        private int _calls;
        private readonly RoutingAvailabilityValidator _inner;

        public BoomFinalValidator()
        {
            var store = new MutableEquipmentRoutingStore();
            store.SeedActiveChain(
                DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
                DispatchGateHarness.CraftId, 1, DispatchGateHarness.Eq);
            store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.UploadFrame, FrameRole.Upload);
            var eq = new TracingEquipmentConfigService(store, new CallTrace());
            _inner = new RoutingAvailabilityValidator(
                store, eq, new FakeFrameRoutingStore(),
                NullLogger<RoutingAvailabilityValidator>.Instance);
        }

        public async Task<RoutingAvailabilityResult> ValidateAsync(
            DispatchRouteContext context, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return await _inner.ValidateAsync(context, ct);
            throw new InvalidOperationException("final validator boom");
        }
    }

    private static Harness BuildUploadHarness(bool allActive, bool disableAfterReserve = false)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(
            DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, craftNode: 1, DispatchGateHarness.Eq);
        store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.UploadFrame, FrameRole.Upload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        Action? disable = null;
        if (disableAfterReserve)
        {
            disable = () =>
            {
                store.SetWorkLineState(DispatchGateHarness.LineId, Disabled);
            };
        }

        var slots = new TracingSlots(
            trace, DispatchGateHarness.UploadFrame,
            equipment: equipment, store: store, disableOnReserveCommit: disable);
        var tasks = new TracingTaskService(trace);
        var plc = new TracingPlcOps(trace);
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, routingStore: store);

        if (!allActive)
            store.SetWorkLineState(DispatchGateHarness.LineId, Disabled);

        return new Harness
        {
            Store = store, Equipment = equipment, Slots = slots,
            Tasks = tasks, Plc = plc, Trace = trace, Scheduler = scheduler
        };
    }

    private static Harness BuildUnloadDownloadHarness(bool allActive, bool disableAfterReserve = false)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        // 末道工序：无下一节点 → 下料架 PUT 路径（真实 ReserveAsync，非交接）
        store.SeedActiveChain(
            DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, craftNode: 1, DispatchGateHarness.Eq);
        store.BindFrame(DispatchGateHarness.Eq, DispatchGateHarness.DownloadFrame, FrameRole.Unload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        Action? disable = null;
        if (disableAfterReserve)
        {
            disable = () => store.SetEquipmentState(DispatchGateHarness.Eq, Disabled);
        }

        var slots = new TracingSlots(
            trace, occupiedFrameId: -1, putFrameId: DispatchGateHarness.DownloadFrame,
            equipment: equipment, store: store, disableOnReserveCommit: disable);
        var tasks = new TracingTaskService(trace);
        var plc = new TracingPlcOps(trace);
        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc, routingStore: store);

        if (!allActive)
            store.SetWorkLineState(DispatchGateHarness.LineId, Disabled);

        return new Harness
        {
            Store = store, Equipment = equipment, Slots = slots,
            Tasks = tasks, Plc = plc, Trace = trace, Scheduler = scheduler
        };
    }
}
