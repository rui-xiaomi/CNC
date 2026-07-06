namespace CncLoader.Core.Rcs;

/// <summary>
/// 逻辑位置 ↔ RCS 点位编码映射（MAS_AUTO_LOCATION_MAP）。
/// 抓取任务用 station 级，搬运任务用 cell 级；缓存区/备料区/托盘回收区用命名点。
/// </summary>
public interface ILocationMapService
{
    Task<IReadOnlyList<LocationMapItem>> GetAllAsync(CancellationToken ct = default);

    /// <summary>新增或更新（Id&gt;0 更新，否则新增）。返回主键。</summary>
    Task<long> SaveAsync(LocationMapItem item, string? author = null, CancellationToken ct = default);

    /// <summary>软删（STATE='1'）。</summary>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>按逻辑机台/加工位解析 RCS 编码（指定 type：station/cell/shelf）；无则 null。</summary>
    Task<LocationMapItem?> ResolvePositionAsync(long equipmentId, long? positionId, string rcsType, CancellationToken ct = default);

    /// <summary>按料架解析 RCS 编码。</summary>
    Task<LocationMapItem?> ResolveFrameAsync(long frameId, string rcsType, CancellationToken ct = default);

    /// <summary>按命名区域解析 RCS 编码。</summary>
    Task<LocationMapItem?> ResolveAreaAsync(string locName, CancellationToken ct = default);
}

/// <summary>位置映射项。</summary>
public sealed record LocationMapItem
{
    public long Id { get; init; }
    /// <summary>EQUIPMENT/POSITION/FRAME/AREA。</summary>
    public string LocType { get; init; } = "AREA";
    public long? EquipmentId { get; init; }
    public long? PositionId { get; init; }
    public long? FrameId { get; init; }
    public string? LocName { get; init; }
    public string RcsCode { get; init; } = "";
    /// <summary>shelf/cell/station。</summary>
    public string RcsType { get; init; } = "station";
    public string? Remark { get; init; }
}
