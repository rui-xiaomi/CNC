using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 R16 / R19 / R22：手动自由文本派工须经权威路由门禁。
/// 打真实 <see cref="CncLoader.UI.ViewModels.Pages.RcsViewModel"/> + <see cref="Communication.Rcs.RcsTaskService"/>。
/// </summary>
[TestFixture]
public sealed class ManualDispatchRoutingGateTests
{
    private const string DisabledMsg = "路由配置已禁用或不可用";

    // ─── R16｜手动自由文本禁用路由 ───────────────────────────────────

    [Test]
    public async Task R16_FromResolvable_SourceEquipmentDisabled_RejectsManualDispatch()
    {
        var h = ManualReplayHarness.Create();
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "1");
        await DispatchManualAsync(h, ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell);
        AssertRejectedManual(h, "源 Equipment 已禁用");
    }

    [Test]
    public async Task R16_ToResolvable_DestEquipmentDisabled_RejectsManualDispatch()
    {
        var h = ManualReplayHarness.Create();
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.DstEq, "1");
        await DispatchManualAsync(h, ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell);
        AssertRejectedManual(h, "目标 Equipment 已禁用");
    }

    [Test]
    public async Task R16_FromTo_PointOrLocationMapDisabled_RejectsManualDispatch()
    {
        var h = ManualReplayHarness.Create();
        // 模拟 LOCATION_MAP 软删：移除 From 映射（活动解析失败）
        h.LocationMap.SetStateByCode(ManualReplayRoutingCodes.FromCell, remove: true);
        await DispatchManualAsync(h, ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell);
        AssertRejectedManual(h, "LOCATION_MAP/Point 不可用");
    }

    [Test]
    public async Task R16_FromTo_Unresolvable_RejectsManualDispatch()
    {
        var h = ManualReplayHarness.Create(seedActiveRoute: true);
        await DispatchManualAsync(h, "NO-SUCH-FROM", "NO-SUCH-TO");
        AssertRejectedManual(h, "无法解析到受管配置");
    }

    [Test]
    public async Task R16_DropdownStaleValue_ConfigDisabledAfterLoad_StillRejects()
    {
        var h = ManualReplayHarness.Create();
        // UI 曾加载活动项：先记下活动 From/To，再禁用 WorkLine，仍提交旧值
        var from = ManualReplayRoutingCodes.FromCell;
        var to = ManualReplayRoutingCodes.ToCell;
        h.Store.SetWorkLineState(ManualReplayRoutingCodes.LineId, "1");
        await DispatchManualAsync(h, from, to);
        AssertRejectedManual(h, "下拉陈旧值+配置已禁用");
    }

    [Test]
    public async Task R16_AllActive_ManualDispatchSucceeds()
    {
        var h = ManualReplayHarness.Create();
        await DispatchManualAsync(h, ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.EqualTo(1), "全活动应允许一次 RCS 下发");
            Assert.That(h.TaskStore.CreateCount, Is.EqualTo(1));
            Assert.That(h.Notify.SuccessCount, Is.EqualTo(1), "全活动 UI Success");
            Assert.That(h.ViewModel.StatusMessage, Does.StartWith("成功").Or.Contain("成功"));
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0), "手动搬运不写 POS_TEST_START");
        });
    }

    [Test]
    public async Task R16_TestConnection_NotBlockedByRoutingGate()
    {
        var h = ManualReplayHarness.Create();
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "1");
        h.ViewModel.BaseUrl = "http://127.0.0.1:8090";
        h.ViewModel.ClientCode = "CNC";

        await h.ViewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.QueryCount, Is.EqualTo(1), "诊断 query 应照常");
            Assert.That(h.Client.TransitCount, Is.EqualTo(0), "测试连接不得创建搬运任务");
            Assert.That(h.TaskStore.CreateCount, Is.EqualTo(0));
        });
    }

    // ─── R19｜禁用即时生效（手动）───────────────────────────────────

    [Test]
    public async Task R19_Manual_DisableWithoutRestart_SecondDispatchRejected()
    {
        var h = ManualReplayHarness.Create();

        await DispatchManualAsync(h, ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell);
        var sendAfterFirst = h.Client.SendCount;
        Assert.That(sendAfterFirst, Is.EqualTo(1), "第一次全活动应下发");

        // 不重建 Service/ViewModel/Validator
        h.Store.SetWorkLineState(ManualReplayRoutingCodes.LineId, "1");
        var queriesBefore = h.Store.Queries.Count;
        h.Notify.Reset(); // 只断言第二次派工的 UI 结果，不受第一次 Success 累加干扰

        await DispatchManualAsync(h, ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.EqualTo(sendAfterFirst),
                "第二次须重新读权威配置并拒发；不得再增加 RCS 调用");
            Assert.That(h.Store.Queries.Count, Is.GreaterThan(queriesBefore),
                "第二次须再查权威 Store（禁止沿用第一次缓存结论）");
            Assert.That(h.Validator.CallCount, Is.GreaterThan(0),
                "第二次新执行须走 Validator（GREEN 接线后）");
            Assert.That(IsManualSuccess(h), Is.False, "第二次 UI 不得 Success");
            Assert.That(IsManualWarning(h), Is.True);
            Assert.That(SafeText(h), Does.Contain(DisabledMsg).IgnoreCase
                .Or.Contain("路由").IgnoreCase);
        });
    }

    // ─── R22｜异常数据 fail-closed（手动）───────────────────────────

    [TestCase("x")]
    [TestCase("")]
    [TestCase(null)]
    public async Task R22_UnknownEquipmentState_ManualRejects(string? state)
    {
        var h = ManualReplayHarness.Create();
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, state!);
        await DispatchManualAsync(h, ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell);
        AssertRejectedManual(h, $"未知 STATE={state ?? "<null>"}");
    }

    [Test]
    public async Task R22_OrphanEquipment_ManualRejects()
    {
        var h = ManualReplayHarness.Create(seedActiveRoute: false);
        h.Store.Equipments.Add(new Core.Abstractions.EquipmentRoutingRow(99, 9999, "0"));
        h.LocationMap.Seed(new LocationMapItem
        {
            Id = 9, LocType = "POSITION", EquipmentId = 99, PositionId = 1,
            RcsCode = "ORPHAN-FROM", RcsType = "cell"
        });
        h.LocationMap.Seed(new LocationMapItem
        {
            Id = 10, LocType = "POSITION", EquipmentId = 99, PositionId = 2,
            RcsCode = "ORPHAN-TO", RcsType = "cell"
        });
        await DispatchManualAsync(h, "ORPHAN-FROM", "ORPHAN-TO");
        AssertRejectedManual(h, "孤儿 Equipment");
    }

    [Test]
    public async Task R22_AmbiguousFromTo_ManualRejects_DoesNotPickFirst()
    {
        var h = ManualReplayHarness.Create();
        h.LocationMap.Seed(new LocationMapItem
        {
            Id = 80, LocType = "POSITION", EquipmentId = ManualReplayRoutingCodes.SrcEq,
            PositionId = 2, RcsCode = ManualReplayRoutingCodes.AmbiguousCell, RcsType = "cell"
        });
        h.LocationMap.Seed(new LocationMapItem
        {
            Id = 81, LocType = "POSITION", EquipmentId = ManualReplayRoutingCodes.DstEq,
            PositionId = 2, RcsCode = ManualReplayRoutingCodes.AmbiguousCell, RcsType = "cell"
        });
        await DispatchManualAsync(h, ManualReplayRoutingCodes.AmbiguousCell, ManualReplayRoutingCodes.ToCell);
        AssertRejectedManual(h, "解析歧义不得取第一条");
    }

    private static async Task DispatchManualAsync(ManualReplayHarness h, string from, string to)
    {
        h.ViewModel.SelectedKind = "搬运";
        h.ViewModel.FromCode = from;
        h.ViewModel.ToCode = to;
        h.ViewModel.Priority = 5;
        await h.ViewModel.DispatchCommand.ExecuteAsync(null);
    }

    private static void AssertRejectedManual(ManualReplayHarness h, string scenario)
    {
        Assert.Multiple(() =>
        {
            Assert.That(h.Validator.CallCount, Is.GreaterThan(0),
                $"{scenario}: 须调用 Validator（当前手动路径未接线 → RED）");
            Assert.That(h.Client.SendCount, Is.EqualTo(0),
                $"{scenario}: RCS SendCount 须为 0");
            Assert.That(h.TaskStore.CreateCount, Is.EqualTo(0),
                $"{scenario}: 不得创建新可执行任务");
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0), $"{scenario}: 不得写 PLC");
            Assert.That(h.Notify.SuccessCount, Is.EqualTo(0), $"{scenario}: UI Success=0");
            Assert.That(h.Notify.WarningCount + h.Notify.ErrorCount, Is.GreaterThanOrEqualTo(1),
                $"{scenario}: UI Warning/Error≥1");
            Assert.That(SafeText(h), Does.Contain(DisabledMsg).IgnoreCase
                .Or.Contain("路由").IgnoreCase
                .Or.Contain("不可用").IgnoreCase,
                $"{scenario}: 文案须表达路由不可用");
            Assert.That(SafeText(h), Does.Not.Contain("http://").IgnoreCase
                .And.Not.Contain("8090"),
                $"{scenario}: 不得泄露完整 RCS URL");
        });
    }

    private static bool IsManualSuccess(ManualReplayHarness h)
        => h.Notify.SuccessCount > 0
           || (h.ViewModel.StatusMessage ?? "").Contains("成功", StringComparison.Ordinal);

    private static bool IsManualWarning(ManualReplayHarness h)
        => !IsManualSuccess(h) && (h.Notify.WarningCount + h.Notify.ErrorCount > 0);

    private static string SafeText(ManualReplayHarness h)
        => string.Join('\n', new[] { h.ViewModel.StatusMessage }
            .Concat(h.Notify.All)
            .Concat(h.ViewModel.TerminalLines)
            .Where(x => !string.IsNullOrWhiteSpace(x)));
}
