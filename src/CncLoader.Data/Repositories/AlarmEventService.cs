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

    public async Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent,
        string? taskCode, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        var msg = $"[车{robotCode}] {warnContent}";
        if (!string.IsNullOrWhiteSpace(taskCode)) msg += $"（任务 {taskCode}）";
        if (!string.IsNullOrWhiteSpace(beginTime)) msg += $" @ {beginTime}";
        if (msg.Length > 500) msg = msg[..500];

        var entity = new AlarmEvent
        {
            AlarmType = "RCS_WARN",
            AlarmLevel = "2", // RCS 严重告警会导致 AGV 停机，按严重处理
            AlarmMsg = msg,
            AlarmState = "0",
            CreateTime = DateTime.Now
        };
        db.AlarmEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        var row = new AlarmRow(entity.Id, entity.CreateTime ?? DateTime.Now, "严重", msg, "未处理");
        AlarmRaised?.Invoke(this, row);
        return entity.Id;
    }

    public async Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, CancellationToken ct = default)
        => await RaiseRcsTaskAlarmAsync("RCS_CANCELED", "2", $"任务 {rcsTaskId} 已取消，需人工处理小车/容器并确认（确认前锁定相关点位派工）", ct);

    public async Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, CancellationToken ct = default)
        => await RaiseRcsTaskAlarmAsync("RCS_NOT_FOUND", "1", $"任务 {rcsTaskId} 在 RCS 侧查无此任务，需人工介入", ct);

    public async Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
        => await RaiseRcsTaskAlarmAsync("RCS_REDO_LIMIT", "2", $"任务 {rcsTaskId} 自动重做已达上限 {maxRedo} 次，需人工介入", ct);

    private async Task<long> RaiseRcsTaskAlarmAsync(string alarmType, string level, string msg, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = new AlarmEvent
        {
            AlarmType = alarmType,
            AlarmLevel = level,
            AlarmMsg = msg.Length > 500 ? msg[..500] : msg,
            AlarmState = "0",
            CreateTime = DateTime.Now
        };
        db.AlarmEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        var row = new AlarmRow(entity.Id, entity.CreateTime ?? DateTime.Now,
            level == "2" ? "严重" : "警告", msg, "未处理");
        AlarmRaised?.Invoke(this, row);
        return entity.Id;
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
