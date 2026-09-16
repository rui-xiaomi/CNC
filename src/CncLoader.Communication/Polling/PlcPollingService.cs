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

        // 同 plcId 仍串行（客户端闸只保证单连接互斥）；不同 plcId 并行，避免多机一轮变成各机耗时之和。
        var plcGroups = points
            .GroupBy(p => (p.EquipmentId, p.PlcId))
            .GroupBy(g => g.Key.PlcId)
            .ToList();

        await Task.WhenAll(plcGroups.Select(async plcGroup =>
        {
            var local = 0;
            foreach (var eqGroup in plcGroup)
                local += await PollEquipmentAsync(eqGroup.Key.EquipmentId, eqGroup.Key.PlcId, eqGroup, ct);
            Interlocked.Add(ref readCount, local);
        }));

        return readCount;
    }

    private async Task<int> PollEquipmentAsync(
        long equipmentId, long plcId, IEnumerable<PlcPointDefinition> eqGroup, CancellationToken ct)
    {
        var client = _connections.Get(plcId);
        var attemptedReads = false;
        var succeededReads = 0;
        var readCount = 0;

        // 读值缓存：(signal, positionId) → On
        var reads = new Dictionary<(SignalKey, long?), bool?>();

        if (client?.IsConnected == true)
        {
            // P2-3：相邻读点位合并为一次读，减少逐点往返；批量读失败（如夹带字不可读）退回该块逐点读，不比原来差。
            foreach (var block in RegisterReadPlanner.Plan(eqGroup.Where(p => !p.IsWrite)))
            {
                int[]? blockValues = null;
                if (block.Points.Count > 1)
                {
                    try { blockValues = await client.ReadRegistersAsync(block.Address, block.Length, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "批量读失败 EQ{Eq} {Addr}×{Len}，退回逐点读", equipmentId, block.Address, block.Length);
                    }
                }

                foreach (var (point, index) in block.Points)
                {
                    attemptedReads = true;
                    try
                    {
                        int value;
                        if (blockValues is not null && index < blockValues.Length)
                        {
                            value = blockValues[index];
                        }
                        else
                        {
                            var raw = await client.ReadRegistersAsync(point.RegisterAddress, point.DataLength, ct);
                            value = raw.Length > 0 ? raw[0] : 0;
                        }
                        var on = SignalConventions.Interpret(value, point);
                        reads[(point.Signal, point.PositionId)] = on;
                        _store.UpdateReading(equipmentId, new SignalReading
                        {
                            Signal = point.Signal, PositionId = point.PositionId,
                            RegisterAddress = point.RegisterAddress, RawValue = value, On = on
                        });
                        succeededReads++;
                        readCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "读点位失败 EQ{Eq} {Addr}", equipmentId, point.RegisterAddress);
                    }
                }
            }
        }

        // 机台级：必须以本轮结束时的链路为准。组开始时还 Connected、读到一半 Faulted
        // 若仍写 PlcOnline=true，调度器会把 Offline 推回 WaitLoad，看板上显示「等待上料」。
        var stillConnected = client?.IsConnected ?? false;
        var online = stillConnected && (!attemptedReads || succeededReads > 0);
        var doorOpen = reads.GetValueOrDefault((SignalKey.Door, null));
        var safe = reads.GetValueOrDefault((SignalKey.MachineSafe, null));
        _store.UpdateMachine(new MachineStatus
        {
            EquipmentId = equipmentId, PlcId = plcId, DoorOpen = doorOpen, Safe = safe, PlcOnline = online
        });

        // 工位级状态合成由 PositionScheduler（第四阶段⑤）统一驱动，轮询只负责信号采集。
        // 调度器按 §7 状态机（含 DISPATCHING/TRANSPORTING/PLC复核）写 PositionStatus。
        // 未启用调度器时，UI 看不到工位态——由调度器兜底置 Offline/WaitLoad。
        return readCount;
    }
}
