using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 槽位账目服务实现（第四阶段⑥a）。
/// 预记用 SLOT_STATE='3' + REMARK=taskId 跟踪（避免 DB schema 变更）。
/// 选槽用"候选选取 + 带条件原子 UPDATE（WHERE SLOT_STATE=期望态）+ 影响行数校验"——
/// 即使有第二写入者（另一次派工/人工校正/盘点回写）也不会双占（Layer 2 数据兜底）。
/// 落账后 REMARK 保留 taskId 作为幂等判定与审计（BindSource='CONFIRMED' 区分已落账）。
/// </summary>
public sealed class SlotAccountService : ISlotAccountService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    private readonly ILogger<SlotAccountService> _logger;

    public SlotAccountService(IDbContextFactory<CncDbContext> factory, ILogger<SlotAccountService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    // 预记方向标记（写入 BIND_SOURCE，供重启对账按方向回滚）。BIND_SOURCE 为 VARCHAR(10)，值须 ≤10 字符。
    private const string ReservePut = "RSV_PUT";
    private const string ReserveTake = "RSV_TAKE";

    public async Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = DateTime.Now;
        // 候选（首个空槽）+ 带条件原子 UPDATE（WHERE SLOT_STATE='0'）。影响行数=0 表示该槽已被并发写入者占用 → 重选下一个候选。
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var slot = await db.FrameSlots.AsNoTracking()
                .Where(s => s.FrameId == frameId && s.SlotState == SlotStates.Empty)
                .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                .Select(s => new { s.Id, s.SlotNo, s.LayerNo, s.PosInLayer })
                .FirstOrDefaultAsync(ct);
            if (slot is null) return null;

            var affected = await db.FrameSlots
                .Where(s => s.Id == slot.Id && s.SlotState == SlotStates.Empty)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.SlotState, SlotStates.Reserved)
                    .SetProperty(s => s.MaterialId, materialId)
                    .SetProperty(s => s.Remark, taskId)
                    .SetProperty(s => s.BindSource, ReservePut)
                    .SetProperty(s => s.BindTime, (DateTime?)now), ct);
            if (affected == 1)
            {
                _logger.LogInformation("入库预记料架 {Frame} 槽 {Slot} taskId={Task} 物料={El}", frameId, slot.SlotNo, taskId, materialId);
                return new ReservedSlot(frameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer, materialId);
            }
            _logger.LogDebug("入库预记料架 {Frame} 槽 {Slot} 被并发占用，重选", frameId, slot.SlotNo);
        }
    }

    public async Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = DateTime.Now;
        // 候选（首个占用槽）+ 带条件原子 UPDATE（WHERE SLOT_STATE='1'）。影响行数=0 表示该槽已被并发写入者取走 → 重选下一个候选，直到成功或无占用槽（无料）。
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var slot = await db.FrameSlots.AsNoTracking()
                .Where(s => s.FrameId == frameId && s.SlotState == SlotStates.Occupied)
                .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                .Select(s => new { s.Id, s.SlotNo, s.LayerNo, s.PosInLayer, s.MaterialId })
                .FirstOrDefaultAsync(ct);
            if (slot is null) return null;

            var affected = await db.FrameSlots
                .Where(s => s.Id == slot.Id && s.SlotState == SlotStates.Occupied)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.SlotState, SlotStates.Reserved)
                    .SetProperty(s => s.Remark, taskId)
                    .SetProperty(s => s.BindSource, ReserveTake)
                    .SetProperty(s => s.BindTime, (DateTime?)now), ct);
            if (affected == 1)
            {
                _logger.LogInformation("取料预记料架 {Frame} 槽 {Slot} taskId={Task} 物料={El}", frameId, slot.SlotNo, taskId, slot.MaterialId);
                return new ReservedSlot(frameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer, slot.MaterialId);
            }
            _logger.LogDebug("取料预记料架 {Frame} 槽 {Slot} 被并发取走，重选", frameId, slot.SlotNo);
        }
    }

    public async Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsTracking().FirstOrDefaultAsync(s => s.Remark == taskId, ct);
        if (slot is null) return false;

        if (slot.SlotState == SlotStates.Empty)
        {
            _logger.LogDebug("取料落账幂等：taskId={Task} 已清空槽 {Slot}", taskId, slot.SlotNo);
            return true; // redo 同 taskId 不重复
        }
        if (slot.SlotState != SlotStates.Reserved)
        {
            _logger.LogWarning("取料落账失败：taskId={Task} 槽 {Slot} 状态={State} 非预记", taskId, slot.SlotNo, slot.SlotState);
            return false;
        }

        slot.SlotState = SlotStates.Empty;
        slot.MaterialId = null;
        slot.Remark = null;
        slot.BindTime = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("取料落账料架 {Frame} 槽 {Slot} taskId={Task}（物料已取走）", slot.FrameId, slot.SlotNo, taskId);
        return true;
    }

    public async Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsTracking()
            .FirstOrDefaultAsync(s => s.Remark == taskId && s.SlotState == SlotStates.Reserved, ct);
        if (slot is null)
        {
            _logger.LogDebug("取料回滚：taskId={Task} 无预记槽（可能已落账或已回滚）", taskId);
            return false;
        }

        slot.SlotState = SlotStates.Occupied; // 物料未取走，恢复占用
        slot.Remark = null;
        slot.BindTime = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("取料回滚料架 {Frame} 槽 {Slot} taskId={Task}（恢复占用）", slot.FrameId, slot.SlotNo, taskId);
        return true;
    }

    public async Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var reserved = await db.FrameSlots.AsTracking()
            .Where(s => s.SlotState == SlotStates.Reserved && s.Remark != null)
            .ToListAsync(ct);
        var active = new HashSet<string>(activeTaskIds);
        var taskIds = reserved.Select(s => s.Remark!).Where(id => !active.Contains(id)).Distinct().ToList();
        var completed = new HashSet<string>(StringComparer.Ordinal);
        if (taskIds.Count > 0)
        {
            var done = await db.AgvTasks.AsNoTracking()
                .Where(t => t.RcsTaskId != null && taskIds.Contains(t.RcsTaskId) && t.TaskState == RcsTaskState.Completed)
                .Select(t => t.RcsTaskId!)
                .ToListAsync(ct);
            foreach (var id in done) completed.Add(id);
        }

        var rolled = 0;
        var skippedCompleted = 0;
        foreach (var slot in reserved)
        {
            if (slot.Remark != null && active.Contains(slot.Remark)) continue; // 仍在执行，不动
            var taskId = slot.Remark!;
            if (completed.Contains(taskId))
            {
                // COMPLETED 预记交调度器按 PLC 门 Confirm，此处不落账也不回滚
                skippedCompleted++;
                continue;
            }
            if (slot.BindSource == ReserveTake)
            {
                slot.SlotState = SlotStates.Occupied; // 取料未完成 → 物料还在
                slot.Remark = null;
                slot.BindTime = null;
                rolled++;
            }
            else
            {
                slot.SlotState = SlotStates.Empty; // 入库未完成/失败 → 槽位仍空
                slot.MaterialId = null;
                slot.Remark = null;
                slot.BindTime = null;
                rolled++;
            }
        }
        if (rolled > 0) await db.SaveChangesAsync(ct);
        if (rolled > 0 || skippedCompleted > 0)
            _logger.LogInformation("陈旧预记：回滚 {R}，跳过 COMPLETED 待 PLC 门 {S}", rolled, skippedCompleted);
        return rolled;
    }

    public async Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
        IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var active = new HashSet<string>(activeTaskIds);
        var reserved = await db.FrameSlots.AsNoTracking()
            .Where(s => s.SlotState == SlotStates.Reserved && s.Remark != null)
            .Select(s => new { s.Remark, s.BindSource, s.FrameId, s.SlotNo })
            .ToListAsync(ct);
        var candidates = reserved
            .Where(s => s.Remark != null && !active.Contains(s.Remark))
            .Select(s => s.Remark!)
            .Distinct()
            .ToList();
        if (candidates.Count == 0) return Array.Empty<CompletedPendingConfirm>();

        var completed = await db.AgvTasks.AsNoTracking()
            .Where(t => t.RcsTaskId != null && candidates.Contains(t.RcsTaskId) && t.TaskState == RcsTaskState.Completed)
            .Select(t => t.RcsTaskId!)
            .ToListAsync(ct);
        var done = new HashSet<string>(completed, StringComparer.Ordinal);

        return reserved
            .Where(s => s.Remark != null && done.Contains(s.Remark))
            .Select(s => new CompletedPendingConfirm(s.Remark!, s.BindSource == ReserveTake, s.FrameId, s.SlotNo))
            .ToList();
    }

    public async Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsTracking().FirstOrDefaultAsync(s => s.Remark == taskId, ct);
        if (slot is null) return false;

        if (slot.SlotState == SlotStates.Occupied)
        {
            _logger.LogDebug("落账幂等：taskId={Task} 已落账槽 {Slot}", taskId, slot.SlotNo);
            return true; // redo 同 taskId 不重复记账
        }
        if (slot.SlotState != SlotStates.Reserved)
        {
            _logger.LogWarning("落账失败：taskId={Task} 槽 {Slot} 状态={State} 非预记", taskId, slot.SlotNo, slot.SlotState);
            return false;
        }

        slot.SlotState = SlotStates.Occupied;
        slot.BindSource = "CONFIRMED"; // ≤10 字符（BIND_SOURCE VARCHAR(10)）
        slot.BindTime = DateTime.Now;
        // REMARK 保留 taskId 作为幂等判定与审计
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("落账料架 {Frame} 槽 {Slot} taskId={Task}", slot.FrameId, slot.SlotNo, taskId);
        return true;
    }

    public async Task<bool> RollbackAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsTracking().FirstOrDefaultAsync(s => s.Remark == taskId && s.SlotState == SlotStates.Reserved, ct);
        if (slot is null)
        {
            _logger.LogDebug("回滚：taskId={Task} 无预记槽（可能已落账或已回滚）", taskId);
            return false;
        }

        slot.SlotState = SlotStates.Empty;
        slot.MaterialId = null;
        slot.Remark = null;
        slot.BindTime = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("回滚预记料架 {Frame} 槽 {Slot} taskId={Task}", slot.FrameId, slot.SlotNo, taskId);
        return true;
    }

    public async Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slots = await db.FrameSlots.AsNoTracking().Where(s => s.FrameId == frameId).ToListAsync(ct);
        var total = slots.Count;
        var occupied = slots.Count(s => s.SlotState == SlotStates.Occupied);
        var reserved = slots.Count(s => s.SlotState == SlotStates.Reserved);
        return new FrameOccupancy(total, occupied, reserved, total - occupied - reserved - slots.Count(s => s.SlotState == SlotStates.Locked));
    }

    public async Task SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsTracking().FirstOrDefaultAsync(s => s.FrameId == frameId && s.SlotNo == slotNo, ct);
        if (slot is null) throw new InvalidOperationException($"料架 {frameId} 槽 {slotNo} 不存在");

        slot.SlotState = slotState;
        slot.MaterialId = materialId;
        if (slotState == SlotStates.Empty) { slot.Remark = null; slot.BindTime = null; }
        slot.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("人工校正料架 {Frame} 槽 {Slot} → state={State} 物料={El} by {Author}", frameId, slotNo, slotState, materialId, author);
    }

    public async Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(materialId)) return null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsNoTracking().FirstOrDefaultAsync(s => s.MaterialId == materialId, ct);
        if (slot is null) return null;
        var frame = await db.Frames.AsNoTracking().FirstOrDefaultAsync(f => f.Id == slot.FrameId, ct);
        return new SlotLocation(slot.FrameId, frame?.FrameName ?? "", slot.SlotNo, slot.LayerNo, slot.PosInLayer, slot.SlotState);
    }

    public async Task<int> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // 按层→层内位物理顺序对齐 identifyQR「按下发孔位顺序」回扫结果
        var slots = await db.FrameSlots.AsTracking()
            .Where(s => s.FrameId == frameId)
            .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
            .ToListAsync(ct);
        if (slots.Count == 0 || products.Count == 0) return 0;

        var (startLayer, startPos) = DecodeIdentifyHole(posStart);
        var startIdx = slots.FindIndex(s => s.LayerNo == startLayer && s.PosInLayer == startPos);
        if (startIdx < 0)
        {
            _logger.LogWarning("盘点校正料架 {Frame} 起始孔位 {Hole}（层{L}位{P}）无对应槽位，跳过",
                frameId, posStart, startLayer, startPos);
            return 0;
        }

        var now = DateTime.Now;
        var corrected = 0;
        for (var i = 0; i < products.Count; i++)
        {
            var idx = startIdx + i;
            if (idx >= slots.Count) break;
            var slot = slots[idx];
            var code = products[i];
            if (!string.IsNullOrWhiteSpace(code))
            {
                slot.MaterialId = code;
                slot.SlotState = SlotStates.Occupied;
                slot.BindSource = "RCS_QR";
                slot.BindTime = now;
                corrected++;
            }
            else
            {
                // 扫得空码 → 该槽位清空（盘点发现空位）
                slot.MaterialId = null;
                slot.SlotState = SlotStates.Empty;
            }
            slot.LastVerifyTime = now;
            slot.UpdateTime = now;
        }
        // 整架打上本次盘点新鲜度（范围外槽位物料不变）
        foreach (var s in slots)
        {
            s.LastVerifyTime = now;
            s.UpdateTime = now;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("盘点校正料架 {Frame} 起始孔位 {Start}（层{L}位{P}）数 {N} → 校正 {C} 个物料",
            frameId, posStart, startLayer, startPos, products.Count, corrected);
        return corrected;
    }

    /// <summary>identifyQR 孔位：三位数百位=面/层、后两位=层内位（101→1层1位）。&lt;100 兼容为第 1 面层内位。</summary>
    private static (int layer, int pos) DecodeIdentifyHole(int hole)
    {
        if (hole < 100) return (1, Math.Max(1, hole));
        var layer = hole / 100;
        var pos = hole % 100;
        return (Math.Max(1, layer), Math.Max(1, pos));
    }

    public async Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slots = await db.FrameSlots.AsNoTracking().Where(s => s.FrameId == frameId).OrderBy(s => s.SlotNo).ToListAsync(ct);
        return slots.Select(s => new SlotRecord(s.FrameId, s.SlotNo, s.LayerNo, s.PosInLayer, s.SlotState, s.MaterialId, s.LastVerifyTime)).ToList();
    }
}
