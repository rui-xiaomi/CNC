using CncLoader.Core.Abstractions;

namespace CncLoader.Core.Rcs;

/// <summary>
/// 受管派工端点类型。只能由 LOCATION_MAP.LocType 映射得出，调用方不得自指定绕过。
/// </summary>
public enum ManagedEndpointKind
{
    Position = 0,
    Area,
    Frame
}

/// <summary>
/// 受管派工端点快照（无 EF Entity）。Kind 仅能通过 <see cref="TryCreate"/> 从持久化 LocType 得到。
/// </summary>
public sealed record ManagedDispatchEndpoint
{
    private ManagedDispatchEndpoint(
        ManagedEndpointKind kind,
        long locationMapId,
        string rcsCode,
        string? locName,
        string locType,
        string rcsType,
        long? equipmentId,
        long? positionId,
        long? frameId)
    {
        Kind = kind;
        LocationMapId = locationMapId;
        RcsCode = rcsCode;
        LocName = locName;
        LocType = locType;
        RcsType = rcsType;
        EquipmentId = equipmentId;
        PositionId = positionId;
        FrameId = frameId;
    }

    public ManagedEndpointKind Kind { get; }
    public long LocationMapId { get; }
    public string RcsCode { get; }
    public string? LocName { get; }
    public string LocType { get; }
    public string RcsType { get; }
    public long? EquipmentId { get; }
    public long? PositionId { get; }
    public long? FrameId { get; }

    /// <summary>安全显示标识（无敏感 URL/Token）。</summary>
    public string SafeDisplay => Kind switch
    {
        ManagedEndpointKind.Area => $"AREA:{LocName}:{RcsCode}",
        ManagedEndpointKind.Frame => $"FRAME:{FrameId}:{RcsCode}",
        ManagedEndpointKind.Position => $"POSITION:EQ{EquipmentId}/P{PositionId}:{RcsCode}",
        _ => $"?:{RcsCode}"
    };

    /// <summary>
    /// 从 LOCATION_MAP 活动行创建端点。未知 LocType → false（由调用方映射为 InvalidRelationship）。
    /// 不根据 EquipmentId 是否为 null 推断类型。
    /// </summary>
    public static bool TryCreate(LocationMapRoutingRow row, out ManagedDispatchEndpoint? endpoint)
    {
        endpoint = null;
        if (!TryMapKind(row.LocType, out var kind))
            return false;

        endpoint = new ManagedDispatchEndpoint(
            kind,
            row.Id,
            row.RcsCode,
            row.LocName,
            row.LocType,
            row.RcsType,
            row.EquipmentId,
            row.PositionId,
            row.FrameId);
        return true;
    }

    public static bool TryMapKind(string? locType, out ManagedEndpointKind kind)
    {
        switch (locType)
        {
            case "POSITION":
                kind = ManagedEndpointKind.Position;
                return true;
            case "AREA":
                kind = ManagedEndpointKind.Area;
                return true;
            case "FRAME":
                kind = ManagedEndpointKind.Frame;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>已配置的 AREA 角色（与 LocationDisplayLabels / 种子一致）。</summary>
    public static bool IsConfiguredAreaRole(string? locName) =>
        locName is "LOAD_AREA" or "UNLOAD_AREA" or "FULL_BUFFER" or "EMPTY_BUFFER" or "PALLET_RETURN";
}

/// <summary>
/// 按端点 Kind 生成 RouteDependency / DispatchRouteContext，防止调用方把 Position 的 Equipment 标成 N/A。
/// </summary>
public static class DispatchRouteContextFactory
{
    public static DispatchRouteContext FromEndpoints(
        ManagedDispatchEndpoint from,
        ManagedDispatchEndpoint to)
    {
        return new DispatchRouteContext
        {
            FromEndpoint = from,
            ToEndpoint = to,
            SourceEquipmentId = from.Kind == ManagedEndpointKind.Position
                ? from.EquipmentId ?? 0
                : 0,
            SourcePositionId = from.Kind == ManagedEndpointKind.Position
                ? from.PositionId
                : null,
            DestEquipmentId = EquipmentDependency(to),
            DestPositionId = PositionDependency(to),
            SourceFrameId = FrameDependency(from),
            DestFrameId = FrameDependency(to),
            FromCode = from.RcsCode,
            ToCode = to.RcsCode,
            RequiresResolvedCells = true
        };
    }

    private static RouteDependency EquipmentDependency(ManagedDispatchEndpoint ep) =>
        ep.Kind switch
        {
            ManagedEndpointKind.Position when ep.EquipmentId is > 0 =>
                RouteDependency.Required(ep.EquipmentId.Value),
            ManagedEndpointKind.Position => RouteDependency.RequiredMissing,
            _ => RouteDependency.NotApplicable
        };

    private static RouteDependency PositionDependency(ManagedDispatchEndpoint ep) =>
        ep.Kind switch
        {
            ManagedEndpointKind.Position when ep.PositionId is > 0 =>
                RouteDependency.Required(ep.PositionId.Value),
            ManagedEndpointKind.Position => RouteDependency.RequiredMissing,
            _ => RouteDependency.NotApplicable
        };

    private static RouteDependency FrameDependency(ManagedDispatchEndpoint ep) =>
        ep.Kind switch
        {
            ManagedEndpointKind.Frame when ep.FrameId is > 0 =>
                RouteDependency.Required(ep.FrameId.Value),
            ManagedEndpointKind.Frame => RouteDependency.RequiredMissing,
            _ => RouteDependency.NotApplicable
        };
}
