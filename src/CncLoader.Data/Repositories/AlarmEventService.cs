using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class AlarmEventService : IAlarmEventService
{
    private readonly IDbContextFactory<CncDbContext> _factory;
    private readonly object _plcAlarmGate = new();
    private readonly Dictionary<long, DateTime> _lastPlcAlarmAt = new();
    private static readonly TimeSpan PlcAlarmThrottle = TimeSpan.FromSeconds(30);

    public AlarmEventService(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public event EventHandler<AlarmRow>? AlarmRaised;
    public event EventHandler? AlarmsChanged;

    public async Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default)
    {
        // 同 PLC 30s 内只落库+弹一次，避免连接/编辑后读点位刷屏。
        lock (_plcAlarmGate)
        {
            var now = DateTime.UtcNow;
            if (_lastPlcAlarmAt.TryGetValue(plcId, out var last) && now - last < PlcAlarmThrottle)
                return;
            _lastPlcAlarmAt[plcId] = now;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var eq = await db.Equipments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.PlcId == plcId && e.State == "0", ct);

        var entity = new AlarmEvent
        {
            EquipmentId = eq?.Id,
            AlarmType = "PLC_COMM",
            AlarmLevel = level,
            AlarmMsg = message.Length > 500 ? message[..500] : message,
            AlarmState = "0",
            CreateTime = DateTime.Now
        };
        db.AlarmEvents.Add(entity);
        await db.SaveChangesAsync(ct);

        var row = new AlarmRow(entity.Id, entity.CreateTime ?? DateTime.Now,
            level == "2" ? "严重" : "警告", entity.AlarmMsg, "未处理", "PLC_COMM");
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

        var row = new AlarmRow(entity.Id, entity.CreateTime ?? DateTime.Now, "严重", msg, "未处理", "RCS_WARN");
        AlarmRaised?.Invoke(this, row);
        return entity.Id;
    }

    public async Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default)
    {
        var detail = string.IsNullOrWhiteSpace(reason)
            ? "需人工处理小车/容器并确认（确认前锁定相关点位派工）"
            : reason.Trim();
        return await RaiseRcsTaskAlarmAsync("RCS_CANCELED", "2", $"任务 {rcsTaskId} 已取消：{detail}", ct);
    }

    public async Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default)
    {
        var detail = string.IsNullOrWhiteSpace(reason)
            ? "RCS 侧查无此任务，需人工介入"
            : reason.Trim();
        return await RaiseRcsTaskAlarmAsync("RCS_NOT_FOUND", "1", $"任务 {rcsTaskId}：{detail}", ct);
    }

    public async Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, string? reason = null, CancellationToken ct = default)
    {
        var baseMsg = $"任务 {rcsTaskId} 自动重做已达上限 {maxRedo} 次，需人工介入";
        if (!string.IsNullOrWhiteSpace(reason)) baseMsg += $"（{reason.Trim()}）";
        return await RaiseRcsTaskAlarmAsync("RCS_REDO_LIMIT", "2", baseMsg, ct);
    }

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
            level == "2" ? "严重" : "警告", msg, "未处理", alarmType);
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
        return rows.Select(ToRow).ToList();
    }

    public async Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.AlarmEvents.AsNoTracking().AsQueryable();
        if (unhandledOnly) query = query.Where(a => a.AlarmState == "0");
        var rows = await query.OrderByDescending(a => a.CreateTime).Take(limit).ToListAsync(ct);
        return rows.Select(ToRow).ToList();
    }

    public async Task MarkHandledAsync(long id, string author, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var a = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return;
        a.AlarmState = "1";
        a.Handler = author;
        a.HandleTime = DateTime.Now;
        await db.SaveChangesAsync(ct);
        AlarmsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var n = await db.AlarmEvents.ExecuteDeleteAsync(ct);
        AlarmsChanged?.Invoke(this, EventArgs.Empty);
        return n;
    }

    public async Task<int> GetUnhandledCountAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AlarmEvents.AsNoTracking().CountAsync(a => a.AlarmState == "0", ct);
    }

    private static AlarmRow ToRow(AlarmEvent a) => new(
        a.Id, a.CreateTime ?? DateTime.MinValue,
        a.AlarmLevel == "2" ? "严重" : "警告",
        a.AlarmMsg,
        a.AlarmState == "0" ? "未处理" : "已处理",
        a.AlarmType);
}
