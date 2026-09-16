namespace CncLoader.Core.Tests.Routing;

/// <summary>手动互斥：自动派工运行中禁止手动下发 / Redo / 换架 / 托盘回收；暂停后放行，取消任务不受限。</summary>
[TestFixture]
public sealed class RcsViewModelManualInterlockTests
{
    [Test]
    public async Task 自动派工运行中_手动下发被拒_不校验路由不调用RCS()
    {
        var h = ManualReplayHarness.Create(schedulerEnabled: true);
        h.ViewModel.SelectedKind = "搬运";
        h.ViewModel.FromCode = ManualReplayRoutingCodes.FromCell;
        h.ViewModel.ToCode = ManualReplayRoutingCodes.ToCell;
        h.ViewModel.Priority = 5;

        await h.ViewModel.DispatchCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.Zero, "自动派工运行中不得手动下发");
            Assert.That(h.Validator.CallCount, Is.Zero, "互斥在路由校验之前拦截");
        });
    }

    [Test]
    public async Task 自动派工运行中_Redo换架托盘回收均被拒且不弹确认()
    {
        var h = ManualReplayHarness.Create(schedulerEnabled: true);
        h.SeedHistoricalTask();
        h.ViewModel.OperateTaskId = "LINE-A-MV-20260804120000-0001";
        h.ViewModel.ChangeFrameEquipmentId = "1";
        h.ViewModel.PalletReturnFromCode = ManualReplayRoutingCodes.FromCell;
        var sendBefore = h.Client.SendCount;

        await h.ViewModel.RedoCommand.ExecuteAsync(null);
        await h.ViewModel.ChangeFrameCommand.ExecuteAsync(null);
        await h.ViewModel.PalletReturnCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.EqualTo(sendBefore));
            Assert.That(h.ChangeFrame.CallCount, Is.Zero);
            Assert.That(h.Notify.ConfirmCount, Is.Zero, "被互斥拦截时不得进入危险操作确认");
        });
    }

    [Test]
    public async Task 暂停自动派工后_Redo放行到确认环节()
    {
        var h = ManualReplayHarness.Create(schedulerEnabled: true);
        h.SeedHistoricalTask();
        h.ViewModel.PauseAutoDispatch = true;
        h.ViewModel.OperateTaskId = "LINE-A-MV-20260804120000-0001";
        h.Notify.ConfirmAnswer = false;

        await h.ViewModel.RedoCommand.ExecuteAsync(null);

        Assert.That(h.Notify.ConfirmCount, Is.EqualTo(1), "暂停后互斥放行，进入危险操作确认");
    }

    [Test]
    public async Task 自动派工运行中_取消任务不受互斥限制()
    {
        var h = ManualReplayHarness.Create(schedulerEnabled: true);
        h.ViewModel.OperateTaskId = "TASK-CANCEL";
        h.Notify.ConfirmAnswer = false;

        await h.ViewModel.CancelCommand.ExecuteAsync(null);

        Assert.That(h.Notify.ConfirmCount, Is.EqualTo(1), "取消任务在自动派工运行中仍须可用");
    }
}
