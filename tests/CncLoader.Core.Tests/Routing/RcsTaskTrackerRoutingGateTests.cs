using System.IO;
using CncLoader.Common.Configuration;
using CncLoader.Communication.Rcs;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 D14：RcsTaskTracker 自动重派须先门禁后原子 Claim（消费 RedoCount）。
/// 真实 Tracker + RcsTaskService + Resolver + Validator；禁止 TracingTaskService。
/// </summary>
[TestFixture]
public sealed class RcsTaskTrackerRoutingGateTests
{
    private const string TaskId = "LINE01-MV-TRACKER-0001";
    private const int MaxAutoRedo = 3;

    private FakeLocationMapForRouting _loc = null!;
    private FakeFrameRoutingStore _frames = null!;
    private MutableEquipmentRoutingStore _eq = null!;
    private CountingManagedRouteResolver _resolver = null!;
    private CountingRoutingValidator _validator = null!;
    private FakeRcsHttpClient _client = null!;
    private MutableRcsTaskStore _tasks = null!;
    private List<string> _order = null!;
    private NoopAlarms _alarms = null!;
    private RcsTaskService _svc = null!;
    private RcsTaskTracker _tracker = null!;

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
        var equipment = new TracingEquipmentConfigService(_eq, new CallTrace());
        _validator = new CountingRoutingValidator(
            new RoutingAvailabilityValidator(
                _eq, equipment, _frames,
                NullLogger<RoutingAvailabilityValidator>.Instance))
        {
            OrderSink = _order
        };

        _client = new FakeRcsHttpClient { OrderSink = _order };
        _tasks = new MutableRcsTaskStore { OrderSink = _order };
        _svc = new RcsTaskService(
            _client, _tasks, new TrackerNoopMsgLog(), new TrackingCallbackProcessor(),
            _resolver, _validator, NullLogger<RcsTaskService>.Instance, new TrackingSlotsForClosure(), new RcsCallbackNotifier());

