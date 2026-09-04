using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.Signals;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class PlcPointManagementService : IPlcPointManagementService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    private readonly IPlcPointSource _pointSource;

    public PlcPointManagementService(IDbContextFactory<CncDbContext> factory, IPlcPointSource pointSource)
    {
        _factory = factory;
        _pointSource = pointSource;
    }

    public async Task<IReadOnlyList<PlcPointRow>> GetByPlcAsync(long plcId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var points = await db.PlcPoints.AsNoTracking()
            .Where(p => p.PlcId == plcId && p.State == ConfigActivity.Active)
            .OrderBy(p => p.RegisterAddr)
            .ToListAsync(ct);
        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.State == ConfigActivity.Active)
            .ToDictionaryAsync(p => p.Id, p => p.PositionName, ct);

        return points.Select(p => ToRow(p, positions)).ToList();
    }

    public async Task SavePointAsync(PlcPointRow row, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        PlcPoint entity;
        if (row.Id > 0)
        {
            entity = await db.PlcPoints.FirstAsync(p => p.Id == row.Id, ct);
        }
        else
        {
            entity = new PlcPoint { State = "0" };
            db.PlcPoints.Add(entity);
        }

        entity.PlcId = row.PlcId;
        entity.EquipmentId = row.EquipmentId;
        entity.PositionId = row.PositionId;
        entity.SignalKey = row.SignalKey;
        entity.Rw = row.IsWrite ? "1" : "0";
        entity.RegisterAddr = row.RegisterAddress;
        entity.IoAddr = row.IoAddress;
        entity.OnValue = row.OnValue;
        entity.OffValue = row.OffValue;
        entity.DataLen = row.DataLength;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        _pointSource.Invalidate();
    }

    public async Task DeletePointAsync(long pointId, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.PlcPoints.FirstOrDefaultAsync(p => p.Id == pointId, ct);
        if (entity is null) return;
        entity.State = "1";
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        _pointSource.Invalidate();
    }

    public async Task<int> ImportSignalTableAsync(long equipmentId, long plcId, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var equipment = await db.Equipments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == equipmentId && e.State == ConfigActivity.Active, ct)
            ?? throw new InvalidOperationException($"机台#{equipmentId} 不存在");

        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.EquipmentId == equipmentId && p.State == ConfigActivity.Active)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        if (positions.Count < 2)
            throw new InvalidOperationException($"机台#{equipmentId} 缺少 2 个加工位");

        var index = equipmentId switch { 1 => 1, 2 => 2, 3 => 3, _ => 0 };
        if (index == 0)
            throw new InvalidOperationException("批量导入仅支持种子机台 EQ01/EQ02/EQ03（ID 1/2/3）");

        var existingKeys = await db.PlcPoints
            .Where(p => p.EquipmentId == equipmentId && p.State == ConfigActivity.Active)
            .Select(p => p.SignalKey + "|" + (p.PositionId ?? 0))
            .ToListAsync(ct);
        var existing = existingKeys.ToHashSet();

        var template = SignalTableTemplates.ForEquipmentIndex(index);
        var pos1 = positions[0].Id;
        var pos2 = positions[1].Id;
        var added = 0;

        foreach (var t in template)
        {
            long? posId = t.PositionSlot switch { 1 => pos1, 2 => pos2, _ => null };
            var key = SignalKeys.ToDbKey(t.Signal) + "|" + (posId ?? 0);
            if (existing.Contains(key)) continue;

            db.PlcPoints.Add(new PlcPoint
            {
                PlcId = plcId,
                EquipmentId = equipmentId,
                PositionId = posId,
                SignalKey = SignalKeys.ToDbKey(t.Signal),
                Rw = t.IsWrite ? "1" : "0",
                RegisterAddr = $"D{t.RegisterOffset}",
                IoAddr = t.IoAddress,
                OnValue = SignalConventions.DefaultOnValue,
                OffValue = SignalConventions.DefaultOffValue,
                DataLen = 1,
                State = "0",
                UpdateTime = DateTime.Now
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            _pointSource.Invalidate();
        }
        return added;
    }

    public async Task<IReadOnlyList<WriteSignalOption>> GetWriteSignalsAsync(long? plcId = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // 管理页写面板：全部活动点位均可下发，不按 Rw 互斥过滤。
        var query = db.PlcPoints.AsNoTracking().Where(p => p.State == ConfigActivity.Active);
        if (plcId.HasValue) query = query.Where(p => p.PlcId == plcId.Value);
        query = query.OrderBy(p => p.RegisterAddr);

        var points = await query.ToListAsync(ct);
        var equipments = await db.Equipments.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.EquipmentName, ct);
        var positions = await db.Positions.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.PositionName, ct);

        return points.Select(p =>
        {
            var eqName = equipments.GetValueOrDefault(p.EquipmentId, "?");
            var posName = p.PositionId is long pid ? positions.GetValueOrDefault(pid, "?") : "";
            var label = string.IsNullOrEmpty(posName)
                ? $"{eqName} · {SignalLabels.Get(p.SignalKey)} ({p.RegisterAddr})"
                : $"{eqName} · {posName} {SignalLabels.Get(p.SignalKey)} ({p.RegisterAddr})";
            return new WriteSignalOption(p.Id, label, p.RegisterAddr, p.PlcId, p.EquipmentId);
        }).ToList();
    }

    private static PlcPointRow ToRow(PlcPoint p, Dictionary<long, string> positions) => new()
    {
        Id = p.Id,
        PlcId = p.PlcId,
        EquipmentId = p.EquipmentId,
        PositionId = p.PositionId,
        PositionName = p.PositionId is long pid ? positions.GetValueOrDefault(pid) : null,
        SignalKey = p.SignalKey,
        SignalLabel = SignalLabels.Get(p.SignalKey),
        IsWrite = p.Rw == "1",
        RegisterAddress = p.RegisterAddr,
        IoAddress = p.IoAddr,
        OnValue = p.OnValue,
        OffValue = p.OffValue,
        DataLength = p.DataLen <= 0 ? 1 : p.DataLen
    };
}
