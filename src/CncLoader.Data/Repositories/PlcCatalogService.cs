using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Data;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class PlcCatalogService : IPlcCatalogService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    private readonly Func<IPlcConnectionService> _connections;

    public PlcCatalogService(IDbContextFactory<CncDbContext> factory, Func<IPlcConnectionService> connections)
    {
        _factory = factory;
        _connections = connections;
    }

    public async Task<IReadOnlyList<PlcListItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var plcs = await db.Plcs.AsNoTracking().Where(p => p.State == "0").OrderBy(p => p.PlcId).ToListAsync(ct);
        var equipments = await db.Equipments.AsNoTracking().Where(e => e.State == "0").ToListAsync(ct);
        var states = _connections().GetLinkStates();

        return plcs.Select(p =>
        {
            var eq = equipments.FirstOrDefault(e => e.PlcId == p.PlcId);
            states.TryGetValue(p.PlcId, out var link);
            return new PlcListItem(
                p.PlcId,
                p.PlcName ?? $"PLC-{p.PlcId}",
                eq?.EquipmentName,
                eq?.EquipmentNo,
                p.PlcComputerIp,
                p.PlcComputerPort ?? 502,
                p.PlcReadWay ?? "ModbusTCP",
                link,
                _connections().IsConnected(p.PlcId));
        }).ToList();
    }

    public async Task<PlcEditModel?> GetByIdAsync(long plcId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var p = await db.Plcs.AsNoTracking().FirstOrDefaultAsync(x => x.PlcId == plcId && x.State == "0", ct);
        return p is null ? null : new PlcEditModel(p.PlcId, p.PlcName ?? "", p.PlcComputerIp, p.PlcComputerPort ?? 502, p.PlcReadWay ?? "ModbusTCP");
    }

    public async Task SaveAsync(PlcEditModel model, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Plcs.FirstOrDefaultAsync(x => x.PlcId == model.PlcId, ct);
        if (entity is null)
        {
            entity = new WorkLinePlc { PlcId = model.PlcId, State = "0" };
            db.Plcs.Add(entity);
        }
        entity.PlcName = model.Name;
        entity.PlcComputerIp = model.Ip;
        entity.PlcComputerPort = model.Port;
        entity.PlcReadWay = model.Protocol;
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EquipmentOption>> GetEquipmentsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var list = await db.Equipments.AsNoTracking()
            .Where(e => e.State == "0" && e.PlcId != null)
            .OrderBy(e => e.EquipmentNo)
            .ToListAsync(ct);
        return list.Select(e => new EquipmentOption(e.Id, $"{e.EquipmentName} ({e.EquipmentNo})", e.PlcId!.Value)).ToList();
    }
}
