using System.Collections.Concurrent;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 按 PLC 分组驱动：跨 PLC 并发、组内串行。一台 PLC 超时只拖本组工位。
/// </summary>
internal sealed class PositionDriveCoordinator
{
    private readonly ILogger _logger;

    public PositionDriveCoordinator(ILogger logger) => _logger = logger;

    public async Task DriveAllAsync(
        IReadOnlyList<(long Eq, long Pos, long PlcId)> positions,
        ConcurrentDictionary<(long Eq, long Pos), PositionContext> contexts,
        Func<CancellationToken, Task> warmBoundTaskRowsAsync,
        Func<PositionContext, CancellationToken, Task> drivePositionAsync,
        Action clearTickTaskRows,
        CancellationToken ct)
    {
        foreach (var (eq, pos, _) in positions)
            contexts.GetOrAdd((eq, pos), k => new PositionContext { EquipmentId = k.Eq, PositionId = k.Pos, State = PositionState.Offline });

        await warmBoundTaskRowsAsync(ct);
        try
        {
            var plcOf = new Dictionary<(long Eq, long Pos), long>();
            foreach (var (eq, pos, plcId) in positions) plcOf[(eq, pos)] = plcId;
            var groups = contexts.Values
                .GroupBy(c => plcOf.TryGetValue((c.EquipmentId, c.PositionId), out var plcId) ? plcId : 0L)
                .Select(g => DriveGroupAsync(g.ToList(), drivePositionAsync, ct))
                .ToList();
            await Task.WhenAll(groups);
        }
        finally
        {
            clearTickTaskRows();
        }
    }

    public async Task DriveGroupAsync(
        IReadOnlyList<PositionContext> contexts,
        Func<PositionContext, CancellationToken, Task> drivePositionAsync,
        CancellationToken ct)
    {
        foreach (var ctx in contexts)
        {
            try
            {
                await drivePositionAsync(ctx, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _logger.LogWarning(ex, "驱动 EQ{Eq} POS{Pos} 异常", ctx.EquipmentId, ctx.PositionId); }
        }
    }
}
