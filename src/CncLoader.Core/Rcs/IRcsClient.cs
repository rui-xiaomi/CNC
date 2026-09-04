namespace CncLoader.Core.Rcs;

/// <summary>
/// RCS 出站 HTTP 客户端（4 接口）。负责公共字段填充、序列化、超时、网络级重试、双向报文流水。
/// 纯通信契约，不涉及任务落库；任务生命周期由 <see cref="IRcsTaskService"/> 编排。
/// <para>
/// <strong>刻意 internal</strong>：受管派工门禁（路由解析 + 活动校验 + 预记）全在
/// <see cref="IRcsTaskService"/> 之内。只有 Core 与 Communication 能命名本接口，
/// 且它不进 DI 容器，所以 UI / App / Data 里「注入 IRcsClient 直接发 RCS」是编译错误，
/// 而不是靠反射审计测试事后发现。新增外部执行必须走 <see cref="IRcsTaskService"/>。
/// </para>
/// </summary>
internal interface IRcsClient
{
    /// <summary>3.1 执行搬运任务（cell 级）。</summary>
    Task<RcsResult> TransitTaskAsync(TransitTaskRequest req, CancellationToken ct = default);

    /// <summary>3.2 执行定制任务（grabTask / identifyQR）。</summary>
    Task<RcsResult> ExcuteTaskAsync(ExcuteTaskRequest req, CancellationToken ct = default);

    /// <summary>3.3 取消任务。</summary>
    Task<RcsResult> CancelTaskAsync(CancelTaskRequest req, CancellationToken ct = default);

    /// <summary>3.4 条件查询任务。</summary>
    Task<RcsResult> QueryTaskAsync(QueryTaskRequest req, CancellationToken ct = default);
}
