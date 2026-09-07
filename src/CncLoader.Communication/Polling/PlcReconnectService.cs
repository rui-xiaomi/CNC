using CncLoader.Common.Configuration;
using CncLoader.Communication.Plc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Polling;

/// <summary>
/// Faulted 后按退避自动重连。人工 Disconnect 不重连。真断网时仍 fail-closed，成功前不恢复派工。
/// </summary>
public sealed class PlcReconnectService : IHostedService
{
    private readonly PlcConnectionManager _manager;
    private readonly PlcOptions _plc;
    private readonly ILogger<PlcReconnectService> _logger;
    private readonly Dictionary<long, int> _delayMs = new();
    private readonly Dictionary<long, DateTime> _nextAttemptUtc = new();
    private readonly HashSet<long> _inFlight = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PlcReconnectService(
        PlcConnectionManager manager,
        IOptions<AppOptions> options,
        ILogger<PlcReconnectService> logger)
    {
        _manager = manager;
        _plc = options.Value.Plc;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_plc.AutoReconnectEnabled)
        {
            _logger.LogInformation("PLC 自动重连未启用（AutoReconnectEnabled=false）。");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("PLC 自动重连已启用（连续 {Threshold} 次超时后断链，初期间隔 {Delay}ms）。",
            _plc.LinkFaultThreshold, Math.Max(500, _plc.AutoReconnectInitialDelayMs));
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
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "PLC 自动重连一轮异常"); }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var client in _manager.All)
        {
            if (client.State != PlcConnectionState.Faulted)
            {
                lock (_gate)
                {
                    _delayMs.Remove(client.PlcId);
                    _nextAttemptUtc.Remove(client.PlcId);
                }
                continue;
            }

            lock (_gate)
            {
                if (_nextAttemptUtc.TryGetValue(client.PlcId, out var due) && now < due)
                    continue;
                if (!_inFlight.Add(client.PlcId))
                    continue;
            }

            try
            {
                _logger.LogWarning("PLC {PlcId} 已 Faulted，尝试自动重连 {Host}:{Port}",
                    client.PlcId, client.Endpoint.Host, client.Endpoint.Port);
                await client.ConnectAsync(ct);
                lock (_gate)
                {
                    _delayMs.Remove(client.PlcId);
                    _nextAttemptUtc.Remove(client.PlcId);
                }
                _logger.LogInformation("PLC {PlcId} 自动重连成功", client.PlcId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                int next;
                lock (_gate)
                {
                    var initial = Math.Max(500, _plc.AutoReconnectInitialDelayMs);
                    var max = Math.Max(initial, _plc.AutoReconnectMaxDelayMs);
                    var prev = _delayMs.GetValueOrDefault(client.PlcId, initial);
                    next = Math.Min(max, prev <= 0 ? initial : prev * 2);
                    _delayMs[client.PlcId] = next;
                    _nextAttemptUtc[client.PlcId] = DateTime.UtcNow.AddMilliseconds(next);
                }
                _logger.LogWarning(ex, "PLC {PlcId} 自动重连失败，{Delay}ms 后再试", client.PlcId, next);
            }
            finally
            {
                lock (_gate) { _inFlight.Remove(client.PlcId); }
            }
        }
    }
}
