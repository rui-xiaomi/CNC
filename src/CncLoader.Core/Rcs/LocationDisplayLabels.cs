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
        "LOAD_AREA" => "上料区",
        "UNLOAD_AREA" => "下料区",
        "FULL_BUFFER" => "满架缓存区",
        "EMPTY_BUFFER" => "空架缓存区",
        "PALLET_RETURN" => "托盘回收区",
        _ => code ?? ""
    };

    public static string AreaNameFromZh(string? zh) => zh switch
    {
        "上料区" => "LOAD_AREA",
        "下料区" => "UNLOAD_AREA",
        "满架缓存区" => "FULL_BUFFER",
        "空架缓存区" => "EMPTY_BUFFER",
        "托盘回收区" => "PALLET_RETURN",
        "LOAD_AREA" or "UNLOAD_AREA" or "FULL_BUFFER" or "EMPTY_BUFFER" or "PALLET_RETURN" => zh!,
        _ => zh ?? ""
    };

    public static string FormatRef(string? name, long? id)
    {
        if (!string.IsNullOrWhiteSpace(name) && id is > 0) return $"{name}({id})";
        if (!string.IsNullOrWhiteSpace(name)) return name!;
        return id is > 0 ? id.Value.ToString() : "";
    }
}
