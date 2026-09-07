using System.Text.Json;

namespace CncLoader.Core.Rcs;

/// <summary>
/// 工序间直送落点。搬运 ToCode 是工位 cell；抓取 ToCode 只有机台站码，须用 dstPos 对到 POSITION。
/// </summary>
public static class GrabHandoffDest
{
    public static int? TryReadDstPos(string? reqParam)
    {
        if (string.IsNullOrWhiteSpace(reqParam)) return null;
        try
        {
            var items = JsonSerializer.Deserialize<GrabItem[]>(reqParam);
            var pos = items?.Select(i => i.DstPos).FirstOrDefault(p => p > 0);
            return pos is > 0 ? pos : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="byCode"/> 已是加工位则原样返回；
    /// 否则在 LOCATION_MAP 里用站码 + 抓取孔找 POSITION cell。
    /// 该机仅一个加工位且无孔信息时回退到该位。
    /// </summary>
    public static LocationMapItem? Resolve(
        IReadOnlyList<LocationMapItem> maps,
        string? toCode,
        LocationMapItem? byCode,
        int? dstPos)
    {
        if (byCode is { EquipmentId: not null, PositionId: not null })
            return byCode;

        var station = toCode?.Trim();
        if (string.IsNullOrEmpty(station)) return null;

        var eqId = byCode?.EquipmentId;
        var positions = maps
            .Where(m => m.LocType == "POSITION" && m.RcsType == "cell"
                        && m.EquipmentId is not null && m.PositionId is not null
                        && (eqId is null || m.EquipmentId == eqId)
                        && RcsGrabHole.TryFromPositionCell(m.RcsCode, station, out _))
            .ToList();

        if (dstPos is int hole)
        {
            var hit = positions.FirstOrDefault(m =>
                RcsGrabHole.TryFromPositionCell(m.RcsCode, station, out var h) && h == hole);
            if (hit is not null) return hit;
        }

        return positions.Count == 1 ? positions[0] : null;
    }
}
