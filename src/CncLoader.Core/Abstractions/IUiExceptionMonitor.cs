namespace CncLoader.Core.Abstractions;

/// <summary>
/// UI 线程未处理异常计数（P2-6）：全局处理器为防崩溃标记 Handled 后，界面状态可能与实际不一致，
/// 看板据此提示操作员核对。只计数与留最近一条摘要，不含堆栈。
/// </summary>
public interface IUiExceptionMonitor
{
    int Count { get; }
    DateTime? LastAt { get; }
    string? LastMessage { get; }

    /// <summary>计数变化（在记录异常的线程上触发，订阅方自行切 UI 线程）。</summary>
    event EventHandler? Changed;

    void Record(Exception exception);
}
