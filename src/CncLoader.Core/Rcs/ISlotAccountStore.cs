namespace CncLoader.Core.Rcs;

/// <summary>
/// 槽位账持久化接缝：外部写（人工校正/清槽/盘点）+ 预记状态机（Reserve/Confirm/Rollback）。
/// 外部写须条件 UPDATE（SLOT_STATE &lt;&gt; Reserved）；状态机走专用条件，不得经外部写入口。
/// 默认状态机成员抛 NotSupported，供仅测外部写的旧 fake 编译；生产 Store 必须实现。
/// </summary>
public interface ISlotAccountStore
{
    /// <summary>
    /// 单槽原子外部写（独立短生命周期 DbContext）：identity 匹配且 SLOT_STATE != Reserved。
    /// </summary>
    Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
        long frameId,
        int slotNo,
        string targetState,
        string? materialId,
        bool clearRemarkAndBindTime,
        DateTime updateTime,
        CancellationToken ct = default);

    /// <summary>打开带事务的批量会话（盘点同事务多槽条件写）。</summary>
    Task<ISlotAccountSession> OpenAsync(CancellationToken ct = default);

    /// <summary>入库预记（PUT）：候选空槽 + WHERE Empty 原子更新。无空槽/全并发失败返回 null。</summary>
    Task<ReservedSlot?> ReservePutAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
        => throw new NotSupportedException("此 Store 未实现预记状态机接缝");

    /// <summary>取料预记（TAKE）：候选占用槽 + WHERE Occupied 原子更新。</summary>
    Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
        => throw new NotSupportedException("此 Store 未实现预记状态机接缝");

    /// <summary>按 taskId 查 Reserved 预记。</summary>
    Task<ReservedSlot?> FindReservedByTaskIdAsync(string taskId, CancellationToken ct = default)
        => throw new NotSupportedException("此 Store 未实现预记状态机接缝");

    /// <summary>取料预记：只锁指定物料占用槽。</summary>
    Task<ReservedSlot?> ReserveTakeByMaterialAsync(long frameId, string taskId, string materialId, CancellationToken ct = default)
        => throw new NotSupportedException("此 Store 未实现预记状态机接缝");

    /// <summary>入库落账（PUT）：按 taskId 所有权确认。</summary>
    Task<bool> ConfirmPutAsync(string taskId, CancellationToken ct = default)
        => throw new NotSupportedException("此 Store 未实现预记状态机接缝");

    /// <summary>取料落账（TAKE）：按 taskId 所有权确认。</summary>
    Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default)
        => throw new NotSupportedException("此 Store 未实现预记状态机接缝");

    /// <summary>入库回滚（PUT）。</summary>
    Task<bool> RollbackPutAsync(string taskId, CancellationToken ct = default)
        => throw new NotSupportedException("此 Store 未实现预记状态机接缝");

    /// <summary>取料回滚（TAKE）。</summary>
    Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
        => throw new NotSupportedException("此 Store 未实现预记状态机接缝");
}

/// <summary>条件更新尝试结果：affected + 只读重查快照；InvalidTargetState 表示目标态非法未执行 UPDATE。</summary>
public sealed record ExternalSlotWriteAttempt(
    int AffectedRows,
    SlotRow? Current,
    bool InvalidTargetState = false);

/// <summary>盘点条件写可选字段（BIND_SOURCE / BIND_TIME / LAST_VERIFY_TIME）。</summary>
/// <param name="ApplyBindFields">true 时写入 BindSource/BindTime（占用扫码）；清空空码时为 false，避免误清绑定。</param>
public sealed record InventorySlotWriteExtras(
    string? BindSource = null,
    DateTime? BindTime = null,
    DateTime? LastVerifyTime = null,
    bool ApplyBindFields = false);

/// <summary>
/// 批量会话：同一 DbContext/事务内条件写；显式 Commit/Rollback；Dispose 未提交则回滚。
/// </summary>
public interface ISlotAccountSession : IAsyncDisposable
{
    Task<SlotRow?> FindByFrameSlotAsync(long frameId, int slotNo, CancellationToken ct = default);

    /// <summary>按 LAYER_NO → POS_IN_LAYER 只读加载整架槽位（与 identifyQR 孔位顺序对齐）。</summary>
    Task<IReadOnlyList<SlotRow>> FindByFrameOrderedAsync(long frameId, CancellationToken ct = default);

    /// <summary>
    /// 事务内原子外部写：FrameId/SlotNo 匹配且 SLOT_STATE != Reserved。
    /// 不得依赖跟踪实体 SaveChanges；affected=0 后仅分类重读。
    /// </summary>
    Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
        long frameId,
        int slotNo,
        string targetState,
        string? materialId,
        bool clearRemarkAndBindTime,
        DateTime updateTime,
        CancellationToken ct = default,
        InventorySlotWriteExtras? extras = null);

    Task CommitAsync(CancellationToken ct = default);
    Task RollbackAsync(CancellationToken ct = default);
}

/// <summary>
/// 可就地修改的槽位行快照。字段与 <c>MAS_AUTO_FRAME_SLOT</c> / FrameSlot 对齐。
/// </summary>
public sealed class SlotRow
{
    public long Id { get; init; }
    public long FrameId { get; init; }
    public int SlotNo { get; init; }
    public int LayerNo { get; set; }
    public int PosInLayer { get; set; }
    public string SlotState { get; set; } = SlotStates.Empty;
    public string? MaterialId { get; set; }
    public DateTime? BindTime { get; set; }
    public string? BindSource { get; set; }
    public DateTime? LastVerifyTime { get; set; }
    public string? Remark { get; set; }
    public DateTime? UpdateTime { get; set; }

    public SlotRow Clone() => new()
    {
        Id = Id,
        FrameId = FrameId,
        SlotNo = SlotNo,
        LayerNo = LayerNo,
        PosInLayer = PosInLayer,
        SlotState = SlotState,
        MaterialId = MaterialId,
        BindTime = BindTime,
        BindSource = BindSource,
        LastVerifyTime = LastVerifyTime,
        Remark = Remark,
        UpdateTime = UpdateTime,
    };
}
