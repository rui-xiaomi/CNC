using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CncLoader.Data.Repositories;

/// <summary>加工记录服务实现（第四阶段⑦）：写 MAS_AUTO_WORK_RECORD，关联任务/工件/加工位/耗时。</summary>
public sealed class WorkRecordService : IWorkRecordService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    private readonly ILogger<WorkRecordService> _logger;

    public WorkRecordService(IDbContextFactory<CncDbContext> factory, ILogger<WorkRecordService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<long> RecordStartAsync(WorkRecordStartArgs args, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // 同加工位若有未结束记录（WORK_RESULT 空）→ 补结为异常（防重启残留）
        var open = await db.WorkRecords.FirstOrDefaultAsync(r => r.EquipmentId == args.EquipmentId
            && r.PositionCode == args.PositionCode && r.WorkResult == "" && r.WorkEndTime == null, ct);
        if (open is not null)
        {
            open.WorkResult = "2";
            open.WorkEndTime = DateTime.Now;
            open.Remark = "重启时未结束，自动补结";
            await db.SaveChangesAsync(ct);
        }

        var entity = new WorkRecord
        {
            WorkLineId = args.WorkLineId,
            CraftworkId = args.CraftworkId ?? 0,
            EquipmentId = args.EquipmentId,
            PositionCode = args.PositionCode,
            ElectrodeId = args.ElectrodeId,
            MaterialCode = args.MaterialCode,
            WorkStartTime = DateTime.Now,
            WorkResult = "",
            Remark = args.RcsTaskId is null ? null : $"rcsTask={args.RcsTaskId}",
            Author = args.Author,
            UpdateTime = DateTime.Now
        };
        db.WorkRecords.Add(entity);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("加工开始 EQ{Eq} POS{Pos} 电极 {El} 记录 {Id}", args.EquipmentId, args.PositionCode, args.ElectrodeId, entity.Id);
        return entity.Id;
    }

    public async Task RecordResultAsync(long recordId, string result, string? remark, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var r = await db.WorkRecords.FirstOrDefaultAsync(x => x.Id == recordId, ct);
        if (r is null) return;
        r.WorkResult = result;
        r.WorkEndTime = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(remark)) r.Remark = string.IsNullOrEmpty(r.Remark) ? remark : $"{r.Remark} | {remark}";
        r.UpdateTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("加工结束 记录 {Id} 结果 {R} 耗时 {S}s", recordId, result, r.WorkStartTime is null ? 0 : (int)(DateTime.Now - r.WorkStartTime.Value).TotalSeconds);
    }

    public async Task<WorkRecordRow?> FindOpenByPositionAsync(long equipmentId, long positionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // 按加工位精确匹配（双工位机台不串位）。PositionCode 目前是调度器写入的合成串 "POS-{positionId}"，
        // 真实 POSITION_CODE 载入留现场；两侧保持一致即可正确定位。
        var positionCode = $"POS-{positionId}";
        var r = await db.WorkRecords.AsNoTracking()
            .Where(x => x.EquipmentId == equipmentId && x.PositionCode == positionCode
                && x.WorkResult == "" && x.WorkEndTime == null)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }

    public async Task<IReadOnlyList<WorkRecordRow>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.WorkRecords.AsNoTracking().OrderByDescending(x => x.Id).Take(limit).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<WorkShiftStats> GetShiftStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var today = DateTime.Today;
        var q = db.WorkRecords.AsNoTracking().Where(x => x.WorkStartTime != null && x.WorkStartTime.Value.Date == today);
        var ok = await q.CountAsync(x => x.WorkResult == "0", ct);
        var ng = await q.CountAsync(x => x.WorkResult == "1", ct);
        var total = await q.CountAsync(ct);
        return new WorkShiftStats(ok, ng, total);
    }

    private static WorkRecordRow Map(WorkRecord r)
    {
        var elapsed = (r.WorkStartTime is not null && r.WorkEndTime is not null)
            ? (int?)(int)(r.WorkEndTime.Value - r.WorkStartTime.Value).TotalSeconds : null;
        return new WorkRecordRow(r.Id, r.EquipmentId, r.PositionCode, r.ElectrodeId, r.WorkStartTime, r.WorkEndTime, r.WorkResult, r.Remark, elapsed);
    }
}
