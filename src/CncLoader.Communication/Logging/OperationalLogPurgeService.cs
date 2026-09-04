using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Logging;

/// <summary>
/// 运行日志归档（P1-4）：定期清理 MAS_AUTO_RCS_MSG_LOG（TEXT 大字段）与 MAS_AUTO_ALARM_EVENT，
/// 防无界膨胀。启动先跑一轮，之后每 6 小时一次；保留天数 ≤0 时不启用对应表。
/// AGV_TASK / WORK_RECORD 为业务记录，保留策略由运维决定，不在本服务自动清理。
/// </summary>
public sealed class OperationalLogPurgeService : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IRcsMessageLog _rcsMsgLog;
    private readonly IAlarmEventService _alarms;
    private readonly LoggingOptions _logging;
    private readonly ILogger<OperationalLogPurgeService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public OperationalLogPurgeService(
        IRcsMessageLog rcsMsgLog,
        IAlarmEventService alarms,
        IOptions<AppOptions> options,
        ILogger<OperationalLogPurgeService> logger)
    {
        _rcsMsgLog = rcsMsgLog;
        _alarms = alarms;
        _logging = options.Value.Logging;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_logging.RcsMsgLogRetentionDays <= 0 && _logging.AlarmEventRetentionDays <= 0)
        {
            _logger.LogInformation("运行日志归档未启用（RcsMsgLogRetentionDays 与 AlarmEventRetentionDays 均 ≤0）。");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
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
            try { await PurgeOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "运行日志归档一轮异常"); }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PurgeOnceAsync(CancellationToken ct)
    {
        if (_logging.RcsMsgLogRetentionDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-_logging.RcsMsgLogRetentionDays);
            var n = await _rcsMsgLog.PurgeOlderThanAsync(cutoff, ct);
            if (n > 0)
                _logger.LogInformation("RCS 报文流水归档：删除 {Count} 条（早于 {Cutoff:yyyy-MM-dd HH:mm}）", n, cutoff);
        }

        if (_logging.AlarmEventRetentionDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-_logging.AlarmEventRetentionDays);
            var n = await _alarms.PurgeOlderThanAsync(cutoff, ct);
            if (n > 0)
                _logger.LogInformation("告警归档：删除 {Count} 条（早于 {Cutoff:yyyy-MM-dd HH:mm}）", n, cutoff);
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
