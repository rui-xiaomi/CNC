using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NModbus;
using NModbus.Data;

namespace CncLoader.Communication.Simulation;

/// <summary>
/// 进程内 Modbus TCP 模拟器（开发期无真机时使用）。为每台虚拟 PLC 在环回地址各开一个监听端口，
/// 按《测试机信号表》预置 D 段保持寄存器。Master 写入会自动反映到对应数据存储，可端到端自测读写。
/// </summary>
public sealed class ModbusTcpSimulator : IDisposable
{
    private readonly ILogger<ModbusTcpSimulator> _logger;
    private readonly IPAddress _bindAddress;
    private readonly ConcurrentDictionary<long, VirtualPlc> _plcs = new();
    private CancellationTokenSource? _cts;

    public ModbusTcpSimulator(string bindAddress, ILogger<ModbusTcpSimulator> logger)
    {
        _logger = logger;
        _bindAddress = IPAddress.Parse(bindAddress);
    }

    public bool IsRunning { get; private set; }

    /// <summary>登记一台虚拟 PLC。seed：寄存器偏移 → 初值（如 1006 → 2）。</summary>
    public void AddPlc(long plcId, int port, IReadOnlyDictionary<int, ushort> seed)
    {
        var store = new DefaultSlaveDataStore();
        foreach (var (offset, value) in seed)
            store.HoldingRegisters.WritePoints((ushort)offset, new[] { value });

        _plcs[plcId] = new VirtualPlc(plcId, port, store);
    }

    public int GetPort(long plcId) =>
        _plcs.TryGetValue(plcId, out var p) ? p.Port : throw new KeyNotFoundException($"模拟器未登记 PLC {plcId}");

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        _cts = new CancellationTokenSource();
        var factory = new ModbusFactory();

        foreach (var plc in _plcs.Values)
        {
            var listener = new TcpListener(_bindAddress, plc.Port);
            listener.Start();
            var network = factory.CreateSlaveNetwork(listener);
            network.AddSlave(factory.CreateSlave(unitId: 1, plc.DataStore));
            plc.Listener = listener;
            // 后台监听；网络异常不致命，仅记日志。
            plc.ListenTask = Task.Run(async () =>
            {
                try { await network.ListenAsync(_cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogWarning(ex, "模拟器 PLC {PlcId} 监听结束", plc.PlcId); }
            });
            _logger.LogInformation("模拟器 PLC {PlcId} 监听 {Addr}:{Port}", plc.PlcId, _bindAddress, plc.Port);
        }

        IsRunning = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        foreach (var plc in _plcs.Values)
        {
            try { plc.Listener?.Stop(); } catch { /* ignore */ }
            if (plc.ListenTask is not null)
            {
                try { await plc.ListenTask; } catch { /* ignore */ }
            }
        }
        IsRunning = false;
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* ignore */ }
        _cts?.Dispose();
    }

    private sealed class VirtualPlc(long plcId, int port, DefaultSlaveDataStore dataStore)
    {
        public long PlcId { get; } = plcId;
        public int Port { get; } = port;
        public DefaultSlaveDataStore DataStore { get; } = dataStore;
        public TcpListener? Listener { get; set; }
        public Task? ListenTask { get; set; }
    }
}
