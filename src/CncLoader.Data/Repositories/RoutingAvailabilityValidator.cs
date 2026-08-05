using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 权威路由活动校验：按端点 Kind 决定 Required/NotApplicable；不读调度 _lineCache。
/// </summary>
public sealed class RoutingAvailabilityValidator : IRoutingAvailabilityValidator
{
    private readonly IEquipmentRoutingStore _routing;
    private readonly IEquipmentConfigService _equipment;
    private readonly IFrameRoutingStore _frames;
    private readonly ILogger<RoutingAvailabilityValidator> _logger;

    public RoutingAvailabilityValidator(
        IEquipmentRoutingStore routing,
        IEquipmentConfigService equipment,
        IFrameRoutingStore frames,
        ILogger<RoutingAvailabilityValidator> logger)
    {
        _routing = routing;
        _equipment = equipment;
        _frames = frames;
        _logger = logger;
    }

    public async Task<RoutingAvailabilityResult> ValidateAsync(
        DispatchRouteContext context, CancellationToken ct = default)
    {
        try
        {
            WorkLineRef? lineRef = null;

            var fromKind = context.FromEndpoint?.Kind;
            var toKind = context.ToEndpoint?.Kind;

            // 未知 Kind（有 endpoint 对象但 Kind 异常）fail-closed — enum 穷尽，缺字段走下方
            if (context.FromEndpoint is not null)
            {
                var fromFail = await ValidateTypedEndpointAsync(context.FromEndpoint, "From", ct);
                if (fromFail is not null) return fromFail;
            }

            if (context.ToEndpoint is not null)
            {
                var toFail = await ValidateTypedEndpointAsync(context.ToEndpoint, "To", ct);
                if (toFail is not null) return toFail;
            }

            // POSITION / 遗留机台上下文：Equipment→Craft→WorkLine
            var sourceNeedsEquipment = fromKind is null or ManagedEndpointKind.Position;
            if (sourceNeedsEquipment)
            {
                if (context.SourceEquipmentId <= 0)
                {
                    return RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.InvalidRelationship, "Equipment", null,
                        "源机台 identity 缺失，拒绝派工");
                }

                var sourceClassified = await ClassifyEquipmentChainAsync(context.SourceEquipmentId, ct);
                var sourceLine = await _equipment.GetWorkLineByEquipmentAsync(context.SourceEquipmentId, ct);
                if (!sourceClassified.IsAvailable)
                    return sourceClassified;
                if (sourceLine is null)
                {
                    return RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.NotFound, "WorkLine", null,
                        $"源机台 {context.SourceEquipmentId} 线体路由不可用");
                }

                lineRef = sourceLine;
            }

            if (context.DestEquipmentId.IsApplicable)
            {
                if (context.DestEquipmentId.IsMissing)
                {
                    return RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.NotFound, "Equipment", null,
                        "目标机台 identity 缺失，拒绝派工");
                }

