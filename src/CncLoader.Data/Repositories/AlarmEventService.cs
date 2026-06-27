using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class AlarmEventService : IAlarmEventService
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public AlarmEventService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public event EventHandler<AlarmRow>? AlarmRaised;

    public async Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var eq = await db.Equipments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.PlcId == plcId && e.State == "0", ct);

        var entity = new AlarmEvent
        {
            EquipmentId = eq?.Id,
            AlarmType = "PLC_COMM",
            AlarmLevel = level,
            AlarmMsg = message,
            AlarmState = "0",
            CreateTime = DateTime.Now
        };
        db.AlarmEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        var row = new AlarmRow(entity.Id, entity.CreateTime ?? DateTime.Now,
            level == "2" ? "严重" : "警告", message, "未处理");
        AlarmRaised?.Invoke(this, row);
    }

    public async Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.AlarmEvents.AsNoTracking()
            .OrderByDescending(a => a.CreateTime)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(a => new AlarmRow(
            a.Id, a.CreateTime ?? DateTime.MinValue,
            a.AlarmLevel == "2" ? "严重" : "警告",
            a.AlarmMsg,
            a.AlarmState == "0" ? "未处理" : "已处理")).ToList();
    }
}
