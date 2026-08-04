using CncLoader.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>PLC 点位原始读取：返回匹配行（含禁用），活动过滤留给 PlcPointSource。</summary>
public sealed class PlcPointRoutingStore : IPlcPointRoutingStore
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public PlcPointRoutingStore(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<PlcPointRoutingRow>> FindAsync(
        long? plcId, long? equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.PlcPoints.AsNoTracking().AsQueryable();
        if (plcId.HasValue) query = query.Where(p => p.PlcId == plcId.Value);
        if (equipmentId.HasValue) query = query.Where(p => p.EquipmentId == equipmentId.Value);
        var rows = await query.ToListAsync(ct);
        return rows.Select(p => new PlcPointRoutingRow(
            p.Id, p.PlcId, p.EquipmentId, p.PositionId, p.SignalKey, p.Rw,
            p.RegisterAddr, p.IoAddr, p.OnValue, p.OffValue, p.DataLen, p.State)).ToList();
    }
}
