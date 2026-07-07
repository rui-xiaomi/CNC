namespace CncLoader.Core.Rcs;

/// <summary>料架角色（FRAME_BIND.FRAME_ROLE 语义扩展 0/1/2/3）。</summary>
public enum FrameRole { Upload = 0, Unload = 1, Transit = 2, NgFrame = 3 }

/// <summary>
/// 换架任务对编排（第四阶段⑥b，§6.2 v2.2/v2.3）：换架 = 一对 RCS 任务——
/// ①拉走旧架（站点→缓存区）→ 等 ① completed 后 → ②送新架（缓存区→站点）。**先拉后送**（站点位置唯一）。
/// 以 TXN_ID 关联两个 taskId；第一发失败 redo≤3 仍失败告警（绑定不解除）；第二发失败 redo≤3 仍失败→锁定工序+工单。
/// 拉走时解除 FRAME_BIND 绑定；新架到位走入库/盘点确认身份后再绑定（绑定操作不在本编排内）。
/// </summary>
public interface IChangeFrameOrchestrator
{
    /// <summary>发起换架：返回 TXN_ID。异步推进，进度经 <see cref="ProgressChanged"/> 事件通知 UI。</summary>
    Task<string> ChangeFrameAsync(long equipmentId, FrameRole role, string author, CancellationToken ct = default);

    /// <summary>换架进度变化（每步 taskId/态/步骤）。</summary>
    event EventHandler<ChangeFrameProgressEvent>? ProgressChanged;

    /// <summary>当前进行中的换架事务（UI 展示）。</summary>
    IReadOnlyList<ChangeFrameProgressEvent> GetActiveTransactions();
}

/// <summary>换架进度事件。</summary>
public sealed record ChangeFrameProgressEvent(
    string TxnId,
    long EquipmentId,
    FrameRole Role,
    ChangeFrameStep Step,
    string? PullTaskId,
    string? PushTaskId,
    string State,   // RUNNING / COMPLETED / FAILED / CANCELED
    string? Message);

/// <summary>换架步骤。</summary>
public enum ChangeFrameStep { PullOld, PushNew, Done, Alarm }
