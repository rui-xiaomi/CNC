using CncLoader.Communication.Plc;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Polling;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Polling;

/// <summary>
/// 中央轮询中枢实现：按点位表读各 PLC → 合成加工位状态 → 写入状态仓（单一数据源）。
/// 每台 PLC 独立处理，一台失败不影响其他机台。
/// </summary>
public sealed class PlcPollingService : IPlcPollingService
{
    private readonly IPlcPointSource _pointSource;
    private readonly PlcConnectionManager _connections;
    private readonly ISignalStateStore _store;
    private readonly IStatusSynthesizer _synthesizer;
    private readonly ILogger<PlcPollingService> _logger;
    private readonly int _intervalMs;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public PlcPollingService(IPlcPointSource pointSource, PlcConnectionManager connections,
        ISignalStateStore store, IStatusSynthesizer synthesizer, ILogger<PlcPollingService> logger,
        int intervalMs)
    {
        _pointSource = pointSource;
        _connections = connections;
        _store = store;
        _synthesizer = synthesizer;
        _logger = logger;
        _intervalMs = intervalMs;
    }

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
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

            // 工位级：按 PositionId 合成
            var positionIds = eqGroup.Where(p => p.PositionId.HasValue)
                .Select(p => p.PositionId!.Value).Distinct();
            foreach (var posId in positionIds)
            {
                var input = new PositionSignalInput
                {
                    PlcOnline = online,
                    MachineSafe = safe,
                    DoorOpen = doorOpen,
                    HasMaterial = reads.GetValueOrDefault((SignalKey.PosHasMat, posId)),
                    AllowLoad = reads.GetValueOrDefault((SignalKey.PosAllowLoad, posId)),
                    Ok = reads.GetValueOrDefault((SignalKey.PosOk, posId)),
                    Ng = reads.GetValueOrDefault((SignalKey.PosNg, posId))
                };
                var state = _synthesizer.Synthesize(input);
                _store.UpdatePosition(new PositionStatus
                {
                    EquipmentId = equipmentId, PositionId = posId, State = state
                });
            }
        }

        return readCount;
    }
}
