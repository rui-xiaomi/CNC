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

    /// <summary>启动对账生命周期状态。</summary>
    ReconciliationState ReconciliationState { get; }

    /// <summary>最近一次对账失败原因（成功后清空；取消不写入）。</summary>
    string? ReconciliationFailureReason { get; }

    /// <summary>
    /// 是否暂停新自动上料/下料 RCS 派工（仅当前进程，默认 false）。
    /// 开启后不产生、不下发新的自动上下料任务；已下发任务的回调/轮询/PLC 复核/收口不受影响。
    /// </summary>
    bool IsAutoDispatchPaused { get; }

    /// <summary>对账完成时触发（UI 可订阅以刷新看板/提示）；仅首次成功开闸一次。</summary>
    event EventHandler? Reconciled;

    /// <summary>
    /// 启动对账状态变化（状态或失败原因变化时触发；相同快照不重复）。
    /// 可能在后台线程触发，订阅方须自行 marshal 到 UI 线程。
    /// </summary>
    event EventHandler<ReconciliationSnapshot>? ReconciliationStateChanged;

    /// <summary>
    /// 设置「仅手动测试 / 暂停自动派工」。线程安全；开关变更写日志。
    /// 不影响手工 RCS 下发、PLC 轮询、回调宿主、任务跟踪器。
    /// </summary>
    void SetAutoDispatchPaused(bool paused);

    /// <summary>
    /// 人工恢复：把指定加工位从 ALARM 重置回 WAIT_LOAD（解除报警后调用）。
    /// 绑定的 RCS 任务未到终态时拒绝恢复并抛 <see cref="InvalidOperationException"/>（消息可直接展示给操作员）。
    /// 绑定或该工位上未确认的 CANCELED 任务在恢复成功时一并记 <c>CANCEL_MANUAL_FLAG=1</c>，无需再到 RCS 页确认。
    /// </summary>
    Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default);

    /// <summary>
    /// 加工位处于 WAIT_LOAD 且有未确认取消占用时，在看板确认现场已处理小车/容器。
    /// 无待确认取消时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task AcknowledgeCancelHoldAsync(long equipmentId, long positionId, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// RCS 任务已放弃（redo 达上限 / 查无等）：绑定工位置 ALARM、回滚预记、清工序间交接登记，
    /// 避免工位停在 DISPATCHING/TRANSPORTING 且看板无「恢复」按钮。
    /// </summary>
    Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default);

    /// <summary>
    /// 料架人工置空后：匹配物料码且处于空闲/告警的加工位，仅在 PLC HasMat 确认无料时清看板物料码。
    /// HasMat 未知或确认有料不得清（fail-closed）。默认空实现供测试桩编译。
    /// </summary>
    Task ClearStaleDisplayMaterialAsync(string? materialId, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>料架绑定变更后失效机台→料架缓存（equipmentId 空则全清）。</summary>
    void InvalidateFrameBindingCache(long? equipmentId = null);

    /// <summary>换架第二发失败等：锁定该机台自动派工（进程内）。</summary>
    void SetEquipmentDispatchHold(long equipmentId, bool held, string? reason = null) { }

    /// <summary>该机台是否因换架失败等被锁定自动派工。</summary>
    bool IsEquipmentDispatchHeld(long equipmentId) => false;
}
