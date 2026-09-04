using System.Reflection;
using CncLoader.Core.Rcs;
using CncLoader.UI.ViewModels.Pages;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 审查补充第二组（D12）：删除 Skip + PalletReturn 类型化门禁。
/// 打真实 RcsViewModel + RcsTaskService + Resolver + Validator。
/// </summary>
[TestFixture]
public sealed class PalletReturnRoutingGateTests
{
    private ManualReplayHarness _h = null!;

    [SetUp]
    public void SetUp() => _h = ManualReplayHarness.CreateForPalletReturn();

    // ─── 真实链还原（文档化断言，防漂移）──────────────────────────

    [Test]
    public void Chain_Documents_PalletReturn_ProductionPath()
    {
        Assert.Multiple(() =>
        {
            // 1 UI RelayCommand
            Assert.That(typeof(RcsViewModel).GetProperty("PalletReturnCommand"), Is.Not.Null);
            // 2 From 绑定
            Assert.That(typeof(RcsViewModel).GetProperty("PalletReturnFromCode"), Is.Not.Null);
            // 5 Skip 已删除（D12）
            Assert.That(typeof(TransitDispatchArgs).GetProperty("SkipManagedRouteGate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            // 操作语义
            Assert.That(typeof(TransitDispatchArgs).GetProperty(nameof(TransitDispatchArgs.Operation)),
                Is.Not.Null);
            Assert.That(Enum.IsDefined(typeof(DispatchOperationKind), DispatchOperationKind.PalletReturn),
                Is.True);
            // 10 DispatchPalletReturnAsync 入口
            Assert.That(typeof(IRcsTaskService).GetMethod(nameof(IRcsTaskService.DispatchPalletReturnAsync)),
                Is.Not.Null);
        });
    }

    // ─── RED 1｜Skip 属性必须消失 ─────────────────────────────────

    [Test]
    public void Red1_SkipManagedRouteGate_MustNotExist_OnTransitDispatchArgs()
    {
        var t = typeof(TransitDispatchArgs);
        Assert.Multiple(() =>
        {
            Assert.That(t.GetProperty("SkipManagedRouteGate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null,
                "RED1: TransitDispatchArgs.SkipManagedRouteGate 必须删除");
            Assert.That(t.GetProperty("SkipValidation",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(t.GetProperty("BypassRouteGate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Null);
            Assert.That(t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(p => p.Name.Contains("Skip", StringComparison.OrdinalIgnoreCase)
                              && p.Name.Contains("Gate", StringComparison.OrdinalIgnoreCase)),
                Is.False,
                "不得存在等价 Skip*Gate 逃生属性");
        });
    }

    // ─── RED 2｜旧 Skip 绕过不可构造；未知路由须经门禁拒发 ─────────

    [Test]
    public async Task Red2_SkipTrue_UnknownFromTo_MustReject_NoCreate_NoRcs()
    {
        Assert.That(typeof(TransitDispatchArgs).GetProperty("SkipManagedRouteGate",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            Is.Null, "旧 Skip 属性已删除，无法再赋值绕过");

        var result = await _h.TaskService.DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = LineId,
            LineCode = LineCode,
            FromCode = "FREE-TEXT-UNKNOWN-FROM",
            ToCode = "FREE-TEXT-UNKNOWN-TO",
            Author = "skip-bypass-red"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "未知路由须经 Final 拒发（无 Skip 逃生）");
            Assert.That(result.FailureKind, Is.EqualTo(RcsFailureKind.RouteUnavailable)
                .Or.EqualTo(RcsFailureKind.ConfigurationUnavailable));
            Assert.That(_h.TaskStore.CreateCount, Is.EqualTo(0));
            Assert.That(_h.Client.SendCount, Is.EqualTo(0));
            Assert.That(_h.RouteResolver.CallCount, Is.GreaterThanOrEqualTo(1),
                "须经 Resolver");
        });
    }

    // ─── 契约 3｜POSITION→PALLET_RETURN ───────────────────────────

    [Test]
    public async Task Contract3_PositionToPalletReturn_MustGate_AndSucceedOnce()
    {
        await DispatchPalletReturnAsync(PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(_h.Client.SendCount, Is.EqualTo(1), "合法路径应 RCS 一次");
            Assert.That(_h.TaskStore.CreateCount, Is.EqualTo(1));
            Assert.That(_h.Notify.SuccessCount, Is.EqualTo(1));
            Assert.That(_h.RouteResolver.CallCount, Is.GreaterThanOrEqualTo(1),
                "须经 Resolver");
            Assert.That(_h.Validator.CallCount, Is.GreaterThanOrEqualTo(1),
                "须经 Validator");
            Assert.That(_h.Plc.WriteCount, Is.EqualTo(0));
        });
    }

    // ─── 契约 4｜FRAME→PALLET_RETURN ──────────────────────────────

    [Test]
    public async Task Contract4_FrameToPalletReturn_MustGate_AndSucceedOnce()
    {
        AssertFrameShape(_h.LocationMap.SnapshotByCode(FrameCellCode).Single());
        await DispatchPalletReturnAsync(FrameCellCode);

        Assert.Multiple(() =>
        {
            Assert.That(_h.Client.SendCount, Is.EqualTo(1));
            Assert.That(_h.TaskStore.CreateCount, Is.EqualTo(1));
            Assert.That(_h.Notify.SuccessCount, Is.EqualTo(1));
            Assert.That(_h.RouteResolver.CallCount, Is.GreaterThanOrEqualTo(1),
                "FRAME→PALLET_RETURN 须经 Resolver");
            Assert.That(_h.Frames.FindCallCount, Is.GreaterThanOrEqualTo(1),
                "须校验 Frame 活动");
            Assert.That(_h.Plc.WriteCount, Is.EqualTo(0));
        });
    }

    // ─── RED 5｜From 未知 ─────────────────────────────────────────

    [Test]
    public async Task Red5_UnknownFrom_Rejects_NoCreate_NoRcs_Warning()
    {
        await DispatchPalletReturnAsync("NO-SUCH-FROM-CODE");
        AssertRejected("From 未知");
    }

    // ─── RED 6｜From 是 AREA ──────────────────────────────────────

    [Test]
    public async Task Red6_FromArea_Rejects_NotAllowed()
    {
        await DispatchPalletReturnAsync(LoadAreaCode);
        AssertRejected("From=AREA 不得 PalletReturn");
        Assert.That(_h.Client.SendCount, Is.EqualTo(0),
            "不允许 AREA→PALLET_RETURN");
    }

    // ─── RED 7｜From POSITION 已禁用 ──────────────────────────────

    [Test]
    public async Task Red7_FromPositionDisabled_Rejects()
    {
        _h.LocationMap.SetStateByCode(PositionCell, remove: false, state: "1");
        await DispatchPalletReturnAsync(PositionCell);
        AssertRejected("From POSITION Map 禁用");
    }

    [Test]
    public async Task Red7_FromPositionWorkLineDisabled_Rejects()
    {
        _h.Store.SetWorkLineState(LineId, "1");
        await DispatchPalletReturnAsync(PositionCell);
        AssertRejected("From POSITION 线体禁用（不得只依赖 UI 列表）");
    }

    // ─── RED 8｜From FRAME 已禁用 ─────────────────────────────────

    [Test]
    public async Task Red8_FromFrameMapDisabled_Rejects()
    {
        _h.LocationMap.SetStateByCode(FrameCellCode, remove: false, state: "1");
        await DispatchPalletReturnAsync(FrameCellCode);
        AssertRejected("From FRAME Map 禁用");
    }

    [Test]
    public async Task Red8_FromFrameEntityDisabled_Rejects()
    {
        _h.Frames.SetState(FrameIdTransit, "1");
        await DispatchPalletReturnAsync(FrameCellCode);
        AssertRejected("From Frame 实体禁用");
    }

    // ─── RED 9｜To 不是 PALLET_RETURN（Service 角色策略缺口）──────

    [Test]
    public async Task Red9_ToNonPalletReturnArea_ServiceMustReject()
    {
        var result = await _h.TaskService.DispatchPalletReturnAsync(
            EqId, PositionId, PositionCell, LoadAreaCode, LineId, LineCode, "role-red");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False,
                "RED9: To=LOAD_AREA 角色不匹配须拒");
            Assert.That(_h.TaskStore.CreateCount, Is.EqualTo(0));
            Assert.That(_h.Client.SendCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Red9b_DispatchOperationKind_PalletReturn_GapDocumented()
    {
        var kindType = typeof(TransitDispatchArgs).Assembly.GetTypes()
            .FirstOrDefault(t => t.Name == "DispatchOperationKind");
        Assert.Multiple(() =>
        {
            Assert.That(kindType, Is.Not.Null,
                "须有 DispatchOperationKind（Service 边界识别 PalletReturn）");
            Assert.That(Enum.GetNames(kindType!), Does.Contain("PalletReturn"));
            Assert.That(Enum.GetNames(kindType!), Does.Contain("Transit"));
            Assert.That(typeof(TransitDispatchArgs).GetProperty(nameof(TransitDispatchArgs.Operation))
                    ?.PropertyType,
                Is.EqualTo(kindType));
        });
    }

    // ─── RED 10｜To PALLET_RETURN 已禁用（UI Pre 后）──────────────

    [Test]
    public async Task Red10_ToPalletReturnDisabledAfterUiResolve_NoCreate_NoRcs()
    {
        _h.LocationMap.DisableAreaAfterResolveCount(LocPalletReturn, resolveCount: 1);

        await DispatchPalletReturnAsync(PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(_h.LocationMap.ResolveAreaCallCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(_h.TaskStore.CreateCount, Is.EqualTo(0),
                "RED10: Final 前 To 禁用须拒");
            Assert.That(_h.Client.SendCount, Is.EqualTo(0));
            Assert.That(_h.Notify.SuccessCount, Is.EqualTo(0));
            Assert.That(_h.Notify.WarningCount + _h.Notify.ErrorCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    // ─── RED 11｜同码歧义 ─────────────────────────────────────────

    [Test]
    public async Task Red11_TwoActivePalletReturnSameCode_Ambiguous_Rejects()
    {
        _h.LocationMap.Seed(new LocationMapItem
        {
            Id = 55, LocType = "AREA", LocName = LocPalletReturn,
            RcsCode = PalletReturnCode, RcsType = "station",
            EquipmentId = null, PositionId = null, FrameId = null
        });

        var result = await _h.TaskService.DispatchPalletReturnAsync(
            EqId, PositionId, PositionCell, PalletReturnCode, LineId, LineCode, "ambig-red");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False, "RED11: 歧义须拒发");
            Assert.That(_h.TaskStore.CreateCount, Is.EqualTo(0));
            Assert.That(_h.Client.SendCount, Is.EqualTo(0));
        });
    }

    // ─── RED 12｜即时失效 ─────────────────────────────────────────

    [Test]
    public async Task Red12_SameInstance_SecondDispatchAfterDisable_NoExtraSend()
    {
        await DispatchPalletReturnAsync(PositionCell);
        Assert.That(_h.Client.SendCount, Is.EqualTo(1), "第一次全活动应发送");
        var firstSend = _h.Client.SendCount;
        var firstCreate = _h.TaskStore.CreateCount;

        _h.Notify.Reset();
        _h.RouteResolver.Reset();
        _h.Validator.Reset();

        _h.LocationMap.SetStateByCode(PositionCell, remove: false, state: "1");
        await DispatchPalletReturnAsync(PositionCell);

        Assert.Multiple(() =>
        {
            Assert.That(_h.Client.SendCount, Is.EqualTo(firstSend),
                "禁用后第二次不得再发；总 RCS 应仍为 1");
            Assert.That(_h.TaskStore.CreateCount, Is.EqualTo(firstCreate));
            Assert.That(_h.Notify.SuccessCount, Is.EqualTo(0));
            Assert.That(_h.Notify.WarningCount + _h.Notify.ErrorCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(_h.RouteResolver.CallCount, Is.GreaterThanOrEqualTo(1),
                "第二次须重新 Resolve，不得复用第一次结论");
        });
    }

    private async Task DispatchPalletReturnAsync(string fromCode)
    {
        _h.ViewModel.PalletReturnFromCode = fromCode;
        await _h.ViewModel.PalletReturnCommand.ExecuteAsync(null);
    }

    private void AssertRejected(string scenario)
    {
        Assert.Multiple(() =>
        {
            Assert.That(_h.Client.SendCount, Is.EqualTo(0), $"{scenario}: RCS=0");
            Assert.That(_h.TaskStore.CreateCount, Is.EqualTo(0), $"{scenario}: Create=0");
            Assert.That(_h.Notify.SuccessCount, Is.EqualTo(0), $"{scenario}: Success=0");
            Assert.That(_h.Notify.WarningCount + _h.Notify.ErrorCount, Is.GreaterThanOrEqualTo(1),
                $"{scenario}: Warning/Error≥1");
            Assert.That(_h.Plc.WriteCount, Is.EqualTo(0), $"{scenario}: 不写 PLC");
            Assert.That(_h.Slots.ReserveCount, Is.EqualTo(0), $"{scenario}: 无预记");
            Assert.That(_h.Slots.RollbackCount + _h.Slots.RollbackTakeCount, Is.EqualTo(0),
                $"{scenario}: 无回滚副作用");
        });
    }
}
