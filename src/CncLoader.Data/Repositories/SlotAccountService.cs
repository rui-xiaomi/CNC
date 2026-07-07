using System.Collections.Concurrent;
using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 槽位账目服务实现（第四阶段⑥a）。
/// 预记用 SLOT_STATE='3' + REMARK=taskId 跟踪（避免 DB schema 变更）；同架并发用内存 SemaphoreSlim 互斥。
/// 落账后 REMARK 保留 taskId 作为幂等判定与审计（BindSource='RCS_CONFIRMED' 区分已落账）。
/// </summary>
public sealed class SlotAccountService : ISlotAccountService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    private readonly ILogger<SlotAccountService> _logger;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _frameLocks = new();

    public SlotAccountService(IDbContextFactory<CncDbContext> factory, ILogger<SlotAccountService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? electrodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        var gate = _frameLocks.GetOrAdd(frameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            var slot = await db.FrameSlots.AsTracking()
                .Where(s => s.FrameId == frameId && s.SlotState == SlotStates.Empty)
                .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                .FirstOrDefaultAsync(ct);
            if (slot is null) return null;

            slot.SlotState = SlotStates.Reserved;
            slot.ElectrodeId = electrodeId;
            slot.Remark = taskId;
            slot.BindTime = DateTime.Now;
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("预记料架 {Frame} 槽 {Slot} taskId={Task} 电极={El}", frameId, slot.SlotNo, taskId, electrodeId);
            return new ReservedSlot(frameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer);
        }
        finally { gate.Release(); }
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
        slot.BindSource = "RCS_CONFIRMED";
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
        slot.ElectrodeId = null;
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

    public async Task SetSlotAsync(long frameId, int slotNo, string? electrodeId, string slotState, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsTracking().FirstOrDefaultAsync(s => s.FrameId == frameId && s.SlotNo == slotNo, ct);
        if (slot is null) throw new InvalidOperationException($"料架 {frameId} 槽 {slotNo} 不存在");

        slot.SlotState = slotState;
        slot.ElectrodeId = electrodeId;
        if (slotState == SlotStates.Empty) { slot.Remark = null; slot.BindTime = null; }
        slot.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("人工校正料架 {Frame} 槽 {Slot} → state={State} 电极={El} by {Author}", frameId, slotNo, slotState, electrodeId, author);
    }

    public async Task<SlotLocation?> LocateElectrodeAsync(string electrodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(electrodeId)) return null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsNoTracking().FirstOrDefaultAsync(s => s.ElectrodeId == electrodeId, ct);
        if (slot is null) return null;
        var frame = await db.Frames.AsNoTracking().FirstOrDefaultAsync(f => f.Id == slot.FrameId, ct);
        return new SlotLocation(slot.FrameId, frame?.FrameName ?? "", slot.SlotNo, slot.LayerNo, slot.PosInLayer, slot.SlotState);
    }

    public async Task<int> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slots = await db.FrameSlots.AsTracking().Where(s => s.FrameId == frameId).OrderBy(s => s.SlotNo).ToListAsync(ct);
        if (slots.Count == 0) return 0;

        var now = DateTime.Now;
        var corrected = 0;
        // posStart 是孔位编号（百位为面），简化为按 SlotNo 从 posStart 起的 count 个槽位
        var startIdx = Math.Max(0, posStart - 1);
        for (var i = 0; i < slots.Count; i++)
        {
            var inRange = i >= startIdx && i < startIdx + products.Count;
            if (inRange)
            {
                var code = products[i - startIdx];
                if (!string.IsNullOrWhiteSpace(code))
                {
                    slots[i].ElectrodeId = code;
                    slots[i].SlotState = SlotStates.Occupied;
                    slots[i].BindSource = "RCS_QR";
                    slots[i].BindTime = now;
                    corrected++;
                }
                else
                {
                    // 扫得空码 → 该槽位清空（盘点发现空位）
                    slots[i].ElectrodeId = null;
                    slots[i].SlotState = SlotStates.Empty;
                }
            }
            slots[i].LastVerifyTime = now;
            slots[i].UpdateTime = now;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("盘点校正料架 {Frame} 起始 {Start} 数 {N} → 校正 {C} 个电极", frameId, posStart, products.Count, corrected);
        return corrected;
    }

    public async Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slots = await db.FrameSlots.AsNoTracking().Where(s => s.FrameId == frameId).OrderBy(s => s.SlotNo).ToListAsync(ct);
        return slots.Select(s => new SlotRecord(s.FrameId, s.SlotNo, s.LayerNo, s.PosInLayer, s.SlotState, s.ElectrodeId, s.LastVerifyTime)).ToList();
    }
}
