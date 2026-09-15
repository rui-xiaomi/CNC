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
        Assert.That(h.ViewModel.HasSelectedTask, Is.False);

        h.ViewModel.SelectedTask = h.SeedHistoricalTask(error: "任务编号不一致");

        Assert.That(h.ViewModel.HasSelectedTask, Is.True);
    }

    [Test]
    public void 查看任务错误_有文案则Alert()
    {
        var h = ManualReplayHarness.Create();
        var row = h.SeedHistoricalTask(error: "任务编号不一致");

        h.ViewModel.ShowTaskErrorCommand.Execute(row);

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

        h.ViewModel.ShowTaskErrorCommand.Execute(row);
        h.ViewModel.ShowTaskErrorCommand.Execute(null);

        Assert.That(h.Notify.AlertCount, Is.Zero);
    }

    [Test]
    public void 查看报文错误_有文案则Alert()
    {
        var h = ManualReplayHarness.Create();
        var row = new RcsMsgRow(1, DateTime.UnixEpoch, "OUT", "transitTask", "T",
            "{}", "{}", 12, false, "UPLOAD_SLOT_OCCUPIED");

        h.ViewModel.ShowMessageErrorCommand.Execute(row);

        Assert.Multiple(() =>
        {
            Assert.That(h.Notify.AlertCount, Is.EqualTo(1));
            Assert.That(h.Notify.All, Does.Contain("UPLOAD_SLOT_OCCUPIED"));
        });
    }

    private static RcsTaskRow NewTask(string? error) => new(
        1, "LINE01-GR-1", "grab", "0", "FAILED", null, 5,
        "101", "201", null, null, null, null, null,
        0, "0", DateTime.UnixEpoch, null, null, error);
}
