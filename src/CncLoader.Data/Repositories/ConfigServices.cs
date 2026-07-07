using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

// Phase 3 配置管理：线体/工序/机台/料架 的读取与（线体/工序）回写。
// 全部经 IDbContextFactory 短连接，State=="0" 视为启用/有效。

internal static class ConfigFlags
{
    public const string Active = "0";
    public const string Disabled = "1";
    public static bool IsEnabled(string state) => state == Active;
    public static string ToState(bool enabled) => enabled ? Active : Disabled;
    public static bool IsTrue(string? flag) => flag == "1";
    public static string ToFlag(bool value) => value ? "1" : "0";
}

public sealed class WorkLineService : IWorkLineService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    public WorkLineService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<WorkLineListItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var lines = await db.WorkLines.AsNoTracking()
            .Where(l => l.State == ConfigFlags.Active)
            .OrderBy(l => l.Id).ToListAsync(ct);
        return lines.Select(l => new WorkLineListItem(
            l.Id,
            l.WorkLineCode,
            l.WorkMachineLine,
            ConfigFlags.IsTrue(l.ScanState),
            l.AgvId > 0 ? $"AGV-{l.AgvId}" : "无",
            ConfigFlags.IsEnabled(l.State))).ToList();
    }

    public async Task<WorkLineEditModel?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var l = await db.WorkLines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? null : new WorkLineEditModel
        {
            Id = l.Id,
            Name = l.WorkMachineLine,
            Code = l.WorkLineCode,
            Computer = l.WorkLineComputer,
            ComputerIp = l.WorkLineComputerIp,
            PlanNum = l.PlanWorkNum,
            ScanEnabled = ConfigFlags.IsTrue(l.ScanState),
            Enabled = ConfigFlags.IsEnabled(l.State)
        };
    }

    public async Task<long> SaveAsync(WorkLineEditModel model, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = model.Id > 0
            ? await db.WorkLines.FirstOrDefaultAsync(x => x.Id == model.Id, ct)
            : null;
        if (entity is null)
        {
            entity = new WorkLineConfig();
            db.WorkLines.Add(entity);
        }
        entity.WorkMachineLine = model.Name;
        entity.WorkLineCode = model.Code;
        entity.WorkLineComputer = model.Computer;
        entity.WorkLineComputerIp = model.ComputerIp;
        entity.PlanWorkNum = model.PlanNum;
        entity.ScanState = ConfigFlags.ToFlag(model.ScanEnabled);
        entity.State = ConfigFlags.ToState(model.Enabled);
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var refs = await db.Craftworks.AsNoTracking()
            .CountAsync(c => c.WorkLineId == id && c.State == ConfigFlags.Active, ct);
        return refs == 0
            ? new DeleteCheckResult(true, 0, "可删除")
            : new DeleteCheckResult(false, refs, $"被 {refs} 道工序引用，禁止删除");
    }

    public async Task DeleteAsync(long id, string author, CancellationToken ct = default)
    {
        var check = await CheckDeleteAsync(id, ct);
        if (!check.CanDelete) throw new InvalidOperationException(check.Message);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.WorkLines.FirstOrDefaultAsync(x => x.Id == id && x.State == ConfigFlags.Active, ct)
            ?? throw new InvalidOperationException("线体不存在或已删除。");
        entity.State = ConfigFlags.Disabled;
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class CraftworkService : ICraftworkService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    public CraftworkService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<NamedOption>> GetWorkLineOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var lines = await db.WorkLines.AsNoTracking()
            .Where(l => l.State == ConfigFlags.Active)
            .OrderBy(l => l.Id).ToListAsync(ct);
        return lines.Select(l => new NamedOption(l.Id, $"{l.WorkMachineLine} ({l.WorkLineCode})")).ToList();
    }

    public async Task<IReadOnlyList<CraftworkListItem>> GetByLineAsync(long? workLineId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.Craftworks.AsNoTracking().Where(c => c.State == ConfigFlags.Active);
        if (workLineId is > 0) query = query.Where(c => c.WorkLineId == workLineId);
        var crafts = await query.OrderBy(c => c.CraftworkNode).ThenBy(c => c.Id).ToListAsync(ct);
        var lines = await db.WorkLines.AsNoTracking().Where(l => l.State == ConfigFlags.Active).ToListAsync(ct);

        return crafts.Select(c =>
        {
            var line = lines.FirstOrDefault(l => l.Id == c.WorkLineId);
            var quality = ConfigFlags.IsTrue(c.SfQuality);
            return new CraftworkListItem(
                c.Id,
                c.CraftworkNode ?? 0,
                c.CraftworkNo,
                c.CraftworkName,
                quality,
                quality ? "品质检测" : "加工",
                line?.WorkMachineLine ?? "—",
                ConfigFlags.IsTrue(c.SfAutoSend),
                c.CraftworkPrior ?? 0,
                ConfigFlags.IsEnabled(c.State));
        }).ToList();
    }

    public async Task<CraftworkEditModel?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var c = await db.Craftworks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? null : new CraftworkEditModel
        {
            Id = c.Id,
            WorkLineId = c.WorkLineId,
            No = c.CraftworkNo,
            Name = c.CraftworkName,
            IsQuality = ConfigFlags.IsTrue(c.SfQuality),
            Sort = c.CraftworkNode ?? 0,
            Prior = c.CraftworkPrior ?? 0,
            AutoSend = ConfigFlags.IsTrue(c.SfAutoSend),
            Enabled = ConfigFlags.IsEnabled(c.State)
        };
    }

    public async Task<long> SaveAsync(CraftworkEditModel model, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = model.Id > 0
            ? await db.Craftworks.FirstOrDefaultAsync(x => x.Id == model.Id, ct)
            : null;
        if (entity is null)
        {
            entity = new Craftwork();
            db.Craftworks.Add(entity);
        }
        entity.WorkLineId = model.WorkLineId;
        entity.CraftworkNo = model.No;
        entity.CraftworkName = model.Name;
        entity.SfQuality = ConfigFlags.ToFlag(model.IsQuality);
        entity.CraftworkNode = model.Sort;
        entity.CraftworkPrior = model.Prior;
        entity.SfAutoSend = ConfigFlags.ToFlag(model.AutoSend);
        entity.State = ConfigFlags.ToState(model.Enabled);
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var refs = await db.Equipments.AsNoTracking()
            .CountAsync(e => e.CraftworkId == id && e.State == ConfigFlags.Active, ct);
        return refs == 0
            ? new DeleteCheckResult(true, 0, "可删除")
            : new DeleteCheckResult(false, refs, $"被 {refs} 台机台引用，禁止删除");
    }

    public async Task DeleteAsync(long id, string author, CancellationToken ct = default)
    {
        var check = await CheckDeleteAsync(id, ct);
        if (!check.CanDelete) throw new InvalidOperationException(check.Message);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Craftworks.FirstOrDefaultAsync(x => x.Id == id && x.State == ConfigFlags.Active, ct)
            ?? throw new InvalidOperationException("工序不存在或已删除。");
        entity.State = ConfigFlags.Disabled;
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class EquipmentConfigService : IEquipmentConfigService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    public EquipmentConfigService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<NamedOption>> GetCraftworkOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var crafts = await db.Craftworks.AsNoTracking()
            .Where(c => c.State == ConfigFlags.Active)
            .OrderBy(c => c.Id).ToListAsync(ct);
        return crafts.Select(c => new NamedOption(c.Id, $"{c.CraftworkName} ({c.CraftworkNo})")).ToList();
    }

    public async Task<IReadOnlyList<EquipmentListItem>> GetByCraftAsync(long? craftworkId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.Equipments.AsNoTracking().Where(e => e.State == ConfigFlags.Active);
        if (craftworkId is > 0) query = query.Where(e => e.CraftworkId == craftworkId);
        var equipments = await query.OrderBy(e => e.EquipmentNo).ToListAsync(ct);
        var crafts = await db.Craftworks.AsNoTracking().Where(c => c.State == ConfigFlags.Active).ToListAsync(ct);
        var plcs = await db.Plcs.AsNoTracking().Where(p => p.State == ConfigFlags.Active).ToListAsync(ct);

        return equipments.Select(e =>
        {
            var craft = crafts.FirstOrDefault(c => c.Id == e.CraftworkId);
            var plc = plcs.FirstOrDefault(p => p.PlcId == e.PlcId);
            var plcText = plc is null
                ? "未绑定"
                : $"{plc.PlcName ?? $"PLC-{plc.PlcId}"} · .{plc.PlcComputerIp.Split('.').LastOrDefault()}";
            return new EquipmentListItem(
                e.Id,
                e.EquipmentNo,
                e.EquipmentName,
                e.EquipmentCode,
                string.IsNullOrEmpty(e.EquipmentType) ? "—" : e.EquipmentType,
                craft?.CraftworkName ?? "—",
                plcText,
                ConfigFlags.IsEnabled(e.State));
        }).ToList();
    }

    public async Task<WorkLineRef?> GetWorkLineByEquipmentAsync(long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var eq = await db.Equipments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == equipmentId, ct);
        if (eq is null) return null;
        var craft = await db.Craftworks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == eq.CraftworkId, ct);
        if (craft is null) return null;
        var line = await db.WorkLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == craft.WorkLineId, ct);
        if (line is null) return null;
        return new WorkLineRef(line.Id, line.WorkLineCode);
    }

    public async Task<IReadOnlyList<PositionItem>> GetPositionsAsync(long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.EquipmentId == equipmentId && p.State == ConfigFlags.Active)
            .OrderBy(p => p.PositionCode).ToListAsync(ct);
        return positions.Select(p => new PositionItem(
            p.PositionName,
            p.PositionCode,
            p.PositionWorkState == "0" ? "空闲可用" : p.PositionWorkState)).ToList();
    }

    public async Task<IReadOnlyList<EquipmentFrameBinding>> GetFrameBindingsAsync(long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var binds = await db.FrameBinds.AsNoTracking()
            .Where(b => b.EquipmentId == equipmentId && b.State == ConfigFlags.Active).ToListAsync(ct);
        var frames = await db.Frames.AsNoTracking().ToListAsync(ct);

        // 上料架（FRAME_ROLE='0'）在前，下料架（'1'）在后
        return binds.OrderBy(b => b.FrameRole).Select(b =>
        {
            var frame = frames.FirstOrDefault(f => f.Id == b.FrameId);
            var isUpload = b.FrameRole == "0";
            return new EquipmentFrameBinding(
                isUpload,
                isUpload ? "上料架" : "下料架",
                frame is null ? "—" : $"{frame.FrameName} ({frame.FrameIdentifyCode})");
        }).ToList();
    }

    public async Task<IReadOnlyList<NamedOption>> GetPlcOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var plcs = await db.Plcs.AsNoTracking().Where(p => p.State == ConfigFlags.Active)
            .OrderBy(p => p.PlcId).ToListAsync(ct);
        return plcs.Select(p => new NamedOption(
            p.PlcId, $"{p.PlcName ?? $"PLC-{p.PlcId}"} ({p.PlcComputerIp})")).ToList();
    }

    public async Task<IReadOnlyList<NamedOption>> GetFrameOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var frames = await db.Frames.AsNoTracking().Where(f => f.State == ConfigFlags.Active)
            .OrderBy(f => f.Id).ToListAsync(ct);
        return frames.Select(f => new NamedOption(f.Id, $"{f.FrameName} ({f.FrameIdentifyCode})")).ToList();
    }

    public async Task<string> SuggestNextNoAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var nos = await db.Equipments.AsNoTracking().Select(e => e.EquipmentNo).ToListAsync(ct);
        var max = 0;
        foreach (var no in nos)
            if (no.StartsWith("EQ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(no.AsSpan(2), out var n) && n > max) max = n;
        return $"EQ{max + 1:D2}";
    }

    public async Task<long> CreateEquipmentAsync(EquipmentCreateModel model, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var eq = new Equipment
        {
            CraftworkId = model.CraftworkId,
            PlcId = model.PlcId > 0 ? model.PlcId : null,
            EquipmentNo = model.No,
            EquipmentName = model.Name,
            EquipmentCode = model.Code,
            EquipmentType = model.Type,
            EquipmentTypeName = $"{model.Name}测试机",
            EquipmentWorkType = "产品",
            State = ConfigFlags.Active,
            Author = author,
            UpdateTime = DateTime.Now
        };
        db.Equipments.Add(eq);
        await db.SaveChangesAsync(ct); // 取回机台主键

        // 自动建 2 个加工位
        for (var i = 1; i <= 2; i++)
        {
            db.Positions.Add(new EquipmentPosition
            {
                EquipmentId = eq.Id,
                PositionName = $"工位{i}",
                PositionCode = $"{model.No}-P{i}",
                PositionWorkState = "0",
                State = ConfigFlags.Active,
                Author = author,
                UpdateTime = DateTime.Now
            });
        }
        await db.SaveChangesAsync(ct);
        return eq.Id;
    }

    public async Task<EquipmentEditModel?> GetByIdAsync(long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var e = await db.Equipments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == equipmentId && x.State == ConfigFlags.Active, ct);
        if (e is null) return null;
        return new EquipmentEditModel
        {
            Id = e.Id,
            CraftworkId = e.CraftworkId,
            No = e.EquipmentNo,
            Name = e.EquipmentName,
            Code = e.EquipmentCode,
            Type = string.IsNullOrEmpty(e.EquipmentType) ? "检测" : e.EquipmentType,
            PlcId = e.PlcId ?? 0
        };
    }

    public async Task UpdateAsync(EquipmentEditModel model, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Equipments.FirstOrDefaultAsync(x => x.Id == model.Id && x.State == ConfigFlags.Active, ct)
            ?? throw new InvalidOperationException("机台不存在或已删除。");
        entity.CraftworkId = model.CraftworkId;
        // EquipmentNo 为业务编号，编辑不改（避免下游加工位编码、点位映射引用断裂）
        entity.EquipmentName = model.Name;
        entity.EquipmentCode = model.Code;
        entity.EquipmentType = model.Type;
        entity.EquipmentTypeName = $"{model.Name}测试机";
        entity.PlcId = model.PlcId > 0 ? model.PlcId : null;
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<EquipmentFrameBindingIds> GetFrameBindingIdsAsync(long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var binds = await db.FrameBinds.AsNoTracking()
            .Where(b => b.EquipmentId == equipmentId && b.State == ConfigFlags.Active).ToListAsync(ct);
        return new EquipmentFrameBindingIds(
            binds.FirstOrDefault(b => b.FrameRole == "0")?.FrameId,
            binds.FirstOrDefault(b => b.FrameRole == "1")?.FrameId);
    }

    public async Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId,
        string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // 仅替换本机台的绑定，不影响一架两用中其他机台的绑定
        var existing = await db.FrameBinds.Where(b => b.EquipmentId == equipmentId).ToListAsync(ct);
        db.FrameBinds.RemoveRange(existing);

        if (uploadFrameId is > 0)
            db.FrameBinds.Add(new FrameBind { FrameId = uploadFrameId.Value, EquipmentId = equipmentId, FrameRole = "0", State = ConfigFlags.Active, Author = author, UpdateTime = DateTime.Now });
        if (downloadFrameId is > 0)
            db.FrameBinds.Add(new FrameBind { FrameId = downloadFrameId.Value, EquipmentId = equipmentId, FrameRole = "1", State = ConfigFlags.Active, Author = author, UpdateTime = DateTime.Now });

        await db.SaveChangesAsync(ct);
    }

    public async Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var ptRefs = await db.PlcPoints.AsNoTracking()
            .CountAsync(p => p.EquipmentId == equipmentId && p.State == ConfigFlags.Active, ct);
        var fbRefs = await db.FrameBinds.AsNoTracking()
            .CountAsync(b => b.EquipmentId == equipmentId && b.State == ConfigFlags.Active, ct);
        if (ptRefs == 0 && fbRefs == 0)
            return new DeleteCheckResult(true, 0, "可删除");
        var parts = new List<string>();
        if (ptRefs > 0) parts.Add($"{ptRefs} 个点位映射");
        if (fbRefs > 0) parts.Add($"{fbRefs} 条料架绑定");
        return new DeleteCheckResult(false, ptRefs + fbRefs, $"被 {string.Join("、", parts)} 引用，禁止删除");
    }

    public async Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default)
    {
        var check = await CheckDeleteAsync(equipmentId, ct);
        if (!check.CanDelete) throw new InvalidOperationException(check.Message);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.Equipments.FirstOrDefaultAsync(x => x.Id == equipmentId && x.State == ConfigFlags.Active, ct)
            ?? throw new InvalidOperationException("机台不存在或已删除。");
        entity.State = ConfigFlags.Disabled;
        entity.Author = author;
        entity.UpdateTime = DateTime.Now;

        // 级联软删其加工位
        var positions = await db.Positions.Where(p => p.EquipmentId == equipmentId && p.State == ConfigFlags.Active).ToListAsync(ct);
        foreach (var p in positions)
        {
            p.State = ConfigFlags.Disabled;
            p.Author = author;
            p.UpdateTime = DateTime.Now;
        }
        await db.SaveChangesAsync(ct);
    }
}

