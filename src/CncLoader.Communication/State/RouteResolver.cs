using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 路由决策：现场搬运为 cell 级（料架货架+层10起+位，如 101101→201101）。
/// 上料命名区 / 下料命名区仅作无料架绑定时的回退；有料架时用 shelf + 预记槽 cell。
/// 解析失败返回 null——调度器据此告警人工；禁止 FRAME-{id} 假码。
/// </summary>
public sealed class RouteResolver : IRouteResolver
{
    public const string LoadAreaName = "LOAD_AREA";
    public const string UnloadAreaName = "UNLOAD_AREA";

    private readonly ILocationMapService _locationMap;
    private readonly ILogger<RouteResolver> _logger;

    public RouteResolver(ILocationMapService locationMap, ILogger<RouteResolver> logger)
    {
        _locationMap = locationMap;
        _logger = logger;
    }

    public async Task<(string from, string to)?> ResolveUploadAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        var from = await _locationMap.ResolveAreaAsync(LoadAreaName, ct);
        var to = await _locationMap.ResolvePositionAsync(equipmentId, positionId, "cell", ct);
        if (from is null || to is null)
        {
            _logger.LogWarning("上料路由解析失败 EQ{Eq} POS{Pos} from={From} to={To}（请录入 LOCATION_MAP）", equipmentId, positionId, from?.RcsCode, to?.RcsCode);
            return null;
        }
        return (from.RcsCode, to.RcsCode);
    }

    public async Task<(string from, string to)?> ResolveUnloadAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        var from = await _locationMap.ResolvePositionAsync(equipmentId, positionId, "cell", ct);
        var to = await _locationMap.ResolveAreaAsync(UnloadAreaName, ct);
        if (from is null || to is null)
        {
            _logger.LogWarning("下料路由解析失败 EQ{Eq} POS{Pos} from={From} to={To}（请录入 LOCATION_MAP）", equipmentId, positionId, from?.RcsCode, to?.RcsCode);
            return null;
        }
        return (from.RcsCode, to.RcsCode);
    }

    public async Task<string?> ResolvePositionCellAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        var cell = await _locationMap.ResolvePositionAsync(equipmentId, positionId, "cell", ct);
        return cell?.RcsCode;
    }

    public async Task<string?> ResolvePositionStationAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        var position = await _locationMap.ResolvePositionAsync(equipmentId, positionId, "station", ct);
        if (!string.IsNullOrWhiteSpace(position?.RcsCode))
            return position.RcsCode;

        var equipment = await _locationMap.ResolvePositionAsync(equipmentId, null, "station", ct);
        return string.IsNullOrWhiteSpace(equipment?.RcsCode) ? null : equipment.RcsCode;
    }

    public async Task<string?> ResolveFrameCellAsync(long frameId, CancellationToken ct = default)
    {
        // 仅返回 LOCATION_MAP 真实编码；缺映射返回 null，禁止 FRAME-{id} 假码下发。
        var cell = await _locationMap.ResolveFrameAsync(frameId, "cell", ct);
        return cell?.RcsCode;
    }

    public async Task<string?> ResolveFrameShelfAsync(long frameId, CancellationToken ct = default)
    {
        var shelf = await _locationMap.ResolveFrameAsync(frameId, "shelf", ct)
                    ?? await _locationMap.ResolveFrameAsync(frameId, "station", ct);
        return string.IsNullOrWhiteSpace(shelf?.RcsCode) ? null : shelf.RcsCode;
    }

    public async Task<string?> ResolveFrameSlotCellAsync(
        long frameId, int layerNo, int posInLayer, CancellationToken ct = default)
    {
        var shelf = await ResolveFrameShelfAsync(frameId, ct);
        var composed = RcsCellCode.TryCompose(shelf, layerNo, posInLayer);
        if (composed is null) return null;

        var hit = await _locationMap.ResolveByRcsCodeAsync(composed, ct);
        if (hit is null || hit.FrameId != frameId || !string.Equals(hit.RcsType, "cell", StringComparison.Ordinal))
            return null;
        return composed;
    }
}
