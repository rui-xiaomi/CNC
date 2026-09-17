using CncLoader.Core.State;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.UI;

[TestFixture]
public sealed class DashboardSurfaceProjectorTests
{
    [Test]
    public void AggregatePriority_报警优先于检测与待料()
    {
        var agg = DashboardSurfaceProjector.AggregatePriority(new[]
        {
            PositionState.WaitLoad, PositionState.Processing, PositionState.Alarm
        });
        Assert.That(agg, Is.EqualTo(PositionState.Alarm));
    }

    [Test]
    public void AggregatePriority_检测优先于搬运()
    {
        var agg = DashboardSurfaceProjector.AggregatePriority(new[]
        {
            PositionState.Dispatching, PositionState.Processing
        });
        Assert.That(agg, Is.EqualTo(PositionState.Processing));
    }

    [Test]
    public void BuildDesiredFlowNodes_头尾锚点夹机台()
    {
        var machines = new[] { new MachineCardVm(7, "内长宽", "EQ07") };
        var nodes = DashboardSurfaceProjector.BuildDesiredFlowNodes(machines);
        Assert.Multiple(() =>
        {
            Assert.That(nodes, Has.Count.EqualTo(3));
            Assert.That(nodes[0].Key, Is.EqualTo("upload"));
            Assert.That(nodes[0].IsAnchor, Is.True);
            Assert.That(nodes[1].EquipmentId, Is.EqualTo(7));
            Assert.That(nodes[2].IsEndFork, Is.True);
        });
    }

    [Test]
    public void ApplyEquipmentAggregate_无工位且在线_显示等待上料()
    {
        var node = new FlowNodeVm("eq-1", "内长宽", 1, isAnchor: false);
        var mach = new MachineCardVm(1, "内长宽") { PlcOnline = true };
        DashboardSurfaceProjector.ApplyEquipmentAggregate(node, mach);
        Assert.Multiple(() =>
        {
            Assert.That(node.AggregateDisplay, Is.EqualTo("等待上料"));
            Assert.That(node.StateBadge, Is.EqualTo("idle"));
            Assert.That(node.SummaryText, Is.EqualTo("无加工位"));
        });
    }

    [Test]
    public void SegFor_报警与检测()
    {
        Assert.That(DashboardSurfaceProjector.SegFor(PositionState.Alarm).Badge, Is.EqualTo("alarm"));
        Assert.That(DashboardSurfaceProjector.SegFor(PositionState.Processing).Text, Is.EqualTo("检测"));
    }

    [Test]
    public void MachineCardVm_UpdateIdentity_刷新编号与名称()
    {
        var card = new MachineCardVm(3, "EQ3", "EQ03");
        card.UpdateIdentity("A基准", "EQ02");
        Assert.Multiple(() =>
        {
            Assert.That(card.Name, Is.EqualTo("A基准"));
            Assert.That(card.EquipmentCode, Is.EqualTo("EQ02"));
        });
    }

    [Test]
    public void BuildDesiredFlowNodes_使用刷新后的编号()
    {
        var card = new MachineCardVm(2, "EQ2", "EQ02");
        card.UpdateIdentity("平面度", "EQ03");
        var nodes = DashboardSurfaceProjector.BuildDesiredFlowNodes(new[] { card });
        Assert.Multiple(() =>
        {
            Assert.That(nodes[1].Title, Is.EqualTo("平面度"));
            Assert.That(nodes[1].EquipmentCode, Is.EqualTo("EQ03"));
        });
    }
}
