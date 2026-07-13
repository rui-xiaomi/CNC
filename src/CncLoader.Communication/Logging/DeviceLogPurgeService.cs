using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Logging;

/// <summary>
/// 定期清理 MAS_AUTO_DEVICE_LOG 过期行（默认保留 <see cref="LoggingOptions.DeviceLogRetentionDays"/> 天）。
/// 启动后先跑一轮，之后每 6 小时一次；RetentionDays≤0 时不启用。
/// </summary>
public sealed class DeviceLogPurgeService : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IDeviceLogStore _store;
    private readonly LoggingOptions _logging;
    private readonly ILogger<DeviceLogPurgeService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public DeviceLogPurgeService(IDeviceLogStore store, IOptions<AppOptions> options, ILogger<DeviceLogPurgeService> logger)
    {
        _store = store;
        _logging = options.Value.Logging;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_logging.DeviceLogRetentionDays <= 0)
        {
            _logger.LogInformation("设备流水自动清理未启用（DeviceLogRetentionDays≤0）。");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("设备流水清理已启动（保留 {Days} 天，周期 {Hours}h）",
            _logging.DeviceLogRetentionDays, Interval.TotalHours);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* ignore */ }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-_logging.DeviceLogRetentionDays);
                var deleted = await _store.PurgeOlderThanAsync(cutoff, ct);
                if (deleted > 0)
                    _logger.LogInformation("设备流水清理：删除 {Count} 条（早于 {Cutoff:yyyy-MM-dd HH:mm}）", deleted, cutoff);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "设备流水清理失败");
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
