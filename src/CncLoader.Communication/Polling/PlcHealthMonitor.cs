using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Polling;

/// <summary>
/// PLC 失联监测（P0-5）：周期检查机台快照，某 PLC 持续离线/快照过期超阈值即补一声「大声告警」。
/// 不负责重连（AGENTS.md 已列技术债）；失联期间工位已由 PositionTransition 停派工（Offline），本项只消除「静默停线」。
/// </summary>
public sealed class PlcHealthMonitor : IHostedService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly ISignalStateStore _store;
    private readonly IAlarmEventService _alarms;
    private readonly PlcOptions _plcOptions;
    private readonly ILogger<PlcHealthMonitor> _logger;

    private readonly object _gate = new();
    /// <summary>plcId → 首次判定失联时间（UTC）。</summary>
    private readonly Dictionary<long, DateTime> _downSince = new();
    /// <summary>本次失联已告警的 plcId（恢复后移除，下次失联重新告警）。</summary>
    private readonly HashSet<long> _alarmed = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PlcHealthMonitor(
        ISignalStateStore store,
        IAlarmEventService alarms,
        IOptions<AppOptions> options,
        ILogger<PlcHealthMonitor> logger)
    {
        _store = store;
        _alarms = alarms;
        _plcOptions = options.Value.Plc;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_plcOptions.OfflineAlarmAfterMs <= 0)
        {
            _logger.LogInformation("PLC 失联监测未启用（OfflineAlarmAfterMs≤0）。");
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
            try { await CheckAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "PLC 失联监测一轮异常"); }

            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>测试接缝：跑一轮失联检查（不启 HostedService 循环）。</summary>
    internal Task ProbeCheckOnceAsync(CancellationToken ct = default) => CheckAsync(ct);

    private async Task CheckAsync(CancellationToken ct)
    {
        var threshold = TimeSpan.FromMilliseconds(_plcOptions.OfflineAlarmAfterMs);
        var maxAge = TimeSpan.FromMilliseconds(_plcOptions.SignalMaxAgeMs);
        var now = DateTime.UtcNow;

        var toAlarm = new List<(long PlcId, TimeSpan DownFor)>();
        lock (_gate)
        {
            var seen = new HashSet<long>();
            foreach (var m in _store.GetAllMachines())
            {
                if (m.PlcId <= 0) continue; // 尚未映射 PLC（启动初期），跳过
                seen.Add(m.PlcId);
                var down = !m.PlcOnline || !m.IsFresh(maxAge);
                if (down)
                {
                    if (!_downSince.ContainsKey(m.PlcId))
                        _downSince[m.PlcId] = now;
                }
                else
                {
                    _downSince.Remove(m.PlcId);
                    _alarmed.Remove(m.PlcId);
                }
            }

            foreach (var id in _downSince.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _downSince.Remove(id);
                _alarmed.Remove(id);
            }

            foreach (var (plcId, since) in _downSince)
            {
                var downFor = now - since;
                if (downFor >= threshold && _alarmed.Add(plcId))
                    toAlarm.Add((plcId, downFor));
            }
        }

        foreach (var (plcId, downFor) in toAlarm)
        {
            await _alarms.RaisePlcAlarmAsync(plcId,
                $"PLC {plcId} 失联已 {downFor.TotalSeconds:0} 秒，相关机台已停止派工。请检查网络/PLC 后手动「全部连接」。", "2", ct);
        }
    }
}
