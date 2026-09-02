using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 R17 / R18 / R19：Redo / Redispatch 新执行须重新权威校验。
/// 打真实 <see cref="Communication.Rcs.RcsTaskService"/>（与 ViewModel RelayCommand）。
/// </summary>
[TestFixture]
public sealed class RcsTaskReplayRoutingGateTests
{
    private const string DisabledMsg = "路由配置已禁用或不可用";

    // ─── R17｜Redo 路由禁用 ─────────────────────────────────────────

    [Test]
    public async Task R17_Redo_DisabledRoute_DoesNotResend_PreservesHistory()
    {
        var h = ManualReplayHarness.Create();
        var original = h.SeedHistoricalTask(
            taskId: "LINE-A-MV-REDO-0001",
            state: RcsTaskState.Failed,
            error: "prev-fail-evidence",
            redoCount: 2);
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "1");

        var before = h.TaskStore.Snapshot(original.RcsTaskId!)!;
        var result = await h.TaskService.RedoAsync(original.RcsTaskId!);
        var after = h.TaskStore.Snapshot(original.RcsTaskId!)!;

        Assert.Multiple(() =>
        {
            Assert.That(h.Validator.CallCount, Is.GreaterThan(0),
                "重发前须权威校验（当前 Redo 未接线 Validator → RED）");
            Assert.That(h.Client.SendCount, Is.EqualTo(0), "禁用路由不得 RCS");
            Assert.That(h.TaskStore.IncrementRedoCount, Is.EqualTo(0),
                "拒发不得先 IncrementRedo（当前实现先递增 → RED）");
            Assert.That(h.Callbacks.ForgetCount, Is.EqualTo(0), "不得清除 callback seen");
            Assert.That(h.Client.TransitTaskIds, Does.Not.Contain(original.RcsTaskId));
            Assert.That(after.RcsTaskId, Is.EqualTo(before.RcsTaskId), "不得创建新 taskId");
            Assert.That(after.TaskState, Is.EqualTo(before.TaskState), "须保持原任务态");
            Assert.That(after.ErrorMsg, Is.EqualTo(before.ErrorMsg), "须保留失败证据");
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount), "RedoCount 不得因拒发变化");
            Assert.That(h.Slots.ReserveCount, Is.EqualTo(0), "不得恢复槽位预记");
            Assert.That(result.Success, Is.False);
            Assert.That(IsRouteUnavailable(result), Is.True, "须返回可区分的 RouteUnavailable");
            Assert.That(result.Error ?? result.Message ?? "", Does.Contain(DisabledMsg).IgnoreCase
                .Or.Contain("路由").IgnoreCase);
        });
    }

    [Test]
    public async Task R17_Redo_ViaViewModel_Disabled_NoSuccessUi()
    {
        var h = ManualReplayHarness.Create();
        var row = h.SeedHistoricalTask("LINE-A-MV-REDO-UI-1");
        h.Store.SetWorkLineState(ManualReplayRoutingCodes.LineId, "1");
        h.ViewModel.OperateTaskId = row.RcsTaskId!;

        await h.ViewModel.RedoCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.EqualTo(0));
            Assert.That(h.Notify.SuccessCount, Is.EqualTo(0), "UI 不得 Success");
            Assert.That(h.Notify.WarningCount + h.Notify.ErrorCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(string.Join('\n', h.Notify.All.Append(h.ViewModel.StatusMessage ?? "")),
                Does.Contain(DisabledMsg).IgnoreCase
                    .Or.Contain("路由").IgnoreCase
                    .Or.Contain("失败").IgnoreCase);
        });
    }

    // ─── R18｜Redispatch 路由禁用（与 Redo 分开）────────────────────

    [Test]
    public async Task R18_Redispatch_DisabledRoute_DoesNotResend_DoesNotMutateHistory()
    {
        var h = ManualReplayHarness.Create();
        var original = h.SeedHistoricalTask(
            taskId: "LINE-A-MV-REDIS-0001",
            state: RcsTaskState.Failed,
            error: "redispatch-evidence",
            redoCount: 1);
        h.Store.SetCraftState(ManualReplayRoutingCodes.CraftId, "1");

        var before = h.TaskStore.Snapshot(original.RcsTaskId!)!;
        // 自动重发路径：调用方已 TryIncrement；此处直接 RedispatchAsync
        var result = await h.TaskService.RedispatchAsync(original.RcsTaskId!);
        var after = h.TaskStore.Snapshot(original.RcsTaskId!)!;

        Assert.Multiple(() =>
        {
            Assert.That(h.Validator.CallCount, Is.GreaterThan(0),
                "Redispatch 须权威校验（不可用 Redo 推断覆盖）");
            Assert.That(h.Client.SendCount, Is.EqualTo(0));
            Assert.That(h.TaskStore.CreateCount, Is.EqualTo(0), "不得生成新可执行记录");
            Assert.That(h.TaskStore.IncrementRedoCount, Is.EqualTo(0),
                "Redispatch 本身不应再 IncrementRedo");
            Assert.That(after.TaskState, Is.EqualTo(before.TaskState), "不得修改原历史任务态");
            Assert.That(after.ErrorMsg, Is.EqualTo(before.ErrorMsg), "不得清理错误");
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount));
            Assert.That(h.Callbacks.ForgetCount, Is.EqualTo(0));
            Assert.That(result.Success, Is.False);
            Assert.That(IsRouteUnavailable(result), Is.True);
            Assert.That((result.Error ?? "").Contains("成功", StringComparison.Ordinal), Is.False);
        });
    }

    [Test]
    public void R18_Redispatch_NotCoveredByRedoAlone_SeparateEntry()
    {
        // 契约锁：RedispatchAsync 与 RedoAsync 是不同入口；本测只打 Redispatch。
        var method = typeof(global::CncLoader.Communication.Rcs.RcsTaskService).GetMethod("RedispatchAsync");
        Assert.That(method, Is.Not.Null);
        Assert.That(method!.DeclaringType, Is.EqualTo(typeof(global::CncLoader.Communication.Rcs.RcsTaskService)));
    }

    // ─── R19｜禁用即时生效（Redo）───────────────────────────────────

    [Test]
    public async Task R19_Redo_DisableWithoutRestart_SecondRejected()
    {
        var h = ManualReplayHarness.Create();
        var row = h.SeedHistoricalTask("LINE-A-MV-R19-REDO", state: RcsTaskState.Failed, error: "e", redoCount: 0);

        var first = await h.TaskService.RedoAsync(row.RcsTaskId!);
        Assert.That(first.Success, Is.True, "第一次路由活动应可重发");
        Assert.That(h.Client.SendCount, Is.EqualTo(1));

        // 不重建 Service / Validator
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "1");
        var sendAfterFirst = h.Client.SendCount;
        var queriesBefore = h.Store.Queries.Count;

        // 恢复为可 Redo 样本（第一次成功会 SetDispatched / Forget）
        h.TaskStore.Seed(row with
        {
            TaskState = RcsTaskState.Failed,
            ErrorMsg = "e2",
            RedoCount = 1
        });

        var second = await h.TaskService.RedoAsync(row.RcsTaskId!);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.EqualTo(sendAfterFirst),
                "第二次禁用后 RCS 总次数不得再增加");
            Assert.That(h.Store.Queries.Count, Is.GreaterThan(queriesBefore),
                "须重新读权威配置");
            Assert.That(second.Success, Is.False);
            Assert.That(IsRouteUnavailable(second), Is.True);
        });
    }

    // ─── R22｜异常数据（Redo/Redispatch）────────────────────────────

    [Test]
    public async Task R22_MissingParentWorkLine_RedoRejects()
    {
        var h = ManualReplayHarness.Create(seedActiveRoute: false);
        // Equipment → Craft 存在，但缺父 WorkLine
        h.Store.Crafts.Add(new Core.Abstractions.CraftworkRoutingRow(20, 10, 1, "0"));
        h.Store.Equipments.Add(new Core.Abstractions.EquipmentRoutingRow(30, 20, "0"));
        h.LocationMap.Seed(new LocationMapItem
        {
            Id = 1, LocType = "POSITION", EquipmentId = 30, PositionId = 1,
            RcsCode = ManualReplayRoutingCodes.FromCell, RcsType = "cell"
        });
        h.LocationMap.Seed(new LocationMapItem
        {
            Id = 2, LocType = "POSITION", EquipmentId = 30, PositionId = 2,
            RcsCode = ManualReplayRoutingCodes.ToCell, RcsType = "cell"
        });
        var row = h.SeedHistoricalTask("LINE-A-MV-ORPHAN-WL");

        var result = await h.TaskService.RedoAsync(row.RcsTaskId!);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.EqualTo(0));
            Assert.That(result.Success, Is.False);
            Assert.That(IsRouteUnavailable(result), Is.True);
        });
    }

    [Test]
    public async Task R22_Redispatch_UnknownState_FailClosed()
    {
        var h = ManualReplayHarness.Create();
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "???");
        var row = h.SeedHistoricalTask("LINE-A-MV-UNK-STATE");

        var result = await h.TaskService.RedispatchAsync(row.RcsTaskId!);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.EqualTo(0));
            Assert.That(result.Success, Is.False);
            Assert.That(IsRouteUnavailable(result), Is.True);
        });
    }

    private static bool IsRouteUnavailable(RcsResult r)
    {
        if (r.Success) return false;
        if (r.FailureKind is RcsFailureKind.RouteUnavailable or RcsFailureKind.ConfigurationUnavailable)
            return true;
        var text = $"{r.Error} {r.Message}";
        return text.Contains("路由", StringComparison.OrdinalIgnoreCase)
               || text.Contains("RouteUnavailable", StringComparison.OrdinalIgnoreCase)
               || text.Contains("不可用", StringComparison.OrdinalIgnoreCase)
               || text.Contains("禁用", StringComparison.OrdinalIgnoreCase);
    }
}
