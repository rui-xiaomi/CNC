namespace CncLoader.Core.Rcs;

/// <summary>
/// 盘点后台任务（第四阶段⑥c，§5.7/§6.2）：发起 identifyQR → 后台等 scanTaskStatus 回调 → products 数组按下发孔位顺序全量校正 FRAME_SLOT 物料码 + 更新 LAST_VERIFY_TIME。
/// 盘点约 3~4 分钟/架，RCS 单任务串行——发起后异步，回调到达通知 UI 刷新。发起前应检查队列无待处理搬运请求（或提示"执行期间搬运将排队"）。
/// </summary>
public interface IInventoryService
{
    /// <summary>发起盘点：下发 identifyQR（frameId 的 station + 起始孔位 + 数量），返回 taskId。异步推进，结果经 <see cref="InventoryCompleted"/> 事件通知。</summary>
    Task<string> StartInventoryAsync(long frameId, int posStart, int count, string author, CancellationToken ct = default);

    /// <summary>盘点完成（成功校正槽位）或失败（RCS 报错/取消）。</summary>
    event EventHandler<InventoryResultEvent>? InventoryCompleted;

    /// <summary>当前进行中的盘点任务（UI 展示）。</summary>
    IReadOnlyList<InventoryTaskInfo> GetActiveInventories();
}

/// <summary>盘点结果事件。</summary>
public sealed record InventoryResultEvent(
    long FrameId,
    string TaskId,
    string State,           // COMPLETED / FAILED / CANCELED
    string? Code,           // 被扫料架编号（scanTaskStatus.code，与 taskId 双重校验）
    IReadOnlyList<string> Products,
    int CorrectedCount,
    string? Error);

/// <summary>进行中的盘点任务信息。</summary>
public sealed record InventoryTaskInfo(long FrameId, string TaskId, int PosStart, int Count, DateTime StartedAt);
