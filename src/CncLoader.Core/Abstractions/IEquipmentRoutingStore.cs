namespace CncLoader.Core.Abstractions;

/// <summary>
/// 调度路由配置读取接缝：返回原始配置行（含 STATE），是否视为活动由调用方决定。
/// Data 实现不得替调用方偷偷过滤 STATE（列表页查询不走此接缝）。
/// </summary>
public interface IEquipmentRoutingStore
{
    Task<EquipmentRoutingRow?> FindEquipmentAsync(long equipmentId, CancellationToken ct = default);
    Task<CraftworkRoutingRow?> FindCraftworkAsync(long craftworkId, CancellationToken ct = default);
    Task<WorkLineRoutingRow?> FindWorkLineAsync(long workLineId, CancellationToken ct = default);

    /// <summary>同线全部工序行（含禁用），不做 STATE 过滤。</summary>
    Task<IReadOnlyList<CraftworkRoutingRow>> FindCraftworksByWorkLineAsync(long workLineId, CancellationToken ct = default);

    /// <summary>指定工序下的全部机台行（含禁用），不做 STATE 过滤。</summary>
    Task<IReadOnlyList<EquipmentRoutingRow>> FindEquipmentsByCraftworkIdsAsync(
        IReadOnlyCollection<long> craftworkIds, CancellationToken ct = default);

    /// <summary>机台全部料架绑定行（含禁用），不做 STATE 过滤。</summary>
    Task<IReadOnlyList<FrameBindRoutingRow>> FindFrameBindsByEquipmentAsync(
        long equipmentId, CancellationToken ct = default);
}

/// <summary>机台路由快照（STATE 原样，含 null/未知）。</summary>
public sealed record EquipmentRoutingRow(long Id, long CraftworkId, string? State);

/// <summary>工序路由快照（STATE 原样，含 null/未知）。</summary>
public sealed record CraftworkRoutingRow(long Id, long WorkLineId, long? CraftworkNode, string? State);

/// <summary>线体路由快照（STATE 原样，含 null/未知）。</summary>
public sealed record WorkLineRoutingRow(long Id, string WorkLineCode, string? State);

/// <summary>料架绑定路由快照（STATE 原样，含 null/未知）。</summary>
public sealed record FrameBindRoutingRow(long Id, long FrameId, long EquipmentId, string FrameRole, string? State);
