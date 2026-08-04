using CncLoader.Core.Abstractions;

namespace CncLoader.Core.Rcs;

/// <summary>路由不可用原因（安全反馈；不含敏感配置细节）。</summary>
public enum RoutingUnavailableReason
{
    Available = 0,
    EquipmentDisabled,
    CraftDisabled,
    WorkLineDisabled,
    PointDisabled,
    LocationMapDisabled,
    FrameBindDisabled,
    NotFound,
    InvalidRelationship,
    ConfigurationUnavailable
}

/// <summary>
/// 路由依赖槽位：区分「不适用」与「必填但缺失」。
/// null Value + Required → 缺失；NotApplicable → 本路径不检查该实体。
/// </summary>
public readonly record struct RouteDependency(bool IsApplicable, long? Id)
{
    public static RouteDependency NotApplicable { get; } = new(false, null);
    public static RouteDependency Required(long id) => new(true, id);
    public static RouteDependency RequiredMissing { get; } = new(true, null);

    public bool IsMissing => IsApplicable && Id is null;
}

/// <summary>派工路由上下文：携带重新查询权威配置所需的真实 identity。</summary>
public sealed record DispatchRouteContext
{
    public required long SourceEquipmentId { get; init; }
    public long? SourcePositionId { get; init; }
    public RouteDependency DestEquipmentId { get; init; } = RouteDependency.NotApplicable;
    public RouteDependency DestPositionId { get; init; } = RouteDependency.NotApplicable;
    public RouteDependency SourceFrameId { get; init; } = RouteDependency.NotApplicable;
    public RouteDependency DestFrameId { get; init; } = RouteDependency.NotApplicable;
    public string? FromCode { get; init; }
    public string? ToCode { get; init; }
    /// <summary>是否要求 From/To 为已解析的受管编码（非空）。</summary>
    public bool RequiresResolvedCells { get; init; } = true;
}

/// <summary>路由可用性校验结果。</summary>
public sealed record RoutingAvailabilityResult
{
    public required bool IsAvailable { get; init; }
    public required RoutingUnavailableReason Reason { get; init; }
    /// <summary>失效实体类型（Equipment/Craft/WorkLine/FrameBind/LocationMap/Point），可供区分反馈。</summary>
    public string? EntityKind { get; init; }
    public long? EntityId { get; init; }
    /// <summary>安全可读说明（无 RCS URL / Token）。</summary>
    public string SafeMessage { get; init; } = "";
    /// <summary>源机台当前活动线体（Available 时非空）。</summary>
    public WorkLineRef? SourceWorkLine { get; init; }

    public static RoutingAvailabilityResult Available(WorkLineRef sourceWorkLine) => new()
    {
        IsAvailable = true,
        Reason = RoutingUnavailableReason.Available,
        SourceWorkLine = sourceWorkLine,
        SafeMessage = "路由可用"
    };

    public static RoutingAvailabilityResult Unavailable(
        RoutingUnavailableReason reason,
        string entityKind,
        long? entityId,
        string safeMessage) => new()
    {
        IsAvailable = false,
        Reason = reason,
        EntityKind = entityKind,
        EntityId = entityId,
        SafeMessage = safeMessage
    };
}

/// <summary>权威路由活动校验（不读调度进程内 _lineCache）。</summary>
public interface IRoutingAvailabilityValidator
{
    Task<RoutingAvailabilityResult> ValidateAsync(DispatchRouteContext context, CancellationToken ct = default);
}
