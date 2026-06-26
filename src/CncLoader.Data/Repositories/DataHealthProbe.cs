using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>数据库连通/映射自检：返回核心表行数，用于 Phase 1 验证 ORM 与连接打通。</summary>
public interface IDataHealthProbe
{
    Task<DbProbeResult> ProbeAsync(CancellationToken ct = default);
}

public sealed record DbProbeResult(bool Connected, int WorkLines, int Plcs, int Equipments, int Positions, int PlcPoints, int Frames, string? Error);

public sealed class DataHealthProbe : IDataHealthProbe
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public DataHealthProbe(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public async Task<DbProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            if (!await db.Database.CanConnectAsync(ct))
                return new DbProbeResult(false, 0, 0, 0, 0, 0, 0, "无法连接数据库");

            return new DbProbeResult(
                Connected: true,
                WorkLines: await db.WorkLines.CountAsync(ct),
                Plcs: await db.Plcs.CountAsync(ct),
                Equipments: await db.Equipments.CountAsync(ct),
                Positions: await db.Positions.CountAsync(ct),
                PlcPoints: await db.PlcPoints.CountAsync(ct),
                Frames: await db.Frames.CountAsync(ct),
                Error: null);
        }
        catch (Exception ex)
        {
            return new DbProbeResult(false, 0, 0, 0, 0, 0, 0, ex.Message);
        }
    }
}
