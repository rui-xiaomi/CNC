namespace CncLoader.Core.State;

/// <summary>
/// 陈旧预记回滚宽限。预记先于任务落库，RCS 下发最长持续「超时 × 次数 + 退避」；
/// 未满宽限的预记可能属于在途派工，不得按「不在未完结列表」回滚，否则双占/双放。
/// </summary>
public static class StaleReservationPolicy
{
    /// <summary>宽限下限：库配置热更新后的超时/重试可能大于启动配置。</summary>
    public static readonly TimeSpan MinimumGrace = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(30);

    /// <summary>按出站超时与尝试次数计算宽限；退避与 RcsClient 一致（300ms × 2^(n-1)，共 n-1 次）。</summary>
    public static TimeSpan ComputeGrace(int requestTimeoutMs, int maxRetries)
    {
        var attempts = Math.Clamp(maxRetries, 1, 20);
        var sendMs = (long)Math.Max(1000, requestTimeoutMs) * attempts;
        var backoffMs = 300L * ((1L << (attempts - 1)) - 1);
        var grace = TimeSpan.FromMilliseconds(sendMs + backoffMs) + Margin;
        return grace > MinimumGrace ? grace : MinimumGrace;
    }

    /// <summary>
    /// queryTask 查无是否已到期。DispatchTime ≥ SendTime 表示该次下发已回写结果，只等 RCS 可见延迟；
    /// 否则视为下发中（含 Claim/重发刚刷新 SEND_TIME），按整段下发宽限，避免把在途重发落 FAILED 并回滚预记。
    /// </summary>
    public static bool IsQueryNotFoundDue(
        DateTime now, DateTime sendTime, DateTime? dispatchTime, TimeSpan visibilityAndPoll, TimeSpan sendGrace)
    {
        if (dispatchTime is DateTime dispatched && dispatched >= sendTime)
            return now - dispatched >= visibilityAndPoll;
        return now - sendTime >= sendGrace;
    }
}
