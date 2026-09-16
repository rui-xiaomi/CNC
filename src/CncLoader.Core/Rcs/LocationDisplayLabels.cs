using CncLoader.Common.Configuration;

namespace CncLoader.Core.Rcs;

/// <summary>
/// 位置映射中英文显示互转（库内仍存英文码；UI 列表/下拉用中文）。
/// </summary>
public static class LocationDisplayLabels
{
    public static string LocTypeToZh(string? code) => code switch
    {
        "AREA" => "区域",
        "EQUIPMENT" => "机台",
        "POSITION" => "加工位",
        "FRAME" => "料架",
        _ => code ?? ""
    };

    public static string LocTypeFromZh(string? zh) => zh switch
    {
        "区域" => "AREA",
        "机台" => "EQUIPMENT",
        "加工位" => "POSITION",
        "料架" => "FRAME",
        // 已是英文码时原样返回（兼容旧选中）
        "AREA" or "EQUIPMENT" or "POSITION" or "FRAME" => zh!,
        _ => zh ?? "AREA"
    };

    public static string RcsTypeToZh(string? code) => code switch
    {
        "station" => "站点",
        "cell" => "仓位",
        "shelf" => "料架站",
        _ => code ?? ""
    };

    public static string RcsTypeFromZh(string? zh) => zh switch
    {
        "站点" => "station",
        "仓位" => "cell",
        "料架站" => "shelf",
        "station" or "cell" or "shelf" => zh!,
        _ => zh ?? "station"
    };

    public static string AreaNameToZh(string? code) => code switch
    {
        LocationAreaNames.LoadArea => "上料区",
        LocationAreaNames.UnloadArea => "下料区",
        LocationAreaNames.FullBuffer => "满架缓存区",
        LocationAreaNames.EmptyBuffer => "空架缓存区",
        LocationAreaNames.PalletReturn => "托盘回收区",
        _ => code ?? ""
    };

    public static string AreaNameFromZh(string? zh) => zh switch
    {
        "上料区" => LocationAreaNames.LoadArea,
        "下料区" => LocationAreaNames.UnloadArea,
        "满架缓存区" => LocationAreaNames.FullBuffer,
        "空架缓存区" => LocationAreaNames.EmptyBuffer,
        "托盘回收区" => LocationAreaNames.PalletReturn,
        LocationAreaNames.LoadArea or LocationAreaNames.UnloadArea or LocationAreaNames.FullBuffer
            or LocationAreaNames.EmptyBuffer or LocationAreaNames.PalletReturn => zh!,
        _ => zh ?? ""
    };

    public static string FormatRef(string? name, long? id)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name!;
        return id is > 0 ? id.Value.ToString() : "";
    }
}
