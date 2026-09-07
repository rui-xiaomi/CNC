using CncLoader.Communication.State;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class RcsCellCodeTests
{
    [Test]
    public void Compose_FieldUploadSlot_Is101101()
        => Assert.That(RcsCellCode.TryCompose("101", 1, 1), Is.EqualTo("101101"));

    [Test]
    public void Compose_FieldInnerDimPosition2_Is201102()
        => Assert.That(RcsCellCode.TryCompose("201", 1, 2), Is.EqualTo("201102"));

    [Test]
    public void Compose_Layer2Pos1_UsesLayer11()
        => Assert.That(RcsCellCode.TryCompose("101", 2, 1), Is.EqualTo("101111"));

    [Test]
    public void Compose_Layer3And10_UsesLayer12And19()
    {
        Assert.That(RcsCellCode.TryCompose("101", 3, 1), Is.EqualTo("101121"));
        Assert.That(RcsCellCode.TryCompose("101", 10, 1), Is.EqualTo("101191"));
        Assert.That(RcsCellCode.TryCompose("101", 10, 2), Is.EqualTo("101192"));
        Assert.That(RcsCellCode.TryCompose("303", 2, 2), Is.EqualTo("303112"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Compose_BlankShelf_ReturnsNull(string? shelf)
        => Assert.That(RcsCellCode.TryCompose(shelf, 1, 1), Is.Null);

    [Test]
    public void Compose_InvalidPos_ReturnsNull()
        => Assert.That(RcsCellCode.TryCompose("101", 1, 0), Is.Null);

    [Test]
    public void Parse_Layer2Pos1_Is111()
    {
        Assert.That(RcsCellCode.TryParse("101111", "101", out var layer, out var pos), Is.True);
        Assert.That(layer, Is.EqualTo(2));
        Assert.That(pos, Is.EqualTo(1));
    }

    [Test]
    public void Parse_Layer10_UsesLayer19()
    {
        Assert.That(RcsCellCode.TryParse("101191", "101", out var layer, out var pos), Is.True);
        Assert.That(layer, Is.EqualTo(10));
        Assert.That(pos, Is.EqualTo(1));
        Assert.That(RcsCellCode.FormatSlotLabel(layer, pos), Is.EqualTo("L10P1"));
    }

    [Test]
    public void Parse_RoundTrip_Layer10Right()
    {
        var composed = RcsCellCode.TryCompose("302", 10, 2);
        Assert.That(composed, Is.EqualTo("302192"));
        Assert.That(RcsCellCode.TryParse(composed, "302", out var layer, out var pos), Is.True);
        Assert.That((layer, pos), Is.EqualTo((10, 2)));
    }

    [Test]
    public void Parse_WrongShelf_ReturnsFalse()
        => Assert.That(RcsCellCode.TryParse("101111", "301", out _, out _), Is.False);

    [Test]
    public void Parse_ShelfOnly_ReturnsFalse()
        => Assert.That(RcsCellCode.TryParse("101", "101", out _, out _), Is.False);

    [Test]
    public void Parse_OldUnpaddedLayerCode_ReturnsFalse()
        => Assert.That(RcsCellCode.TryParse("1011001", "101", out _, out _), Is.False);
}

[TestFixture]
public sealed class LocationMapItemDisplayTests
{
    [Test]
    public void FrameCell_NameUsesParsedSlot_NotStaleLocName()
    {
        var item = new LocationMapItem
        {
            LocType = "FRAME",
            RcsType = "cell",
            RcsCode = "101111",
            FrameCode = "101",
            LocName = "L1P3"
        };
        Assert.Multiple(() =>
        {
            Assert.That(item.LocNameText, Is.EqualTo("L2P1"));
            Assert.That(item.LayerText, Is.EqualTo("2"));
            Assert.That(item.PosText, Is.EqualTo("1"));
        });
    }

    [Test]
    public void FrameShelf_NameKeepsLocName()
    {
        var item = new LocationMapItem
        {
            LocType = "FRAME",
            RcsType = "shelf",
            RcsCode = "101",
            FrameCode = "101",
            LocName = "上料架101"
        };
        Assert.That(item.LocNameText, Is.EqualTo("上料架101"));
        Assert.That(item.LayerText, Is.Empty);
    }
}

[TestFixture]
public sealed class RouteResolverSlotCellTests
{
    [Test]
    public async Task SlotCell_ShelfAndCellMapped_ReturnsComposedCode()
    {
        var map = new StubMap();
        map.SeedFrame(1, "shelf", "101");
        map.SeedFrame(1, "cell", "101101");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolveFrameSlotCellAsync(1, 1, 1), Is.EqualTo("101101"));
        Assert.That(await resolver.ResolveFrameShelfAsync(1), Is.EqualTo("101"));
    }

    [Test]
    public async Task SlotCell_Layer2_UsesLayer11Code()
    {
        var map = new StubMap();
        map.SeedFrame(1, "shelf", "101");
        map.SeedFrame(1, "cell", "101111");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolveFrameSlotCellAsync(1, 2, 1), Is.EqualTo("101111"));
    }

    [Test]
    public async Task SlotCell_ShelfOnly_DoesNotFabricate()
    {
        var map = new StubMap();
        map.SeedFrame(1, "shelf", "101");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolveFrameSlotCellAsync(1, 1, 1), Is.Null);
    }

    [Test]
    public async Task SlotCell_CellBelongsToOtherFrame_ReturnsNull()
    {
        var map = new StubMap();
        map.SeedFrame(1, "shelf", "101");
        map.SeedFrame(2, "cell", "101101");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolveFrameSlotCellAsync(1, 1, 1), Is.Null);
    }

    [Test]
    public async Task PositionStation_优先加工位station()
    {
        var map = new StubMap();
        map.SeedPosition(9, 1, "station", "201");
        map.SeedPosition(9, null, "station", "200");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolvePositionStationAsync(9, 1), Is.EqualTo("201"));
    }

    [Test]
    public async Task PositionStation_缺加工位回退机台station()
    {
        var map = new StubMap();
        map.SeedPosition(9, null, "station", "200");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolvePositionStationAsync(9, 1), Is.EqualTo("200"));
    }

    [Test]
    public async Task PositionStation_只有cell_不回退()
    {
        var map = new StubMap();
        map.SeedPosition(9, 1, "cell", "201101");
        var resolver = new RouteResolver(map, NullLogger<RouteResolver>.Instance);

        Assert.That(await resolver.ResolvePositionStationAsync(9, 1), Is.Null);
    }

    private sealed class StubMap : ILocationMapService
    {
        private readonly List<LocationMapItem> _items = new();

        public void SeedFrame(long frameId, string rcsType, string rcsCode)
            => _items.Add(new LocationMapItem
            {
                LocType = "FRAME", FrameId = frameId, RcsCode = rcsCode, RcsType = rcsType
            });

        public Task<IReadOnlyList<LocationMapItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LocationMapItem>>(_items);

        public Task<long> SaveAsync(LocationMapItem item, string? author = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(long id, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void SeedPosition(long equipmentId, long? positionId, string rcsType, string rcsCode)
            => _items.Add(new LocationMapItem
            {
                LocType = positionId is null ? "EQUIPMENT" : "POSITION",
                EquipmentId = equipmentId,
                PositionId = positionId,
                RcsCode = rcsCode,
                RcsType = rcsType
            });

        public Task<LocationMapItem?> ResolvePositionAsync(long equipmentId, long? positionId,
            string rcsType, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(i =>
                i.EquipmentId == equipmentId && i.PositionId == positionId && i.RcsType == rcsType));

        public Task<LocationMapItem?> ResolveFrameAsync(long frameId, string rcsType, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(i => i.FrameId == frameId && i.RcsType == rcsType));

        public Task<LocationMapItem?> ResolveAreaAsync(string locName, CancellationToken ct = default)
            => Task.FromResult<LocationMapItem?>(null);

        public Task<LocationMapItem?> ResolveByRcsCodeAsync(string rcsCode, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(i => i.RcsCode == rcsCode));
    }
}
