using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>RCS 任务落库仓储（MAS_AUTO_AGV_TASK）。支持先落库后发送与状态推进。</summary>
public sealed class RcsTaskStore : IRcsTaskStore
{
    private static readonly string[] Unfinished =
        { RcsTaskState.Created, RcsTaskState.Dispatched, RcsTaskState.Executing };

    private readonly IDbContextFactory<CncDbContext> _factory;

    public RcsTaskStore(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = new AgvTask
        {
            RcsTaskId = record.RcsTaskId,
            RcsKind = RcsTaskKindNames.ToDbKind(record.Kind),
            WorkLineId = record.WorkLineId,
            TaskType = record.TaskType,
            TaskStatus = "0",
            TaskState = RcsTaskState.Created,
            Priority = record.Priority,
            FromFrameCode = record.FromCode,
            ToFrameCode = record.ToCode,
            EquipmentId = record.EquipmentId,
            PositionId = record.PositionId,
            CraftworkId = record.CraftworkId,
            ElectrodeId = record.ElectrodeId,
            TxnId = record.TxnId,
            ReqParam = record.ReqParam,
            SendTime = DateTime.Now,
            RedoCount = 0,
            CancelManualFlag = "0",
            Author = record.Author
        };
        db.AgvTasks.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity.Id;
    }

    public async Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var t = await db.AgvTasks.FirstOrDefaultAsync(x => x.RcsTaskId == rcsTaskId, ct);
        if (t is null) return;
        t.TaskState = RcsTaskState.Dispatched;
        t.TaskStatus = "1";
        t.DispatchTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null,
        string? error = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var t = await db.AgvTasks.FirstOrDefaultAsync(x => x.RcsTaskId == rcsTaskId, ct);
        if (t is null) return;
        t.TaskState = taskState;
        if (rcsStatus is not null) t.RcsStatus = rcsStatus;
        if (error is not null) t.ErrorMsg = error.Length > 500 ? error[..500] : error;
        t.TaskStatus = taskState switch
        {
            RcsTaskState.Completed => "2",
            RcsTaskState.Failed => "3",
            RcsTaskState.Executing => "1",
            _ => t.TaskStatus
        };
        if (taskState is RcsTaskState.Completed or RcsTaskState.Canceled)
            t.FinishTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
    }

    public async Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var t = await db.AgvTasks.FirstOrDefaultAsync(x => x.RcsTaskId == rcsTaskId, ct);
        if (t is null) return;
        t.RedoCount += 1;
        t.TaskState = RcsTaskState.Dispatched;
        t.ErrorMsg = null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryIncrementRedoIfUnderAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var t = await db.AgvTasks.FirstOrDefaultAsync(x => x.RcsTaskId == rcsTaskId, ct);
        if (t is null) return false;
        if (t.RedoCount >= maxRedo) return false;
        t.RedoCount += 1;
        t.TaskState = RcsTaskState.Dispatched;
        t.TaskStatus = "1";
        t.ErrorMsg = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var t = await db.AgvTasks.FirstOrDefaultAsync(x => x.RcsTaskId == rcsTaskId, ct);
        if (t is null) return;
        t.CancelManualFlag = "1";
        await db.SaveChangesAsync(ct);
    }

    public async Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var t = await db.AgvTasks.AsNoTracking().FirstOrDefaultAsync(x => x.RcsTaskId == rcsTaskId, ct);
        return t is null ? null : Map(t);
    }

    public async Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.AgvTasks.AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AgvTasks.AsNoTracking()
            .Where(x => x.RcsTaskId != null && Unfinished.Contains(x.TaskState))
            .Select(x => x.RcsTaskId!)
            .ToListAsync(ct);
    }

    private static RcsTaskRow Map(AgvTask t) => new(
        t.Id, t.RcsTaskId, t.RcsKind, t.TaskState, t.RcsStatus, t.Priority,
        t.FromFrameCode, t.ToFrameCode, t.EquipmentId, t.PositionId, t.ElectrodeId, t.TxnId,
        t.ReqParam, t.RedoCount, t.CancelManualFlag, t.SendTime, t.DispatchTime, t.FinishTime, t.ErrorMsg);
}
