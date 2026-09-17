using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class EquipmentConfigService : IEquipmentConfigService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    private readonly IEquipmentRoutingStore _routing;

    public EquipmentConfigService(IDbContextFactory<CncDbContext> factory, IEquipmentRoutingStore routing)
    {
        _factory = factory;
        _routing = routing;
    }

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
                : $"{plc.PlcName ?? $"PLC-{plc.PlcId}"} ({plc.PlcComputerIp})";
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
        // 调度反查：Equipment→Craft→WorkLine 三层均须 STATE=="0"；Service 自身 fail-closed（不信任 store 预过滤）。
        var chain = await _routing.FindEquipmentChainAsync(equipmentId, ct);
        if (chain.Equipment is null || !ConfigActivity.IsActive(chain.Equipment.State)) return null;
        if (chain.Craftwork is null || !ConfigActivity.IsActive(chain.Craftwork.State)) return null;
        if (chain.WorkLine is null || !ConfigActivity.IsActive(chain.WorkLine.State)) return null;
        return new WorkLineRef(chain.WorkLine.Id, chain.WorkLine.WorkLineCode);
    }

    public async Task<IReadOnlyList<PositionItem>> GetPositionsAsync(long equipmentId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var positions = await db.Positions.AsNoTracking()
            .Where(p => p.EquipmentId == equipmentId && p.State == ConfigFlags.Active)
            .OrderBy(p => p.PositionCode).ToListAsync(ct);
        return positions.Select(p => new PositionItem(
            p.Id,
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

        // 按角色排序：上料0 / 下料1 / 中转2 / NG3，全部展示（不再只认上/下料）。
        return binds.OrderBy(b => b.FrameRole).Select(b =>
        {
            var frame = frames.FirstOrDefault(f => f.Id == b.FrameId);
            var role = b.FrameRole ?? "";
            return new EquipmentFrameBinding(
                role == "0",
                ConfigFlags.FrameRoleText(role),
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
        var binds = (await _routing.FindFrameBindsByEquipmentAsync(equipmentId, ct))
            .Where(b => b.State == ConfigFlags.Active)
            .ToList();
        return new EquipmentFrameBindingIds(
            binds.FirstOrDefault(b => b.FrameRole == "0")?.FrameId,
            binds.FirstOrDefault(b => b.FrameRole == "1")?.FrameId);
    }

    public async Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var binds = await db.FrameBinds.AsNoTracking()
            .Where(b => b.FrameId == frameId && b.State == ConfigFlags.Active).ToListAsync(ct);
        return binds
            .Where(b => TryParseRole(b.FrameRole, out _))
            .Select(b => { TryParseRole(b.FrameRole, out var role); return new FrameBindingInfo(b.EquipmentId, role); })
            .ToList();
    }

    public async Task<long?> GetFrameBindingByRoleAsync(long equipmentId, FrameRole role, CancellationToken ct = default)
    {
        var roleFlag = ((int)role).ToString();
        var bind = (await _routing.FindFrameBindsByEquipmentAsync(equipmentId, ct))
            .FirstOrDefault(b => b.FrameRole == roleFlag && b.State == ConfigFlags.Active);
        return bind?.FrameId;
    }

    public async Task<IReadOnlyList<long>> GetNextProcessEquipmentsAsync(long equipmentId, CancellationToken ct = default)
    {
        // 下一工序候选：源 Equipment/Craft、父 WorkLine、下游 Craft/Equipment 均须活动；关系缺失 fail-closed。
        var eq = await _routing.FindEquipmentAsync(equipmentId, ct);
        if (eq is null || !ConfigActivity.IsActive(eq.State)) return Array.Empty<long>();
        var craft = await _routing.FindCraftworkAsync(eq.CraftworkId, ct);
        if (craft is null || !ConfigActivity.IsActive(craft.State)) return Array.Empty<long>();
        var line = await _routing.FindWorkLineAsync(craft.WorkLineId, ct);
        if (line is null || !ConfigActivity.IsActive(line.State)) return Array.Empty<long>();
        var currentNode = craft.CraftworkNode ?? 0;

        var laterCrafts = (await _routing.FindCraftworksByWorkLineAsync(craft.WorkLineId, ct))
            .Where(c => ConfigActivity.IsActive(c.State) && (c.CraftworkNode ?? 0) > currentNode)
            .ToList();
        if (laterCrafts.Count == 0) return Array.Empty<long>();
        var nextNode = laterCrafts.Min(c => c.CraftworkNode ?? 0);
        var nextCraftIds = laterCrafts.Where(c => (c.CraftworkNode ?? 0) == nextNode).Select(c => c.Id).ToHashSet();

        var eqs = await _routing.FindEquipmentsByCraftworkIdsAsync(nextCraftIds, ct);
        return eqs.Where(e => ConfigActivity.IsActive(e.State)).Select(e => e.Id).ToList();
    }

    public async Task<bool> HasSubsequentProcessAsync(long equipmentId, CancellationToken ct = default)
    {
        // 忽略 STATE：只要同线存在更大 CraftworkNode，即视为“配置了后续工序”。
        var eq = await _routing.FindEquipmentAsync(equipmentId, ct);
        if (eq is null) return false;
        var craft = await _routing.FindCraftworkAsync(eq.CraftworkId, ct);
        if (craft is null) return false;
        var currentNode = craft.CraftworkNode ?? 0;
        var crafts = await _routing.FindCraftworksByWorkLineAsync(craft.WorkLineId, ct);
        return crafts.Any(c => (c.CraftworkNode ?? 0) > currentNode);
    }

    /// <summary>FRAME_ROLE 字符串("0"/"1"/"2"/"3") → FrameRole 枚举。非法值返回 false。</summary>
    private static bool TryParseRole(string flag, out FrameRole role)
    {
        switch (flag)
        {
            case "0": role = FrameRole.Upload; return true;
            case "1": role = FrameRole.Unload; return true;
            case "2": role = FrameRole.Transit; return true;
            case "3": role = FrameRole.NgFrame; return true;
            default: role = FrameRole.Upload; return false;
        }
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
        return await CheckDeleteCoreAsync(db, equipmentId, ct);
    }

    private static async Task<DeleteCheckResult> CheckDeleteCoreAsync(
        CncDbContext db, long equipmentId, CancellationToken ct)
    {
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

    public Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default)
        => ConfigSoftDelete.RunAsync(_factory, async (db, token) =>
        {
            var check = await CheckDeleteCoreAsync(db, equipmentId, token);
            if (!check.CanDelete) throw new InvalidOperationException(check.Message);

            var entity = await db.Equipments.FirstOrDefaultAsync(x => x.Id == equipmentId && x.State == ConfigFlags.Active, token)
                ?? throw new InvalidOperationException("机台不存在或已删除。");
            entity.State = ConfigFlags.Disabled;
            entity.Author = author;
            entity.UpdateTime = DateTime.Now;

            // 级联软删其加工位（与机台软删同事务，避免半删）
            var positions = await db.Positions
                .Where(p => p.EquipmentId == equipmentId && p.State == ConfigFlags.Active)
                .ToListAsync(token);
            foreach (var p in positions)
            {
                p.State = ConfigFlags.Disabled;
                p.Author = author;
                p.UpdateTime = DateTime.Now;
            }
        }, ct);
}
