using CncLoader.Core.Rcs;
using CncLoader.Core.Tests.Routing;

namespace CncLoader.Core.Tests.UI;

/// <summary>任务列表：长编号/错误可点选查看，空错误不弹框。</summary>
[TestFixture]
public sealed class RcsTaskListDisplayTests
{
    [Test]
    public void HasError_仅非空白文案为真()
    {
        var empty = NewTask(null);
        var blank = NewTask("  ");
        var hit = NewTask("任务编号与回包号不一致");

        Assert.Multiple(() =>
        {
            Assert.That(empty.HasError, Is.False);
            Assert.That(blank.HasError, Is.False);
            Assert.That(hit.HasError, Is.True);
            Assert.That(new RcsMsgRow(1, DateTime.UnixEpoch, "OUT", "transitTask", "T",
                null, null, 1, false, null).HasError, Is.False);
            Assert.That(new RcsMsgRow(1, DateTime.UnixEpoch, "OUT", "transitTask", "T",
                null, null, 1, false, "timeout").HasError, Is.True);
        });
    }

    [Test]
    public void 点选任务后详情区可见()
    {
        var h = ManualReplayHarness.Create();
        Assert.That(h.ViewModel.TaskBoard.HasSelectedTask, Is.False);

        h.ViewModel.TaskBoard.SelectedTask = h.SeedHistoricalTask(error: "任务编号不一致");

        Assert.That(h.ViewModel.TaskBoard.HasSelectedTask, Is.True);
    }

    [Test]
    public void 查看任务错误_有文案则Alert()
    {
        var h = ManualReplayHarness.Create();
        var row = h.SeedHistoricalTask(error: "任务编号不一致");

        h.ViewModel.TaskBoard.ShowTaskErrorCommand.Execute(row);

        Assert.Multiple(() =>
        {
            Assert.That(h.Notify.AlertCount, Is.EqualTo(1));
            Assert.That(h.Notify.All, Does.Contain("任务编号不一致"));
        });
    }

    [Test]
    public void 查看任务错误_无文案不弹()
    {
        var h = ManualReplayHarness.Create();
        var row = h.SeedHistoricalTask(error: "");

        h.ViewModel.TaskBoard.ShowTaskErrorCommand.Execute(row);
        h.ViewModel.TaskBoard.ShowTaskErrorCommand.Execute(null);

        Assert.That(h.Notify.AlertCount, Is.Zero);
    }

    [Test]
    public async Task 进页激活_拉最新任务_不必点刷新()
    {
        var h = ManualReplayHarness.Create();
        await h.ViewModel.TaskBoard.RefreshTaskListAsync();

        var id = "LINE01-GR-20260917174000-0099";
        h.SeedHistoricalTask(taskId: id, state: RcsTaskState.Dispatched);

        Assert.That(h.ViewModel.TaskBoard.Tasks.Any(t => t.RcsTaskId == id), Is.False);

        h.ViewModel.OnActivated();
        await WaitUntilAsync(
            () => h.ViewModel.TaskBoard.Tasks.Any(t => t.RcsTaskId == id),
            TimeSpan.FromSeconds(2));

        h.ViewModel.OnDeactivated();
    }

    [Test]
    public async Task 状态回调_刷新任务列表()
    {
        var notifier = new RcsCallbackNotifier();
        var h = ManualReplayHarness.Create(callbackNotifier: notifier);
        await h.ViewModel.TaskBoard.RefreshTaskListAsync();

        var id = "LINE01-GR-20260917174000-0088";
        h.SeedHistoricalTask(taskId: id, state: RcsTaskState.Completed);

        Assert.That(h.ViewModel.TaskBoard.Tasks.Any(t => t.RcsTaskId == id), Is.False);

        notifier.RaiseTaskStatus(new RcsTaskStatusEvent(
            id, RcsErrorCode.Success, null, RcsTaskState.Completed));
        await WaitUntilAsync(
            () => h.ViewModel.TaskBoard.Tasks.Any(t => t.RcsTaskId == id),
            TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task 查找任务号_选中并回填操作框()
    {
        var h = ManualReplayHarness.Create();
        var id = "LINE01-GR-20260914164312-0001";
        h.SeedHistoricalTask(taskId: id, state: RcsTaskState.Canceled);

        h.ViewModel.TaskBoard.TaskQuery = id;
        await h.ViewModel.TaskBoard.FindTaskCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.ViewModel.TaskBoard.OperateTaskId, Is.EqualTo(id));
            Assert.That(h.ViewModel.TaskBoard.SelectedTask?.RcsTaskId, Is.EqualTo(id));
            Assert.That(h.ViewModel.TaskBoard.HasSelectedTask, Is.True);
        });
    }

    [Test]
    public void 查看报文错误_有文案则Alert()
    {
        var h = ManualReplayHarness.Create();
        var row = new RcsMsgRow(1, DateTime.UnixEpoch, "OUT", "transitTask", "T",
            "{}", "{}", 12, false, "UPLOAD_SLOT_OCCUPIED");

        h.ViewModel.TaskBoard.ShowMessageErrorCommand.Execute(row);

        Assert.Multiple(() =>
        {
            Assert.That(h.Notify.AlertCount, Is.EqualTo(1));
            Assert.That(h.Notify.All, Does.Contain("UPLOAD_SLOT_OCCUPIED"));
        });
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }

        Assert.Fail($"条件在 {timeout.TotalSeconds:0.#}s 内未满足");
    }

    private static RcsTaskRow NewTask(string? error) => new(
        1, "LINE01-GR-1", "grab", "0", "FAILED", null, 5,
        "101", "201", null, null, null, null, null,
        0, "0", DateTime.UnixEpoch, null, null, error);
}
