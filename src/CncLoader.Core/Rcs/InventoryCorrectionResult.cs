namespace CncLoader.Core.Rcs;

/// <summary>批量盘点校正结果分类（D5）。</summary>
public enum InventoryCorrectionStatus
{
    /// <summary>已完成且无冲突/失败。</summary>
    Completed = 0,
    /// <summary>提交前取消，不得记部分 Updated。</summary>
    Cancelled = 1,
    /// <summary>提交失败，整批回滚。</summary>
    DatabaseError = 2,
    /// <summary>已完成但含跳过/未找到/并发冲突等警告。</summary>
    CompletedWithWarnings = 3,
}

/// <summary>
/// 盘点批量校正计数契约。各类别互斥；ConflictSlots 展示上限截断，总冲突数以 ReservationConflictCount 为准。
/// </summary>
public sealed record InventoryCorrectionResult(
    InventoryCorrectionStatus Status,
    int RequestedCount,
    int UpdatedCount,
    int UnchangedCount,
    int ReservationConflictCount,
    int NotFoundCount,
    IReadOnlyList<SlotMutationSnapshot> ConflictSlots,
    string? Message = null,
    int ConcurrencyConflictCount = 0)
{
    /// <summary>整批完成且无冲突/错误。</summary>
    public bool Succeeded =>
        Status == InventoryCorrectionStatus.Completed
        && !HasWarnings
        && string.IsNullOrEmpty(Message);

    /// <summary>有跳过预记/未找到/并发等需运维关注的项。</summary>
    public bool HasWarnings =>
        ReservationConflictCount > 0
        || NotFoundCount > 0
        || ConcurrencyConflictCount > 0;

    public int CategorizedTotal =>
        UpdatedCount + UnchangedCount + ReservationConflictCount + NotFoundCount + ConcurrencyConflictCount;

    public static InventoryCorrectionResult Empty(InventoryCorrectionStatus status = InventoryCorrectionStatus.Completed) =>
        new(status, 0, 0, 0, 0, 0, Array.Empty<SlotMutationSnapshot>());
}
