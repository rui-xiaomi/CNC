namespace CncLoader.Core.Rcs;

/// <summary>本机回调探针失败分类；成功为 <see cref="None"/>。</summary>
public enum RcsLocalCallbackProbeFailureKind
{
    None = 0,
    NotListening,
    Forbidden,
    Unreachable,
    UnexpectedStatus
}

/// <summary>
/// 本机 Kestrel 环回探针结果。只证明本机监听可达，不代表 RCS→工控机网络已通。
/// </summary>
public sealed record RcsLocalCallbackProbeResult(
    bool Ok,
    int HttpStatus,
    int ElapsedMs,
    string? AckBody,
    RcsLocalCallbackProbeFailureKind FailureKind,
    string? Error)
{
    public static RcsLocalCallbackProbeResult NotListening(string? listenError)
        => new(false, 0, 0, null, RcsLocalCallbackProbeFailureKind.NotListening,
            string.IsNullOrWhiteSpace(listenError) ? "回调宿主未启动" : listenError);

    public static RcsLocalCallbackProbeResult Success(int httpStatus, int elapsedMs, string? ackBody)
        => new(true, httpStatus, elapsedMs, ackBody, RcsLocalCallbackProbeFailureKind.None, null);

    public static RcsLocalCallbackProbeResult Forbidden(int httpStatus, int elapsedMs, string? ackBody)
        => new(false, httpStatus, elapsedMs, ackBody, RcsLocalCallbackProbeFailureKind.Forbidden, null);

    public static RcsLocalCallbackProbeResult Unreachable(string error, int elapsedMs = 0)
        => new(false, 0, elapsedMs, null, RcsLocalCallbackProbeFailureKind.Unreachable, error);

    public static RcsLocalCallbackProbeResult UnexpectedStatus(int httpStatus, int elapsedMs, string? ackBody)
        => new(false, httpStatus, elapsedMs, ackBody, RcsLocalCallbackProbeFailureKind.UnexpectedStatus, null);
}
