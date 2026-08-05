using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 受管 From/To 解析：LOCATION_MAP.RcsCode 精确匹配 + LocType 类型化验证。
/// 多条活动匹配 → Ambiguous；仅禁用/未知 → Disabled；零匹配 → NotFound。
/// </summary>
public sealed class ManagedDispatchRouteResolver : IManagedDispatchRouteResolver
{
    private readonly ILocationMapRoutingStore _locationMaps;
    private readonly IFrameRoutingStore _frames;
    private readonly ILogger<ManagedDispatchRouteResolver> _logger;

    public ManagedDispatchRouteResolver(
        ILocationMapRoutingStore locationMaps,
        IFrameRoutingStore frames,
        ILogger<ManagedDispatchRouteResolver> logger)
    {
        _locationMaps = locationMaps;
        _frames = frames;
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

            var from = await ResolveTypedEndpointAsync(fromCode, "From", ct);
            if (from.Status != ManagedDispatchRouteStatus.Resolved)
                return from.ToFail();

            var to = await ResolveTypedEndpointAsync(toCode, "To", ct);
            if (to.Status != ManagedDispatchRouteStatus.Resolved)
                return to.ToFail();

            var ctx = DispatchRouteContextFactory.FromEndpoints(from.Endpoint!, to.Endpoint!);
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

    private async Task<EndpointHit> ResolveTypedEndpointAsync(
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

        var row = active[0];
        if (!ManagedDispatchEndpoint.TryCreate(row, out var endpoint) || endpoint is null)
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.InvalidRelationship,
                $"{side} 位置映射 LocType 不受管或不支持",
                "LocationMap",
                row.Id);
        }

        return endpoint.Kind switch
        {
            ManagedEndpointKind.Position => ValidatePosition(endpoint, side),
            ManagedEndpointKind.Area => ValidateArea(endpoint, side),
            ManagedEndpointKind.Frame => await ValidateFrameAsync(endpoint, side, ct),
            _ => EndpointHit.Fail(
                ManagedDispatchRouteStatus.InvalidRelationship,
                $"{side} 端点类型未知",
                "LocationMap",
                row.Id)
        };
    }

    private static EndpointHit ValidatePosition(ManagedDispatchEndpoint ep, string side)
    {
        if (ep.EquipmentId is null or <= 0)
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.InvalidRelationship,
                $"{side} 加工位映射缺少机台关联",
                "LocationMap",
                ep.LocationMapId);
        }

        if (ep.PositionId is null or <= 0)
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.InvalidRelationship,
                $"{side} 加工位映射缺少工位关联",
                "LocationMap",
                ep.LocationMapId);
        }

        return EndpointHit.Ok(ep);
    }

    private static EndpointHit ValidateArea(ManagedDispatchEndpoint ep, string side)
    {
        if (string.IsNullOrWhiteSpace(ep.RcsCode))
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.InvalidRelationship,
                $"{side} 区域映射缺少 RCS 编码",
                "LocationMap",
                ep.LocationMapId);
        }

        if (string.IsNullOrWhiteSpace(ep.LocName)
            || !ManagedDispatchEndpoint.IsConfiguredAreaRole(ep.LocName))
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.InvalidRelationship,
                $"{side} 区域角色未配置或不可用",
                "LocationMap",
                ep.LocationMapId);
        }

        // AREA 合法允许 EquipmentId/PositionId/FrameId 为 null；不查 Equipment 链
        return EndpointHit.Ok(ep);
    }

    private async Task<EndpointHit> ValidateFrameAsync(
        ManagedDispatchEndpoint ep, string side, CancellationToken ct)
    {
        if (ep.FrameId is null or <= 0)
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.InvalidRelationship,
                $"{side} 料架映射缺少 FrameId",
                "LocationMap",
                ep.LocationMapId);
        }

        FrameRoutingSnapshot? frame;
        try
        {
            frame = await _frames.FindAsync(ep.FrameId.Value, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (frame is null)
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.NotFound,
                $"{side} 料架不存在",
                "Frame",
                ep.FrameId);
        }

        if (!ConfigActivity.IsActive(frame.State))
        {
            return EndpointHit.Fail(
                ManagedDispatchRouteStatus.Disabled,
                $"{side} 料架已禁用或状态不可用",
                "Frame",
                frame.Id);
        }

        // FRAME 合法允许 EquipmentId/PositionId 为 null；FrameBind 由操作 Context 另标
        return EndpointHit.Ok(ep);
    }

    private sealed class EndpointHit
    {
        public ManagedDispatchRouteStatus Status { get; init; }
        public ManagedDispatchEndpoint? Endpoint { get; init; }
        public string SafeMessage { get; init; } = "";
        public string? EntityKind { get; init; }
        public long? EntityId { get; init; }

        public static EndpointHit Ok(ManagedDispatchEndpoint endpoint) => new()
        {
            Status = ManagedDispatchRouteStatus.Resolved,
            Endpoint = endpoint
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
