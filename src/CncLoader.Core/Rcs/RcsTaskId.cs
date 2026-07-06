namespace CncLoader.Core.Rcs;

/// <summary>
/// taskId 生成器：`{线体}-{类型}-{yyyyMMddHHmmss}-{4位序列}`。
/// 全局唯一、先落库后发送保证幂等；同秒内序列递增。
/// </summary>
public static class RcsTaskId
{
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
