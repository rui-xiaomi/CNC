using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>上料料源与抓取参数规划。不下发、不预记。</summary>
internal sealed class UploadDispatchPlanner
{
    private readonly IEquipmentConfigService _equipment;
    private readonly ISlotAccountService _slots;
    private readonly IRouteResolver _routes;
    private readonly IAlarmEventService _alarms;
    private readonly ILogger _logger;
    private readonly Func<long, CancellationToken, Task<EquipmentFrameBindingIds>> _resolveBindings;

    public UploadDispatchPlanner(
        IEquipmentConfigService equipment,
        ISlotAccountService slots,
        IRouteResolver routes,
        IAlarmEventService alarms,
        ILogger logger,
        Func<long, CancellationToken, Task<EquipmentFrameBindingIds>> resolveBindings)
    {
        _equipment = equipment;
        _slots = slots;
        _routes = routes;
        _alarms = alarms;
        _logger = logger;
        _resolveBindings = resolveBindings;
    }

    /// <summary>本机上料架(role0)；无上料绑定时才回退 role2。无料源占用 → WaitMaterial；路由未配置 → Failed。</summary>
    public async Task<UploadPlan> ResolveUploadPlanAsync(PositionContext ctx, CancellationToken ct)
    {
        var binds = await _resolveBindings(ctx.EquipmentId, ct);
        long? sourceFrameId = binds.UploadFrameId;
        if (sourceFrameId is null)
            sourceFrameId = await _equipment.GetFrameBindingByRoleAsync(ctx.EquipmentId, FrameRole.Transit, ct);

        if (sourceFrameId is not long srcFrame)
        {
            _logger.LogDebug("EQ{Eq} POS{Pos} 无上料架绑定，等待上游入架", ctx.EquipmentId, ctx.PositionId);
            return new UploadPlan(UploadDecision.WaitMaterial, null, null, null);
        }

        var occ = await _slots.GetOccupancyAsync(srcFrame, ct);
        if (occ.Occupied == 0)
        {
            _logger.LogDebug("EQ{Eq} POS{Pos} 上料架 {Frame} 无料（账面 occupied=0），保持等料",
                ctx.EquipmentId, ctx.PositionId, srcFrame);
            return new UploadPlan(UploadDecision.WaitMaterial, null, null, null);
        }

        var posCell = await _routes.ResolvePositionCellAsync(ctx.EquipmentId, ctx.PositionId, ct);
        var shelf = await _routes.ResolveFrameShelfAsync(srcFrame, ct);
        if (posCell is null || shelf is null)
        {
            await _alarms.RaiseRcsTaskNotFoundAsync($"UPLOAD-EQ{ctx.EquipmentId}-POS{ctx.PositionId}",
                $"EQ{ctx.EquipmentId} POS{ctx.PositionId} 上料路由未配置（LOCATION_MAP 缺上料架 shelf 或加工位 cell）", ct);
            _logger.LogWarning("EQ{Eq} POS{Pos} 上料路由未配置（LOCATION_MAP 缺上料架 shelf/加工位 cell）", ctx.EquipmentId, ctx.PositionId);
            return new UploadPlan(UploadDecision.Failed, null, null, null);
        }

        return new UploadPlan(UploadDecision.Queued, srcFrame, shelf, posCell);
    }

    public async Task<GrabDispatchArgs?> TryBuildUploadGrabArgsAsync(
        string taskId,
        long workLineId,
        string lineCode,
        string srcStation,
        ReservedSlot slot,
        long equipmentId,
        long positionId,
        CancellationToken ct)
    {
        var dstStation = await _routes.ResolvePositionStationAsync(equipmentId, positionId, ct);
        var dstCell = await _routes.ResolvePositionCellAsync(equipmentId, positionId, ct);
        if (dstStation is null || dstCell is null)
            return null;
        if (!RcsCellCode.TryParse(dstCell, dstStation, out var dstLayer, out var dstPos))
            return null;
        var item = RcsGrabHole.TryBuild(
            srcStation, slot.LayerNo, slot.PosInLayer,
            dstStation, dstLayer, dstPos, slot.MaterialId);
        if (item is null)
            return null;

        return new GrabDispatchArgs
        {
            TaskId = taskId,
            WorkLineId = workLineId,
            LineCode = lineCode,
            TaskType = "0",
            Priority = 5,
            SrcStation = srcStation,
            DstStation = dstStation,
            Items = new[] { item },
            EquipmentId = equipmentId,
            PositionId = positionId,
            MaterialId = slot.MaterialId,
            Author = "scheduler"
        };
    }
}
