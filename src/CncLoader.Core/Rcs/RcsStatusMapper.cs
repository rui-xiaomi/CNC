namespace CncLoader.Core.Rcs;

/// <summary>
/// RCS 任务 11 态（接口文档 §3.4 queryTask 返回的 status 枚举）。
/// </summary>
public static class RcsStatus
{
    public const string Uninitialized = "uninitialized";
    public const string Blocked = "blocked";
    public const string Error = "error";
    public const string Failed = "failed";
    public const string Queued = "queued";
    public const string Standby = "standby";
    public const string Underway = "underway";
    public const string Delayed = "delayed";
    public const string Skipped = "skipped";
    public const string Canceled = "canceled";
    public const string Killed = "killed";
    public const string Completed = "completed";
}

/// <summary>
/// RCS 11 态 → 本系统 5 态映射（开发文档 §4.5）：
/// uninitialized/queued/standby/blocked/delayed → DISPATCHED；
/// underway → EXECUTING；
/// completed → COMPLETED（PLC 复核由步骤⑤补，本步骤先落 COMPLETED）；
/// failed/error/skipped → FAILED（可 redo）；
/// canceled/killed → CANCELED（联动人工工单）。
/// 未知态 → null（不推进，等下一次轮询/回调）。
/// </summary>
public static class RcsStatusMapper
{
    public static string? ToTaskState(string? rcsStatus)
    {
        if (string.IsNullOrWhiteSpace(rcsStatus)) return null;
        var s = rcsStatus.Trim();
        if (s.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
            s = RcsStatus.Canceled;
        else
            s = s.ToLowerInvariant();

        return s switch
        {
            RcsStatus.Uninitialized or RcsStatus.Queued or RcsStatus.Standby
                or RcsStatus.Blocked or RcsStatus.Delayed => RcsTaskState.Dispatched,
            RcsStatus.Underway => RcsTaskState.Executing,
            RcsStatus.Completed => RcsTaskState.Completed,
            RcsStatus.Failed or RcsStatus.Error or RcsStatus.Skipped => RcsTaskState.Failed,
            RcsStatus.Canceled or RcsStatus.Killed => RcsTaskState.Canceled,
            _ => null
        };
    }

    /// <summary>是否为终态（不再轮询）。</summary>
    public static bool IsTerminal(string taskState)
        => taskState is RcsTaskState.Completed or RcsTaskState.Failed or RcsTaskState.Canceled;
}

/// <summary>
/// cancelTask 失败文案：RCS 已取消或已删任务时，文档事例为「任务编号{taskId}不存在」。
/// 本地未完结任务应按取消收口，不能卡在 DISPATCHED/EXECUTING。
/// </summary>
public static class RcsCancelSemantics
{
    public static bool IsAlreadyCanceledOrGone(string? message, string? error)
        => LooksGone(message) || LooksGone(error);

    private static bool LooksGone(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return t.Contains("不存在", StringComparison.Ordinal)
               || t.Contains("已取消", StringComparison.Ordinal)
               || t.Contains("already cancel", StringComparison.OrdinalIgnoreCase)
               || t.Contains("not found", StringComparison.OrdinalIgnoreCase)
               || t.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
