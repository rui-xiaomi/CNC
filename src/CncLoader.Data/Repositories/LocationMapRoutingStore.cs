using CncLoader.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>LOCATION_MAP 原始读取：按键匹配全部行（含禁用），活动过滤留给 Service。</summary>
public sealed class LocationMapRoutingStore : ILocationMapRoutingStore
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public LocationMapRoutingStore(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<LocationMapRoutingRow>> FindByPositionAsync(
        long equipmentId, long? positionId, string rcsType, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.LocationMaps.AsNoTracking()
            .Where(x => x.RcsType == rcsType && x.EquipmentId == equipmentId
                        && (positionId == null ? x.PositionId == null : x.PositionId == positionId))
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<LocationMapRoutingRow>> FindByFrameAsync(
        long frameId, string rcsType, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.LocationMaps.AsNoTracking()
            .Where(x => x.RcsType == rcsType && x.FrameId == frameId)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<LocationMapRoutingRow>> FindByAreaAsync(
        string locName, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.LocationMaps.AsNoTracking()
            .Where(x => x.LocType == "AREA" && x.LocName == locName)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<LocationMapRoutingRow>> FindByRcsCodeAsync(
        string rcsCode, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // 精确匹配：不 Trim、不忽略大小写；含全部 STATE
        var rows = await db.LocationMaps.AsNoTracking()
            .Where(x => x.RcsCode == rcsCode)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    private static LocationMapRoutingRow Map(Entities.LocationMap e) => new(
        e.Id, e.LocType, e.EquipmentId, e.PositionId, e.FrameId,
        e.LocName, e.RcsCode, e.RcsType, e.State);
}
