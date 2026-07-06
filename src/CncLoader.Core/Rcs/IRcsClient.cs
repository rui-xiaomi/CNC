namespace CncLoader.Core.Rcs;

/// <summary>
/// RCS 出站 HTTP 客户端（4 接口）。负责公共字段填充、序列化、超时、网络级重试、双向报文流水。
/// 纯通信契约，不涉及任务落库；任务生命周期由 <see cref="IRcsTaskService"/> 编排。
/// </summary>
public interface IRcsClient
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
