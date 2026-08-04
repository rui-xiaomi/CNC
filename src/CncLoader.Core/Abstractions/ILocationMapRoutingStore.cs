namespace CncLoader.Core.Abstractions;

/// <summary>
/// LOCATION_MAP 原始读取接缝：返回匹配行（含 STATE），活动过滤由 <see cref="CncLoader.Core.Rcs.ILocationMapService"/> 决定。
/// </summary>
public interface ILocationMapRoutingStore
{
    Task<IReadOnlyList<LocationMapRoutingRow>> FindByPositionAsync(
        long equipmentId, long? positionId, string rcsType, CancellationToken ct = default);

    Task<IReadOnlyList<LocationMapRoutingRow>> FindByFrameAsync(
        long frameId, string rcsType, CancellationToken ct = default);

    Task<IReadOnlyList<LocationMapRoutingRow>> FindByAreaAsync(
        string locName, CancellationToken ct = default);

    /// <summary>
    /// 按 RCS 编码精确匹配全部行（含禁用/未知 STATE），不做活动过滤、不取 First。
    /// </summary>
    Task<IReadOnlyList<LocationMapRoutingRow>> FindByRcsCodeAsync(
        string rcsCode, CancellationToken ct = default);
}

/// <summary>位置映射路由快照（STATE 原样）。</summary>
public sealed record LocationMapRoutingRow(
    long Id,
    string LocType,
    long? EquipmentId,
    long? PositionId,
    long? FrameId,
    string? LocName,
    string RcsCode,
    string RcsType,
    string State);
