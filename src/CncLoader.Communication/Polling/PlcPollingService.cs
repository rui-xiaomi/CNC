using CncLoader.Communication.Plc;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Polling;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Polling;

/// <summary>
/// 中央轮询中枢实现：按点位表读各 PLC → 写入信号仓（单一数据源）。工位状态不在此合成，
/// 由 PositionScheduler 经 PositionTransition 统一推进。每台 PLC 独立处理，一台失败不影响其他机台。
/// 作为 <see cref="IHostedService"/> 随主机启动持续轮询（StartAsync 内部 Task.Run 立即返回，不阻塞启动）；
/// PLC 建链在窗口显示后由 PlcRuntimeBootstrapper 完成，本循环容忍"暂无连接"并在建链后自动读到实时值。
/// </summary>
public sealed class PlcPollingService : IPlcPollingService, IHostedService
{
    private readonly IPlcPointSource _pointSource;
    private readonly PlcConnectionManager _connections;
    private readonly ISignalStateStore _store;
    private readonly ILogger<PlcPollingService> _logger;
    private readonly int _intervalMs;
    private readonly bool _pollingEnabled;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public PlcPollingService(IPlcPointSource pointSource, PlcConnectionManager connections,
        ISignalStateStore store, ILogger<PlcPollingService> logger,
        int intervalMs, bool pollingEnabled = true)
    {
        _pointSource = pointSource;
        _connections = connections;
        _store = store;
        _logger = logger;
        _intervalMs = intervalMs;
        _pollingEnabled = pollingEnabled;
    }

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (!_pollingEnabled)
        {
            _logger.LogInformation("持续轮询已关闭（Plc.PollingEnabled=false，手动单步调试模式）；信号仓不自动刷新。");
            return Task.CompletedTask;
        }
        if (IsRunning) return Task.CompletedTask;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
        IsRunning = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!IsRunning) return;
        _loopCts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; } catch (OperationCanceledException) { }
        }
        IsRunning = false;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "轮询一轮发生异常"); }
            try { await Task.Delay(_intervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        var points = await _pointSource.GetAllAsync(ct);
        var readCount = 0;

        // 按机台分组，便于合成工位状态。
        foreach (var eqGroup in points.GroupBy(p => p.EquipmentId))
        {
            var equipmentId = eqGroup.Key;
            var plcId = eqGroup.First().PlcId;
            var client = _connections.Get(plcId);
            var online = client?.IsConnected ?? false;

            // 读值缓存：(signal, positionId) → On
            var reads = new Dictionary<(SignalKey, long?), bool?>();

            if (online)
            {
                foreach (var point in eqGroup.Where(p => !p.IsWrite))
                {
                    try
                    {
                        var raw = await client!.ReadRegistersAsync(point.RegisterAddress, point.DataLength, ct);
                        var value = raw.Length > 0 ? raw[0] : 0;
                        var on = SignalConventions.Interpret(value, point);
                        reads[(point.Signal, point.PositionId)] = on;
                        _store.UpdateReading(equipmentId, new SignalReading
                        {
                            Signal = point.Signal, PositionId = point.PositionId,
                            RegisterAddress = point.RegisterAddress, RawValue = value, On = on
                        });
                        readCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "读点位失败 EQ{Eq} {Addr}", equipmentId, point.RegisterAddress);
                    }
                }
            }

            // 机台级
            var doorOpen = reads.GetValueOrDefault((SignalKey.Door, null));
            var safe = reads.GetValueOrDefault((SignalKey.MachineSafe, null));
            _store.UpdateMachine(new MachineStatus
            {
                EquipmentId = equipmentId, DoorOpen = doorOpen, Safe = safe, PlcOnline = online
            });

            // 工位级状态合成由 PositionScheduler（第四阶段⑤）统一驱动，轮询只负责信号采集。
            // 调度器按 §7 状态机（含 DISPATCHING/TRANSPORTING/PLC复核）写 PositionStatus。
            // 未启用调度器时，UI 看不到工位态——由调度器兜底置 Offline/WaitLoad。
        }

        return readCount;
    }
}
