using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 路由决策默认实现（§6.2 简化版）：
/// 上料：from=上料区命名点（LOCATION_MAP LOC_NAME="LOAD_AREA"），to=加工位 cell；
/// 下料：from=加工位 cell，to=下料区命名点（LOC_NAME="UNLOAD_AREA"）。
/// 加工位 cell 由 LOCATION_MAP 按 (EQUIPMENT_ID+POSITION_ID, rcsType="cell") 解析。
/// 解析失败返回 null——调度器据此告警人工（产线配置未录入）。
/// 完整工序间流转/中转架/NG分流留步骤⑥。
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

    public async Task<string?> ResolveFrameCellAsync(long frameId, CancellationToken ct = default)
    {
        // 仅返回 LOCATION_MAP 真实编码；缺映射返回 null，禁止 FRAME-{id} 假码下发。
        var cell = await _locationMap.ResolveFrameAsync(frameId, "cell", ct);
        return cell?.RcsCode;
    }
}
