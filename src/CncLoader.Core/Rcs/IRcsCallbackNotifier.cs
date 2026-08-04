using Microsoft.Extensions.Logging;

namespace CncLoader.Core.Rcs;

/// <summary>
/// RCS 回调事件总线（只读订阅端）。回调服务端解析后经此派发内部事件，
/// 任务跟踪器（步骤④）、槽位账目（步骤⑥）、UI 均订阅同一数据源。
/// 事件在回调线程池线程上触发，订阅方需自行 marshal 到 UI 线程。
/// </summary>
public interface IRcsCallbackNotifier
{
    /// <summary>pushTaskStatus 到达（搬运/抓取任务结果）。</summary>
    event EventHandler<RcsTaskStatusEvent>? TaskStatusReceived;

    /// <summary>scanTaskStatus 到达（识别/盘点结果）。</summary>
    event EventHandler<RcsScanResultEvent>? ScanResultReceived;

    /// <summary>warnCallback 到达（严重告警，逐项）。</summary>
    event EventHandler<RcsWarnEvent>? WarnReceived;
}

/// <summary>回调事件总线的默认实现（单例；处理器调用 Raise*，订阅方监听事件）。</summary>
public sealed class RcsCallbackNotifier : IRcsCallbackNotifier
{
    private readonly ILogger<RcsCallbackNotifier>? _logger;

    public RcsCallbackNotifier(ILogger<RcsCallbackNotifier>? logger = null)
    {
        _logger = logger;
    }

    public event EventHandler<RcsTaskStatusEvent>? TaskStatusReceived;
    public event EventHandler<RcsScanResultEvent>? ScanResultReceived;
    public event EventHandler<RcsWarnEvent>? WarnReceived;

    public void RaiseTaskStatus(RcsTaskStatusEvent e)
        => InvokeHandlersSafely(
            TaskStatusReceived,
            e,
            string.Equals(e.Source, "poll", StringComparison.OrdinalIgnoreCase) ? "poll" : "push",
            SanitizeId(e.TaskId));

    public void RaiseScanResult(RcsScanResultEvent e)
        => InvokeHandlersSafely(ScanResultReceived, e, "scan", SanitizeId(e.TaskId));

    public void RaiseWarn(RcsWarnEvent e)
        => InvokeHandlersSafely(WarnReceived, e, "warn", SanitizeWarnSubject(e.RobotCode, e.BeginTime));

    /// <summary>
    /// 按注册顺序逐个调用订阅者；单订阅者异常记日志并继续，不向外抛、不阻断后续。
    /// </summary>
    private void InvokeHandlersSafely<TEvent>(
        EventHandler<TEvent>? handlers,
        TEvent args,
        string callbackType,
        string subject)
    {
        if (handlers is null) return;

        foreach (var d in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TEvent>)d).Invoke(this, args);
            }
            catch (Exception ex)
            {
                var subscriber = d.Target?.GetType().Name ?? "static";
                var method = d.Method.Name;
                _logger?.LogWarning(ex,
                    "回调事件订阅者异常：type={CallbackType} subject={Subject} subscriber={Subscriber}.{Method}",
                    callbackType, subject, subscriber, method);
            }
        }
    }

    private static string SanitizeId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        return id.Length <= 64 ? id : id[..64] + "…";
    }

    private static string SanitizeWarnSubject(string? robotCode, string? beginTime)
    {
        var robot = SanitizeId(robotCode);
        var begin = string.IsNullOrEmpty(beginTime)
            ? ""
            : (beginTime.Length <= 32 ? beginTime : beginTime[..32] + "…");
        return $"{robot}|{begin}";
    }
}
