using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.UI;

[TestFixture]
public sealed class DashboardProcessOrderTests
{
    [Test]
    public void OrderByProcess_按工序节点而不是机台主键()
    {
        // 主键仍是 1内长宽 / 2平面度 / 3A基准；工序顺序是 1 → 3 → 2
        var ids = new[] { 1L, 2L, 3L };
        var order = new Dictionary<long, int> { [1] = 0, [3] = 1, [2] = 2 };

        var sorted = DashboardViewModel.OrderByProcess(ids, id => id, order);

        Assert.That(sorted, Is.EqualTo(new[] { 1L, 3L, 2L }));
    }

    [Test]
    public void OrderByProcess_无排序表时回退主键()
    {
        var ids = new[] { 3L, 1L, 2L };
        var sorted = DashboardViewModel.OrderByProcess(ids, id => id, new Dictionary<long, int>());
        Assert.That(sorted, Is.EqualTo(new[] { 1L, 2L, 3L }));
    }
}
