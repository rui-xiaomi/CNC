namespace CncLoader.Core.Abstractions;

/// <summary>
/// 料架路由活动快照接缝：只读 Frame.STATE，供受管端点 Final/Resolve 使用。
/// </summary>
public interface IFrameRoutingStore
{
    Task<FrameRoutingSnapshot?> FindAsync(long frameId, CancellationToken ct = default);
}

/// <summary>料架路由快照（STATE 原样；无 EF Entity）。</summary>
public sealed record FrameRoutingSnapshot(
    long Id,
    string? Code,
    string State);