                var destId = context.DestEquipmentId.Id!.Value;
                var destClassified = await ClassifyEquipmentChainAsync(destId, ct);
                var destLine = await _equipment.GetWorkLineByEquipmentAsync(destId, ct);
                if (!destClassified.IsAvailable) return destClassified;
                lineRef ??= destLine ?? destClassified.SourceWorkLine;
            }
            else if (toKind is ManagedEndpointKind.Position)
            {
                // Kind=Position 但工厂未标 DestEquipment → fail-closed
                return RoutingAvailabilityResult.Unavailable(
                    RoutingUnavailableReason.InvalidRelationship, "Equipment", null,
                    "目标加工位缺少机台依赖");
            }

            // Frame 实体：类型化 FRAME 已在 ValidateTypedEndpoint 检查；
            // 遗留路径 SourceFrameId + 有机台 → FrameBind（调度预记）
            if (context.FromEndpoint is null && context.SourceFrameId.IsApplicable)
            {
                var bindFail = await RequireActiveFrameBindAsync(
                    context.SourceEquipmentId, context.SourceFrameId.Id, ct);
                if (bindFail is not null) return bindFail;
            }

            if (context.FromEndpoint is null && context.DestFrameId.IsApplicable)
            {
                var onSource = await IsActiveFrameBindAsync(
                    context.SourceEquipmentId, context.DestFrameId.Id, ct);
                var onDest = context.DestEquipmentId.Id is long destEq
                    && await IsActiveFrameBindAsync(destEq, context.DestFrameId.Id, ct);
                if (!onSource && !onDest)
                {
                    return await RequireActiveFrameBindAsync(
                        context.SourceEquipmentId, context.DestFrameId.Id, ct)
                        ?? RoutingAvailabilityResult.Unavailable(
                            RoutingUnavailableReason.NotFound, "FrameBind",
                            context.DestFrameId.Id, "目标料架绑定不可用");
                }
            }

            // 类型化 FRAME + 同时存在机台上下文时才校验 FrameBind（通用路径；换架见下方 Operation）
            if (context.FromEndpoint?.Kind == ManagedEndpointKind.Frame
                && context.SourceEquipmentId > 0
                && context.SourceFrameId.IsApplicable)
            {
                var bindFail = await RequireActiveFrameBindAsync(
                    context.SourceEquipmentId, context.SourceFrameId.Id, ct);
                if (bindFail is not null) return bindFail;
            }

            if (context.ToEndpoint?.Kind == ManagedEndpointKind.Frame
                && context.DestEquipmentId.Id is long bindEq
                && context.DestFrameId.IsApplicable)
            {
                if (!await IsActiveFrameBindAsync(bindEq, context.DestFrameId.Id, ct))
                {
                    return await RequireActiveFrameBindAsync(bindEq, context.DestFrameId.Id, ct)
                        ?? RoutingAvailabilityResult.Unavailable(
                            RoutingUnavailableReason.NotFound, "FrameBind",
                            context.DestFrameId.Id, "目标料架绑定不可用");
                }
            }

            // ChangeFrame：必须用 OperationEquipmentId（非 Map.EquipmentId）权威校验 FrameBind
            if (context.Operation == DispatchOperationKind.ChangeFrame)
            {
                var cfBind = await ValidateChangeFrameBindAsync(context, ct);
                if (cfBind is not null) return cfBind;
                lineRef ??= await _equipment.GetWorkLineByEquipmentAsync(
                    context.OperationEquipmentId!.Value, ct);
            }

            if (context.RequiresResolvedCells
                && (string.IsNullOrWhiteSpace(context.FromCode)
                    || string.IsNullOrWhiteSpace(context.ToCode)))
            {
                return RoutingAvailabilityResult.Unavailable(
                    RoutingUnavailableReason.LocationMapDisabled, "LocationMap", null,
                    "起终点 cell 未解析到受管配置，拒绝派工");
            }

            return RoutingAvailabilityResult.Available(lineRef);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "路由可用性校验异常，fail-closed");
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.ConfigurationUnavailable, "Configuration", null,
                "路由配置读取异常，拒绝派工");
        }
    }

    private async Task<RoutingAvailabilityResult?> ValidateTypedEndpointAsync(
        ManagedDispatchEndpoint ep, string side, CancellationToken ct)
    {
        switch (ep.Kind)
        {
            case ManagedEndpointKind.Area:
                if (string.IsNullOrWhiteSpace(ep.RcsCode)
                    || !ManagedDispatchEndpoint.IsConfiguredAreaRole(ep.LocName))
                {
                    return RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.InvalidRelationship, "LocationMap", ep.LocationMapId,
                        $"{side} 区域端点配置不完整");
                }
                // Equipment/Craft/WorkLine 明确 N/A — 不查询
                return null;

            case ManagedEndpointKind.Frame:
                if (ep.FrameId is null or <= 0)
                {
                    return RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.InvalidRelationship, "Frame", null,
                        $"{side} 料架端点缺少 FrameId");
                }

                var frame = await _frames.FindAsync(ep.FrameId.Value, ct);
                if (frame is null)
                {
                    return RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.NotFound, "Frame", ep.FrameId,
                        $"{side} 料架不存在");
                }

                if (!ConfigActivity.IsActive(frame.State))
                {
                    // 无独立 FrameDisabled 枚举；EntityKind=Frame + 不可用即可拒发
                    return RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.NotFound, "Frame", frame.Id,
                        $"{side} 料架已禁用或状态不可用");
                }

                return null;

            case ManagedEndpointKind.Position:
                if (ep.EquipmentId is null or <= 0 || ep.PositionId is null or <= 0)
                {
                    return RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.InvalidRelationship, "LocationMap", ep.LocationMapId,
                        $"{side} 加工位端点缺少机台/工位");
                }
                return null;

            default:
                return RoutingAvailabilityResult.Unavailable(
                    RoutingUnavailableReason.InvalidRelationship, "LocationMap", ep.LocationMapId,
                    $"{side} 端点类型未知");
        }
    }

    private async Task<RoutingAvailabilityResult> ClassifyEquipmentChainAsync(
        long equipmentId, CancellationToken ct)
    {
        var eq = await _routing.FindEquipmentAsync(equipmentId, ct);
        if (eq is null)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.NotFound, "Equipment", equipmentId,
                $"机台 {equipmentId} 不存在");
        }
        if (!ConfigActivity.IsActive(eq.State))
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.EquipmentDisabled, "Equipment", equipmentId,
                $"机台 {equipmentId} 已禁用或状态不可用");
        }

        var craft = await _routing.FindCraftworkAsync(eq.CraftworkId, ct);
        if (craft is null)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.InvalidRelationship, "Craft", eq.CraftworkId,
                $"机台 {equipmentId} 工序关系缺失");
        }
        if (!ConfigActivity.IsActive(craft.State))
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.CraftDisabled, "Craft", craft.Id,
                $"工序 {craft.Id} 已禁用或状态不可用");
        }

        var line = await _routing.FindWorkLineAsync(craft.WorkLineId, ct);
        if (line is null)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.InvalidRelationship, "WorkLine", craft.WorkLineId,
                $"工序 {craft.Id} 线体关系缺失");
        }
        if (!ConfigActivity.IsActive(line.State))
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.WorkLineDisabled, "WorkLine", line.Id,
                $"线体 {line.Id} 已禁用或状态不可用");
        }

        return RoutingAvailabilityResult.Available(new WorkLineRef(line.Id, line.WorkLineCode));
    }

    /// <summary>
    /// 换架 Final Bind：OperationEquipmentId + FRAME 端点 FrameId；
    /// 机台链活动 + Bind 存在且 STATE=0 且 Eq/Frame 精确匹配。
    /// </summary>
    private async Task<RoutingAvailabilityResult?> ValidateChangeFrameBindAsync(
        DispatchRouteContext context, CancellationToken ct)
    {
        if (context.OperationEquipmentId is null or <= 0)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.InvalidRelationship, "Equipment", null,
                "换架缺少机台上下文，拒绝派工");
        }

        var opEq = context.OperationEquipmentId.Value;
        var eqClassified = await ClassifyEquipmentChainAsync(opEq, ct);
        if (!eqClassified.IsAvailable) return eqClassified;

        long? frameId = null;
        if (context.FromEndpoint?.Kind == ManagedEndpointKind.Frame
            && context.SourceFrameId.IsApplicable)
            frameId = context.SourceFrameId.Id;
        else if (context.ToEndpoint?.Kind == ManagedEndpointKind.Frame
                 && context.DestFrameId.IsApplicable)
            frameId = context.DestFrameId.Id;

        if (frameId is null or <= 0)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.InvalidRelationship, "Frame", null,
                "换架路径缺少 FRAME 端点，拒绝派工");
        }

        // Frame 实体活动：类型化路径已在 ValidateTypedEndpoint 检查；此处再权威读一次防 TOCTOU
        var frame = await _frames.FindAsync(frameId.Value, ct);
        if (frame is null)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.NotFound, "Frame", frameId,
                $"换架料架 {frameId} 不存在");
        }
        if (!ConfigActivity.IsActive(frame.State))
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.NotFound, "Frame", frameId,
                $"换架料架 {frameId} 已禁用或状态不可用");
        }

        return await RequireActiveFrameBindAsync(opEq, frameId, ct);
    }

    private async Task<bool> IsActiveFrameBindAsync(long equipmentId, long? frameId, CancellationToken ct)
    {
        if (frameId is null) return false;
        var binds = await _routing.FindFrameBindsByEquipmentAsync(equipmentId, ct);
        var hit = binds.FirstOrDefault(b => b.FrameId == frameId.Value);
        return hit is not null && ConfigActivity.IsActive(hit.State);
    }

    private async Task<RoutingAvailabilityResult?> RequireActiveFrameBindAsync(
        long equipmentId, long? frameId, CancellationToken ct)
    {
        if (frameId is null)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.NotFound, "FrameBind", null,
                $"机台 {equipmentId} 料架绑定缺失");
        }

        var binds = await _routing.FindFrameBindsByEquipmentAsync(equipmentId, ct);
        var hit = binds.FirstOrDefault(b =>
            b.FrameId == frameId.Value && b.EquipmentId == equipmentId);
        if (hit is null)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.NotFound, "FrameBind", frameId,
                $"机台 {equipmentId} 未绑定料架 {frameId}");
        }
        if (!ConfigActivity.IsActive(hit.State))
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.FrameBindDisabled, "FrameBind", frameId,
                $"料架绑定 {frameId} 已禁用");
        }
        return null;
    }
}
