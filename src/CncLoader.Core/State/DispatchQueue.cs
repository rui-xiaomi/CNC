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
}

/// <summary>上料/下料阶段。</summary>
public enum PositionPhase { Upload, Unload }

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
}
