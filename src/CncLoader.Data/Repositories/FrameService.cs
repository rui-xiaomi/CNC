using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class FrameService : IFrameService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    private readonly IFrameStructureStore _structure;

    public FrameService(IDbContextFactory<CncDbContext> factory, IFrameStructureStore structure)
    {
        _factory = factory;
        _structure = structure;
    }

    public async Task<IReadOnlyList<FrameListItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var frames = await db.Frames.AsNoTracking().Where(f => f.State == ConfigFlags.Active)
            .OrderBy(f => f.Id).ToListAsync(ct);
        var slots = await db.FrameSlots.AsNoTracking().ToListAsync(ct);
        var ngFrameIds = await db.FrameBinds.AsNoTracking()
            .Where(b => b.State == ConfigFlags.Active && b.FrameRole == "3")
            .Select(b => b.FrameId)
            .Distinct()
            .ToListAsync(ct);
        var ngSet = ngFrameIds.ToHashSet();

        return frames.Select(f => new FrameListItem(
            f.Id,
            f.FrameName,
            f.FrameIdentifyCode,
            $"{f.LayerTotal}×{f.SlotsPerLayer}",
            f.SlotTotal,
            slots.Count(s => s.FrameId == f.Id && s.SlotState == "1"),
            ngSet.Contains(f.Id))).ToList();
    }

    public async Task<FrameDetail?> GetDetailAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var frame = await db.Frames.AsNoTracking().FirstOrDefaultAsync(f => f.Id == frameId, ct);
        if (frame is null) return null;

        var slots = await db.FrameSlots.AsNoTracking()
            .Where(s => s.FrameId == frameId).OrderBy(s => s.SlotNo).ToListAsync(ct);
        var binds = await db.FrameBinds.AsNoTracking()
            .Where(b => b.FrameId == frameId && b.State == ConfigFlags.Active).ToListAsync(ct);
        var equipments = await db.Equipments.AsNoTracking().ToListAsync(ct);

        var bindRows = binds.OrderBy(b => b.EquipmentId).ThenBy(b => b.FrameRole).Select(b =>
        {
            var eq = equipments.FirstOrDefault(e => e.Id == b.EquipmentId);
            return new FrameBindRow(
                b.Id,
                b.EquipmentId,
                eq is null ? "—" : $"{eq.EquipmentName} ({eq.EquipmentNo})",
                b.FrameRole == "0",
                b.FrameRole,
                ConfigFlags.FrameRoleText(b.FrameRole));
        }).ToList();

        var slotItems = slots.Select(s => new SlotItem(
            s.SlotNo,
            s.LayerNo,
            s.PosInLayer,
            $"{s.LayerNo}层{s.PosInLayer}位",
            s.SlotState == "1" || s.SlotState == "3" ? s.MaterialId : null,
            s.SlotState,
            s.SlotState == "1")).ToList();

        return new FrameDetail(
            frame.Id,
            frame.FrameName,
            frame.FrameIdentifyCode,
            frame.LayerTotal,
            frame.SlotsPerLayer,
            frame.SlotTotal,
            slotItems.Count(s => s.Occupied),
            bindRows,
            slotItems);
    }

    public async Task<long> CreateFrameAsync(FrameCreateModel model, string author, CancellationToken ct = default)
    {
        var layers = Math.Max(1, model.LayerTotal);
        var perLayer = Math.Max(1, model.SlotsPerLayer);
        var total = layers * perLayer;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var frame = new Frame
        {
            FrameName = model.Name,
            FrameCode = model.Code,
            FrameIdentifyCode = model.IdentifyCode,
            LayerTotal = layers,
            SlotsPerLayer = perLayer,
            SlotTotal = total,
            State = ConfigFlags.Active,
            Author = author,
            UpdateTime = DateTime.Now
        };
        db.Frames.Add(frame);
        await db.SaveChangesAsync(ct); // 取回料架主键

        // 按 层×每层数 预建空槽位（SLOT_STATE='0'）
        for (var slotNo = 1; slotNo <= total; slotNo++)
        {
            db.FrameSlots.Add(new FrameSlot
            {
                FrameId = frame.Id,
                SlotNo = slotNo,
                LayerNo = (slotNo - 1) / perLayer + 1,
                PosInLayer = (slotNo - 1) % perLayer + 1,
                SlotState = "0",
                UpdateTime = DateTime.Now
            });
        }
        await db.SaveChangesAsync(ct);
        return frame.Id;
    }

    public async Task<IReadOnlyList<NamedOption>> GetEquipmentOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var eqs = await db.Equipments.AsNoTracking().Where(e => e.State == ConfigFlags.Active)
            .OrderBy(e => e.Id).ToListAsync(ct);
        return eqs.Select(e => new NamedOption(e.Id, $"{e.EquipmentName} ({e.EquipmentNo})")).ToList();
    }

    public async Task BindEquipmentAsync(long frameId, long equipmentId, string roleCode, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // 一机一角色一料架：先清该机台该角色的旧绑定（任意料架），再绑本料架，避免同机台同角色多料架冲突。
        var existing = await db.FrameBinds
            .Where(b => b.EquipmentId == equipmentId && b.FrameRole == roleCode && b.State == ConfigFlags.Active)
            .ToListAsync(ct);
        db.FrameBinds.RemoveRange(existing);
        db.FrameBinds.Add(new FrameBind
        {
            FrameId = frameId, EquipmentId = equipmentId, FrameRole = roleCode,
            State = ConfigFlags.Active, Author = author, UpdateTime = DateTime.Now
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task UnbindAsync(long bindId, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var bind = await db.FrameBinds.FirstOrDefaultAsync(b => b.Id == bindId, ct);
        if (bind is null) return;
        db.FrameBinds.Remove(bind);
        await db.SaveChangesAsync(ct);
    }

    public async Task<FrameEditModel?> GetFrameForEditAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var f = await db.Frames.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return null;
        return new FrameEditModel
        {
            Id = f.Id, Name = f.FrameName, Code = f.FrameCode, IdentifyCode = f.FrameIdentifyCode,
            LayerTotal = f.LayerTotal, SlotsPerLayer = f.SlotsPerLayer
        };
    }

    public async Task UpdateFrameAsync(FrameEditModel model, string author, CancellationToken ct = default)
    {
        var layers = Math.Max(1, model.LayerTotal);
        var perLayer = Math.Max(1, model.SlotsPerLayer);

        var current = await _structure.FindAsync(model.Id, ct)
            ?? throw new InvalidOperationException($"料架 {model.Id} 不存在");

        var layoutChanged = current.LayerTotal != layers || current.SlotsPerLayer != perLayer;
        if (layoutChanged)
        {
            // 改层数/每层槽数 → 重建空槽：先确认无占用/预记/锁定（非空）槽位，否则拒绝（避免丢账）
            var nonEmpty = await _structure.CountNonEmptySlotsAsync(model.Id, ct);
            if (nonEmpty > 0)
                throw new InvalidOperationException($"料架仍有 {nonEmpty} 个占用/预记/锁定槽位，请先清空再改层数或每层槽数。");
        }

        await _structure.ApplyUpdateAsync(new FrameStructureUpdate(
            model.Id,
            model.Name,
            string.IsNullOrWhiteSpace(model.Code) ? model.IdentifyCode : model.Code,
            model.IdentifyCode,
            author,
            layers,
            perLayer,
            RebuildEmptySlots: layoutChanged), ct);
    }

    public async Task<DeleteCheckResult> CheckDeleteFrameAsync(long id, CancellationToken ct = default)
    {
        var binds = await _structure.CountActiveBindsAsync(id, ct);
        var occupied = await _structure.CountNonEmptySlotsAsync(id, ct);
        if (binds > 0)
            return new DeleteCheckResult(false, binds, $"该料架已被 {binds} 台机台绑定，请先在绑定关系里解绑再删除。");
        if (occupied > 0)
            return new DeleteCheckResult(false, occupied, $"该料架仍有 {occupied} 个占用/预记槽位，请先清空再删除。");
        return new DeleteCheckResult(true, 0, "可删除");
    }

    public async Task DeleteFrameAsync(long id, string author, CancellationToken ct = default)
    {
        var check = await CheckDeleteFrameAsync(id, ct);
        if (!check.CanDelete) throw new InvalidOperationException(check.Message);
        await _structure.SoftDeleteAsync(id, author, ct);
    }
}
