using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Routing;

/// <summary>RCS 页「测试本机监听」只消费 <see cref="IRcsCallbackListener"/> 探针结果，不直连 HTTP。</summary>
[TestFixture]
public sealed class RcsViewModelCallbackProbeTests
{
    [Test]
    public async Task 未监听_展示未监听并Warning()
    {
        var listener = new ManualReplayHarness.StubCallbackListener
        {
            ListenError = "端口占用",
            NextProbe = RcsLocalCallbackProbeResult.NotListening("端口占用")
        };
        var h = ManualReplayHarness.Create(callbackListener: listener);

        await h.ViewModel.Connection.TestCallbackCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(listener.ProbeCount, Is.EqualTo(1));
            Assert.That(h.ViewModel.Connection.CallbackHealthText, Is.EqualTo("未监听"));
            Assert.That(h.ViewModel.Connection.CallbackHealthBrushKey, Is.EqualTo("AlarmBrush"));
            Assert.That(h.ViewModel.Connection.IsTestingCallback, Is.False);
            Assert.That(h.Notify.WarningCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task 探针成功_展示本机可达并保留仅证明Kestrel语义()
    {
        var listener = new ManualReplayHarness.StubCallbackListener
        {
            IsListening = true,
            BoundPort = 19080,
            NextProbe = RcsLocalCallbackProbeResult.Success(200, 12, """{"taskId":""}""")
        };
        var h = ManualReplayHarness.Create(callbackListener: listener);

        await h.ViewModel.Connection.TestCallbackCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.ViewModel.Connection.CallbackHealthText, Is.EqualTo("本机可达 12ms"));
            Assert.That(h.ViewModel.Connection.CallbackHealthBrushKey, Is.EqualTo("OkBrush"));
            Assert.That(h.Notify.SuccessCount, Is.EqualTo(1));
            Assert.That(h.Notify.All[0], Does.Contain("不代表 RCS 服务器回调网络已打通"));
            Assert.That(h.ViewModel.Terminal.TerminalLines, Has.Some.Contains("仅证明本机 Kestrel"));
        });
    }

    [Test]
    public async Task 白名单拒绝_展示本机被拒()
    {
        var listener = new ManualReplayHarness.StubCallbackListener
        {
            NextProbe = RcsLocalCallbackProbeResult.Forbidden(403, 3, "forbidden")
        };
        var h = ManualReplayHarness.Create(callbackListener: listener);

        await h.ViewModel.Connection.TestCallbackCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.ViewModel.Connection.CallbackHealthText, Is.EqualTo("本机被拒"));
            Assert.That(h.Notify.WarningCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task 网络失败_展示本机不通()
    {
        var listener = new ManualReplayHarness.StubCallbackListener
        {
            NextProbe = RcsLocalCallbackProbeResult.Unreachable("连接被拒绝")
        };
        var h = ManualReplayHarness.Create(callbackListener: listener);

        await h.ViewModel.Connection.TestCallbackCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.ViewModel.Connection.CallbackHealthText, Is.EqualTo("本机不通"));
            Assert.That(h.Notify.ErrorCount, Is.EqualTo(1));
        });
    }
}
