using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>下料入队与终点/抓取规划。终点决策在单消费者内串行调用。</summary>
internal sealed class UnloadDispatchPlanner
{
    private readonly IEquipmentConfigService _equipment;
    private readonly IRouteResolver _routes;
    private readonly IAlarmEventService _alarms;
    private readonly IDispatchQueue _queue;
    private readonly ILogger _logger;
    private readonly Func<long, CancellationToken, Task<EquipmentFrameBindingIds>> _resolveBindings;
    private readonly Func<long, CancellationToken, Task<WorkLineRef?>> _resolveLine;
    private readonly Action<long, string> _logRouteUnavailable;

    public UnloadDispatchPlanner(
        IEquipmentConfigService equipment,
        IRouteResolver routes,
        IAlarmEventService alarms,
        IDispatchQueue queue,
        ILogger logger,
        Func<long, CancellationToken, Task<EquipmentFrameBindingIds>> resolveBindings,
        Func<long, CancellationToken, Task<WorkLineRef?>> resolveLine,
        Action<long, string> logRouteUnavailable)
    {
        _equipment = equipment;
        _routes = routes;
        _alarms = alarms;
        _queue = queue;
        _logger = logger;
        _resolveBindings = resolveBindings;
        _resolveLine = resolveLine;
        _logRouteUnavailable = logRouteUnavailable;
    }

