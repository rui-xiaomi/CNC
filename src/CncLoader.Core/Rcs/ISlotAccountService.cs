namespace CncLoader.Core.Rcs;

/// <summary>
/// 槽位账目服务（第四阶段⑥a）：grab 任务（抓取/电极级）的槽位占用意向"预记"、回调确认后"落账"、取消/失败"回滚预记"。
/// 时序（开发文档 §6.1 补充规则）：
/// 1) 选定孔位时 Reserve（槽位置预记，绑定 taskId）；
/// 2) RCS 回调 completed + PLC 复核通过后 Confirm（落账：源槽位清空、目标槽位占用）；
/// 3) 取消/失败 Rollback（预记回滚到原状）；redo 不重复记账（同 taskId 幂等）。
/// 同一料架并发保护：多加工位同时从一料架取料时，选孔位需加锁，防止两任务选中同一电极。
/// </summary>
public interface ISlotAccountService
{
    /// <summary>入库预记（PUT 方向）：在 dest 料架选一个空槽位（按层+层内位顺序首个空位），置预记态 + 绑定 taskId + electrodeId。
    /// 返回选中的 (slotNo, layerNo, posInLayer)；无空槽或并发冲突返回 null。同架互斥保证不会两任务选同一槽。</summary>
    Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? electrodeId, CancellationToken ct = default);

    /// <summary>入库落账（PUT）：按 taskId 找到预记槽位，置占用（电极落账）。redo 同 taskId 幂等（已落账不再重复）。</summary>
    Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default);

    /// <summary>入库回滚（PUT）：按 taskId 找到预记槽位，恢复为空（取消/失败时调用）。已落账的不回滚。</summary>
    Task<bool> RollbackAsync(string taskId, CancellationToken ct = default);

    /// <summary>取料预记（TAKE 方向）：在 source 料架选首个占用槽位，置预记态 + 绑定 taskId，保留电极码。
    /// 返回选中的槽位（含 electrodeId）；无占用槽或并发冲突返回 null。用于上料/中转架回流从料架取件。</summary>
    Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default);

    /// <summary>取料落账（TAKE）：按 taskId 找到预记槽位，清空（电极已被取走）。redo 同 taskId 幂等。</summary>
    Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default);

    /// <summary>取料回滚（TAKE）：按 taskId 找到预记槽位，恢复为占用（取消/失败时调用）。</summary>
    Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default);

    /// <summary>重启对账：回滚全部陈旧预记（Reserved 且 Remark 不在 activeTaskIds 内）——
    /// 按 BindSource 方向还原（RESERVE_PUT→空、RESERVE_TAKE→占用）。返回回滚数。</summary>
    Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default);

    /// <summary>料架占用统计（total/occupied/reserved/empty），供水位监视器与 UI。</summary>
    Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default);

    /// <summary>人工校正：直接设置槽位电极码与状态（处理账实不符后人工闭环）。</summary>
    Task SetSlotAsync(long frameId, int slotNo, string? electrodeId, string slotState, string author, CancellationToken ct = default);

    /// <summary>电极反查：按电极码找所在料架+槽位（UI 高亮 + NG 处理定位）。</summary>
    Task<SlotLocation?> LocateElectrodeAsync(string electrodeId, CancellationToken ct = default);

    /// <summary>盘点校正：posStart 为 identifyQR 孔位（三位数、百位=面/层，如 101=1层1位），
    /// products 按下发孔位顺序映射到 LAYER_NO/POS_IN_LAYER 物理序；范围外槽位电极不变，整架 LAST_VERIFY_TIME=now。返回校正数。</summary>
    Task<int> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default);

    /// <summary>取料架全部槽位（按 SlotNo 顺序），供盘点/NG 处理/UI。</summary>
    Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default);
}

/// <summary>槽位记录（盘点/NG 处理/UI 共用）。</summary>
public sealed record SlotRecord(long FrameId, int SlotNo, int LayerNo, int PosInLayer, string SlotState, string? ElectrodeId, DateTime? LastVerifyTime);

/// <summary>预记选中的槽位（TAKE 方向带出被取电极码）。</summary>
public sealed record ReservedSlot(long FrameId, int SlotNo, int LayerNo, int PosInLayer, string? ElectrodeId = null);

/// <summary>料架占用统计。</summary>
public sealed record FrameOccupancy(int Total, int Occupied, int Reserved, int Empty);

/// <summary>电极所在位置（反查）。</summary>
public sealed record SlotLocation(long FrameId, string FrameName, int SlotNo, int LayerNo, int PosInLayer, string SlotState);

/// <summary>槽位状态常量（MAS_AUTO_FRAME_SLOT.SLOT_STATE 语义扩展）。</summary>
public static class SlotStates
{
    public const string Empty = "0";       // 空
    public const string Occupied = "1";    // 占用（已落账）
    public const string Locked = "2";      // 锁定/不可用
    public const string Reserved = "3";    // 预记（占用意向，绑定 taskId 在 REMARK）
}