public sealed class FrameService : IFrameService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    public FrameService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<IReadOnlyList<FrameListItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var frames = await db.Frames.AsNoTracking().Where(f => f.State == ConfigFlags.Active)
            .OrderBy(f => f.Id).ToListAsync(ct);
        var slots = await db.FrameSlots.AsNoTracking().ToListAsync(ct);

        return frames.Select(f => new FrameListItem(
            f.Id,
            f.FrameName,
            f.FrameIdentifyCode,
            $"{f.LayerTotal}×{f.SlotsPerLayer}",
            f.SlotTotal,
            slots.Count(s => s.FrameId == f.Id && s.SlotState == "1"))).ToList();
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

        var bindRows = binds.OrderBy(b => b.EquipmentId).Select(b =>
        {
            var eq = equipments.FirstOrDefault(e => e.Id == b.EquipmentId);
            var isUpload = b.FrameRole == "0";
            return new FrameBindRow(
                eq is null ? "—" : $"{eq.EquipmentName} ({eq.EquipmentNo})",
                isUpload,
                isUpload ? "上料架" : "下料架");
        }).ToList();

        var slotItems = slots.Select(s => new SlotItem(
            s.SlotNo,
            s.LayerNo,
            s.PosInLayer,
            $"{s.LayerNo}层{s.PosInLayer}位",
            s.SlotState == "1" || s.SlotState == "3" ? s.ElectrodeId : null,
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
}
