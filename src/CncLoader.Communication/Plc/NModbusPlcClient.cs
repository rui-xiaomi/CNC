using System.Diagnostics;
using System.Net.Sockets;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Signals;
using Microsoft.Extensions.Logging;
using NModbus;

namespace CncLoader.Communication.Plc;

/// <summary>
/// 基于 NModbus 的 Modbus TCP 客户端。D 区寄存器 → 保持寄存器读写。
/// 含连接状态、心跳、超时；通信读写记录设备流水。
/// </summary>
public sealed class NModbusPlcClient : IPlcClient
{
    private const byte SlaveAddress = 1; // 一机一 PLC，单元号固定 1

    private readonly ILogger<NModbusPlcClient> _logger;
    private readonly IDeviceLogger _deviceLogger;
    private readonly int _connectTimeoutMs;
    private readonly int _rwTimeoutMs;
    private readonly object _sync = new();

    private TcpClient? _tcp;
    private IModbusMaster? _master;
    private PlcConnectionState _state = PlcConnectionState.Disconnected;

    public NModbusPlcClient(long plcId, PlcEndpoint endpoint, int connectTimeoutMs, int rwTimeoutMs,
        ILogger<NModbusPlcClient> logger, IDeviceLogger deviceLogger)
    {
        PlcId = plcId;
        Endpoint = endpoint;
        _connectTimeoutMs = connectTimeoutMs;
        _rwTimeoutMs = rwTimeoutMs;
        _logger = logger;
        _deviceLogger = deviceLogger;
    }

    public long PlcId { get; }
    public PlcEndpoint Endpoint { get; }
    public PlcConnectionState State => _state;
    public bool IsConnected => _state == PlcConnectionState.Connected && (_tcp?.Connected ?? false);

    public event EventHandler<PlcConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(PlcConnectionState.Connecting);
        var sw = Stopwatch.StartNew();
        try
        {
            var tcp = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_connectTimeoutMs);
            await tcp.ConnectAsync(Endpoint.Host, Endpoint.Port, linked.Token).AsTask();

            var factory = new ModbusFactory();
            var master = factory.CreateMaster(tcp);
            master.Transport.ReadTimeout = _rwTimeoutMs;
            master.Transport.WriteTimeout = _rwTimeoutMs;

            lock (_sync)
            {
                _tcp = tcp;
                _master = master;
            }
            SetState(PlcConnectionState.Connected);
            _deviceLogger.Log(new DeviceLogEntry
            {
                DeviceType = DeviceType.Plc, DeviceId = PlcId, Action = DeviceAction.Connect,
                Request = $"{Endpoint.Host}:{Endpoint.Port}", Success = true, CostMs = (int)sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            SetState(PlcConnectionState.Faulted, ex.Message);
            _deviceLogger.Log(new DeviceLogEntry
            {
                DeviceType = DeviceType.Plc, DeviceId = PlcId, Action = DeviceAction.Connect,
                Request = $"{Endpoint.Host}:{Endpoint.Port}", Success = false, Error = ex.Message,
                CostMs = (int)sw.ElapsedMilliseconds
            });
            throw;
        }
    }

    public Task DisconnectAsync()
    {
        lock (_sync)
        {
            _master?.Dispose();
            _tcp?.Close();
            _master = null;
            _tcp = null;
        }
        SetState(PlcConnectionState.Disconnected);
        _deviceLogger.Log(new DeviceLogEntry
        {
            DeviceType = DeviceType.Plc, DeviceId = PlcId, Action = DeviceAction.Disconnect, Success = true
        });
        return Task.CompletedTask;
    }

    public async Task<bool> HeartbeatAsync(CancellationToken ct = default)
    {
        if (_master is null) return false;
        try
        {
            // 读 1 个寄存器作为探活（地址 0，PLC 通常可读）。
            await ReadRegistersAsync("D0", 1, ct);
            return true;
        }
        catch
        {
            SetState(PlcConnectionState.Faulted, "心跳失败");
            return false;
        }
    }

    public async Task<int[]> ReadRegistersAsync(string registerAddress, int length, CancellationToken ct = default)
    {
        var master = _master ?? throw new InvalidOperationException($"PLC {PlcId} 未连接");
        var start = (ushort)RegisterAddress.ToRegisterIndex(registerAddress);
        var sw = Stopwatch.StartNew();
        try
        {
            var regs = await master.ReadHoldingRegistersAsync(SlaveAddress, start, (ushort)length);
            var result = Array.ConvertAll(regs, r => (int)r);
            _deviceLogger.Log(new DeviceLogEntry
            {
                DeviceType = DeviceType.Plc, DeviceId = PlcId, Action = DeviceAction.Read,
                RegisterAddress = registerAddress, Response = string.Join(',', result),
                Success = true, CostMs = (int)sw.ElapsedMilliseconds
            });
            return result;
        }
        catch (Exception ex)
        {
            _deviceLogger.Log(new DeviceLogEntry
            {
                DeviceType = DeviceType.Plc, DeviceId = PlcId, Action = DeviceAction.Read,
                RegisterAddress = registerAddress, Success = false, Error = ex.Message,
                CostMs = (int)sw.ElapsedMilliseconds
            });
            throw;
        }
    }

    public async Task WriteRegisterAsync(string registerAddress, int value, CancellationToken ct = default)
    {
        var master = _master ?? throw new InvalidOperationException($"PLC {PlcId} 未连接");
        var addr = (ushort)RegisterAddress.ToRegisterIndex(registerAddress);
        var sw = Stopwatch.StartNew();
        try
        {
            await master.WriteSingleRegisterAsync(SlaveAddress, addr, (ushort)value);
            _deviceLogger.Log(new DeviceLogEntry
            {
                DeviceType = DeviceType.Plc, DeviceId = PlcId, Action = DeviceAction.Write,
                RegisterAddress = registerAddress, Request = value.ToString(),
                Success = true, CostMs = (int)sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            _deviceLogger.Log(new DeviceLogEntry
            {
                DeviceType = DeviceType.Plc, DeviceId = PlcId, Action = DeviceAction.Write,
                RegisterAddress = registerAddress, Request = value.ToString(),
                Success = false, Error = ex.Message, CostMs = (int)sw.ElapsedMilliseconds
            });
            throw;
        }
    }

    private void SetState(PlcConnectionState state, string? message = null)
    {
        if (_state == state) return;
        _state = state;
        ConnectionStateChanged?.Invoke(this, new PlcConnectionStateChangedEventArgs(PlcId, state, message));
    }

    public void Dispose()
    {
        _master?.Dispose();
        _tcp?.Dispose();
    }
}
