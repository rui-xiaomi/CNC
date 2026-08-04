namespace CncLoader.Core.Rcs;

/// <summary>受管路由解析结果状态（不含敏感配置）。</summary>
public enum ManagedDispatchRouteStatus
{
    Resolved = 0,
    NotFound,
    Ambiguous,
    Disabled,
    InvalidRelationship,
    ConfigurationUnavailable
}

/// <summary>
/// 将手动/历史任务 From/To 解析为完整 <see cref="DispatchRouteContext"/>。
/// 不负责发送、不修改任务、不缓存权威结论。
/// </summary>
public interface IManagedDispatchRouteResolver
{
    Task<ManagedDispatchRouteResult> ResolveAsync(
        string? fromCode, string? toCode, CancellationToken ct = default);
}

/// <summary>受管路由解析结果。</summary>
public sealed record ManagedDispatchRouteResult
{
    public required ManagedDispatchRouteStatus Status { get; init; }
    public DispatchRouteContext? Context { get; init; }
    /// <summary>安全可读说明（无 RCS URL / Token）。</summary>
    public string SafeMessage { get; init; } = "";
    public string? EntityKind { get; init; }
    public long? EntityId { get; init; }

    public bool IsResolved => Status == ManagedDispatchRouteStatus.Resolved && Context is not null;

    public static ManagedDispatchRouteResult Resolved(DispatchRouteContext context) => new()
    {
        Status = ManagedDispatchRouteStatus.Resolved,
        Context = context,
        SafeMessage = "路由已解析"
    };

    public static ManagedDispatchRouteResult Fail(
        ManagedDispatchRouteStatus status,
        string safeMessage,
        string? entityKind = null,
        long? entityId = null) => new()
    {
        Status = status,
        SafeMessage = safeMessage,
        EntityKind = entityKind,
        EntityId = entityId
    };
}
