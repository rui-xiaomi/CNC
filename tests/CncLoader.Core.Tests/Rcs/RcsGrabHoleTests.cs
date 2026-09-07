using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class RcsGrabHoleTests
{
    [Test]
    public void FromSlot_一层一位_为101()
    {
        Assert.That(RcsGrabHole.FromSlot(1, 1), Is.EqualTo(101));
    }

    [Test]
    public void FromSlot_二层二位_为202()
    {
        Assert.That(RcsGrabHole.FromSlot(2, 2), Is.EqualTo(202));
    }

    [Test]
    public void TryFromPositionCell_加工位cell_拆出101()
    {
        Assert.That(RcsGrabHole.TryFromPositionCell("201101", "201", out var hole), Is.True);
        Assert.That(hole, Is.EqualTo(101));
    }

    [Test]
    public void TryStationNo_非数字_失败()
    {
        Assert.That(RcsGrabHole.TryStationNo("ST-201", out _), Is.False);
    }

    [Test]
    public void TryBuild_料架到加工位_组包正确()
    {
        var item = RcsGrabHole.TryBuild("101", 1, 1, "201", 1, 2, "MAT-1");
        Assert.That(item, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(item!.SrcNo, Is.EqualTo(101));
            Assert.That(item.SrcPos, Is.EqualTo(101));
            Assert.That(item.DstNo, Is.EqualTo(201));
            Assert.That(item.DstPos, Is.EqualTo(102));
            Assert.That(item.Data, Is.EqualTo("MAT-1"));
        });
    }

    [Test]
    public void TryBuildFromCells_两端cell_组包正确()
    {
        var item = RcsGrabHole.TryBuildFromCells("201", "201101", "101", "101102", "M");
        Assert.That(item, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(item!.SrcPos, Is.EqualTo(101));
            Assert.That(item.DstPos, Is.EqualTo(102));
        });
    }

    [Test]
    public void TryBuild_站码非法_返回null()
    {
        Assert.That(RcsGrabHole.TryBuild("LOAD", 1, 1, "201", 1, 1, "M"), Is.Null);
    }
}
