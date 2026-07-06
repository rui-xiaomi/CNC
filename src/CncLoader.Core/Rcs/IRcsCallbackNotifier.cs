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
    public event EventHandler<RcsTaskStatusEvent>? TaskStatusReceived;
    public event EventHandler<RcsScanResultEvent>? ScanResultReceived;
    public event EventHandler<RcsWarnEvent>? WarnReceived;

    public void RaiseTaskStatus(RcsTaskStatusEvent e) => TaskStatusReceived?.Invoke(this, e);
    public void RaiseScanResult(RcsScanResultEvent e) => ScanResultReceived?.Invoke(this, e);
    public void RaiseWarn(RcsWarnEvent e) => WarnReceived?.Invoke(this, e);
}
