using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>逻辑位置↔RCS点位编码映射（MAS_AUTO_LOCATION_MAP）。</summary>
public sealed class LocationMapService : ILocationMapService
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public LocationMapService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<LocationMapItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.LocationMaps.AsNoTracking()
            .Where(x => x.State == "0")
            .OrderBy(x => x.LocType).ThenBy(x => x.RcsCode)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<long> SaveAsync(LocationMapItem item, string? author = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        LocationMap entity;
        if (item.Id > 0)
        {
            entity = await db.LocationMaps.FirstOrDefaultAsync(x => x.Id == item.Id, ct)
                     ?? throw new InvalidOperationException($"位置映射不存在：{item.Id}");
        }
        else
        {
            entity = new LocationMap();
            db.LocationMaps.Add(entity);
        }

        entity.LocType = item.LocType;
        entity.EquipmentId = item.EquipmentId;
        entity.PositionId = item.PositionId;
        entity.FrameId = item.FrameId;
        entity.LocName = item.LocName;
        entity.RcsCode = item.RcsCode;
        entity.RcsType = item.RcsType;
        entity.Remark = item.Remark;
        entity.State = "0";
        if (author is not null) entity.Author = author;
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var e = await db.LocationMaps.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return;
        e.State = "1";
        await db.SaveChangesAsync(ct);
    }

    public async Task<LocationMapItem?> ResolvePositionAsync(long equipmentId, long? positionId, string rcsType, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var e = await db.LocationMaps.AsNoTracking()
            .Where(x => x.State == "0" && x.RcsType == rcsType && x.EquipmentId == equipmentId
                        && (positionId == null ? x.PositionId == null : x.PositionId == positionId))
            .FirstOrDefaultAsync(ct);
        return e is null ? null : Map(e);
    }

    public async Task<LocationMapItem?> ResolveFrameAsync(long frameId, string rcsType, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var e = await db.LocationMaps.AsNoTracking()
            .Where(x => x.State == "0" && x.RcsType == rcsType && x.FrameId == frameId)
            .FirstOrDefaultAsync(ct);
        return e is null ? null : Map(e);
    }

    public async Task<LocationMapItem?> ResolveAreaAsync(string locName, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var e = await db.LocationMaps.AsNoTracking()
            .Where(x => x.State == "0" && x.LocType == "AREA" && x.LocName == locName)
            .FirstOrDefaultAsync(ct);
        return e is null ? null : Map(e);
    }

    public async Task<LocationMapItem?> ResolveByRcsCodeAsync(string rcsCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rcsCode)) return null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var e = await db.LocationMaps.AsNoTracking()
            .Where(x => x.State == "0" && x.RcsCode == rcsCode)
            .FirstOrDefaultAsync(ct);
        return e is null ? null : Map(e);
    }

    private static LocationMapItem Map(LocationMap e) => new()
    {
        Id = e.Id,
        LocType = e.LocType,
        EquipmentId = e.EquipmentId,
        PositionId = e.PositionId,
        FrameId = e.FrameId,
        LocName = e.LocName,
        RcsCode = e.RcsCode,
        RcsType = e.RcsType,
        Remark = e.Remark
    };
}