        _alarms = new NoopAlarms();
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions
            {
                TrackerEnabled = true,
                MaxAutoRedo = MaxAutoRedo,
                PollIntervalMs = 60_000
            }
        });

        _tracker = new RcsTaskTracker(
            _svc, _tasks, _alarms, new RcsCallbackNotifier(),
            options, new StubRuntime(), new ServiceCollection().BuildServiceProvider(),
            NullLogger<RcsTaskTracker>.Instance);

        SeedFailedTask();
    }

    [TearDown]
    public async Task TearDown() => await _tracker.DisposeAsync();

    // ─── 真实链文档化 ────────────────────────────────────────────

    [Test]
    public void Chain_Documents_Tracker_AutoRedo_GateThenClaim()
    {
        // FAILED → AutoRedoAsync → AutoRedispatchAsync（门禁）→ TryClaimAutoRedo → Send
        Assert.Multiple(() =>
        {
            Assert.That(typeof(RcsTaskTracker).GetMethod("ProbeAutoRedoOnceAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
                Is.Not.Null, "须有探测入口打真实 AutoRedo");
            Assert.That(typeof(IRcsTaskService).GetMethod(nameof(IRcsTaskService.AutoRedispatchAsync)),
                Is.Not.Null, "Tracker 须走统一 AutoRedispatch 入口");
            Assert.That(typeof(IRcsTaskService).GetMethod(nameof(IRcsTaskService.RedispatchAsync)),
                Is.Not.Null, "手动 Redispatch 入口保留且不增计数");
            Assert.That(typeof(IRcsTaskService).GetMethod(nameof(IRcsTaskService.RedoAsync)),
                Is.Not.Null);
            Assert.That(typeof(IRcsTaskStore).GetMethod(nameof(IRcsTaskStore.TryClaimAutoRedoAsync)),
                Is.Not.Null, "原子 Claim");
            Assert.That(typeof(IRcsTaskStore).GetMethod(nameof(IRcsTaskStore.IncrementRedoAsync)),
                Is.Not.Null, "手动 Redo 用 IncrementRedo");
            Assert.That(typeof(IRcsTaskStore).GetMethod("TryIncrementRedoIfUnderAsync"),
                Is.Null, "旧门禁前 Increment 不得再作为 Store 契约");
            Assert.That(AutoRedoClaimRules.ClaimableStates, Is.EqualTo(new[] { RcsTaskState.Failed }));
        });
    }

    // ─── C. 全活动成功（harness + 顺序契约）──────────────────────

    [Test]
    public async Task Active_Baseline_SendsOnce_AndClaimsOnce()
    {
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.TryClaimSuccessCount, Is.EqualTo(1), "合法路径须 Claim 一次");
            Assert.That(_client.TransitCount, Is.EqualTo(1), "合法路径须 Send 一次（harness）");
            Assert.That(_tasks.Snapshot(TaskId)!.RedoCount, Is.EqualTo(before.RedoCount + 1));
            Assert.That(_resolver.CallCount, Is.EqualTo(1), "AutoRedispatch 发送边界一次");
            Assert.That(_validator.CallCount, Is.EqualTo(1));
            Assert.That(_tasks.IncrementRedoCount, Is.EqualTo(0),
                "Tracker 不得再走 IncrementRedoAsync（避免双增）");
        });
    }

    [Test]
    public async Task Active_Order_MustBe_GateThenClaimThenSend()
    {
        await _tracker.ProbeAutoRedoOnceAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(_order, Does.Contain("TryClaim"));
            Assert.That(_order, Does.Contain("Validate"));
            Assert.That(_order, Does.Contain("RcsTransit"));
            Assert.That(_order.IndexOf("Validate"), Is.LessThan(_order.IndexOf("TryClaim")),
                $"D14 顺序须为 Gate→Claim→Send；Actual={Fmt()}");
            Assert.That(_order.IndexOf("TryClaim"), Is.LessThan(_order.IndexOf("RcsTransit")),
                "Claim 须在 Send 前");
            Assert.That(_order, Does.Not.Contain("TryIncrement"),
                "不得再出现旧门禁前 TryIncrement 标记");
        });
    }

    // ─── A. 禁用配置不得消耗 RedoCount ───────────────────────────

    [Test]
    public async Task Disabled_Source_MustNotClaim_NoSend()
    {
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        AssertDisabledNoConsume(before, after, "Source AREA 禁用");
    }

    [Test]
    public async Task Disabled_Target_MustNotClaim_NoSend()
    {
        _loc.SetStateByCode(PositionCell, remove: false, state: "1");
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        AssertDisabledNoConsume(before, after, "Target POSITION 禁用");
    }

    [Test]
    public async Task Disabled_Equipment_MustNotClaim_NoSend()
    {
        _eq.SetEquipmentState(EqId, "1");
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        AssertDisabledNoConsume(before, after, "Equipment 禁用");
    }

    [Test]
    public async Task Disabled_WorkLine_MustNotClaim_NoSend()
    {
        _eq.SetWorkLineState(LineId, "1");
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        AssertDisabledNoConsume(before, after, "WorkLine 禁用");
    }

    [Test]
    public async Task Unknown_FromCode_MustNotClaim_NoSend()
    {
        ReseedFailedTask(from: "NO-SUCH-FROM", to: PositionCell);
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        AssertDisabledNoConsume(before, after, "未知 From");
    }

    [Test]
    public async Task Ambiguous_FromCode_MustNotClaim_NoSend()
    {
        _loc.Seed(new LocationMapItem
        {
            Id = 80, LocType = "AREA", LocName = LocLoadArea,
            RcsCode = LoadAreaCode, RcsType = "station",
            EquipmentId = null, PositionId = null, FrameId = null
        });
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        AssertDisabledNoConsume(before, after, "同码歧义");
    }

    [Test]
    public async Task SameInstance_SecondAfterDisable_NoExtraClaimOrSend()
    {
        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        Assert.That(_client.TransitCount, Is.EqualTo(1));
        var redoAfterFirst = _tasks.Snapshot(TaskId)!.RedoCount;

        ReseedFailedTask(redoCount: redoAfterFirst);
        _tasks.ResetTryIncrementCounters();
        _resolver.Reset();
        _validator.Reset();
        _order.Clear();
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");
        var sendBefore = _client.TransitCount;
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        Assert.Multiple(() =>
        {
            Assert.That(_client.TransitCount, Is.EqualTo(sendBefore), "二次禁用不得再 Send");
            Assert.That(_tasks.TryClaimCallCount, Is.EqualTo(0),
                $"二次禁用不得 Claim；Actual={_tasks.TryClaimCallCount} " +
                $"Redo {before.RedoCount}→{after.RedoCount} 顺序={Fmt()}");
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount));
        });
    }

    // ─── D. 原子上限与并发 ───────────────────────────────────────

    [Test]
    public async Task Claim_False_AtMax_NoSend_NotRouteFailure()
    {
        ReseedFailedTask(redoCount: MaxAutoRedo);
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.TryClaimCallCount, Is.EqualTo(1), "门禁通过后才 Claim");
            Assert.That(_tasks.TryClaimSuccessCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount));
            Assert.That(after.TaskState, Is.EqualTo(before.TaskState));
            Assert.That(after.ErrorMsg, Is.EqualTo(before.ErrorMsg));
            Assert.That(_resolver.CallCount, Is.EqualTo(1), "上限路径仍先完成门禁");
            Assert.That(_alarms.RaiseCount, Is.GreaterThanOrEqualTo(1), "达上限告警人工");
        });
    }

    [Test]
    public async Task Concurrent_TwoProbes_AtMostOneClaimAndSend()
    {
        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.BeforeTryClaimAsync = async (_, _) =>
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.TrySetResult();
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var t1 = _tracker.ProbeAutoRedoOnceAsync(TaskId, "probe-a");
        var t2 = _tracker.ProbeAutoRedoOnceAsync(TaskId, "probe-b");
        await Task.WhenAll(t1, t2);

        Assert.Multiple(() =>
        {
            Assert.That(_resolver.CallCount, Is.EqualTo(2),
                "两者均可完成发送边界门禁；Actual=" + _resolver.CallCount);
            Assert.That(_validator.CallCount, Is.EqualTo(2));
            Assert.That(_tasks.TryClaimCallCount, Is.EqualTo(2), "两者都尝试 Claim");
            Assert.That(_tasks.TryClaimSuccessCount, Is.EqualTo(1),
                $"原子 Claim 只允许一个成功；Actual={_tasks.TryClaimSuccessCount}");
            Assert.That(_client.TransitCount, Is.EqualTo(1),
                $"并发最多一次 Send；Actual Transit={_client.TransitCount} 顺序={Fmt()}");
            Assert.That(_tasks.Snapshot(TaskId)!.RedoCount, Is.EqualTo(1),
                "RedoCount 最多 +1");
        });
    }

    // ─── E. 失败边界 ─────────────────────────────────────────────

    [Test]
    public async Task ResolverThrows_MustNotClaim_OrSend()
    {
        var throwingResolver = new CountingManagedRouteResolver(new ThrowingResolver())
        {
            OrderSink = _order
        };
        RebuildTracker(throwingResolver, _validator);
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        Assert.Multiple(() =>
        {
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_tasks.TryClaimCallCount, Is.EqualTo(0),
                $"Resolver 异常前不得 Claim；Actual={_tasks.TryClaimCallCount} " +
                $"Redo {before.RedoCount}→{after.RedoCount}");
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount));
        });
    }

    [Test]
    public async Task ClaimThrows_NoSend()
    {
        _tasks.ThrowOnNextTryClaim = new InvalidOperationException("claim-store-down");

        try
        {
            await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        }
        catch (InvalidOperationException)
        {
            // Probe 直调 AutoRedo，异常上抛；HandleStatusEvent 会吞——此处允许抛
        }

        Assert.Multiple(() =>
        {
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_tasks.TryClaimSuccessCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ClaimSuccess_ThenRcsSendFails_KeepsConsumedCount()
    {
        _client.FailNextTransit = true;
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.TryClaimSuccessCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(1), "已形成真实发送尝试");
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount + 1),
                "D14：Send 失败不回滚已消耗 RedoCount");
            Assert.That(_client.TransitCount, Is.EqualTo(1), "不得隐式二次发送");
        });
    }

    [Test]
    public async Task RouteReject_NoGrowl_NoStationAlarm_ButMustNotConsume_D14()
    {
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");
        var before = _tasks.Snapshot(TaskId)!;

        await _tracker.ProbeAutoRedoOnceAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        Assert.Multiple(() =>
        {
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0),
                "路由拒发不经 IAlarmEventService（收口走调度器 NotifyTaskAbandoned）");
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount),
                $"路由拒发不得耗 RedoCount；Actual {before.RedoCount}→{after.RedoCount} " +
                $"ClaimCalls={_tasks.TryClaimCallCount} 顺序={Fmt()}");
        });
    }

    [Test]
    public async Task RouteReject_NotifiesSchedulerAbandoned()
    {
        var scheduler = new AbandonRecordingScheduler();
        var services = new ServiceCollection()
            .AddSingleton<IPositionScheduler>(scheduler)
            .BuildServiceProvider();
        await using var tracker = new RcsTaskTracker(
            _svc, _tasks, _alarms, new RcsCallbackNotifier(),
            Options.Create(new AppOptions
            {
                Rcs = new RcsOptions { TrackerEnabled = true, MaxAutoRedo = MaxAutoRedo, PollIntervalMs = 60_000 }
            }),
            new StubRuntime(), services, NullLogger<RcsTaskTracker>.Instance);
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");

        await tracker.ProbeAutoRedoOnceAsync(TaskId);

        Assert.That(scheduler.Abandoned, Is.EqualTo(new[] { (TaskId, "AUTO_REDO_ROUTE_UNAVAILABLE") }));
    }

    [Test]
    public async Task Redispatch_Itself_DoesNotIncrement()
    {
        // 入口隔离：Redispatch 契约不消费 RedoCount
        var before = _tasks.Snapshot(TaskId)!;
        await _svc.RedispatchAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.TryClaimCallCount, Is.EqualTo(0));
            Assert.That(_tasks.IncrementRedoCount, Is.EqualTo(0));
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount));
            Assert.That(_client.TransitCount, Is.EqualTo(1), "活动路由 Redispatch 可发送");
        });
    }

    [Test]
    public async Task ManualRedo_StillGatesBeforeIncrement()
    {
        var before = _tasks.Snapshot(TaskId)!;
        _loc.SetStateByCode(LoadAreaCode, remove: false, state: "1");

        var result = await _svc.RedoAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(_tasks.IncrementRedoCount, Is.EqualTo(0));
            Assert.That(_tasks.TryClaimCallCount, Is.EqualTo(0));
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ManualRedo_Active_IncrementsOnce_ViaIncrementRedo()
    {
        var before = _tasks.Snapshot(TaskId)!;
        var result = await _svc.RedoAsync(TaskId);
        var after = _tasks.Snapshot(TaskId)!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.IncrementRedoCount, Is.EqualTo(1));
            Assert.That(_tasks.TryClaimCallCount, Is.EqualTo(0), "手动 Redo 不走 AutoRedo Claim");
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount + 1));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void DataStore_ClaimPredicate_RequiresFailedStateAndBudget()
    {
        // 最小接缝：生产 Store 源码 WHERE 须含 FAILED + RedoCount 上限（禁止查后无条件 Save）
        var root = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", ".."));
        var src = File.ReadAllText(Path.Combine(
            root, "src", "CncLoader.Data", "Repositories", "RcsTaskStore.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(src, Does.Contain("TryClaimAutoRedoAsync"));
            Assert.That(src, Does.Contain("ExecuteUpdateAsync"));
            Assert.That(src, Does.Contain("RcsTaskState.Failed"));
            Assert.That(src, Does.Contain("x.RedoCount < maxRedo"));
            Assert.That(AutoRedoClaimRules.ClassifyMiss(false, null, 0, 3),
                Is.EqualTo(AutoRedoClaimResult.NotFound));
            Assert.That(AutoRedoClaimRules.ClassifyMiss(true, RcsTaskState.Failed, 3, 3),
                Is.EqualTo(AutoRedoClaimResult.LimitReached));
            Assert.That(AutoRedoClaimRules.ClassifyMiss(true, RcsTaskState.Dispatched, 0, 3),
                Is.EqualTo(AutoRedoClaimResult.NotClaimable));
        });
    }

    [Test]
    public async Task NotFound_Task_FallsToFailed_AndLeavesUnfinishedList()
    {
        // P0-3：RCS 查无任务 → 落 FAILED 终态，移出未完结列表，防止 queryTask IN 列表只增不减。
        const string orphanId = "LINE01-MV-ORPHAN-0001";
        _tasks.Seed(new RcsTaskRow(
            2, orphanId, "transit", "0", RcsTaskState.Executing, null, 5,
            LoadAreaCode, PositionCell, EqId, PositionId, null, null, null,
            0, "0", DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(-4), null, null));

        await _tracker.ProbePollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.Snapshot(orphanId)!.TaskState, Is.EqualTo(RcsTaskState.Failed),
                "查无任务必须落 FAILED 终态");
            Assert.That(_tasks.Snapshot(orphanId)!.ErrorMsg, Does.Contain("查无"), "留失败原因");
            Assert.That(_alarms.NotFoundCount, Is.GreaterThanOrEqualTo(1), "查无告警一次");
        });
        var unfinished = await _tasks.GetUnfinishedTaskIdsAsync();
        Assert.That(unfinished, Does.Not.Contain(orphanId), "FAILED 后不得再进未完结轮询列表");
    }

    [Test]
    public async Task NotFound_刚落库未满下发宽限_不判查无不落FAILED()
    {
        // P0-3：先落库后下发，下发窗口内 RCS 尚无此任务，不得误判查无、不得落 FAILED（否则陈旧清扫回滚其预记）。
        const string inFlightId = "LINE01-MV-INFLIGHT-0001";
        _tasks.Seed(new RcsTaskRow(
            50, inFlightId, "transit", "0", RcsTaskState.Dispatched, null, 5,
            LoadAreaCode, PositionCell, EqId, PositionId, null, null, null,
            0, "0", DateTime.Now.AddSeconds(-5), null, null, null));

        await _tracker.ProbePollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_client.LastQuery?.Condition.Conditions.Single().Value, Does.Contain(inFlightId),
                "前置：该任务确实进了 queryTask 且 RCS 未返回");
            Assert.That(_tasks.Snapshot(inFlightId)!.TaskState, Is.EqualTo(RcsTaskState.Dispatched));
            Assert.That(_alarms.NotFoundCount, Is.Zero);
        });
    }

    [Test]
    public async Task NotFound_已下发超过可见延迟加轮询周期_无需等下发宽限即判查无()
    {
        // RCS 现场确认新建任务约 3s 后可查：已拿到下发结果的首发任务，从 DISPATCH_TIME 起等 3s + 轮询周期即可判查无。
        const string id = "LINE01-MV-DISPATCHED-0001";
        _tasks.Seed(new RcsTaskRow(
            51, id, "transit", "0", RcsTaskState.Dispatched, null, 5,
            LoadAreaCode, PositionCell, EqId, PositionId, null, null, null,
            0, "0", DateTime.Now.AddSeconds(-70), DateTime.Now.AddSeconds(-70), null, null));

        await _tracker.ProbePollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.Snapshot(id)!.TaskState, Is.EqualTo(RcsTaskState.Failed),
                "70s 已超 3s + 60s 轮询周期，且短于 2min 下发宽限，须走快速判定");
            Assert.That(_alarms.NotFoundCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public async Task NotFound_自动redo已刷新SendTime_旧DispatchTime不得立即判查无()
    {
        const string id = "LINE01-MV-REDO-WINDOW-0001";
        _tasks.Seed(new RcsTaskRow(
            53, id, "transit", "0", RcsTaskState.Failed, null, 5,
            LoadAreaCode, PositionCell, EqId, PositionId, null, null, null,
            0, "0", DateTime.Now.AddMinutes(-20), DateTime.Now.AddMinutes(-19), null, null));

        var claim = await _tasks.TryClaimAutoRedoAsync(id, MaxAutoRedo);
        Assert.That(claim, Is.EqualTo(AutoRedoClaimResult.Claimed));
        var after = _tasks.Snapshot(id)!;
        Assert.That(after.SendTime, Is.GreaterThan(after.DispatchTime!.Value),
            "Claim 须刷新 SEND_TIME，否则查无窗口仍按创建时刻计算");

        await _tracker.ProbePollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.Snapshot(id)!.TaskState, Is.EqualTo(RcsTaskState.Dispatched),
                "重发窗口内 query 未命中不得落 FAILED、不得回滚预记");
            Assert.That(_alarms.NotFoundCount, Is.Zero);
        });
    }

    [Test]
    public async Task NotFound_通知换架编排与盘点收口_否则结果未知的事务一直挂起()
    {
        const string id = "LINE01-CF-UNKNOWN-0001";
        _tasks.Seed(new RcsTaskRow(
            52, id, "change_frame", "1", RcsTaskState.Dispatched, null, 9,
            FrameShelfCode, EmptyBufferCode, EqId, null, null, "CF-1", null,
            0, "0", DateTime.Now.AddSeconds(-70), DateTime.Now.AddSeconds(-70), null, null));
        var changeFrame = new AbandonRecordingChangeFrame();
        var inventory = new AbandonRecordingInventory();
        var services = new ServiceCollection()
            .AddSingleton<IChangeFrameOrchestrator>(changeFrame)
            .AddSingleton<IInventoryService>(inventory)
            .BuildServiceProvider();
        await using var tracker = new RcsTaskTracker(
            _svc, _tasks, _alarms, new RcsCallbackNotifier(),
            Options.Create(new AppOptions
            {
                Rcs = new RcsOptions { TrackerEnabled = true, MaxAutoRedo = MaxAutoRedo, PollIntervalMs = 60_000 }
            }),
            new StubRuntime(), services, NullLogger<RcsTaskTracker>.Instance);

        await tracker.ProbePollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.Snapshot(id)!.TaskState, Is.EqualTo(RcsTaskState.Failed));
            Assert.That(changeFrame.Abandoned, Is.EqualTo(new[] { (id, "RCS_NOT_FOUND") }));
            Assert.That(inventory.Abandoned, Is.EqualTo(new[] { (id, "RCS_NOT_FOUND") }));
        });
    }

    private sealed class AbandonRecordingScheduler : IPositionScheduler
    {
        public List<(string TaskId, string Reason)> Abandoned { get; } = new();
        public bool IsReconciled => true;
        public ReconciliationState ReconciliationState => ReconciliationState.Succeeded;
        public string? ReconciliationFailureReason => null;
        public bool IsAutoDispatchPaused => false;
#pragma warning disable CS0067
        public event EventHandler? Reconciled;
        public event EventHandler<ReconciliationSnapshot>? ReconciliationStateChanged;
#pragma warning restore CS0067
        public void SetAutoDispatchPaused(bool paused) { }
        public Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default)
        {
            Abandoned.Add((taskId, reason));
            return Task.CompletedTask;
        }
        public void InvalidateFrameBindingCache(long? equipmentId = null) { }
    }

    private sealed class AbandonRecordingChangeFrame : IChangeFrameOrchestrator
    {
        public List<(string TaskId, string Reason)> Abandoned { get; } = new();
#pragma warning disable CS0067
        public event EventHandler<ChangeFrameProgressEvent>? ProgressChanged;
#pragma warning restore CS0067
        public Task<string> ChangeFrameAsync(long equipmentId, FrameRole role, string author, CancellationToken ct = default)
            => Task.FromResult("");
        public IReadOnlyList<ChangeFrameProgressEvent> GetActiveTransactions() => Array.Empty<ChangeFrameProgressEvent>();
        public Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default)
        {
            Abandoned.Add((taskId, reason));
            return Task.CompletedTask;
        }
    }

    private sealed class AbandonRecordingInventory : IInventoryService
    {
        public List<(string TaskId, string Reason)> Abandoned { get; } = new();
#pragma warning disable CS0067
        public event EventHandler<InventoryResultEvent>? InventoryCompleted;
#pragma warning restore CS0067
        public Task<string> StartInventoryAsync(long frameId, int posStart, int count, string author, CancellationToken ct = default)
            => Task.FromResult("");
        public IReadOnlyList<InventoryTaskInfo> GetActiveInventories() => Array.Empty<InventoryTaskInfo>();
        public Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default)
        {
            Abandoned.Add((taskId, reason));
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Poll_QueryUsesIdKeyAndLocalTaskId()
    {
        const string local = "L1-GB-20260911142538-9209";
        _tasks.Seed(new RcsTaskRow(
            3, local, "grab", "0", RcsTaskState.Dispatched, null, 5,
            LoadAreaCode, PositionCell, EqId, PositionId, null, null, null,
            0, "0", DateTime.Now, DateTime.Now, null, null)
        {
            RcsRemoteId = "CNC_WMS_TASK_2_2026-09-11_0416355691"
        });

        await _tracker.ProbePollOnceAsync();

        var cond = _client.LastQuery?.Condition.Conditions.Single();
        Assert.Multiple(() =>
        {
            Assert.That(_client.LastQuery, Is.Not.Null);
            Assert.That(cond?.Key, Is.EqualTo(QueryTaskRequest.IdKey));
            Assert.That(cond?.Operator, Is.EqualTo("IN"));
            Assert.That(cond?.Value, Is.EqualTo(local));
            Assert.That(cond?.Value, Does.Not.Contain("CNC_WMS_"));
        });
    }

    [Test]
    public async Task Poll_SkipsAlreadyReboundRemoteIds()
    {
        _tasks.Seed(new RcsTaskRow(
            4, "CNC_WMS_TASK_2_2026-09-11_0214329453", "grab", "0", RcsTaskState.Dispatched, null, 5,
            LoadAreaCode, PositionCell, EqId, PositionId, null, null, null,
            0, "0", DateTime.Now, DateTime.Now, null, null));

        await _tracker.ProbePollOnceAsync();
        Assert.That(_client.QueryCount, Is.EqualTo(0), "已回写成回包号的历史行不得再按 ID 去查");
    }

    // ─── helpers ─────────────────────────────────────────────────

    private void AssertDisabledNoConsume(RcsTaskRow before, RcsTaskRow after, string label)
    {
        Assert.Multiple(() =>
        {
            Assert.That(_client.TransitCount, Is.EqualTo(0), $"{label}: Send=0");
            Assert.That(_tasks.TryClaimCallCount, Is.EqualTo(0),
                $"{label}: ClaimCalls 须为 0；Actual={_tasks.TryClaimCallCount} " +
                $"Resolver={_resolver.CallCount} Validator={_validator.CallCount} " +
                $"Redo {before.RedoCount}→{after.RedoCount} 顺序={Fmt()}");
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount), $"{label}: RedoCount 不变");
            Assert.That(after.TaskState, Is.EqualTo(before.TaskState),
                $"{label}: 不得副作用改为 Dispatched");
            Assert.That(after.ErrorMsg, Is.EqualTo(before.ErrorMsg), $"{label}: 保留失败证据");
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0), $"{label}: 路由拒发不 Alarm");
        });
    }

    private void SeedFailedTask(int redoCount = 0)
        => ReseedFailedTask(LoadAreaCode, PositionCell, redoCount);

    private void ReseedFailedTask(string? from = null, string? to = null, int redoCount = 0)
    {
        _tasks.Seed(new RcsTaskRow(
            1, TaskId, "transit", "0", RcsTaskState.Failed, "failed", 5,
            from ?? LoadAreaCode, to ?? PositionCell,
            EqId, PositionId, null, null, null,
            redoCount, "0", DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(-4),
            DateTime.Now.AddMinutes(-3), "prev-fail-evidence"));
    }

    private void RebuildTracker(
        CountingManagedRouteResolver resolver, CountingRoutingValidator validator)
    {
        _resolver = resolver;
        _validator = validator;
        resolver.OrderSink = _order;
        validator.OrderSink = _order;
        _svc = new RcsTaskService(
            _client, _tasks, new TrackerNoopMsgLog(), new TrackingCallbackProcessor(),
            resolver, validator, NullLogger<RcsTaskService>.Instance, new TrackingSlotsForClosure(), new RcsCallbackNotifier());
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions { TrackerEnabled = true, MaxAutoRedo = MaxAutoRedo, PollIntervalMs = 60_000 }
        });
        _tracker = new RcsTaskTracker(
            _svc, _tasks, _alarms, new RcsCallbackNotifier(),
            options, new StubRuntime(), new ServiceCollection().BuildServiceProvider(),
            NullLogger<RcsTaskTracker>.Instance);
    }

    private string Fmt() => string.Join("→", _order);

    private sealed class ThrowingResolver : IManagedDispatchRouteResolver
    {
        public Task<ManagedDispatchRouteResult> ResolveAsync(
            string? fromCode, string? toCode, CancellationToken ct = default)
            => throw new InvalidOperationException("resolver-down");
    }

    private sealed class StubRuntime : IRcsRuntimeConfig
    {
        public string BaseUrl => "http://127.0.0.1";
        public string ClientCode => "CNC";
        public string Version => "1.0.0";
        public string TokenCode => "0";
        public int RequestTimeoutMs => 1000;
        public int MaxRetries => 0;
        public string CallbackHost => "127.0.0.1";
        public int CallbackPort => 9080;
        public int PollIntervalMs => 60_000;
        public string BootCallbackHost => CallbackHost;
        public int BootCallbackPort => CallbackPort;
        public void Apply(RcsConnectionConfig config) { }
        public void CaptureBootCallback() { }
        public RcsConnectionConfig Snapshot() => new()
        {
            BaseUrl = BaseUrl, ClientCode = ClientCode, CallbackHost = CallbackHost,
            CallbackPort = CallbackPort, RequestTimeoutMs = RequestTimeoutMs,
            MaxRetries = MaxRetries, PollIntervalMs = PollIntervalMs
        };
    }

    private sealed class TrackerNoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
