namespace CncLoader.Core.Rcs;

/// <summary>
/// RCS 任务编排：生成 taskId → 先落库(CREATED) → 调 <see cref="IRcsClient"/> 下发 →
/// 成功置 DISPATCHED，失败置 FAILED。报文流水由客户端记录。
/// 本阶段①提供下发/取消/查询与手动测试入口；跟踪/复核在后续步骤接入。
/// </summary>
public interface IRcsTaskService
{
    /// <summary>下发搬运任务（cell 级）。position 首取末放。</summary>
    Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default);

    /// <summary>下发抓取任务（station 级 + GrabItem 参数）。</summary>
    Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default);

    /// <summary>下发识别/盘点任务（identifyQR）。</summary>
    Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default);

    /// <summary>取消任务（按已落库 taskId）。</summary>
    Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>redo：同 taskId 幂等重发（REDO_COUNT+1）。手动触发用。</summary>
    Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>自动重做专用：不再递增 REDO_COUNT（调用前已由 store 原子递增），只按原参数重发。</summary>
    Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>空托盘回收（人工触发）：点位→托盘回收区，transitTask + Kind=PalletReturn。不建托盘账。</summary>
    Task<RcsResult> DispatchPalletReturnAsync(long equipmentId, long? positionId, string fromCode, string toCode,
        long workLineId, string lineCode, string author, CancellationToken ct = default);

    /// <summary>条件查询任务（兜底/手动）。</summary>
    Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<RcsTaskRow>> GetRecentTasksAsync(int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<RcsMsgRow>> GetRecentMessagesAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>按条件查询报文流水（方向/接口/taskId/条数），供报文流水页筛选。</summary>
    Task<IReadOnlyList<RcsMsgRow>> QueryMessagesAsync(RcsMsgQuery query, CancellationToken ct = default);

    /// <summary>标记已取消任务的人工处理已确认（CANCEL_MANUAL_FLAG=1，解锁相关点位派工由步骤⑤状态机处理）。</summary>
    Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default);
}

/// <summary>搬运下发入参。</summary>
public sealed record TransitDispatchArgs
{
    public long WorkLineId { get; init; }
    public string LineCode { get; init; } = "LINE";
    /// <summary>0=上料 1=下料 2=转序。</summary>
    public string TaskType { get; init; } = "2";
    public int Priority { get; init; } = 5;
    /// <summary>起点 cell 编码（取）。</summary>
    public required string FromCode { get; init; }
    /// <summary>终点 cell 编码（放）。</summary>
    public required string ToCode { get; init; }
    public long? EquipmentId { get; init; }
    public long? PositionId { get; init; }
    public long? CraftworkId { get; init; }
    public string? ElectrodeId { get; init; }
    public string? TxnId { get; init; }
    public RcsTaskKind Kind { get; init; } = RcsTaskKind.Transit;
    public string? Author { get; init; }
}

/// <summary>抓取下发入参。</summary>
public sealed record GrabDispatchArgs
{
    public long WorkLineId { get; init; }
    public string LineCode { get; init; } = "LINE";
    public int Priority { get; init; } = 5;
    /// <summary>起点 station 编码。</summary>
    public required string SrcStation { get; init; }
    /// <summary>终点 station 编码。</summary>
    public required string DstStation { get; init; }
    /// <summary>抓取明细（srcNo/srcPos/dstNo/dstPos/data）。</summary>
    public required IReadOnlyList<GrabItem> Items { get; init; }
    public long? EquipmentId { get; init; }
    public long? PositionId { get; init; }
    public string? ElectrodeId { get; init; }
    public string? Author { get; init; }
}

/// <summary>识别/盘点下发入参。</summary>
public sealed record IdentifyDispatchArgs
{
    public long WorkLineId { get; init; }
    public string LineCode { get; init; } = "LINE";
    public int Priority { get; init; } = 8;
    /// <summary>料架 station 编码。</summary>
    public required string Station { get; init; }
    /// <summary>起始孔位（三位数，百位为面），如 101。</summary>
    public int PosStart { get; init; } = 101;
    /// <summary>识别个数。</summary>
    public int Count { get; init; } = 1;
    public long? FrameId { get; init; }
    public string? Author { get; init; }
}
