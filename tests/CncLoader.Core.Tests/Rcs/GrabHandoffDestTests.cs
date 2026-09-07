using System.Text.Json;
using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class GrabHandoffDestTests
{
    [Test]
    public void TryReadDstPos_抓取报文_读出101()
    {
        var json = JsonSerializer.Serialize(new[] { new GrabItem { DstPos = 101, SrcPos = 101 } });
        Assert.That(GrabHandoffDest.TryReadDstPos(json), Is.EqualTo(101));
    }

    [Test]
    public void Resolve_工位cell_原样返回()
    {
        var cell = Pos(2, 3, "202101");
        var hit = GrabHandoffDest.Resolve([cell], "202101", cell, 101);
        Assert.That(hit, Is.SameAs(cell));
    }

    [Test]
    public void Resolve_机台站码加dstPos_落到202101()
    {
        var station = new LocationMapItem
        {
            LocType = "EQUIPMENT", EquipmentId = 2, RcsCode = "202", RcsType = "station"
        };
        var pos3 = Pos(2, 3, "202101");
        var posOther = Pos(1, 1, "201101");
        var hit = GrabHandoffDest.Resolve([pos3, posOther], "202", station, 101);
        Assert.That(hit, Is.SameAs(pos3));
    }

    [Test]
    public void Resolve_该机仅一位且无孔_回退该位()
    {
        var station = new LocationMapItem
        {
            LocType = "EQUIPMENT", EquipmentId = 2, RcsCode = "202", RcsType = "station"
        };
        var pos3 = Pos(2, 3, "202101");
        var hit = GrabHandoffDest.Resolve([pos3], "202", station, null);
        Assert.That(hit, Is.SameAs(pos3));
    }

    [Test]
    public void Resolve_站码对不上工位_返回空()
    {
        var station = new LocationMapItem
        {
            LocType = "EQUIPMENT", EquipmentId = 2, RcsCode = "202", RcsType = "station"
        };
        var hit = GrabHandoffDest.Resolve([Pos(1, 1, "201101")], "202", station, 101);
        Assert.That(hit, Is.Null);
    }

    private static LocationMapItem Pos(long eq, long pos, string cell) => new()
    {
        LocType = "POSITION", EquipmentId = eq, PositionId = pos, RcsCode = cell, RcsType = "cell"
    };
}
