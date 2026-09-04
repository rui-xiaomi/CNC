namespace CncLoader.Core.State;

/// <summary>启动对账生命周期状态（供调度器与看板只读观测）。</summary>
public enum ReconciliationState
{
    /// <summary>尚未开始对账。</summary>
    NotStarted = 0,

    /// <summary>正在执行一轮对账。</summary>
    Reconciling = 1,

    /// <summary>本轮失败，等待按间隔自动重试。</summary>
    WaitingForRetry = 2,

    /// <summary>对账成功，已开闸。</summary>
    Succeeded = 3,

    /// <summary>调度器未启用，未对账、未开闸。</summary>
    Disabled = 4
}
