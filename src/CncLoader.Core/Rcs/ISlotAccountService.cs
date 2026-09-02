namespace CncLoader.Core.Rcs;

/// <summary>
/// 槽位账目服务（第四阶段⑥a）：grab 任务（抓取/物料级）的槽位占用意向"预记"、回调确认后"落账"、取消/失败"回滚预记"。
/// 时序（开发文档 §6.1 补充规则）：
/// 1) 选定孔位时 Reserve（槽位置预记，绑定 taskId）；
/// 2) RCS 回调 completed + PLC 复核通过后 Confirm（落账：源槽位清空、目标槽位占用）；
/// 3) 取消/失败 Rollback（预记回滚到原状）；redo 已有预记则幂等跳过，已回滚须先再预记再下发。
/// 同一料架并发保护：多加工位同时从一料架取料时，选孔位需加锁，防止两任务选中同一物料。
/// </summary>
public interface ISlotAccountService
{
    /// <summary>入库预记（PUT 方向）：在 dest 料架选一个空槽位（按层+层内位顺序首个空位），置预记态 + 绑定 taskId + materialId。
    /// 返回选中的 (slotNo, layerNo, posInLayer)；无空槽或并发冲突返回 null。同架互斥保证不会两任务选同一槽。</summary>
    Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default);

    /// <summary>入库落账（PUT）：按 taskId 找到预记槽位，置占用（物料落账）。redo 同 taskId 幂等（已落账不再重复）。</summary>
    Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default);

    /// <summary>入库回滚（PUT）：按 taskId 找到预记槽位，恢复为空（取消/失败时调用）。已落账的不回滚。</summary>
    Task<bool> RollbackAsync(string taskId, CancellationToken ct = default);

    /// <summary>取料预记（TAKE 方向）：在 source 料架选首个占用槽位，置预记态 + 绑定 taskId，保留物料码。
    /// 返回选中的槽位（含 materialId）；无占用槽或并发冲突返回 null。用于上料/中转架回流从料架取件。</summary>
    Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default);

    /// <summary>按 taskId 查仍有效的预记；无则 null。默认空实现供旧 fake 编译。</summary>
    Task<ReservedSlot?> FindReservedAsync(string taskId, CancellationToken ct = default)
        => Task.FromResult<ReservedSlot?>(null);

    /// <summary>取料预记：只锁指定物料所在占用槽；找不到该物料返回 null（不改抢其它件）。</summary>
    Task<ReservedSlot?> ReserveTakeByMaterialAsync(long frameId, string taskId, string materialId, CancellationToken ct = default)
        => ReserveTakeAsync(frameId, taskId, ct);

    /// <summary>取料落账（TAKE）：按 taskId 找到预记槽位，清空（物料已被取走）。redo 同 taskId 幂等。</summary>
    Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default);

    /// <summary>取料回滚（TAKE）：按 taskId 找到预记槽位，恢复为占用（取消/失败时调用）。</summary>
    Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default);

    /// <summary>收口陈旧预记（Reserved 且 Remark 不在 activeTaskIds 内）：
    /// 源任务已 COMPLETED 的预记<strong>跳过</strong>（由调度器按 PLC 门补 Confirm，见 <see cref="ListCompletedPendingConfirmAsync"/>）；
    /// 其余按方向回滚（PUT→空、TAKE→占用）。返回回滚槽位数。</summary>
    Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default);

    /// <summary>列出「不在 active、任务已 COMPLETED、槽位仍为预记」的项（TaskId + 是否 TAKE 方向），供调度器按 PLC 复核后 Confirm。</summary>
    Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
        IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default);

    /// <summary>料架占用统计（total/occupied/reserved/empty），供水位监视器与 UI。</summary>
    Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default);

    /// <summary>
    /// 人工校正/清槽：条件更新槽位物料码与状态。
    /// 遇 Reserved 返回 <see cref="SlotMutationStatus.ReservationConflict"/>，不得覆盖预记字段。
    /// </summary>
    Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default);

    /// <summary>物料反查：按物料码找所在料架+槽位（UI 高亮 + NG 处理定位）。</summary>
    Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default);

    /// <summary>盘点校正：posStart 为 identifyQR 孔位（三位数、百位=面/层，如 101=1层1位），
    /// products 按下发孔位顺序映射到 LAYER_NO/POS_IN_LAYER 物理序；范围外槽位物料不变，整架 LAST_VERIFY_TIME=now。
    /// 返回 <see cref="InventoryCorrectionResult"/> 计数字段（D5）；Reserved 跳过为 GREEN。</summary>
    Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default);

    /// <summary>取料架全部槽位（按 SlotNo 顺序），供盘点/NG 处理/UI。</summary>
    Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default);
}

/// <summary>槽位记录（盘点/NG 处理/UI 共用）。</summary>
public sealed record SlotRecord(long FrameId, int SlotNo, int LayerNo, int PosInLayer, string SlotState, string? MaterialId, DateTime? LastVerifyTime);

/// <summary>预记选中的槽位（TAKE 方向带出被取物料码）。</summary>
public sealed record ReservedSlot(long FrameId, int SlotNo, int LayerNo, int PosInLayer, string? MaterialId = null);

/// <summary>任务已 COMPLETED 但仍为预记的槽位，待 PLC 门收口。</summary>
/// <param name="IsTake">true=取料预记（RSV_TAKE），false=入库预记（RSV_PUT）。</param>
public sealed record CompletedPendingConfirm(string TaskId, bool IsTake, long FrameId, int SlotNo);

/// <summary>料架占用统计。</summary>
public sealed record FrameOccupancy(int Total, int Occupied, int Reserved, int Empty);

/// <summary>物料所在位置（反查）。</summary>
public sealed record SlotLocation(long FrameId, string FrameName, int SlotNo, int LayerNo, int PosInLayer, string SlotState);

/// <summary>槽位状态常量（MAS_AUTO_FRAME_SLOT.SLOT_STATE 语义扩展）。</summary>
public static class SlotStates
{
    public const string Empty = "0";       // 空
    public const string Occupied = "1";    // 占用（已落账）
    public const string Locked = "2";      // 锁定/不可用
    public const string Reserved = "3";    // 预记（占用意向，绑定 taskId 在 REMARK）
}
