namespace CncLoader.Core.Tests.Routing;

[TestFixture]
public sealed class RcsViewModelSafetyGateTests
{
    [Test]
    public async Task 测试连接不得Apply运行时出站()
    {
        var h = ManualReplayHarness.Create();
        h.ViewModel.BaseUrl = "http://10.0.0.8:5050";
        h.ViewModel.ClientCode = "PROBE";

        await h.ViewModel.TestConnectionCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Runtime.ApplyCount, Is.Zero, "探测不得改 IRcsRuntimeConfig");
            Assert.That(h.Runtime.BaseUrl, Is.EqualTo("http://127.0.0.1:8090"));
            Assert.That(h.Client.QueryCount, Is.EqualTo(1));
            Assert.That(h.Client.LastQueryAtBaseUrl, Is.EqualTo("http://10.0.0.8:5050"));
            Assert.That(h.Client.TransitCount, Is.Zero);
        });
    }

    [Test]
    public async Task 取消确认拒绝则不下发()
    {
        var h = ManualReplayHarness.Create();
        h.ViewModel.OperateTaskId = "TASK-CANCEL";
        h.Notify.ConfirmAnswer = false;

        await h.ViewModel.CancelCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Notify.ConfirmCount, Is.EqualTo(1));
            Assert.That(h.Client.CancelCount, Is.Zero);
        });
    }

    [Test]
    public async Task 重做确认拒绝则不下发()
    {
        var h = ManualReplayHarness.Create();
        h.SeedHistoricalTask();
        h.ViewModel.OperateTaskId = "LINE-A-MV-20260804120000-0001";
        h.Notify.ConfirmAnswer = false;
        var sendBefore = h.Client.SendCount;

        await h.ViewModel.RedoCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Notify.ConfirmCount, Is.EqualTo(1));
            Assert.That(h.Client.SendCount, Is.EqualTo(sendBefore));
        });
    }

    [Test]
    public async Task 换架确认拒绝则不发起()
    {
        var h = ManualReplayHarness.Create();
        h.ViewModel.ChangeFrameEquipmentId = "1";
        h.Notify.ConfirmAnswer = false;

        await h.ViewModel.ChangeFrameCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Notify.ConfirmCount, Is.EqualTo(1));
            Assert.That(h.ChangeFrame.CallCount, Is.Zero);
        });
    }

    [Test]
    public async Task 空托盘回收确认拒绝则不下发()
    {
        var h = ManualReplayHarness.CreateForPalletReturn();
        h.ViewModel.PalletReturnFromCode = TypedEndpointSeedShapes.PositionCell;
        h.Notify.ConfirmAnswer = false;
        var sendBefore = h.Client.SendCount;

        await h.ViewModel.PalletReturnCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Notify.ConfirmCount, Is.EqualTo(1));
            Assert.That(h.Client.SendCount, Is.EqualTo(sendBefore));
        });
    }
}
