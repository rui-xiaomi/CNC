using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

// Phase 3 配置管理：线体/工序/机台/料架 的读取与（线体/工序）回写。
// 全部经 IDbContextFactory 短连接，State=="0" 视为启用/有效。

internal static class ConfigFlags
{
    // 软删语义的唯一定义在 Core 的 ConfigActivity；此处只做转发，避免"0"/"1"在两处各写一遍。
    public const string Active = ConfigActivity.Active;
    public const string Disabled = ConfigActivity.Disabled;
    public static bool IsEnabled(string state) => ConfigActivity.IsActive(state);
    public static string ToState(bool enabled) => enabled ? Active : Disabled;
    public static bool IsTrue(string? flag) => flag == "1";
    public static string ToFlag(bool value) => value ? "1" : "0";

    /// <summary>FRAME_ROLE "0"/"1"/"2"/"3" → 中文角色名（列表展示用）。</summary>
    public static string FrameRoleText(string roleCode) => roleCode switch
    {
        "0" => "上料架",
        "1" => "下料架",
        "2" => "中转架",
        "3" => "NG架",
        _ => "未知"
    };
}

/// <summary>
/// 配置软删的事务纪律：引用校验、软删、级联软删必须在同一 context + 同一事务内完成。
/// 拆成两次连接时，校验通过后引用可能刚被新增（TOCTOU），级联也可能只删一半。
/// 新增配置实体的删除一律走这里，别再各写一遍 BeginTransaction/Commit。
/// </summary>
internal static class ConfigSoftDelete
{
    public static async Task RunAsync(
        IDbContextFactory<CncDbContext> factory,
        Func<CncDbContext, CancellationToken, Task> checkThenMarkDeleted,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await checkThenMarkDeleted(db, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }
}

public sealed class WorkLineService : IWorkLineService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    public WorkLineService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public event EventHandler? WorkLinesChanged;

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
            ConfigFlags.IsEnabled(l.State),
            l.AgvId)).ToList();
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
        WorkLinesChanged?.Invoke(this, EventArgs.Empty);
        return entity.Id;
    }

    public async Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await CheckDeleteCoreAsync(db, id, ct);
    }

    private static async Task<DeleteCheckResult> CheckDeleteCoreAsync(
        CncDbContext db, long id, CancellationToken ct)
    {
        var refs = await db.Craftworks.AsNoTracking()
            .CountAsync(c => c.WorkLineId == id && c.State == ConfigFlags.Active, ct);
        return refs == 0
            ? new DeleteCheckResult(true, 0, "可删除")
            : new DeleteCheckResult(false, refs, $"被 {refs} 道工序引用，禁止删除");
    }

    public async Task DeleteAsync(long id, string author, CancellationToken ct = default)
    {
        await ConfigSoftDelete.RunAsync(_factory, async (db, token) =>
        {
            var check = await CheckDeleteCoreAsync(db, id, token);
            if (!check.CanDelete) throw new InvalidOperationException(check.Message);

            var entity = await db.WorkLines.FirstOrDefaultAsync(x => x.Id == id && x.State == ConfigFlags.Active, token)
                ?? throw new InvalidOperationException("线体不存在或已删除。");
            entity.State = ConfigFlags.Disabled;
            entity.Author = author;
            entity.UpdateTime = DateTime.Now;
        }, ct);

        WorkLinesChanged?.Invoke(this, EventArgs.Empty);
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
        return await CheckDeleteCoreAsync(db, id, ct);
    }

    private static async Task<DeleteCheckResult> CheckDeleteCoreAsync(
        CncDbContext db, long id, CancellationToken ct)
    {
        var refs = await db.Equipments.AsNoTracking()
            .CountAsync(e => e.CraftworkId == id && e.State == ConfigFlags.Active, ct);
        return refs == 0
            ? new DeleteCheckResult(true, 0, "可删除")
            : new DeleteCheckResult(false, refs, $"被 {refs} 台机台引用，禁止删除");
    }

    public Task DeleteAsync(long id, string author, CancellationToken ct = default)
        => ConfigSoftDelete.RunAsync(_factory, async (db, token) =>
        {
            var check = await CheckDeleteCoreAsync(db, id, token);
            if (!check.CanDelete) throw new InvalidOperationException(check.Message);

            var entity = await db.Craftworks.FirstOrDefaultAsync(x => x.Id == id && x.State == ConfigFlags.Active, token)
                ?? throw new InvalidOperationException("工序不存在或已删除。");
            entity.State = ConfigFlags.Disabled;
            entity.Author = author;
            entity.UpdateTime = DateTime.Now;
        }, ct);
}

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
