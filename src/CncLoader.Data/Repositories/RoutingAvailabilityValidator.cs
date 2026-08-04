using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 权威路由活动校验：读 IEquipmentRoutingStore 当前 STATE，不读调度 _lineCache。
/// </summary>
public sealed class RoutingAvailabilityValidator : IRoutingAvailabilityValidator
{
    private readonly IEquipmentRoutingStore _routing;
    private readonly IEquipmentConfigService _equipment;
    private readonly ILogger<RoutingAvailabilityValidator> _logger;

    public RoutingAvailabilityValidator(
        IEquipmentRoutingStore routing,
        IEquipmentConfigService equipment,
        ILogger<RoutingAvailabilityValidator> logger)
    {
        _routing = routing;
        _equipment = equipment;
        _logger = logger;
    }

    public async Task<RoutingAvailabilityResult> ValidateAsync(
        DispatchRouteContext context, CancellationToken ct = default)
    {
        try
        {
            // Store 分层判定原因；无论成败都经 Service.GetWorkLine 再确认（调用序 / 契约对齐）
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
                _ = await _equipment.GetWorkLineByEquipmentAsync(destId, ct);
                if (!destClassified.IsAvailable) return destClassified;
            }

            if (context.SourceFrameId.IsApplicable)
            {
                var bindFail = await RequireActiveFrameBindAsync(
                    context.SourceEquipmentId, context.SourceFrameId.Id, ct);
                if (bindFail is not null) return bindFail;
            }

            if (context.DestFrameId.IsApplicable)
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

            if (context.RequiresResolvedCells
                && (string.IsNullOrWhiteSpace(context.FromCode)
                    || string.IsNullOrWhiteSpace(context.ToCode)))
            {
                return RoutingAvailabilityResult.Unavailable(
                    RoutingUnavailableReason.LocationMapDisabled, "LocationMap", null,
                    "起终点 cell 未解析到受管配置，拒绝派工");
            }

            return RoutingAvailabilityResult.Available(sourceLine);
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
        var hit = binds.FirstOrDefault(b => b.FrameId == frameId.Value);
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