    public async Task<bool> EnqueueUnloadAsync(PositionContext ctx, bool isOk, CancellationToken ct)
    {
        var fromCell = await _routes.ResolvePositionCellAsync(ctx.EquipmentId, ctx.PositionId, ct);
        if (fromCell is null)
        {
            await _alarms.RaiseRcsTaskNotFoundAsync($"UNLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}",
                $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 下料源 cell 未配置（LOCATION_MAP 缺加工位 cell）", ct);
            _logger.LogWarning("EQ{Eq} POS{Pos} 下料源 cell 未配置（LOCATION_MAP 缺加工位 cell）", ctx.EquipmentId, ctx.PositionId);
            return false;
        }
        var line = await _resolveLine(ctx.EquipmentId, ct);
        if (line is null)
        {
            _logger.LogWarning("EQ{Eq} POS{Pos} 下料入队失败：线体路由不可用，不入队",
                ctx.EquipmentId, ctx.PositionId);
            return false;
        }
        _queue.Enqueue(new DispatchItem
        {
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId, Phase = PositionPhase.Unload,
            Priority = 8, FromCode = fromCell, ToCode = "", IsOk = isOk,
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, Author = "scheduler",
            MaterialId = ctx.MaterialId
        });
        ctx.Phase = PositionPhase.Unload;
        ctx.CurrentTaskId = null;
        _logger.LogInformation("EQ{Eq} POS{Pos} 入下料队 from={From} isOk={Ok}（终点由消费者决策）", ctx.EquipmentId, ctx.PositionId, fromCell, isOk);
        return true;
    }

    public async Task<UnloadDecision?> ResolveUnloadTargetAsync(PositionContext ctx, bool isOk, CancellationToken ct)
    {
        if (!isOk)
        {
            var ngFrame = await _equipment.GetFrameBindingByRoleAsync(ctx.EquipmentId, FrameRole.NgFrame, ct);
            if (ngFrame is long ng)
            {
                var shelf = await _routes.ResolveFrameShelfAsync(ng, ct);
                if (shelf is not null) return new UnloadDecision(shelf, UnloadTarget.NgFrame, ng, null, null);
            }
            _logger.LogWarning("EQ{Eq} POS{Pos} NG 但未绑定 NG 架（role3），回退下料架", ctx.EquipmentId, ctx.PositionId);
            return await ResolveDownloadFrameFallbackAsync(ctx, ct);
        }

        var nextEqs = await _equipment.GetNextProcessEquipmentsAsync(ctx.EquipmentId, ct);
        if (nextEqs.Count == 0)
        {
            if (await _equipment.HasSubsequentProcessAsync(ctx.EquipmentId, ct))
            {
                _logRouteUnavailable(ctx.EquipmentId,
                    $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 下一工序已配置但无活动可用机台，拒绝下料（不回退命名区）");
                return null;
            }
            return await ResolveDownloadFrameFallbackAsync(ctx, ct);
        }

        var nextEq = nextEqs[0];
        var ownUnload = await TryBoundFrameAsync(ctx.EquipmentId, FrameRole.Unload, UnloadTarget.DownloadFrame, nextEq, ct);
        if (ownUnload is not null) return ownUnload;

        foreach (var eq in nextEqs)
        {
            var viaUpload = await TryBoundFrameAsync(eq, FrameRole.Upload, UnloadTarget.DownloadFrame, eq, ct);
            if (viaUpload is not null) return viaUpload;
            var viaTransit = await TryBoundFrameAsync(eq, FrameRole.Transit, UnloadTarget.TransitFrame, eq, ct);
            if (viaTransit is not null) return viaTransit;
        }

        _logger.LogWarning("EQ{Eq} POS{Pos} OK 且有下一工序，但本机无下料架、下游无上料/中转架，拒绝下料",
            ctx.EquipmentId, ctx.PositionId);
        return null;
    }

    public static bool RequiresUnloadReservation(UnloadDecision decision) =>
        decision.Target == UnloadTarget.NextMachineCell || decision.DestFrameId is not null;

    public static bool PreferGrabForUnload(UnloadDecision d, bool usesGrab)
        => usesGrab && (d.DestFrameId is not null || d.Target == UnloadTarget.NextMachineCell);

    public static DispatchRouteContext BuildUnloadRouteContext(DispatchItem item, UnloadDecision d)
    {
        var destEq = d.DestEquipmentId is long de
            ? RouteDependency.Required(de)
            : RouteDependency.NotApplicable;
        var destPos = d.DestPositionId is long dp
            ? RouteDependency.Required(dp)
            : RouteDependency.NotApplicable;
        var destFrame = d.DestFrameId is long df
            ? RouteDependency.Required(df)
            : RouteDependency.NotApplicable;

        return new DispatchRouteContext
        {
            SourceEquipmentId = item.EquipmentId,
            SourcePositionId = item.PositionId,
            DestEquipmentId = destEq,
            DestPositionId = destPos,
            DestFrameId = destFrame,
            FromCode = item.FromCode,
            ToCode = d.ToCell,
            RequiresResolvedCells = true
        };
    }

    public async Task<GrabDispatchArgs?> TryBuildUnloadGrabArgsAsync(
        string taskId,
        long workLineId,
        string lineCode,
        DispatchItem item,
        UnloadDecision d,
        UnloadReservation? reserved,
        CancellationToken ct)
    {
        var srcStation = await _routes.ResolvePositionStationAsync(item.EquipmentId, item.PositionId, ct);
        var srcCell = await _routes.ResolvePositionCellAsync(item.EquipmentId, item.PositionId, ct);
        if (srcStation is null || srcCell is null)
            return null;
        if (!RcsCellCode.TryParse(srcCell, srcStation, out var srcLayer, out var srcPos))
            return null;

        string? dstStation;
        int dstLayer;
        int dstPos;
        if (d.Target == UnloadTarget.NextMachineCell
            && d.DestEquipmentId is long destEq
            && d.DestPositionId is long destPosition)
        {
            dstStation = await _routes.ResolvePositionStationAsync(destEq, destPosition, ct);
            var dstCell = await _routes.ResolvePositionCellAsync(destEq, destPosition, ct);
            if (dstStation is null || dstCell is null)
                return null;
            if (!RcsCellCode.TryParse(dstCell, dstStation, out dstLayer, out dstPos))
                return null;
        }
        else if (reserved?.Slot is { } put && d.DestFrameId is long destFrame)
        {
            dstStation = await _routes.ResolveFrameShelfAsync(destFrame, ct);
            if (dstStation is null)
                return null;
            dstLayer = put.LayerNo;
            dstPos = put.PosInLayer;
        }
        else
            return null;

        var grabItem = RcsGrabHole.TryBuild(
            srcStation, srcLayer, srcPos,
            dstStation, dstLayer, dstPos, item.MaterialId);
        if (grabItem is null)
            return null;

        return new GrabDispatchArgs
        {
            TaskId = taskId,
            WorkLineId = workLineId,
            LineCode = lineCode,
            TaskType = "1",
            Priority = item.Priority,
            SrcStation = srcStation,
            DstStation = dstStation,
            Items = new[] { grabItem },
            EquipmentId = item.EquipmentId,
            PositionId = item.PositionId,
            MaterialId = item.MaterialId,
            Author = item.Author
        };
    }

    private async Task<UnloadDecision?> TryBoundFrameAsync(
        long boundEquipmentId, FrameRole role, UnloadTarget target, long destEquipmentId, CancellationToken ct)
    {
        var frameId = await _equipment.GetFrameBindingByRoleAsync(boundEquipmentId, role, ct);
        if (frameId is not long id) return null;
        var shelf = await _routes.ResolveFrameShelfAsync(id, ct);
        return shelf is null ? null : new UnloadDecision(shelf, target, id, destEquipmentId, null);
    }

    private async Task<UnloadDecision?> ResolveDownloadFrameFallbackAsync(PositionContext ctx, CancellationToken ct)
    {
        var binds = await _resolveBindings(ctx.EquipmentId, ct);
        if (binds.DownloadFrameId is long df)
        {
            var shelf = await _routes.ResolveFrameShelfAsync(df, ct);
            if (shelf is not null) return new UnloadDecision(shelf, UnloadTarget.DownloadFrame, df, null, null);
        }
        var route = await _routes.ResolveUnloadAsync(ctx.EquipmentId, ctx.PositionId, ct);
        if (route is not null) return new UnloadDecision(route.Value.to, UnloadTarget.DownloadFrame, null, null, null);
        return null;
    }
}
