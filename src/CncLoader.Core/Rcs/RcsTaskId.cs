namespace CncLoader.Core.Rcs;

/// <summary>
/// taskId 生成器：`{线体}-{类型}-{yyyyMMddHHmmss}-{4位序列}`。
/// 全局唯一、先落库后发送保证幂等；同秒内序列递增。
/// </summary>
public static class RcsTaskId
{
    /// <summary>与 <c>MAS_AUTO_AGV_TASK.RCS_TASK_ID</c> / <c>RCS_REMOTE_ID</c> VARCHAR(50) 对齐。</summary>
    public const int MaxLength = 50;

    /// <summary>现场回包号形如 <c>CNC_WMS_TASK_2_…</c>。升级前已回写到 <c>RCS_TASK_ID</c> 的在途行不得再当 queryTask 的 ID。</summary>
    public static bool IsAssignedRemoteId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.StartsWith("CNC_WMS_", StringComparison.Ordinal);

    private static readonly object Gate = new();
    private static string _lastStamp = "";
    private static int _seq;

    public static string Next(string lineCode, RcsTaskKind kind)
    {
        var line = string.IsNullOrWhiteSpace(lineCode) ? "LINE" : lineCode.Trim();
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        int seq;
        lock (Gate)
        {
            if (stamp == _lastStamp) seq = ++_seq;
            else { _lastStamp = stamp; _seq = 1; seq = 1; }
        }
        return $"{line}-{RcsTaskKindNames.ToAbbrev(kind)}-{stamp}-{seq:D4}";
    }
}
