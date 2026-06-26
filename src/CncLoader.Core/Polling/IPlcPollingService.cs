namespace CncLoader.Core.Polling;

/// <summary>
/// 中央 PLC 轮询中枢：按点位表周期读各 PLC → 合成加工位状态 → 写入状态仓。
/// 实现位于 Communication 层（依赖 IPlcClient）。作为后台服务运行。
/// </summary>
public interface IPlcPollingService
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    /// <summary>立即执行一轮读取（用于无真机时的单轮验证）。返回读到的点位数。</summary>
    Task<int> PollOnceAsync(CancellationToken ct = default);
}
