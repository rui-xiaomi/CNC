using CncLoader.Core.Rcs;

namespace CncLoader.Core.State;

/// <summary>
/// 加工位调度请求（入优先级队列）。由状态机在 WAIT_LOAD→DISPATCHING / DONE→DISPATCHING 时产生。
/// RCS 单任务串行执行，队列按 priority 出队（紧急下料 > 常规上料）。
/// </summary>
public sealed record DispatchItem
{
    public required long EquipmentId { get; init; }
    public required long PositionId { get; init; }
    /// <summary>Upload=上料 / Unload=下料。决定 RCS 任务类型与 priority 与 PLC 复核方向。</summary>
    public required PositionPhase Phase { get; init; }
    /// <summary>1~10，大者优先。上传默认 5，下料默认 8（紧急下料 > 常规上料）。</summary>
    public int Priority { get; init; } = 5;
    /// <summary>起点 RCS cell 编码（LOCATION_MAP 解析）。</summary>
    public required string FromCode { get; init; }
    /// <summary>终点 RCS cell 编码。</summary>
    public required string ToCode { get; init; }
    public required long WorkLineId { get; init; }
    public required string LineCode { get; init; }
    public string? Author { get; init; }

    /// <summary>下料终点类型（决定槽位账与交接处理）。上料忽略。</summary>
    public UnloadTarget UnloadTarget { get; init; } = UnloadTarget.DownloadFrame;
    /// <summary>上料取料源料架 ID（上料架/中转架）；无则从命名区取，不记料架账。</summary>
    public long? SourceFrameId { get; init; }
    /// <summary>下料入库目标料架 ID（下料/中转/NG 架）；直接交接到下一台机时为 null。</summary>
    public long? DestFrameId { get; init; }
    /// <summary>直接交接到下一台机时的目标机台/工位（登记待入库）。</summary>
    public long? DestEquipmentId { get; init; }
    public long? DestPositionId { get; init; }
    /// <summary>随件物料码（供入库落账/交接溯源）。</summary>
    public string? MaterialId { get; init; }
    /// <summary>下料结果（true=OK / false=NG）。上料忽略。下料终点（选位/中转/NG/下料架）由单消费者出队后统一决策，故结果随请求入队。</summary>
    public bool IsOk { get; init; } = true;
}

/// <summary>上料/下料阶段。</summary>
public enum PositionPhase { Upload, Unload }

/// <summary>下料终点类型（§6.2 OK/NG 分流）。</summary>
public enum UnloadTarget
{
    /// <summary>下料架（末道工序，role1）。</summary>
    DownloadFrame,
    /// <summary>旧 role2 中转架。现场一架两用走 <see cref="DownloadFrame"/>。</summary>
    TransitFrame,
    /// <summary>NG 专用架（role3，不再流转）。</summary>
    NgFrame,
    /// <summary>工位直送（现场自动调度不再选用；收口/手动遗留仍识别）。</summary>
    NextMachineCell,
}

/// <summary>
/// 优先级派工队列（Core 抽象）。Communication 提供基于优先级的并发安全实现。
/// 调度器把状态机产生的请求入队，派工循环按优先级出队下发 RCS。
/// </summary>
public interface IDispatchQueue
{
    void Enqueue(DispatchItem item);
    /// <summary>按 priority 降序（同 priority FIFO）取一条；空则 null。</summary>
    DispatchItem? Dequeue();
    int Count { get; }
    void Clear();
}

/// <summary>
/// 路由决策（§6.2 简化版）：把"上料/下料"解析为 RCS 起终点 cell 编码。
/// 默认实现用 LOCATION_MAP；找不到返回 null（调用方告警人工）。
/// </summary>
public interface IRouteResolver
{
    /// <summary>上料：起点=上料区/上料架 cell，终点=加工位 cell。</summary>
    Task<(string from, string to)?> ResolveUploadAsync(long equipmentId, long positionId, CancellationToken ct = default);
    /// <summary>下料：起点=加工位 cell，终点=下料区/下料架 cell。</summary>
    Task<(string from, string to)?> ResolveUnloadAsync(long equipmentId, long positionId, CancellationToken ct = default);
    /// <summary>解析加工位 cell 编码（LOCATION_MAP rcsType="cell"）；失败返回 null。</summary>
    Task<string?> ResolvePositionCellAsync(long equipmentId, long positionId, CancellationToken ct = default);
    /// <summary>
    /// 解析加工位 station（先 POSITION station，缺则 EQUIPMENT station）。
    /// 禁止回退 cell。缺映射返回 null。
    /// </summary>
    Task<string?> ResolvePositionStationAsync(long equipmentId, long positionId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    /// <summary>解析料架 cell 编码（LOCATION_MAP FrameId + rcsType="cell"；缺映射返回 null，禁止假码）。</summary>
    Task<string?> ResolveFrameCellAsync(long frameId, CancellationToken ct = default);

    /// <summary>解析料架 shelf/station（整架搬运/盘点）。缺映射返回 null。</summary>
    Task<string?> ResolveFrameShelfAsync(long frameId, CancellationToken ct = default)
        => ResolveFrameCellAsync(frameId, ct);

    /// <summary>解析料架指定槽 cell（货架+层编码10起+位）。缺 LOCATION_MAP 返回 null，禁止假码。</summary>
    Task<string?> ResolveFrameSlotCellAsync(long frameId, int layerNo, int posInLayer, CancellationToken ct = default)
        => ResolveFrameCellAsync(frameId, ct);
}
