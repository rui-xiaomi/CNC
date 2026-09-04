using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
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
        var plcs = await db.Plcs.AsNoTracking().Where(p => p.State == ConfigActivity.Active).OrderBy(p => p.PlcId).ToListAsync(ct);
        var equipments = await db.Equipments.AsNoTracking().Where(e => e.State == ConfigActivity.Active).ToListAsync(ct);
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
        var p = await db.Plcs.AsNoTracking().FirstOrDefaultAsync(x => x.PlcId == plcId && x.State == ConfigActivity.Active, ct);
        return p is null ? null : new PlcEditModel(p.PlcId, p.PlcName ?? "", p.PlcComputerIp, p.PlcComputerPort ?? 502, p.PlcReadWay ?? "ModbusTCP");
    }

    public async Task SaveAsync(PlcEditModel model, string author, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            throw new ArgumentException("PLC 名称必填。", nameof(model));
        if (string.IsNullOrWhiteSpace(model.Ip))
            throw new ArgumentException("PLC IP 必填。", nameof(model));
        if (model.Port <= 0 || model.Port > 65535)
            throw new ArgumentException("PLC 端口范围 1..65535。", nameof(model));

        await using var db = await _factory.CreateDbContextAsync(ct);

        if (model.IsNew)
        {
            // 新增：PlcId 唯一性校验（含已软删的行，避免冲突）。
            var dup = await db.Plcs.AnyAsync(x => x.PlcId == model.PlcId, ct);
            if (dup)
                throw new InvalidOperationException($"PLC_ID={model.PlcId} 已存在，请改用建议值。");

            var entity = new WorkLinePlc
            {
                PlcId = model.PlcId,
                PlcName = model.Name,
                PlcConnectType = "客户端",
                PlcComputerIp = model.Ip,
                PlcComputerPort = model.Port,
                PlcReadWay = model.Protocol,
                State = "0",
                Author = author,
                UpdateTime = DateTime.Now
            };
            db.Plcs.Add(entity);
        }
        else
        {
            var entity = await db.Plcs.FirstOrDefaultAsync(x => x.PlcId == model.PlcId && x.State == ConfigActivity.Active, ct)
                ?? throw new InvalidOperationException($"PLC_ID={model.PlcId} 不存在或已删除。");
            // PlcId 不改（业务键，被机台/点位外键引用）。
            entity.PlcName = model.Name;
            entity.PlcComputerIp = model.Ip;
            entity.PlcComputerPort = model.Port;
            entity.PlcReadWay = model.Protocol;
            entity.Author = author;
            entity.UpdateTime = DateTime.Now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<long> SuggestNextPlcIdAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var hasAny = await db.Plcs.AnyAsync(ct);
        if (!hasAny) return 1;
        return await db.Plcs.AsNoTracking().MaxAsync(p => p.PlcId, ct) + 1;
    }

    public async Task<PlcDeleteCheckResult> CheckDeleteAsync(long plcId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await CheckDeleteCoreAsync(db, plcId, ct);
    }

    private static async Task<PlcDeleteCheckResult> CheckDeleteCoreAsync(
        CncDbContext db, long plcId, CancellationToken ct)
    {
        var eqRefs = await db.Equipments.AsNoTracking()
            .CountAsync(e => e.PlcId == plcId && e.State == ConfigActivity.Active, ct);
        var ptRefs = await db.PlcPoints.AsNoTracking()
            .CountAsync(p => p.PlcId == plcId && p.State == ConfigActivity.Active, ct);

        if (eqRefs == 0 && ptRefs == 0)
            return new PlcDeleteCheckResult(true, 0, 0, "可删除");

        var msg = eqRefs > 0 && ptRefs > 0
            ? $"被 {eqRefs} 台机台、{ptRefs} 个点位引用，禁止删除"
            : eqRefs > 0
                ? $"被 {eqRefs} 台机台引用，禁止删除"
                : $"被 {ptRefs} 个点位引用，禁止删除";
        return new PlcDeleteCheckResult(false, eqRefs, ptRefs, msg);
    }

    public Task DeleteAsync(long plcId, string author, CancellationToken ct = default)
        => ConfigSoftDelete.RunAsync(_factory, async (db, token) =>
        {
            var check = await CheckDeleteCoreAsync(db, plcId, token);
            if (!check.CanDelete)
                throw new InvalidOperationException(check.Message);

            var entity = await db.Plcs.FirstOrDefaultAsync(x => x.PlcId == plcId && x.State == ConfigActivity.Active, token)
                ?? throw new InvalidOperationException($"PLC_ID={plcId} 不存在或已删除。");
            entity.State = ConfigActivity.Disabled;
            entity.Author = author;
            entity.UpdateTime = DateTime.Now;
        }, ct);

    public async Task BindEquipmentAsync(long plcId, long? equipmentId, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // 清所有原绑到本 PLC 的机台（一机一 PLC，保证 PLC 侧唯一绑定）
        var prevBound = await db.Equipments.Where(e => e.PlcId == plcId && e.State == ConfigActivity.Active).ToListAsync(ct);
        foreach (var e in prevBound) { e.PlcId = null; e.Author = author; e.UpdateTime = DateTime.Now; }

        if (equipmentId is > 0)
        {
            var target = await db.Equipments.FirstOrDefaultAsync(e => e.Id == equipmentId && e.State == ConfigActivity.Active, ct)
                ?? throw new InvalidOperationException("目标机台不存在或已删除。");
            target.PlcId = plcId;
            target.Author = author;
            target.UpdateTime = DateTime.Now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<EquipmentOption>> GetEquipmentsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var list = await db.Equipments.AsNoTracking()
            .Where(e => e.State == ConfigActivity.Active && e.PlcId != null)
            .OrderBy(e => e.EquipmentNo)
            .ToListAsync(ct);
        return list.Select(e => new EquipmentOption(e.Id, $"{e.EquipmentName} ({e.EquipmentNo})", e.PlcId!.Value)).ToList();
    }
}
