namespace CncLoader.Core.Config;

/// <summary>
/// 配置表软删/启用位（STATE）活动判断。
/// 仅用于 WorkLine / Craft / Equipment / LOCATION_MAP / PlcPoint / FrameBind 等配置实体；
/// 禁止套用到 SLOT_STATE、告警 STATE、RCS TASK_STATE。
/// </summary>
public static class ConfigActivity
{
    public const string Active = "0";
    public const string Disabled = "1";

    /// <summary>
    /// 仅精确等于 <see cref="Active"/> 视为活动。
    /// null、空、未知值一律不可用；不 Trim、不做模糊兼容。
    /// </summary>
    public static bool IsActive(string? state) => state == Active;
}
