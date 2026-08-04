namespace CncLoader.Core.Rcs;

/// <summary>单槽外部写（人工校正/清槽）结果分类。</summary>
public enum SlotMutationStatus
{
    /// <summary>条件更新成功写入。</summary>
    Updated = 0,
    /// <summary>已等于目标，幂等成功（未重复保存）。</summary>
    Unchanged = 1,
    /// <summary>当前为预记态，外部写被拒绝。</summary>
    ReservationConflict = 2,
    /// <summary>槽位行不存在。</summary>
    NotFound = 3,
    /// <summary>行存在、非预记，但与目标仍不同（并发丢失）。</summary>
    ConcurrencyConflict = 4,
    /// <summary>查询/更新异常。</summary>
    DatabaseError = 5,
    /// <summary>调用取消，不得记为 Updated。</summary>
    Cancelled = 6,
    /// <summary>外部写目标状态非法（如人工试图设置 Reserved）；安全拒绝，未写库。</summary>
    InvalidTargetState = 7,
}

/// <summary>结果中附带的槽位必要快照（诊断/UI，不含完整敏感 taskId 强制展示义务）。</summary>
public sealed record SlotMutationSnapshot(
    long Id,
    long FrameId,
    int SlotNo,
    string SlotState,
    string? MaterialId,
    string? Remark,
    string? BindSource,
    DateTime? BindTime);

/// <summary>单槽外部写结果。Updated/Unchanged 为成功；其余不得视为成功。</summary>
public sealed record SlotMutationResult(
    SlotMutationStatus Status,
    long FrameId,
    int SlotNo,
    long? SlotId,
    string? Message,
    SlotMutationSnapshot? Snapshot)
{
    public bool Succeeded =>
        Status is SlotMutationStatus.Updated or SlotMutationStatus.Unchanged;

    public static SlotMutationResult From(
        SlotMutationStatus status,
        long frameId,
        int slotNo,
        SlotRow? row,
        string? message = null) =>
        new(status, frameId, slotNo, row?.Id,
            message,
            row is null
                ? null
                : new SlotMutationSnapshot(
                    row.Id, row.FrameId, row.SlotNo, row.SlotState,
                    row.MaterialId, row.Remark, row.BindSource, row.BindTime));
}
