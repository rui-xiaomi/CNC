namespace CncLoader.Core.Abstractions;

/// <summary>
/// 料架改层/删架结构接缝：非空槽（含 Reserved）计数与元数据更新。
/// 行为与原 FrameService EF 路径一致；测试可注入内存 fake。
/// </summary>
public interface IFrameStructureStore
{
    Task<FrameStructureRow?> FindAsync(long frameId, CancellationToken ct = default);

    /// <summary>SLOT_STATE != Empty 的槽位数（含 Occupied/Locked/Reserved）。</summary>
    Task<int> CountNonEmptySlotsAsync(long frameId, CancellationToken ct = default);

    Task<int> CountActiveBindsAsync(long frameId, CancellationToken ct = default);

    /// <summary>写回料架元数据；RebuildEmptySlots=true 时删除旧槽并重建空槽。</summary>
    Task ApplyUpdateAsync(FrameStructureUpdate update, CancellationToken ct = default);

    Task SoftDeleteAsync(long frameId, string author, CancellationToken ct = default);
}

/// <summary>料架结构快照（改层/删架判定用）。</summary>
public sealed record FrameStructureRow(
    long Id,
    string Name,
    string? Code,
    string IdentifyCode,
    int LayerTotal,
    int SlotsPerLayer,
    string State);

/// <summary>料架更新载荷。</summary>
public sealed record FrameStructureUpdate(
    long Id,
    string Name,
    string Code,
    string IdentifyCode,
    string Author,
    int LayerTotal,
    int SlotsPerLayer,
    bool RebuildEmptySlots);
