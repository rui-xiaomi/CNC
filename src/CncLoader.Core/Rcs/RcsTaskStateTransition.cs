namespace CncLoader.Core.Rcs;

/// <summary>
/// 任务态迁移守卫（P1-1）：回调与 queryTask 轮询可能乱序到达，终态不得被迟到的旧态覆盖。
/// COMPLETED 不可回退（可幂等重复写 COMPLETED）；CANCELED 只可被 COMPLETED 覆盖（实物已完成以 RCS 为准，落账仍经 PLC 复核）；
/// FAILED 等非终态可被任何新态覆盖（redo 与真实执行进度以 RCS 为准）。
/// </summary>
public static class RcsTaskStateTransition
{
    private static readonly string[] None = Array.Empty<string>();
    private static readonly string[] CompletedOnly = { RcsTaskState.Completed };
    private static readonly string[] Terminal = { RcsTaskState.Completed, RcsTaskState.Canceled };

    /// <summary>目标态为 <paramref name="next"/> 时禁止被覆盖的当前态（供存储层条件 UPDATE）。</summary>
    public static IReadOnlyCollection<string> BlockedFrom(string next) => next switch
    {
        RcsTaskState.Completed => None,
        RcsTaskState.Canceled => CompletedOnly,
        _ => Terminal
    };

    public static bool IsAllowed(string? current, string next)
        => current is null || !BlockedFrom(next).Contains(current);
}
