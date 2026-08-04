using CncLoader.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 调度路由配置读取：原样返回行（含 STATE），不做活动过滤——与改造前 GetWorkLine/绑定查库语义对齐（不过滤）。
/// </summary>
public sealed class EquipmentRoutingStore : IEquipmentRoutingStore
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public EquipmentRoutingStore(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<EquipmentRoutingRow?> FindEquipmentAsync(long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var e = await db.Equipments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == equipmentId, ct);
        return e is null ? null : new EquipmentRoutingRow(e.Id, e.CraftworkId, e.State);
    }

    public async Task<CraftworkRoutingRow?> FindCraftworkAsync(long craftworkId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var c = await db.Craftworks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == craftworkId, ct);
        return c is null ? null : new CraftworkRoutingRow(c.Id, c.WorkLineId, c.CraftworkNode, c.State);
    }

    public async Task<WorkLineRoutingRow?> FindWorkLineAsync(long workLineId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var l = await db.WorkLines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workLineId, ct);
        return l is null ? null : new WorkLineRoutingRow(l.Id, l.WorkLineCode, l.State);
    }

    public async Task<IReadOnlyList<CraftworkRoutingRow>> FindCraftworksByWorkLineAsync(
        long workLineId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Craftworks.AsNoTracking()
            .Where(c => c.WorkLineId == workLineId)
            .ToListAsync(ct);
        return rows.Select(c => new CraftworkRoutingRow(c.Id, c.WorkLineId, c.CraftworkNode, c.State)).ToList();
    }

    public async Task<IReadOnlyList<EquipmentRoutingRow>> FindEquipmentsByCraftworkIdsAsync(
        IReadOnlyCollection<long> craftworkIds, CancellationToken ct = default)
    {
        if (craftworkIds.Count == 0) return Array.Empty<EquipmentRoutingRow>();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var idSet = craftworkIds as HashSet<long> ?? craftworkIds.ToHashSet();
        var rows = await db.Equipments.AsNoTracking()
            .Where(e => idSet.Contains(e.CraftworkId))
            .ToListAsync(ct);
        return rows.Select(e => new EquipmentRoutingRow(e.Id, e.CraftworkId, e.State)).ToList();
    }

    public async Task<IReadOnlyList<FrameBindRoutingRow>> FindFrameBindsByEquipmentAsync(
        long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.FrameBinds.AsNoTracking()
            .Where(b => b.EquipmentId == equipmentId)
            .ToListAsync(ct);
        return rows.Select(b => new FrameBindRoutingRow(
            b.Id, b.FrameId, b.EquipmentId, b.FrameRole ?? "", b.State)).ToList();
    }
}
