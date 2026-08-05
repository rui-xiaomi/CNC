using CncLoader.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>料架路由活动读取（权威 Frame 表 STATE）。</summary>
public sealed class FrameRoutingStore : IFrameRoutingStore
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public FrameRoutingStore(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<FrameRoutingSnapshot?> FindAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var f = await db.Frames.AsNoTracking()
            .Where(x => x.Id == frameId)
            .Select(x => new { x.Id, x.FrameCode, x.State })
            .FirstOrDefaultAsync(ct);
        return f is null
            ? null
            : new FrameRoutingSnapshot(f.Id, f.FrameCode, f.State);
    }
}
