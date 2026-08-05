using CncLoader.Common.Configuration;
using CncLoader.Communication.Rcs;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 D15：ChangeFrame FRAME↔AREA 真实 Service 路由门禁专项。
/// 真实 ChangeFrameOrchestrator + RcsTaskService + Resolver + Validator；禁止 Tracing/AlwaysAvailable/Skip。
/// </summary>
[TestFixture]
public sealed class ChangeFrameRoutingGateTests
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
    private ChangeFrameOrchestrator _orch = null!;
    private List<ChangeFrameProgressEvent> _progress = null!;
    private StoreBackedFrameBindEquipment _equipment = null!;

    [SetUp]
    public void SetUp()
    {
        _loc = new FakeLocationMapForRouting();
        SeedChangeFrameMaps(_loc);

        _frames = new FakeFrameRoutingStore();
        _frames.Seed(FrameIdTransit);
        _frames.Seed(FrameIdDownload);

        _eq = new MutableEquipmentRoutingStore();
        SeedChangeFrameEquipment(_eq);

        _order = new List<string>();
        _resolver = new CountingManagedRouteResolver(
            new ManagedDispatchRouteResolver(
                _loc, _frames, NullLogger<ManagedDispatchRouteResolver>.Instance))
        {
            OrderSink = _order
        };
        _equipment = new StoreBackedFrameBindEquipment(_eq);
        _validator = new CountingRoutingValidator(
            new RoutingAvailabilityValidator(
                _eq, _equipment, _frames,
                NullLogger<RoutingAvailabilityValidator>.Instance))
        {
            OrderSink = _order
        };

        _client = new FakeRcsHttpClient { OrderSink = _order };
        _tasks = new MutableRcsTaskStore { OrderSink = _order };
        var taskSvc = new RcsTaskService(
            _client, _tasks, new CfNoopMsgLog(), new TrackingCallbackProcessor(),
            _resolver, _validator, NullLogger<RcsTaskService>.Instance);

        _alarms = new NoopAlarms();
        _notifier = new RcsCallbackNotifier();
        _progress = new List<ChangeFrameProgressEvent>();

        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions
            {
                EmptyBufferArea = LocEmptyBuffer,
                FullBufferArea = LocFullBuffer,
                MaxAutoRedo = 3
            }
        });

        _orch = new ChangeFrameOrchestrator(
            taskSvc, _tasks, _equipment, _loc, _alarms,
            new IdlePositionScheduler(), _notifier, options,
            NullLogger<ChangeFrameOrchestrator>.Instance);
        _orch.ProgressChanged += (_, e) => _progress.Add(e);
    }

    // ─── A. 真实链文档化 ─────────────────────────────────────────

    [Test]
    public void Chain_Documents_ChangeFrame_PullPush_RealPath()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(ChangeFrameOrchestrator).GetMethod(
                nameof(ChangeFrameOrchestrator.ChangeFrameAsync)), Is.Not.Null);
            Assert.That(typeof(IRcsTaskService).GetMethod(nameof(IRcsTaskService.DispatchTransitAsync)),
                Is.Not.Null);
            AssertAreaShape(_loc.SnapshotByCode(EmptyBufferCode).Single(), LocEmptyBuffer);
            AssertAreaShape(_loc.SnapshotByCode(FullBufferCode).Single(), LocFullBuffer);
            AssertFrameShape(_loc.SnapshotByCode(FrameCellCode).Single());
            AssertFrameShape(_loc.SnapshotByCode(FrameDownloadCellCode).Single());
            Assert.That(_loc.SnapshotByCode(EmptyBufferCode).Single().EquipmentId, Is.Null);
            Assert.That(_loc.SnapshotByCode(FrameCellCode).Single().EquipmentId, Is.Null);
            Assert.That(_loc.SnapshotByCode(FrameCellCode).Single().FrameId, Is.EqualTo(FrameIdTransit));
            Assert.That(_loc.SnapshotByCode(FrameDownloadCellCode).Single().FrameId, Is.EqualTo(FrameIdDownload));
            Assert.That(_eq.FrameBinds.Any(b =>
                    b.EquipmentId == EqId && b.FrameId == FrameIdTransit && b.FrameRole == "0"),
                Is.True, "Upload 角色 Bind → FRAME 中转架");
            Assert.That(_eq.FrameBinds.Any(b =>
                    b.EquipmentId == EqId && b.FrameId == FrameIdDownload && b.FrameRole == "1"),
                Is.True, "Unload 角色 Bind → 下料架");
        });
    }

    // ─── A. 合法基线 pull / push ─────────────────────────────────

    [Test]
    public async Task Pull_Upload_FrameToEmptyBuffer_AllActive_Succeeds_ViaRealService()
    {
        var pull = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(pull.State, Is.EqualTo("RUNNING"), "合法 pull 须报告 RUNNING");
            Assert.That(pull.Step, Is.EqualTo(ChangeFrameStep.PullOld));
            Assert.That(pull.PullTaskId, Is.Not.Null.And.Not.Empty);
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
            Assert.That(_client.ExcuteCount, Is.EqualTo(0), "换架走 Transit 非 Excute");
            Assert.That(_tasks.Created.Single().Kind, Is.EqualTo(RcsTaskKind.ChangeFrame));
            Assert.That(_tasks.Created.Single().FromCode, Is.EqualTo(FrameCellCode));
            Assert.That(_tasks.Created.Single().ToCode, Is.EqualTo(EmptyBufferCode));
            Assert.That(_loc.SnapshotByCode(FrameCellCode).Single().EquipmentId, Is.Null);
            Assert.That(_loc.SnapshotByCode(EmptyBufferCode).Single().EquipmentId, Is.Null);
            Assert.That(_resolver.CallCount, Is.EqualTo(2), "Transit Pre+Final Resolve");
            Assert.That(_validator.CallCount, Is.EqualTo(2), "Transit Pre+Final Validate");
            Assert.That(_validator.Contexts.All(c => c.Operation == DispatchOperationKind.ChangeFrame), Is.True);
            Assert.That(_validator.Contexts.All(c => c.OperationEquipmentId == EqId), Is.True,
                "Final Context 须携带换架机台");
            Assert.That(_eq.FindFrameBindsCallCount, Is.GreaterThanOrEqualTo(2),
                "业务 Pre + Service Pre/Final 须查 FrameBind");
            Assert.That(_order.IndexOf("Validate"), Is.LessThan(_order.IndexOf("CreateTask")),
                $"Bind/Validate 须在 Create 前；顺序={string.Join("→", _order)}");
            Assert.That(_order.IndexOf("Resolve"), Is.LessThan(_order.IndexOf("CreateTask")),
                $"顺序={string.Join("→", _order)}");
            Assert.That(_order.Count(x => x == "CreateTask"), Is.EqualTo(1));
            Assert.That(_order.Count(x => x == "RcsTransit"), Is.EqualTo(1));
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Push_Upload_EmptyBufferToFrame_AllActive_Succeeds_ViaRealService()
    {
        var pull = await StartPullAsync(FrameRole.Upload);
        var createBefore = _tasks.CreateCount;
        var transitBefore = _client.TransitCount;
        _resolver.Reset();
        _validator.Reset();
        _order.Clear();

        var push = await CompletePullAndAwaitPushAsync(pull.PullTaskId!);

        Assert.Multiple(() =>
        {
            Assert.That(push.Step, Is.EqualTo(ChangeFrameStep.PushNew),
                $"push Actual step={push.Step} state={push.State} msg={push.Message}");
            Assert.That(push.State, Is.EqualTo("RUNNING"));
            Assert.That(push.PushTaskId, Is.Not.Null.And.Not.Empty);
            Assert.That(_tasks.CreateCount - createBefore, Is.EqualTo(1), "push 再 Create 一次");
            Assert.That(_client.TransitCount - transitBefore, Is.EqualTo(1));
            Assert.That(_tasks.Created.Last().FromCode, Is.EqualTo(EmptyBufferCode));
            Assert.That(_tasks.Created.Last().ToCode, Is.EqualTo(FrameCellCode));
            Assert.That(_resolver.CallCount, Is.EqualTo(2), "push 须 Pre+Final");
            Assert.That(_validator.CallCount, Is.EqualTo(2));
            Assert.That(_validator.Contexts.All(c => c.Operation == DispatchOperationKind.ChangeFrame), Is.True);
            Assert.That(_validator.Contexts.All(c => c.OperationEquipmentId == EqId), Is.True);
            Assert.That(_validator.Contexts.All(c =>
                    c.FromEndpoint?.Kind != ManagedEndpointKind.Position
                    || c.FromEndpoint.EquipmentId is not null),
                Is.True, "AREA/FRAME 无 EquipmentId 不得被误杀为 InvalidRelationship");
        });
    }

    [Test]
    public async Task Pull_Unload_FrameToFullBuffer_AllActive_Succeeds()
    {
        var pull = await StartPullAsync(FrameRole.Unload);

        Assert.Multiple(() =>
        {
            Assert.That(pull.State, Is.EqualTo("RUNNING"));
            Assert.That(_tasks.Created.Single().FromCode, Is.EqualTo(FrameDownloadCellCode));
            Assert.That(_tasks.Created.Single().ToCode, Is.EqualTo(FullBufferCode));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
            Assert.That(_resolver.CallCount, Is.EqualTo(2));
        });
    }

    // ─── B. 端点软删与类型保护（pull）────────────────────────────

    [Test]
    public async Task Pull_FrameMapDisabled_Rejects_NoCreate_NoRcs()
    {
        _loc.SetStateByCode(FrameCellCode, remove: false, state: "1");
        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "FRAME Map 禁用");
    }

    [Test]
    public async Task Pull_FrameEntityDisabled_Rejects_NoCreate_NoRcs()
    {
        _frames.SetState(FrameIdTransit, "1");

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "Frame 实体禁用");
        Assert.That(_resolver.CallCount, Is.GreaterThanOrEqualTo(1),
            $"须经 Service 门禁；顺序={Fmt()}");
    }

    [Test]
    public async Task Pull_FrameEntityMissing_Rejects_NoCreate_NoRcs()
    {
        _frames.Remove(FrameIdTransit);

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "Frame 实体不存在");
    }

    [Test]
    public async Task Pull_AreaMapDisabled_Rejects_NoCreate_NoRcs()
    {
        _loc.SetStateByCode(EmptyBufferCode, remove: false, state: "1");

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "AREA Map 禁用");
    }

    [Test]
    public async Task Pull_AreaRoleMutatedAfterResolve_FinalRejects_NoCreate()
    {
        _loc.MutateLocNameAfterResolveAreaCount(LocEmptyBuffer, resolveCount: 1, newLocName: "WEIRD_BUFFER");

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"AREA 角色非法须拒；Actual={e.State}/{e.Message} Create={_tasks.CreateCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_loc.ResolveAreaCallCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public async Task Pull_UnknownFrameRcs_ViaMapRemoved_Rejects()
    {
        _loc.SetStateByCode(FrameCellCode, remove: true);
        _loc.SetStateByCode(FrameShelfCode, remove: true);

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "FRAME Rcs 不可解析");
        Assert.That(_tasks.CreateCount, Is.EqualTo(0));
        Assert.That(_client.TransitCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Pull_UnknownArea_Rejects()
    {
        _loc.SetStateByCode(EmptyBufferCode, remove: true);

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "AREA 未知");
    }

    [Test]
    public async Task Pull_AmbiguousFrameCode_Rejects_NoCreate()
    {
        _loc.Seed(new LocationMapItem
        {
            Id = 90, LocType = "FRAME", FrameId = FrameIdTransit, LocName = "dup",
            RcsCode = FrameCellCode, RcsType = "cell",
            EquipmentId = null, PositionId = null
        });

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"同码双活动 Ambiguous 须拒；Create={_tasks.CreateCount} Transit={_client.TransitCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Pull_UnknownMapState_FailClosed()
    {
        _loc.SetStateByCode(FrameCellCode, remove: false, state: "9");
        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "9");

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "未知 STATE fail-closed");
    }

    [Test]
    public async Task Pull_WrongLocTypeOnFrameCode_Rejects()
    {
        _loc.SetLocTypeByCode(FrameCellCode, "AREA");
        _loc.SetLocNameByCode(FrameCellCode, LocLoadArea);
        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"错误 LocType 须拒；Actual={e.Message} Create={_tasks.CreateCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    // ─── B. push 端点保护（pull 成功后禁用再 push）───────────────

    [Test]
    public async Task Push_FrameMapDisabledBeforePush_Rejects_NoExtraCreate()
    {
        var pull = await StartPullAsync(FrameRole.Upload);
        var createBefore = _tasks.CreateCount;
        var transitBefore = _client.TransitCount;

        _loc.SetStateByCode(FrameCellCode, remove: false, state: "1");
        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");

        var push = await CompletePullAndAwaitPushAsync(pull.PullTaskId!);

        Assert.Multiple(() =>
        {
            Assert.That(push.Step, Is.EqualTo(ChangeFrameStep.Alarm));
            Assert.That(push.State, Is.EqualTo("FAILED"));
            Assert.That(_tasks.CreateCount - createBefore, Is.EqualTo(0), "push 禁用不得 Create");
            Assert.That(_client.TransitCount - transitBefore, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Push_AreaDisabledBeforePush_Rejects_NoExtraCreate()
    {
        var pull = await StartPullAsync(FrameRole.Upload);
        var createBefore = _tasks.CreateCount;
        var transitBefore = _client.TransitCount;
        _loc.SetStateByCode(EmptyBufferCode, remove: false, state: "1");

        var push = await CompletePullAndAwaitPushAsync(pull.PullTaskId!);

        Assert.Multiple(() =>
        {
            Assert.That(push.State, Is.EqualTo("FAILED"));
            Assert.That(_tasks.CreateCount - createBefore, Is.EqualTo(0));
            Assert.That(_client.TransitCount - transitBefore, Is.EqualTo(0));
        });
    }

    // ─── C. FrameBind + 机台上下文 ───────────────────────────────

    [Test]
    public async Task Pull_ActiveBind_CorrectEquipmentFrame_Succeeds()
    {
        var e = await StartPullAsync(FrameRole.Upload);
        Assert.That(e.State, Is.EqualTo("RUNNING"));
        Assert.That(_eq.FindFrameBindsCallCount, Is.GreaterThanOrEqualTo(1),
            "业务 Pre 须读 FrameBind");
    }

    [Test]
    public async Task Pull_BindMissing_Rejects_NoCreate()
    {
        _eq.ClearFrameBinds();

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "FrameBind 不存在");
        Assert.That(_client.TransitCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Pull_BindDisabled_Rejects_NoCreate()
    {
        _eq.SetFrameBindState(EqId, FrameIdTransit, "1");

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "FrameBind 禁用");
    }

    [Test]
    public async Task Pull_BindPointsOtherEquipment_Rejects()
    {
        _eq.ClearFrameBinds();
        _eq.SeedNextEquipment(99, CraftId, 1, LineId);
        _eq.BindFrame(99, FrameIdTransit, FrameRole.Upload);

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "Bind 指向其他 Equipment");
    }

    [Test]
    public async Task Pull_BindPointsOtherFrame_Rejects()
    {
        // Upload 角色绑到「另一 Frame」，并去掉该 Frame 的 Map → 业务 Pre 拒发
        _eq.ClearFrameBinds();
        _eq.BindFrame(EqId, FrameIdDownload, FrameRole.Upload);
        _loc.SetStateByCode(FrameDownloadCellCode, remove: false, state: "1");
        _loc.SetStateByCode(FrameDownloadShelfCode, remove: false, state: "1");

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "Bind 指向其他/无 Map Frame");
    }

    [Test]
    public async Task Pull_CannotBypassBind_WithOnlyFrameMapActive()
    {
        _eq.ClearFrameBinds();
        Assert.That(_loc.SnapshotByCode(FrameCellCode).Single().State, Is.EqualTo("0"));

        var e = await StartPullAsync(FrameRole.Upload);

        AssertRejectedPull(e, "不得仅凭 FRAME Map 绕过 Bind");
    }

    [Test]
    public async Task Pull_FinalContext_CarriesOperationEquipment_AndRejectsDisabledBind()
    {
        // 业务 Pre 读 Bind 成功后禁用 → Final 用 OperationEquipmentId 重读 Bind 拒发
        _eq.DisableFrameBindAfterFindCount(EqId, FrameIdTransit, findCount: 1);

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(_eq.FrameBinds.Single(b => b.FrameId == FrameIdTransit).State, Is.EqualTo("1"),
                "业务 Pre 返回后 Bind 已禁用");
            Assert.That(e.State, Is.EqualTo("FAILED"));
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_validator.Contexts.Any(c =>
                    c.Operation == DispatchOperationKind.ChangeFrame
                    && c.OperationEquipmentId == EqId),
                Is.True,
                $"Context 须带 OperationEquipmentId；顺序={Fmt()}");
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0), "路由拒发不 Raise");
        });
    }

    [Test]
    public async Task ChangeFrame_MissingEquipmentId_FailClosed_NoCreate()
    {
        var taskSvc = new RcsTaskService(
            _client, _tasks, new CfNoopMsgLog(), new TrackingCallbackProcessor(),
            _resolver, _validator, NullLogger<RcsTaskService>.Instance);

        var result = await taskSvc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = LineId, LineCode = LineCode, TaskType = "1",
            FromCode = FrameCellCode, ToCode = EmptyBufferCode,
            Kind = RcsTaskKind.ChangeFrame,
            Operation = DispatchOperationKind.ChangeFrame,
            EquipmentId = null,
            Author = "cf-no-eq"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureKind, Is.EqualTo(RcsFailureKind.RouteUnavailable)
                .Or.EqualTo(RcsFailureKind.ConfigurationUnavailable));
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task GenericTransit_FrameToArea_WithoutEquipmentContext_StillSucceeds_NoBindRequired()
    {
        // 通用手动 FRAME↔AREA：无换架机台上下文时不得无条件要求 FrameBind
        _eq.ClearFrameBinds();
        var taskSvc = new RcsTaskService(
            _client, _tasks, new CfNoopMsgLog(), new TrackingCallbackProcessor(),
            _resolver, _validator, NullLogger<RcsTaskService>.Instance);

        var result = await taskSvc.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = LineId, LineCode = LineCode, TaskType = "1",
            FromCode = FrameCellCode, ToCode = EmptyBufferCode,
            Kind = RcsTaskKind.Transit, Author = "generic-transit"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True,
                "通用 Transit FRAME→AREA 无 Eq 上下文不得因 Bind 误杀");
            Assert.That(_tasks.CreateCount, Is.EqualTo(1));
            Assert.That(_client.TransitCount, Is.EqualTo(1));
        });
    }

    // ─── D. TOCTOU / 缓存 ────────────────────────────────────────

    [Test]
    public async Task Pull_DisableFrameMapAfterBusinessResolve_FinalRejects()
    {
        // cell 命中计 1：返回后禁 Frame Map → Service Final 拒
        _loc.DisableFrameMapAfterResolveCount(FrameIdTransit, resolveCount: 1);

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(_loc.ResolveFrameCallCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"Pre 后禁 FRAME Map Final 须拒；Actual={e.State} Create={_tasks.CreateCount} 顺序={Fmt()}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_resolver.CallCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public async Task Pull_DisableAreaAfterBusinessResolve_FinalRejects()
    {
        _loc.DisableAreaAfterResolveCount(LocEmptyBuffer, resolveCount: 1);

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"Pre 后禁 AREA Final 须拒；Create={_tasks.CreateCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Pull_DisableFrameEntityAfterPreValidate_FinalRejects()
    {
        _frames.DisableAfterFindCount(FrameIdTransit, findCount: 1);

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"Pre 后禁 Frame 实体须拒；Create={_tasks.CreateCount} Finds={_frames.FindCallCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_frames.FindCallCount, Is.GreaterThanOrEqualTo(2), "Pre+Final 各读 Frame");
        });
    }

    [Test]
    public async Task Pull_EquipmentDisabledAfterPreValidate_FinalRejects()
    {
        var inner = new RoutingAvailabilityValidator(
            _eq, _equipment, _frames, NullLogger<RoutingAvailabilityValidator>.Instance);
        var validator = new CountingRoutingValidator(
            new DisableEquipmentAfterFirstValidate(inner, _eq, EqId))
        {
            OrderSink = _order
        };
        RebuildOrchestrator(_resolver, validator);

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"Pre 后禁 Equipment Final 须拒；Create={_tasks.CreateCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Pull_DisableBindAfterPre_FinalStillMustReject()
    {
        _eq.DisableFrameBindAfterFindCount(EqId, FrameIdTransit, findCount: 1);

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"Pre 后禁 Bind Final 须拒；Actual={e.State} Create={_tasks.CreateCount} " +
                $"Transit={_client.TransitCount} BindQueries={_eq.FindFrameBindsCallCount} 顺序={Fmt()}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Pull_SameInstance_SecondAfterDisable_NoExtraTransit()
    {
        var first = await StartPullAsync(FrameRole.Upload);
        Assert.That(first.State, Is.EqualTo("RUNNING"));
        Assert.That(_client.TransitCount, Is.EqualTo(1));
        var create1 = _tasks.CreateCount;

        _loc.SetStateByCode(FrameCellCode, remove: false, state: "1");
        _loc.SetStateByCode(FrameShelfCode, remove: false, state: "1");
        _progress.Clear();
        _resolver.Reset();
        _validator.Reset();

        var second = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(second.State, Is.EqualTo("FAILED"));
            Assert.That(_client.TransitCount, Is.EqualTo(1), "第二次不得再 Transit");
            Assert.That(_tasks.CreateCount, Is.EqualTo(create1));
        });
    }

    [Test]
    public async Task Pull_FinalReadsCurrentStore_NotStaleCache()
    {
        // 第一次成功证明 Store 活动；禁用后同实例立刻拒 — 缓存非权威
        await StartPullAsync(FrameRole.Upload);
        Assert.That(_client.TransitCount, Is.EqualTo(1));
        var createBefore = _tasks.CreateCount;
        var transitBefore = _client.TransitCount;
        _loc.SetStateByCode(EmptyBufferCode, remove: false, state: "1");
        _progress.Clear();

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"));
            Assert.That(_tasks.CreateCount, Is.EqualTo(createBefore));
            Assert.That(_client.TransitCount, Is.EqualTo(transitBefore));
        });
    }

    // ─── E. Cancellation / Exception ─────────────────────────────

    [Test]
    public async Task Pull_CancelledBeforeFinal_NoCreate_NoRcs()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await _orch.ChangeFrameAsync(EqId, FrameRole.Upload, "cf-red", cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 允许取消异常
        }

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_progress.All(p => p.State != "RUNNING" || p.PullTaskId is null), Is.True,
                "取消不得留下 pull 成功态");
        });
    }

    [Test]
    public async Task Pull_ResolverThrows_FailClosed_NoCreate()
    {
        var throwing = new CountingManagedRouteResolver(new ThrowingResolver());
        RebuildOrchestrator(throwing, _validator);

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"Resolver 异常须 fail-closed；Actual={e.State} Create={_tasks.CreateCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Pull_ValidatorThrows_FailClosed_NoCreate()
    {
        var throwing = new CountingRoutingValidator(new ThrowingValidator());
        RebuildOrchestrator(_resolver, throwing);

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"Validator 异常须 fail-closed；Create={_tasks.CreateCount}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Pull_FrameBindQueryThrows_FailClosed_NoCreate()
    {
        _eq.ThrowOnNextFindFrameBinds = new InvalidOperationException("bind-store-down");

        ChangeFrameProgressEvent? e = null;
        try
        {
            e = await StartPullAsync(FrameRole.Upload);
        }
        catch (InvalidOperationException)
        {
            // 业务 Pre 未吞异常亦可；关键 Create/RCS=0
        }

        Assert.Multiple(() =>
        {
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            if (e is not null)
                Assert.That(e.State, Is.Not.EqualTo("RUNNING"));
        });
    }

    // ─── 通知契约（只记录，不改生产）────────────────────────────

    [Test]
    public async Task Pull_RouteReject_NoRaise_NoAlarm_D8()
    {
        _loc.SetStateByCode(EmptyBufferCode, remove: false, state: "1");

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"));
            Assert.That(_tasks.CreateCount, Is.EqualTo(0));
            Assert.That(_client.TransitCount, Is.EqualTo(0));
            Assert.That(_alarms.RaiseCount, Is.EqualTo(0),
                "D8：路由拒发 Raise=0 / 不置工位 Alarm");
            Assert.That(e.Step, Is.EqualTo(ChangeFrameStep.Alarm));
            Assert.That(_progress.Any(p => p.State == "RUNNING" && p.PullTaskId is not null),
                Is.False, "不报告成功派发");
        });
    }

    [Test]
    public async Task Pull_NonRouteRcsFailure_StillRaisesAlarm()
    {
        _client.FailNextTransit = true;

        var e = await StartPullAsync(FrameRole.Upload);

        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"));
            Assert.That(_client.TransitCount, Is.EqualTo(1), "已形成真实发送尝试");
            Assert.That(_alarms.RaiseCount, Is.GreaterThanOrEqualTo(1),
                "非路由 RCS 失败须保持既有 Alarm，不得被 D8 分支吞掉");
        });
    }

    // ─── helpers ─────────────────────────────────────────────────

    private async Task<ChangeFrameProgressEvent> StartPullAsync(FrameRole role)
    {
        var before = _progress.Count;
        await _orch.ChangeFrameAsync(EqId, role, "cf-red");
        Assert.That(_progress.Count, Is.GreaterThan(before), "须有 Progress 事件");
        return _progress[^1];
    }

    private async Task<ChangeFrameProgressEvent> CompletePullAndAwaitPushAsync(string pullTaskId)
    {
        var tcs = new TaskCompletionSource<ChangeFrameProgressEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, ChangeFrameProgressEvent e)
        {
            if (e.Step is ChangeFrameStep.PushNew or ChangeFrameStep.Alarm)
                tcs.TrySetResult(e);
        }

        _orch.ProgressChanged += Handler;
        try
        {
            _notifier.RaiseTaskStatus(new RcsTaskStatusEvent(
                pullTaskId, RcsErrorCode.Success, "ok", RcsTaskState.Completed));
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _orch.ProgressChanged -= Handler;
        }
    }

    private void AssertRejectedPull(ChangeFrameProgressEvent e, string label)
    {
        Assert.Multiple(() =>
        {
            Assert.That(e.State, Is.EqualTo("FAILED"),
                $"{label}：须 FAILED；Actual={e.State}/{e.Message} " +
                $"Resolver={_resolver.CallCount} Validator={_validator.CallCount} " +
                $"BindQ={_eq.FindFrameBindsCallCount} Create={_tasks.CreateCount} " +
                $"Transit={_client.TransitCount} 顺序={Fmt()}");
            Assert.That(_tasks.CreateCount, Is.EqualTo(0), $"{label}: Create=0");
            Assert.That(_client.TransitCount, Is.EqualTo(0), $"{label}: RCS=0");
            Assert.That(e.Step, Is.EqualTo(ChangeFrameStep.Alarm));
            Assert.That(_progress.Any(p =>
                    p.State == "RUNNING" && !string.IsNullOrEmpty(p.PullTaskId)),
                Is.False, $"{label}: 不得遗留 pull 成功态");
        });
    }

    private void RebuildOrchestrator(
        CountingManagedRouteResolver resolver, CountingRoutingValidator validator)
    {
        _resolver = resolver;
        _validator = validator;
        resolver.OrderSink = _order;
        validator.OrderSink = _order;
        var taskSvc = new RcsTaskService(
            _client, _tasks, new CfNoopMsgLog(), new TrackingCallbackProcessor(),
            resolver, validator, NullLogger<RcsTaskService>.Instance);
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions
            {
                EmptyBufferArea = LocEmptyBuffer,
                FullBufferArea = LocFullBuffer,
                MaxAutoRedo = 3
            }
        });
        _notifier = new RcsCallbackNotifier();
        _orch = new ChangeFrameOrchestrator(
            taskSvc, _tasks, _equipment, _loc, _alarms,
            new IdlePositionScheduler(), _notifier, options,
            NullLogger<ChangeFrameOrchestrator>.Instance);
        _orch.ProgressChanged += (_, e) => _progress.Add(e);
    }

    private string Fmt() => string.Join("→", _order);

    private sealed class ThrowingResolver : IManagedDispatchRouteResolver
    {
        public Task<ManagedDispatchRouteResult> ResolveAsync(
            string? fromCode, string? toCode, CancellationToken ct = default)
            => throw new InvalidOperationException("resolver-down");
    }

    private sealed class ThrowingValidator : IRoutingAvailabilityValidator
    {
        public Task<RoutingAvailabilityResult> ValidateAsync(
            DispatchRouteContext context, CancellationToken ct = default)
            => throw new InvalidOperationException("validator-down");
    }

    /// <summary>Pre Validate 返回后禁用机台，供 ChangeFrame Final Classify 拒发。</summary>
    private sealed class DisableEquipmentAfterFirstValidate : IRoutingAvailabilityValidator
    {
        private readonly IRoutingAvailabilityValidator _inner;
        private readonly MutableEquipmentRoutingStore _eq;
        private readonly long _equipmentId;
        private int _calls;

        public DisableEquipmentAfterFirstValidate(
            IRoutingAvailabilityValidator inner, MutableEquipmentRoutingStore eq, long equipmentId)
        {
            _inner = inner;
            _eq = eq;
            _equipmentId = equipmentId;
        }

        public async Task<RoutingAvailabilityResult> ValidateAsync(
            DispatchRouteContext context, CancellationToken ct = default)
        {
            var r = await _inner.ValidateAsync(context, ct);
            if (Interlocked.Increment(ref _calls) == 1)
                _eq.SetEquipmentState(_equipmentId, "1");
            return r;
        }
    }

    private sealed class CfNoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    }
}
