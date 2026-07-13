using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

public sealed class DeviceLogStore : IDeviceLogStore
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public DeviceLogStore(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public event EventHandler<DeviceLogRow>? LogAppended;

    public async Task<long> AppendAsync(DeviceLogEntry entry, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = new DeviceLog
        {
            DeviceType = entry.DeviceType.ToString().ToUpperInvariant(),
            DeviceId = entry.DeviceId,
            Action = entry.Action.ToString().ToUpperInvariant(),
            RegisterAddr = entry.RegisterAddress,
            RequestData = entry.Request,
            ResponseData = entry.Response,
            Result = entry.Success ? "0" : "1",
            CostMs = entry.CostMs,
            ErrorMsg = entry.Error,
            Author = entry.Author,
            CreateTime = DateTime.Now
        };
        db.DeviceLogs.Add(entity);
        await db.SaveChangesAsync(ct);
        var row = ToRow(entity);
        LogAppended?.Invoke(this, row);
        return entity.Id;
    }

    public async Task UpdateAsync(long id, string? response, bool success, int? costMs, string? error, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var entity = await db.DeviceLogs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (entity is null) return;
        entity.ResponseData = response;
        entity.Result = success ? "0" : "1";
        entity.CostMs = costMs;
        entity.ErrorMsg = error;
        await db.SaveChangesAsync(ct);
        LogAppended?.Invoke(this, ToRow(entity));
    }

    public async Task<IReadOnlyList<DeviceLogRow>> GetRecentAsync(long? deviceId, int limit = 50, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.DeviceLogs.AsNoTracking().AsQueryable();
        if (deviceId.HasValue) query = query.Where(x => x.DeviceId == deviceId);
        var rows = await query.OrderByDescending(x => x.CreateTime).Take(limit).ToListAsync(ct);
        return rows.Select(ToRow).ToList();
    }

    public async Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DeviceLogs
            .Where(x => x.CreateTime != null && x.CreateTime < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static DeviceLogRow ToRow(DeviceLog e)
    {
        var time = e.CreateTime ?? DateTime.Now;
        var action = e.Action switch
        {
            "WRITE" => "W",
            "READ" => "R",
            "CONNECT" => "C",
            "DISCONNECT" => "D",
            _ => e.Action[..1]
        };
        var line = e.Action == "WRITE"
            ? $"{time:HH:mm:ss} W {e.RegisterAddr}={e.RequestData} {(e.Result == "0" ? "OK" : "FAIL")} 回读={e.ResponseData}"
            : $"{time:HH:mm:ss} {action} {e.RegisterAddr} {e.ResponseData} {e.CostMs}ms";
        return new DeviceLogRow(e.Id, time, e.DeviceId, e.Action, e.RegisterAddr,
            e.RequestData, e.ResponseData, e.Result == "0", e.CostMs, e.Author, line);
    }
}
