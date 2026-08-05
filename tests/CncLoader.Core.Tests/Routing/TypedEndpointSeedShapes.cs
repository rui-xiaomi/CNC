using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 D11/D15：与 <c>docs/sql/cnc_schema.sql</c> 六 b 对齐的 LOCATION_MAP 种子形状。
/// AREA/FRAME 的 EquipmentId/PositionId 必须保持 null，禁止测试为方便伪造。
/// </summary>
internal static class TypedEndpointSeedShapes
{
    // ── 线体 / 机台（POSITION 反查用）────────────────────────────
    public const long LineId = 10;
    public const string LineCode = "LINE01";
    public const long CraftId = 20;
    public const long EqId = 1;
    public const long PositionId = 1;
    public const long FrameIdTransit = 2;
    public const long FrameIdDownload = 3;

    // ── AREA（种子：无 Eq/Pos/Frame）────────────────────────────
    public const string LoadAreaCode = "601001";
    public const string UnloadAreaCode = "609001";
    public const string FullBufferCode = "650001";
    public const string EmptyBufferCode = "650002";
    public const string PalletReturnCode = "650003";

    public const string LocLoadArea = "LOAD_AREA";
    public const string LocUnloadArea = "UNLOAD_AREA";
    public const string LocFullBuffer = "FULL_BUFFER";
    public const string LocEmptyBuffer = "EMPTY_BUFFER";
    public const string LocPalletReturn = "PALLET_RETURN";

    // ── POSITION cell ───────────────────────────────────────────
    public const string PositionCell = "601203";

    // ── FRAME shelf / cell（无 Eq）──────────────────────────────
    public const string FrameShelfCode = "651002";
    public const string FrameCellCode = "653002";
    /// <summary>下料架（Unload 换架）独立 RcsCode，与中转架区分。</summary>
    public const string FrameDownloadShelfCode = "651003";
    public const string FrameDownloadCellCode = "653003";

    public static LocationMapItem Area(string locName, string rcsCode, long id) => new()
    {
        Id = id,
        LocType = "AREA",
        LocName = locName,
        RcsCode = rcsCode,
        RcsType = "station",
        EquipmentId = null,
        PositionId = null,
        FrameId = null
    };

    public static LocationMapItem PositionCellMap(long id = 10) => new()
    {
        Id = id,
        LocType = "POSITION",
        EquipmentId = EqId,
        PositionId = PositionId,
        RcsCode = PositionCell,
        RcsType = "cell",
        FrameId = null
    };

    /// <summary>非法 POSITION：LocType=POSITION 但缺 EquipmentId（契约 4）。</summary>
    public static LocationMapItem PositionMissingEquipment(string rcsCode = "601299", long id = 99) => new()
    {
        Id = id,
        LocType = "POSITION",
        EquipmentId = null,
        PositionId = PositionId,
        RcsCode = rcsCode,
        RcsType = "cell"
    };

    public static LocationMapItem FrameShelf(long frameId = FrameIdTransit, long id = 20) => new()
    {
        Id = id,
        LocType = "FRAME",
        FrameId = frameId,
        LocName = "EQ2中转架",
        RcsCode = FrameShelfCode,
        RcsType = "shelf",
        EquipmentId = null,
        PositionId = null
    };

    public static LocationMapItem FrameCell(long frameId = FrameIdTransit, long id = 21) => new()
    {
        Id = id,
        LocType = "FRAME",
        FrameId = frameId,
        LocName = "EQ2中转架cell",
        RcsCode = FrameCellCode,
        RcsType = "cell",
        EquipmentId = null,
        PositionId = null
    };

    public static LocationMapItem FrameDownloadShelf(long id = 22) => new()
    {
        Id = id,
        LocType = "FRAME",
        FrameId = FrameIdDownload,
        LocName = "EQ下料架",
        RcsCode = FrameDownloadShelfCode,
        RcsType = "shelf",
        EquipmentId = null,
        PositionId = null
    };

    public static LocationMapItem FrameDownloadCell(long id = 23) => new()
    {
        Id = id,
        LocType = "FRAME",
        FrameId = FrameIdDownload,
        LocName = "EQ下料架cell",
        RcsCode = FrameDownloadCellCode,
        RcsType = "cell",
        EquipmentId = null,
        PositionId = null
    };

    /// <summary>换架种子：标准 AREA + 上料/中转 FRAME + 下料 FRAME；EquipmentId 全 null。</summary>
    public static void SeedChangeFrameMaps(FakeLocationMapForRouting loc)
    {
        SeedStandardAreas(loc);
        loc.Seed(PositionCellMap());
        loc.Seed(FrameShelf());
        loc.Seed(FrameCell());
        loc.Seed(FrameDownloadShelf());
        loc.Seed(FrameDownloadCell());
    }

    /// <summary>换架机台链：Upload=中转架 / Unload=下料架（对齐 ChangeFrameOrchestrator 角色绑定）。</summary>
    public static void SeedChangeFrameEquipment(MutableEquipmentRoutingStore store)
    {
        store.SeedActiveChain(LineId, LineCode, CraftId, craftNode: 1, equipmentId: EqId);
        store.BindFrame(EqId, FrameIdTransit, FrameRole.Upload);
        store.BindFrame(EqId, FrameIdDownload, FrameRole.Unload);
    }

    public static void SeedStandardAreas(FakeLocationMapForRouting loc)
    {
        loc.Seed(Area(LocLoadArea, LoadAreaCode, 1));
        loc.Seed(Area(LocUnloadArea, UnloadAreaCode, 2));
        loc.Seed(Area(LocFullBuffer, FullBufferCode, 3));
        loc.Seed(Area(LocEmptyBuffer, EmptyBufferCode, 4));
        loc.Seed(Area(LocPalletReturn, PalletReturnCode, 5));
    }

    public static void SeedActiveEquipmentChain(MutableEquipmentRoutingStore store)
    {
        store.SeedActiveChain(LineId, LineCode, CraftId, craftNode: 1, equipmentId: EqId);
        store.BindFrame(EqId, FrameIdTransit, FrameRole.Transit);
        store.BindFrame(EqId, FrameIdDownload, FrameRole.Unload);
    }

    public static void AssertAreaShape(LocationMapRoutingRow row, string locName)
    {
        Assert.Multiple(() =>
        {
            Assert.That(row.LocType, Is.EqualTo("AREA"));
            Assert.That(row.LocName, Is.EqualTo(locName));
            Assert.That(row.EquipmentId, Is.Null, "AREA 不得伪造 EquipmentId");
            Assert.That(row.PositionId, Is.Null);
            Assert.That(row.FrameId, Is.Null);
            Assert.That(row.RcsType, Is.EqualTo("station"));
        });
    }

    public static void AssertFrameShape(LocationMapRoutingRow row)
    {
        Assert.Multiple(() =>
        {
            Assert.That(row.LocType, Is.EqualTo("FRAME"));
            Assert.That(row.FrameId, Is.Not.Null.And.GreaterThan(0));
            Assert.That(row.EquipmentId, Is.Null, "FRAME 不得伪造 EquipmentId");
            Assert.That(row.PositionId, Is.Null);
        });
    }
}
