namespace CncLoader.Core.State;

/// <summary>
/// 加工位状态机调度器（第四阶段⑤）。每加工位一个独立状态机实例并行运行（双位并行），
/// 驱动 §7 状态机：WAIT_LOAD→DISPATCHING→TRANSPORTING→(PLC复核)→LOADED→PROCESSING→DONE→DISPATCHING→TRANSPORTING→UNLOADED→WAIT_LOAD。
/// LOADED/UNLOADED 双条件（RCS completed 且 PLC 复核通过）；复核不过 ALARM 不写启动（安全底线）。
/// 启动时先做 §6.3 对账，对账完成前不自动派工。
/// </summary>
public interface IPositionScheduler
{
    /// <summary>对账是否完成（完成后才开始自动派工）。</summary>
    bool IsReconciled { get; }

    /// <summary>对账完成时触发（UI 可订阅以刷新看板/提示）。</summary>
    event EventHandler? Reconciled;

    /// <summary>人工恢复：把指定加工位从 ALARM 重置回 WAIT_LOAD（解除报警后调用）。</summary>
    Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default);

    /// <summary>
    /// RCS 任务已放弃（redo 达上限 / 查无等）：绑定工位置 ALARM、回滚预记、清工序间交接登记，
    /// 避免工位停在 DISPATCHING/TRANSPORTING 且看板无「恢复」按钮。
    /// </summary>
    Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default);

    /// <summary>料架绑定变更后失效机台→料架缓存（equipmentId 空则全清）。</summary>
    void InvalidateFrameBindingCache(long? equipmentId = null);
}
