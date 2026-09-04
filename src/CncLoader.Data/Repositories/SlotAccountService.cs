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
    private readonly ISlotAccountStore _slotStore;
    private readonly ILogger<SlotAccountService> _logger;

    public SlotAccountService(
        IDbContextFactory<CncDbContext> factory,
        ISlotAccountStore slotStore,
        ILogger<SlotAccountService> logger)
    {
        _factory = factory;
        _slotStore = slotStore;
        _logger = logger;
    }

    // 预记方向标记（写入 BIND_SOURCE，供重启对账按方向回滚）。BIND_SOURCE 为 VARCHAR(10)，值须 ≤10 字符。
    private const string ReservePut = "RSV_PUT";
    private const string ReserveTake = "RSV_TAKE";

    public async Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        // 行为保持：候选选取 + WHERE Empty 原子更新已下沉至 ISlotAccountStore（可测并发接缝）。
        var reserved = await _slotStore.ReservePutAsync(frameId, taskId, materialId, ct);
        if (reserved is not null)
            _logger.LogInformation("入库预记料架 {Frame} 槽 {Slot} taskId={Task} 物料={El}",
                frameId, reserved.SlotNo, taskId, materialId);
        return reserved;
    }

    public async Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        var reserved = await _slotStore.ReserveTakeAsync(frameId, taskId, ct);
        if (reserved is not null)
            _logger.LogInformation("取料预记料架 {Frame} 槽 {Slot} taskId={Task} 物料={El}",
                frameId, reserved.SlotNo, taskId, reserved.MaterialId);
        return reserved;
    }

    public async Task<ReservedSlot?> FindReservedAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        return await _slotStore.FindReservedByTaskIdAsync(taskId, ct);
    }

    public async Task<ReservedSlot?> ReserveTakeByMaterialAsync(long frameId, string taskId, string materialId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(materialId)) return null;
        var reserved = await _slotStore.ReserveTakeByMaterialAsync(frameId, taskId, materialId, ct);
        if (reserved is not null)
            _logger.LogInformation("按物料取料预记料架 {Frame} 槽 {Slot} taskId={Task} 物料={El}",
                frameId, reserved.SlotNo, taskId, materialId);
        return reserved;
    }

    public async Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        var ok = await _slotStore.ConfirmTakeAsync(taskId, ct);
        if (ok) _logger.LogInformation("取料落账 taskId={Task}", taskId);
        else _logger.LogWarning("取料落账失败：taskId={Task}", taskId);
        return ok;
    }

    public async Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        var ok = await _slotStore.RollbackTakeAsync(taskId, ct);
        if (ok) _logger.LogInformation("取料回滚 taskId={Task}", taskId);
        else _logger.LogDebug("取料回滚：taskId={Task} 无预记槽（可能已落账或已回滚）", taskId);
        return ok;
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
        var ok = await _slotStore.ConfirmPutAsync(taskId, ct);
        if (ok) _logger.LogInformation("落账 taskId={Task}", taskId);
        else _logger.LogWarning("落账失败：taskId={Task}", taskId);
        return ok;
    }

    public async Task<bool> RollbackAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        var ok = await _slotStore.RollbackPutAsync(taskId, ct);
        if (ok) _logger.LogInformation("回滚预记 taskId={Task}", taskId);
        else _logger.LogDebug("回滚：taskId={Task} 无预记槽（可能已落账或已回滚）", taskId);
        return ok;
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

    public async Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var normalized = NormalizeExternalSlotState(slotState);
            if (normalized is null)
            {
                var isReservedTarget = string.Equals(
                    (slotState ?? string.Empty).Trim(), SlotStates.Reserved, StringComparison.Ordinal);
                _logger.LogWarning(
                    "人工校正拒绝非法目标状态：料架 {Frame} 槽 {Slot} target={State} by {Author}",
                    frameId, slotNo, slotState, author);
                return SlotMutationResult.From(
                    SlotMutationStatus.InvalidTargetState, frameId, slotNo, null,
                    isReservedTarget
                        ? "预记状态只能由派工流程创建，不能人工设置"
                        : "不支持的槽位状态，不能人工设置");
            }

            var clearRemarkAndBindTime = normalized == SlotStates.Empty;
            var now = DateTime.Now;

            // 不先查状态再放行：直接条件原子写；affected=0 再只读分类。
            var attempt = await _slotStore.TrySetExternalSlotAsync(
                frameId, slotNo, normalized, materialId, clearRemarkAndBindTime, now, ct);

            if (attempt.InvalidTargetState)
            {
                _logger.LogWarning(
                    "Store 拒绝非法目标状态：料架 {Frame} 槽 {Slot} target={State}",
                    frameId, slotNo, normalized);
                return SlotMutationResult.From(
                    SlotMutationStatus.InvalidTargetState, frameId, slotNo, attempt.Current,
                    "预记状态只能由派工流程创建，不能人工设置");
            }

            if (attempt.AffectedRows == 1)
            {
                _logger.LogInformation(
                    "人工校正料架 {Frame} 槽 {Slot} → state={State} 物料={El} by {Author}",
                    frameId, slotNo, normalized, materialId, author);
                return SlotMutationResult.From(SlotMutationStatus.Updated, frameId, slotNo, attempt.Current);
            }

            var current = attempt.Current;
            if (current is null)
            {
                return SlotMutationResult.From(
                    SlotMutationStatus.NotFound, frameId, slotNo, null,
                    $"料架 {frameId} 槽 {slotNo} 不存在");
            }

            if (current.SlotState == SlotStates.Reserved)
            {
                if (IsAnomalousReservation(current))
                {
                    _logger.LogWarning(
                        "异常预记数据不一致：料架 {Frame} 槽 {Slot} STATE=Reserved 但 REMARK/BIND_SOURCE 异常（REMARK空={EmptyRemark}, BIND_SOURCE={Bind}），拒绝外部校正",
                        frameId, slotNo, string.IsNullOrEmpty(current.Remark), current.BindSource);
                }
                else
                {
                    _logger.LogWarning(
                        "槽位已被任务预记，不能人工校正：料架 {Frame} 槽 {Slot}",
                        frameId, slotNo);
                }

                return SlotMutationResult.From(
                    SlotMutationStatus.ReservationConflict, frameId, slotNo, current,
                    "槽位已被任务预记，不能人工校正");
            }

            if (MatchesExternalTarget(current, slotState, materialId, clearRemarkAndBindTime))
            {
                return SlotMutationResult.From(SlotMutationStatus.Unchanged, frameId, slotNo, current);
            }

            _logger.LogWarning(
                "人工校正并发冲突：料架 {Frame} 槽 {Slot} 非预记但与目标不一致（当前 state={State} material={El}）",
                frameId, slotNo, current.SlotState, current.MaterialId);
            return SlotMutationResult.From(
                SlotMutationStatus.ConcurrencyConflict, frameId, slotNo, current,
                "槽位已被并发修改，请刷新后重试");
        }
        catch (OperationCanceledException)
        {
            return SlotMutationResult.From(SlotMutationStatus.Cancelled, frameId, slotNo, null, "操作已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "人工校正料架 {Frame} 槽 {Slot} 数据库异常", frameId, slotNo);
            return SlotMutationResult.From(
                SlotMutationStatus.DatabaseError, frameId, slotNo, null, ex.Message);
        }
    }

    private static bool IsAnomalousReservation(SlotRow slot) =>
        string.IsNullOrEmpty(slot.Remark)
        || (slot.BindSource != ReservePut && slot.BindSource != ReserveTake);

    /// <summary>
    /// 外部写仅允许空/占用/锁定；Reserved 与未知串（含 "03"/空格变体未归一为合法码）一律拒绝。
    /// </summary>
    private static string? NormalizeExternalSlotState(string? slotState)
    {
        var s = (slotState ?? string.Empty).Trim();
        if (s is SlotStates.Empty or SlotStates.Occupied or SlotStates.Locked)
            return s;
        return null;
    }

    private static bool MatchesExternalTarget(
        SlotRow current, string targetState, string? materialId, bool clearRemarkAndBindTime)
    {
        if (current.SlotState != targetState) return false;
        if (!string.Equals(current.MaterialId, materialId, StringComparison.Ordinal)) return false;
        if (clearRemarkAndBindTime)
            return current.Remark is null && current.BindTime is null;
        return true;
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

    public async Task<InventoryCorrectionResult> CorrectFromInventoryAsync(
        long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
    {
        const int conflictSlotsCap = 10;
        try
        {
            return await _slotStore.ExecuteInTransactionAsync(ApplyAsync, ct);
        }
        catch (OperationCanceledException)
        {
            return new InventoryCorrectionResult(
                InventoryCorrectionStatus.Cancelled, products.Count, 0, 0, 0, 0,
                Array.Empty<SlotMutationSnapshot>(), "操作已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "盘点校正料架 {Frame} 数据库异常，已整批回滚", frameId);
            return new InventoryCorrectionResult(
                InventoryCorrectionStatus.DatabaseError, products.Count, 0, 0, 0, 0,
                Array.Empty<SlotMutationSnapshot>(), "槽位操作失败，请查看日志");
        }

        async Task<InventoryCorrectionResult> ApplyAsync(ISlotAccountSession session, CancellationToken token)
        {
            var slots = (await session.FindByFrameOrderedAsync(frameId, token)).ToList();
            if (slots.Count == 0 || products.Count == 0)
            {
                await session.RollbackAsync(token);
                return InventoryCorrectionResult.Empty();
            }

            var (startLayer, startPos) = DecodeIdentifyHole(posStart);
            var startIdx = slots.FindIndex(s => s.LayerNo == startLayer && s.PosInLayer == startPos);
            if (startIdx < 0)
            {
                _logger.LogWarning("盘点校正料架 {Frame} 起始孔位 {Hole}（层{L}位{P}）无对应槽位，跳过",
                    frameId, posStart, startLayer, startPos);
                await session.RollbackAsync(token);
                return InventoryCorrectionResult.Empty();
            }

            var now = DateTime.Now;
            var updated = 0;
            var unchanged = 0;
            var conflicts = 0;
            var notFound = 0;
            var concurrency = 0;
            var anomalousReserved = 0;
            var conflictSlots = new List<SlotMutationSnapshot>();

            for (var i = 0; i < products.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var idx = startIdx + i;
                if (idx >= slots.Count)
                {
                    notFound++;
                    continue;
                }

                var slot = slots[idx];
                var code = products[i];
                var hasCode = !string.IsNullOrWhiteSpace(code);
                var targetState = hasCode ? SlotStates.Occupied : SlotStates.Empty;
                var targetMaterial = hasCode ? code.Trim() : null;

                if (slot.SlotState == SlotStates.Reserved)
                {
                    conflicts++;
                    if (IsAnomalousReservation(slot)) anomalousReserved++;
                    if (conflictSlots.Count < conflictSlotsCap)
                        conflictSlots.Add(ToConflictSnapshot(slot));
                    continue;
                }

                if (MatchesInventoryTarget(slot, targetState, targetMaterial))
                {
                    unchanged++;
                    continue;
                }

                var extras = hasCode
                    ? new InventorySlotWriteExtras("RCS_QR", now, now, ApplyBindFields: true)
                    : new InventorySlotWriteExtras(LastVerifyTime: now, ApplyBindFields: false);

                var attempt = await session.TrySetExternalSlotAsync(
                    frameId, slot.SlotNo, targetState, targetMaterial,
                    clearRemarkAndBindTime: false, now, token, extras);

                if (attempt.AffectedRows == 1)
                {
                    updated++;
                    continue;
                }

                // affected=0：只读分类，禁止无条件写。
                var current = attempt.Current;
                if (current is null)
                {
                    notFound++;
                    continue;
                }

                if (current.SlotState == SlotStates.Reserved)
                {
                    conflicts++;
                    if (IsAnomalousReservation(current)) anomalousReserved++;
                    if (conflictSlots.Count < conflictSlotsCap)
                        conflictSlots.Add(ToConflictSnapshot(current));
                    continue;
                }

                if (MatchesInventoryTarget(current, targetState, targetMaterial))
                {
                    unchanged++;
                    continue;
                }

                concurrency++;
            }

            if (updated > 0)
                await session.CommitAsync(token);
            else
                await session.RollbackAsync(token);

            var status = (conflicts > 0 || notFound > 0 || concurrency > 0)
                ? InventoryCorrectionStatus.CompletedWithWarnings
                : InventoryCorrectionStatus.Completed;

            var result = new InventoryCorrectionResult(
                status,
                RequestedCount: products.Count,
                UpdatedCount: updated,
                UnchangedCount: unchanged,
                ReservationConflictCount: conflicts,
                NotFoundCount: notFound,
                ConflictSlots: conflictSlots,
                ConcurrencyConflictCount: concurrency);

            if (conflicts > 0)
            {
                var slotNos = string.Join(',', conflictSlots.Select(s => s.SlotNo));
                _logger.LogWarning(
                    "盘点跳过预记槽：料架 {Frame} 请求 {Req} 更新 {Upd} 冲突 {Conflict} 槽位[{Slots}]",
                    frameId, result.RequestedCount, result.UpdatedCount, result.ReservationConflictCount, slotNos);
            }

            if (anomalousReserved > 0)
            {
                _logger.LogWarning(
                    "盘点发现异常预记 {N} 个（REMARK/BIND_SOURCE 异常），均已跳过未覆盖：料架 {Frame}",
                    anomalousReserved, frameId);
            }

            _logger.LogInformation(
                "盘点校正料架 {Frame} 起始孔位 {Start}（层{L}位{P}）请求 {Req} → 更新 {Upd} 未变 {Unch} 冲突 {Conflict} 未找到 {Nf} 并发 {Cc}",
                frameId, posStart, startLayer, startPos, result.RequestedCount,
                result.UpdatedCount, result.UnchangedCount, result.ReservationConflictCount,
                result.NotFoundCount, result.ConcurrencyConflictCount);

            return result;
        }
    }

    private static bool MatchesInventoryTarget(SlotRow current, string targetState, string? materialId) =>
        current.SlotState == targetState
        && string.Equals(current.MaterialId, materialId, StringComparison.Ordinal);

    /// <summary>冲突快照脱敏：不携带完整 taskId（REMARK）。</summary>
    private static SlotMutationSnapshot ToConflictSnapshot(SlotRow s) =>
        new(s.Id, s.FrameId, s.SlotNo, s.SlotState, s.MaterialId, Remark: null, s.BindSource, s.BindTime);

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
