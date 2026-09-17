namespace CncLoader.Core.Rcs;

/// <summary>
/// RCS 任务落库仓储（MAS_AUTO_AGV_TASK）。承载"先落库后发送"与状态推进。
/// </summary>
public interface IRcsTaskStore
{
    /// <summary>先落库（TASK_STATE=CREATED），返回主键 ID1。</summary>
    Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default);

    /// <summary>
    /// 保存 RCS ACK 回包号到 <c>RCS_REMOTE_ID</c>，不改 <c>RCS_TASK_ID</c>（本地号）。
    /// 目标回包号已被其它行占用或源行不存在返回 false。同号或已绑定同一回包号视为成功。
    /// </summary>
    Task<bool> BindRemoteIdAsync(string localTaskId, string remoteTaskId, CancellationToken ct = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(localTaskId) && !string.IsNullOrWhiteSpace(remoteTaskId));

    /// <summary>下发成功：置 DISPATCHED + DISPATCH_TIME。</summary>
    Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>
    /// 更新任务态（可带 RCS 原始态与错误信息）。COMPLETED/CANCELED 时置 FINISH_TIME。
    /// 迁移受 <see cref="RcsTaskStateTransition"/> 约束：终态不被迟到的旧态覆盖。
    /// 返回 true=已找到行并保存成功；false=任务行不存在（未落库）或迁移被拒；异常/取消照常抛出。
    /// </summary>
    Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null,
        string? error = null, CancellationToken ct = default);

    /// <summary>redo：REDO_COUNT+1，态回到 DISPATCHED，刷新 SEND_TIME。手动 <c>RedoAsync</c> 用。</summary>
    Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>
    /// 自动重派原子 Claim：仅当 taskId 匹配、仍为候选态（FAILED）、REDO_COUNT &lt; maxRedo 时，
    /// 单条条件更新 REDO_COUNT+1、态→DISPATCHED、清 Error、刷新 SEND_TIME，返回 <see cref="AutoRedoClaimResult.Claimed"/>。
    /// 并发下最多一个成功；失败不修改行，并区分上限 / 状态已被占 / 不存在。
    /// </summary>
    Task<AutoRedoClaimResult> TryClaimAutoRedoAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default);

    /// <summary>
    /// 重发开始：只刷新 SEND_TIME，查无宽限从本次尝试起算。不改 REDO_COUNT / TASK_STATE。
    /// <see cref="IncrementRedoAsync"/> 与 <see cref="TryClaimAutoRedoAsync"/> 已内含同等刷新。
    /// </summary>
    Task MarkResendAttemptAsync(string rcsTaskId, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>标记取消后人工处理已确认。仅 TASK_STATE=CANCELED 允许；任务不存在或状态不符抛 InvalidOperationException。</summary>
    Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>按本地号或回包号取一行（不存在返回 null）。</summary>
    Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>批量按本地号或回包号取行（缺行不进字典）。默认逐条回退。</summary>
    Task<IReadOnlyDictionary<string, RcsTaskRow>> GetByTaskIdsAsync(
        IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        return LoadByTaskIdsFallbackAsync(this, ids, ct);

        static async Task<IReadOnlyDictionary<string, RcsTaskRow>> LoadByTaskIdsFallbackAsync(
            IRcsTaskStore store, IReadOnlyList<string> keys, CancellationToken token)
        {
            var map = new Dictionary<string, RcsTaskRow>(StringComparer.Ordinal);
            foreach (var id in keys.Where(static x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
            {
                var row = await store.GetByTaskIdAsync(id, token);
                if (row is null) continue;
                map[id] = row;
                if (!string.IsNullOrEmpty(row.RcsTaskId)) map[row.RcsTaskId] = row;
                if (!string.IsNullOrEmpty(row.RcsRemoteId)) map[row.RcsRemoteId] = row;
            }
            return map;
        }
    }

    /// <summary>该工位是否有未人工确认的取消任务（CANCEL_MANUAL_FLAG≠1）。</summary>
    Task<bool> HasUnconfirmedCanceledAsync(long equipmentId, long positionId, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>该工位未确认取消任务的本地号（新→旧）。看板就地确认。</summary>
    Task<IReadOnlyList<string>> ListUnconfirmedCanceledTaskIdsAsync(
        long equipmentId, long positionId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>最近 N 条任务（倒序），供 UI 展示。</summary>
    Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>全部未完结（CREATED/DISPATCHED/EXECUTING）任务的本地 taskId，供 queryTask(key=Id) 兜底轮询。</summary>
    Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default);
}

/// <summary>任务落库入参（先落库时构建）。</summary>
public sealed record RcsTaskRecord
{
    public required string RcsTaskId { get; init; }
    public required RcsTaskKind Kind { get; init; }
    public long WorkLineId { get; init; }
    /// <summary>0=上料 1=下料 2=转序（TASK_TYPE，兼容旧列）。</summary>
    public string TaskType { get; init; } = "0";
    public int Priority { get; init; } = 5;
    public string FromCode { get; init; } = "";
    public string ToCode { get; init; } = "";
    public long? EquipmentId { get; init; }
    public long? PositionId { get; init; }
    public long? CraftworkId { get; init; }
    public string? MaterialId { get; init; }
    public string? TxnId { get; init; }
    public string? ReqParam { get; init; }
    public string? Author { get; init; }
}

/// <summary>任务展示行。</summary>
public sealed record RcsTaskRow(
    long Id,
    string? RcsTaskId,
    string? Kind,
    /// <summary>0=上料 1=下料 2=转序（用于对账时区分阶段）。</summary>
    string TaskType,
    string TaskState,
    string? RcsStatus,
    int Priority,
    string FromCode,
    string ToCode,
    long? EquipmentId,
    long? PositionId,
    string? MaterialId,
    string? TxnId,
    string? ReqParam,
    int RedoCount,
    string CancelManualFlag,
    DateTime SendTime,
    DateTime? DispatchTime,
    DateTime? FinishTime,
    string? ErrorMsg)
{
    /// <summary>RCS ACK 回包号（<c>cancelTask</c> 用）。空则取消仍用 <see cref="RcsTaskId"/>。</summary>
    public string? RcsRemoteId { get; init; }

    /// <summary>有错误文案，供表格强调 / 详情区展示。</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMsg);
}
