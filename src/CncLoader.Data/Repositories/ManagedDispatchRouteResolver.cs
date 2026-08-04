using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 受管 From/To 解析：仅 LOCATION_MAP.RcsCode 精确匹配（本期无其他受管自由文本来源）。
/// 多条活动匹配 → Ambiguous；仅禁用/未知 → Disabled；零匹配 → NotFound。
/// </summary>
public sealed class ManagedDispatchRouteResolver : IManagedDispatchRouteResolver
{
    private readonly ILocationMapRoutingStore _locationMaps;
    private readonly ILogger<ManagedDispatchRouteResolver> _logger;

    public ManagedDispatchRouteResolver(
        ILocationMapRoutingStore locationMaps,
        ILogger<ManagedDispatchRouteResolver> logger)
    {
        _locationMaps = locationMaps;
        _logger = logger;
    }

    public async Task<ManagedDispatchRouteResult> ResolveAsync(
        string? fromCode, string? toCode, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(fromCode) || string.IsNullOrEmpty(toCode))
            {
                return ManagedDispatchRouteResult.Fail(
                    ManagedDispatchRouteStatus.NotFound,
                    "起终点未解析到受管配置",
                    "LocationMap");
            }

            var from = await ResolveEndpointAsync(fromCode, "From", ct);
            if (from.Status != ManagedDispatchRouteStatus.Resolved)
                return from.ToFail();

            var to = await ResolveEndpointAsync(toCode, "To", ct);
            if (to.Status != ManagedDispatchRouteStatus.Resolved)
                return to.ToFail();

            var fromRow = from.Row!;
            var toRow = to.Row!;

            if (fromRow.EquipmentId is null || fromRow.EquipmentId <= 0)
            {
                return ManagedDispatchRouteResult.Fail(
                    ManagedDispatchRouteStatus.InvalidRelationship,
                    "起点映射缺少机台关联",
                    "LocationMap", fromRow.Id);
            }

            if (toRow.EquipmentId is null || toRow.EquipmentId <= 0)
            {
                return ManagedDispatchRouteResult.Fail(
                    ManagedDispatchRouteStatus.InvalidRelationship,
                    "终点映射缺少机台关联",
                    "LocationMap", toRow.Id);
            }

            var ctx = new DispatchRouteContext
            {
                SourceEquipmentId = fromRow.EquipmentId.Value,
                SourcePositionId = fromRow.PositionId,
                DestEquipmentId = RouteDependency.Required(toRow.EquipmentId.Value),
                DestPositionId = toRow.PositionId is long dp
                    ? RouteDependency.Required(dp)
                    : RouteDependency.NotApplicable,
                SourceFrameId = RouteDependency.NotApplicable,
                DestFrameId = RouteDependency.NotApplicable,
                FromCode = fromCode,
                ToCode = toCode,
                RequiresResolvedCells = true
            };
            return ManagedDispatchRouteResult.Resolved(ctx);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "受管路由解析异常，fail-closed");
            return ManagedDispatchRouteResult.Fail(
                ManagedDispatchRouteStatus.ConfigurationUnavailable,
                "路由配置读取异常，拒绝派工",
                "Configuration");
        }
    }

    private async Task<EndpointHit> ResolveEndpointAsync(
        string code, string side, CancellationToken ct)
    {
        var rows = await _locationMaps.FindByRcsCodeAsync(code, ct);
        if (rows.Count == 0)
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.NotFound,
                $"{side} 未找到受管 LOCATION_MAP",
                "LocationMap");
        }

        // 活动与禁用分开：禁用不得回落为 NotFound 后当自由文本发送
        var active = rows.Where(r => ConfigActivity.IsActive(r.State)).ToList();
        if (active.Count == 0)
        {
            var disabled = rows.FirstOrDefault(r => r.State == ConfigActivity.Disabled);
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.Disabled,
                $"{side} 位置映射已禁用或状态不可用",
                "LocationMap",
                disabled?.Id ?? rows[0].Id);
        }

        if (active.Count > 1)
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.Ambiguous,
                $"{side} 位置映射存在多条活动匹配，拒绝取第一条",
                "LocationMap");
        }

        return EndpointHit.Ok(active[0]);
    }

    private sealed class EndpointHit
    {
        public ManagedDispatchRouteStatus Status { get; init; }
        public LocationMapRoutingRow? Row { get; init; }
        public string SafeMessage { get; init; } = "";
        public string? EntityKind { get; init; }
        public long? EntityId { get; init; }

        public static EndpointHit Ok(LocationMapRoutingRow row) => new()
        {
            Status = ManagedDispatchRouteStatus.Resolved,
            Row = row
        };

        public static EndpointHit Fail(
            ManagedDispatchRouteStatus status, string msg, string? kind, long? id = null) => new()
        {
            Status = status,
            SafeMessage = msg,
            EntityKind = kind,
            EntityId = id
        };

        public ManagedDispatchRouteResult ToFail() => ManagedDispatchRouteResult.Fail(
            Status, SafeMessage, EntityKind, EntityId);
    }
}
