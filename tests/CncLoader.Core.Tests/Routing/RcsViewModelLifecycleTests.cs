using CncLoader.Core.Rcs;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.Routing;

[TestFixture]
public sealed class RcsViewModelLifecycleTests
{
    [Test]
    public void Dispose后_回调不再追加终端()
    {
        var notifier = new RcsCallbackNotifier();
        var h = ManualReplayHarness.Create(callbackNotifier: notifier);
        var vm = h.ViewModel;
        Assert.That(vm, Is.InstanceOf<IDisposable>());

        ((IDisposable)vm).Dispose();
        var before = vm.Terminal.TerminalLines.Count;
        notifier.RaiseTaskStatus(new RcsTaskStatusEvent("T-1", RcsErrorCode.Success, null, RcsTaskState.Completed));
        notifier.RaiseScanResult(new RcsScanResultEvent("T-1", RcsErrorCode.Success, "F1", Array.Empty<string>(), null));
        notifier.RaiseWarn(new RcsWarnEvent("R1", DateTime.Now.ToString("s"), "w", null));

        Assert.That(vm.Terminal.TerminalLines.Count, Is.EqualTo(before));
    }

    [Test]
    public void Dispose后_换架进度不再追加终端()
    {
        var h = ManualReplayHarness.Create();
        var vm = h.ViewModel;
        ((IDisposable)vm).Dispose();
        var before = vm.Terminal.TerminalLines.Count;

        h.ChangeFrame.RaiseProgress(new ChangeFrameProgressEvent(
            "txn-1", 1, FrameRole.Upload, ChangeFrameStep.PullOld, null, null, "RUNNING", null));

        Assert.That(vm.Terminal.TerminalLines.Count, Is.EqualTo(before));
    }
}
