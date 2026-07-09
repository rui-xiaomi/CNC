namespace CncLoader.Core.Rcs;

/// <summary>RCS 任务/报文展示用中文标签（落库仍用英文码）。</summary>
public static class RcsDisplayLabels
{
    public static string KindToZh(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "transit" => "搬运",
        "grab" => "抓取",
        "identify" => "识别",
        "pallet_return" => "空托回收",
        "change_frame" => "换架",
        "" => "—",
        _ => kind!
    };

    public static string StateToZh(string? state) => (state ?? "").Trim().ToUpperInvariant() switch
    {
        "CREATED" => "已创建",
        "DISPATCHED" => "已下发",
        "EXECUTING" => "执行中",
        "RUNNING" => "进行中",
        "COMPLETED" => "已完成",
        "FAILED" => "失败",
        "CANCELED" => "已取消",
        "" => "—",
        _ => state!
    };

    public static string DirectionToZh(string? dir) => (dir ?? "").Trim().ToUpperInvariant() switch
    {
        "OUT" => "出站",
        "IN" => "入站",
        "" => "—",
        _ => dir!
    };

    /// <summary>筛选下拉「出站/入站」→ 库内 OUT/IN；全部/空 → null。</summary>
    public static string? DirectionFromZh(string? zh) => (zh ?? "").Trim() switch
    {
        "出站" => "OUT",
        "入站" => "IN",
        "OUT" => "OUT",
        "IN" => "IN",
        _ => null
    };

    public static string InterfaceToZh(string? iface) => (iface ?? "").Trim() switch
    {
        "transitTask" => "搬运下发",
        "excuteTask" => "定制任务",
        "cancelTask" => "取消任务",
        "queryTask" => "查询任务",
        "pushTaskStatus" => "状态回调",
        "scanTaskStatus" => "扫码回调",
        "warnCallback" => "告警回调",
        "" => "—",
        _ => iface!
    };

    /// <summary>筛选下拉中文 → 接口英文码；全部/空/未知 → null（不限）。</summary>
    public static string? InterfaceFromZh(string? zh) => (zh ?? "").Trim() switch
    {
        "搬运下发" or "transitTask" => "transitTask",
        "定制任务" or "excuteTask" => "excuteTask",
        "取消任务" or "cancelTask" => "cancelTask",
        "查询任务" or "queryTask" => "queryTask",
        "状态回调" or "pushTaskStatus" => "pushTaskStatus",
        "扫码回调" or "scanTaskStatus" => "scanTaskStatus",
        "告警回调" or "warnCallback" => "warnCallback",
        _ => null
    };

    public static string ResultToZh(bool success) => success ? "成功" : "失败";

    public static string CostToZh(int? costMs) => costMs is null ? "—" : $"{costMs} ms";

    public static string FrameRoleToZh(FrameRole role) => role switch
    {
        FrameRole.Upload => "上料架",
        FrameRole.Unload => "下料架",
        FrameRole.Transit => "中转架",
        FrameRole.NgFrame => "NG架",
        _ => role.ToString()
    };

    public static string ChangeFrameStepToZh(ChangeFrameStep step) => step switch
    {
        ChangeFrameStep.PullOld => "拉旧架",
        ChangeFrameStep.PushNew => "送新架",
        ChangeFrameStep.Done => "完成",
        ChangeFrameStep.Alarm => "告警",
        _ => step.ToString()
    };

    /// <summary>尽量格式化 JSON；失败则原样返回。</summary>
    public static string FormatJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return raw;
        }
    }
}
