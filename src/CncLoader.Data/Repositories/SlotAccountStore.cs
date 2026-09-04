using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 槽位外部写持久化：条件 <c>ExecuteUpdateAsync</c>（identity + SLOT_STATE != Reserved）。
/// 批量会话：同一 DbContext + 显式事务；盘点须走 <see cref="ExecuteInTransactionAsync{T}"/>，
/// 不可只用 <see cref="OpenAsync"/>（Pomelo 重试策略会拒绝事务内后续 EF 操作）。
/// </summary>
public sealed class SlotAccountStore : ISlotAccountStore
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public SlotAccountStore(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
        long frameId,
        int slotNo,
        string targetState,
        string? materialId,
        bool clearRemarkAndBindTime,
        DateTime updateTime,
        CancellationToken ct = default)
    {
        if (IsForbiddenExternalTarget(targetState))
            return new ExternalSlotWriteAttempt(0, null, InvalidTargetState: true);

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await ExecuteConditionalUpdateAsync(
            db, frameId, slotNo, targetState, materialId, clearRemarkAndBindTime, updateTime, null, ct);
    }

    public async Task<ISlotAccountSession> OpenAsync(CancellationToken ct = default)
    {
        var db = await _factory.CreateDbContextAsync(ct);
        try
        {
            var tx = await db.Database.BeginTransactionAsync(ct);
            return new EfSlotAccountSession(db, tx);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<ISlotAccountSession, CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var session = new EfSlotAccountSession(db, tx, ownsResources: false);
            try
            {
                return await work(session, ct);
            }
            finally
            {
                if (!session.IsCompleted)
                {
                    try { await session.RollbackAsync(CancellationToken.None); }
                    catch { /* 已回滚或连接已断 */ }
                }
            }
        });
    }

    private const string ReservePut = "RSV_PUT";
    private const string ReserveTake = "RSV_TAKE";

    public async Task<ReservedSlot?> ReservePutAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = DateTime.Now;
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
                return new ReservedSlot(frameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer, materialId);
        }
    }

    public async Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = DateTime.Now;
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
                return new ReservedSlot(frameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer, slot.MaterialId);
        }
    }

    public async Task<ReservedSlot?> FindReservedByTaskIdAsync(string taskId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var slot = await db.FrameSlots.AsNoTracking()
            .Where(s => s.Remark == taskId && s.SlotState == SlotStates.Reserved)
            .Select(s => new { s.FrameId, s.SlotNo, s.LayerNo, s.PosInLayer, s.MaterialId })
            .FirstOrDefaultAsync(ct);
        return slot is null
            ? null
            : new ReservedSlot(slot.FrameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer, slot.MaterialId);
    }

    public async Task<ReservedSlot?> ReserveTakeByMaterialAsync(long frameId, string taskId, string materialId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(materialId)) return null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = DateTime.Now;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var slot = await db.FrameSlots.AsNoTracking()
                .Where(s => s.FrameId == frameId
                            && s.SlotState == SlotStates.Occupied
                            && s.MaterialId == materialId)
                .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                .Select(s => new { s.Id, s.SlotNo, s.LayerNo, s.PosInLayer, s.MaterialId })
                .FirstOrDefaultAsync(ct);
            if (slot is null) return null;

            var affected = await db.FrameSlots
                .Where(s => s.Id == slot.Id
                            && s.SlotState == SlotStates.Occupied
                            && s.MaterialId == materialId)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.SlotState, SlotStates.Reserved)
                    .SetProperty(s => s.Remark, taskId)
                    .SetProperty(s => s.BindSource, ReserveTake)
                    .SetProperty(s => s.BindTime, (DateTime?)now), ct);
            if (affected == 1)
                return new ReservedSlot(frameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer, slot.MaterialId);
        }
    }

    public async Task<bool> ConfirmPutAsync(string taskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // 幂等：同 taskId 已落账占用（既有契约，不经无条件二次写）。
        var alreadyOccupied = await db.FrameSlots.AsNoTracking()
            .AnyAsync(s => s.Remark == taskId && s.SlotState == SlotStates.Occupied, ct);
        if (alreadyOccupied) return true;

        // 原子：Reserved + REMARK + BIND_SOURCE=RSV_PUT；方向不匹配 → affected=0 拒绝。
        var now = DateTime.Now;
        var affected = await db.FrameSlots
            .Where(s => s.Remark == taskId
                        && s.SlotState == SlotStates.Reserved
                        && s.BindSource == ReservePut)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.SlotState, SlotStates.Occupied)
                .SetProperty(s => s.BindSource, "CONFIRMED")
                .SetProperty(s => s.BindTime, (DateTime?)now), ct);
        return affected == 1;
    }

    public async Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // 幂等：同 taskId 已清空（既有契约）。
        var alreadyEmpty = await db.FrameSlots.AsNoTracking()
            .AnyAsync(s => s.Remark == taskId && s.SlotState == SlotStates.Empty, ct);
        if (alreadyEmpty) return true;

        // 原子：Reserved + REMARK + BIND_SOURCE=RSV_TAKE；方向不匹配 → affected=0 拒绝。
        var affected = await db.FrameSlots
            .Where(s => s.Remark == taskId
                        && s.SlotState == SlotStates.Reserved
                        && s.BindSource == ReserveTake)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.SlotState, SlotStates.Empty)
                .SetProperty(s => s.MaterialId, (string?)null)
                .SetProperty(s => s.Remark, (string?)null)
                .SetProperty(s => s.BindTime, (DateTime?)null), ct);
        return affected == 1;
    }

    public async Task<bool> RollbackPutAsync(string taskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // 原子：仅 RSV_PUT 预记可回滚为空；RSV_TAKE 方向不匹配 → affected=0。
        var affected = await db.FrameSlots
            .Where(s => s.Remark == taskId
                        && s.SlotState == SlotStates.Reserved
                        && s.BindSource == ReservePut)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.SlotState, SlotStates.Empty)
                .SetProperty(s => s.MaterialId, (string?)null)
                .SetProperty(s => s.Remark, (string?)null)
                .SetProperty(s => s.BindTime, (DateTime?)null), ct);
        return affected == 1;
    }

    public async Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        // 原子：仅 RSV_TAKE 预记可回滚为占用；RSV_PUT 方向不匹配 → affected=0。
        var affected = await db.FrameSlots
            .Where(s => s.Remark == taskId
                        && s.SlotState == SlotStates.Reserved
                        && s.BindSource == ReserveTake)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.SlotState, SlotStates.Occupied)
                .SetProperty(s => s.Remark, (string?)null)
                .SetProperty(s => s.BindTime, (DateTime?)null), ct);
        return affected == 1;
    }

    private static bool IsForbiddenExternalTarget(string? targetState) =>
        string.Equals((targetState ?? string.Empty).Trim(), SlotStates.Reserved, StringComparison.Ordinal);

    private static async Task<ExternalSlotWriteAttempt> ExecuteConditionalUpdateAsync(
        CncDbContext db,
        long frameId,
        int slotNo,
        string targetState,
        string? materialId,
        bool clearRemarkAndBindTime,
        DateTime updateTime,
        InventorySlotWriteExtras? extras,
        CancellationToken ct)
    {
        if (IsForbiddenExternalTarget(targetState))
            return new ExternalSlotWriteAttempt(0, null, InvalidTargetState: true);

        int affected;
        if (clearRemarkAndBindTime)
        {
            // 与单槽 SetSlot Empty 语义一致：清 Remark/BindTime，不碰 BIND_SOURCE。
            affected = await db.FrameSlots
                .Where(s => s.FrameId == frameId && s.SlotNo == slotNo && s.SlotState != SlotStates.Reserved)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.SlotState, targetState)
                    .SetProperty(s => s.MaterialId, materialId)
                    .SetProperty(s => s.Remark, (string?)null)
                    .SetProperty(s => s.BindTime, (DateTime?)null)
                    .SetProperty(s => s.UpdateTime, (DateTime?)updateTime), ct);
        }
        else if (extras is not null)
        {
            var lastVerify = extras.LastVerifyTime;
            if (extras.ApplyBindFields)
            {
                var bindSource = extras.BindSource;
                var bindTime = extras.BindTime;
                affected = await db.FrameSlots
                    .Where(s => s.FrameId == frameId && s.SlotNo == slotNo && s.SlotState != SlotStates.Reserved)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.SlotState, targetState)
                        .SetProperty(s => s.MaterialId, materialId)
                        .SetProperty(s => s.UpdateTime, (DateTime?)updateTime)
                        .SetProperty(s => s.BindSource, bindSource)
                        .SetProperty(s => s.BindTime, bindTime)
                        .SetProperty(s => s.LastVerifyTime, lastVerify), ct);
            }
            else
            {
                // 盘点空码：只改 STATE/物料/时间，不碰 Remark/BIND_SOURCE。
                affected = await db.FrameSlots
                    .Where(s => s.FrameId == frameId && s.SlotNo == slotNo && s.SlotState != SlotStates.Reserved)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(s => s.SlotState, targetState)
                        .SetProperty(s => s.MaterialId, materialId)
                        .SetProperty(s => s.UpdateTime, (DateTime?)updateTime)
                        .SetProperty(s => s.LastVerifyTime, lastVerify), ct);
            }
        }
        else
        {
            affected = await db.FrameSlots
                .Where(s => s.FrameId == frameId && s.SlotNo == slotNo && s.SlotState != SlotStates.Reserved)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(s => s.SlotState, targetState)
                    .SetProperty(s => s.MaterialId, materialId)
                    .SetProperty(s => s.UpdateTime, (DateTime?)updateTime), ct);
        }

        var current = await db.FrameSlots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.FrameId == frameId && s.SlotNo == slotNo, ct);
        return new ExternalSlotWriteAttempt(affected, current is null ? null : FromEntity(current));
    }

    private static SlotRow FromEntity(FrameSlot e) => new()
    {
        Id = e.Id,
        FrameId = e.FrameId,
        SlotNo = e.SlotNo,
        LayerNo = e.LayerNo,
        PosInLayer = e.PosInLayer,
        SlotState = e.SlotState,
        MaterialId = e.MaterialId,
        BindTime = e.BindTime,
        BindSource = e.BindSource,
        LastVerifyTime = e.LastVerifyTime,
        Remark = e.Remark,
        UpdateTime = e.UpdateTime,
    };

    private sealed class EfSlotAccountSession : ISlotAccountSession
    {
        private readonly CncDbContext _db;
        private readonly IDbContextTransaction _tx;
        private readonly bool _ownsResources;
        private bool _completed;

        public bool IsCompleted => _completed;

        public EfSlotAccountSession(CncDbContext db, IDbContextTransaction tx, bool ownsResources = true)
        {
            _db = db;
            _tx = tx;
            _ownsResources = ownsResources;
        }

        public async Task<SlotRow?> FindByFrameSlotAsync(long frameId, int slotNo, CancellationToken ct = default)
        {
            var entity = await _db.FrameSlots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.FrameId == frameId && s.SlotNo == slotNo, ct);
            return entity is null ? null : FromEntity(entity);
        }

        public async Task<IReadOnlyList<SlotRow>> FindByFrameOrderedAsync(long frameId, CancellationToken ct = default)
        {
            var entities = await _db.FrameSlots.AsNoTracking()
                .Where(s => s.FrameId == frameId)
                .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                .ToListAsync(ct);
            return entities.Select(FromEntity).ToList();
        }

        public Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
            long frameId,
            int slotNo,
            string targetState,
            string? materialId,
            bool clearRemarkAndBindTime,
            DateTime updateTime,
            CancellationToken ct = default,
            InventorySlotWriteExtras? extras = null)
            => ExecuteConditionalUpdateAsync(
                _db, frameId, slotNo, targetState, materialId, clearRemarkAndBindTime, updateTime, extras, ct);

        public async Task CommitAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await _tx.CommitAsync(ct);
            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            if (_completed) return;
            await _tx.RollbackAsync(ct);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                try { await _tx.RollbackAsync(); }
                catch { /* dispose 路径尽力回滚 */ }
                _completed = true;
            }

            if (!_ownsResources) return;

            await _tx.DisposeAsync();
            await _db.DisposeAsync();
        }
    }
}
