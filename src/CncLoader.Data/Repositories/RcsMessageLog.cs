using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>RCS 双向报文流水落库（MAS_AUTO_RCS_MSG_LOG）。</summary>
public sealed class RcsMessageLog : IRcsMessageLog
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public RcsMessageLog(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.RcsMsgLogs.Add(new RcsMsgLog
        {
            Direction = entry.Direction,
            InterfaceName = entry.Interface,
            Url = entry.Url,
            TaskId = entry.TaskId,
            RequestBody = Trim(entry.RequestBody),
            ResponseBody = Trim(entry.ResponseBody),
            CostMs = entry.CostMs,
            Result = entry.Success ? "0" : "1",
            ErrorMsg = entry.Error is null ? null : (entry.Error.Length > 500 ? entry.Error[..500] : entry.Error),
            CreateTime = DateTime.Now
        });
        await db.SaveChangesAsync(ct);
    }

    public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
        => QueryAsync(new RcsMsgQuery { Limit = limit }, ct);

    public async Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.RcsMsgLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Direction))
            q = q.Where(m => m.Direction == query.Direction);
        if (!string.IsNullOrWhiteSpace(query.Interface))
            q = q.Where(m => m.InterfaceName == query.Interface);
        if (!string.IsNullOrWhiteSpace(query.TaskId))
        {
            var t = query.TaskId.Trim();
            q = q.Where(m => m.TaskId != null && m.TaskId.Contains(t));
        }

        var limit = Math.Clamp(query.Limit, 1, 5000);
        var rows = await q.OrderByDescending(m => m.Id).Take(limit).ToListAsync(ct);
        return rows.Select(m => new RcsMsgRow(
            m.Id, m.CreateTime ?? DateTime.MinValue, m.Direction, m.InterfaceName, m.TaskId,
            m.RequestBody, m.ResponseBody, m.CostMs, m.Result == "0", m.ErrorMsg)).ToList();
    }

    private static string? Trim(string? s)
        => s is null ? null : (s.Length > 60000 ? s[..60000] : s);
}
