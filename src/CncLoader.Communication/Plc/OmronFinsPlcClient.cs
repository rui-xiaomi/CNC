using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Signals;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Plc;

/// <summary>
/// 手写的欧姆龙 FINS/UDP（端口 9600）客户端。D 区寄存器 → DM 区字读写。
/// 与 <see cref="NModbusPlcClient"/> 对齐：连接状态、心跳、超时；通信读写记录设备流水。
/// 节点号自动推导：目的节点取 PLC IP 末段，源节点取本机 IP 末段，网络号/单元号固定 0。
/// </summary>
public sealed class OmronFinsPlcClient : IPlcClient
{
    // FINS 内存区字代码：DM=0x82（本期仅 DM 字读写，其余预留以便后续扩展）。
    private static readonly IReadOnlyDictionary<char, byte> WordAreaCodes = new Dictionary<char, byte>
    {
        ['D'] = 0x82, // DM
        ['C'] = 0xB0, // CIO
        ['W'] = 0xB1, // WR
        ['H'] = 0xB2, // HR
        ['A'] = 0xB3, // AR
    };

    private readonly ILogger<OmronFinsPlcClient> _logger;
    private readonly IDeviceLogger _deviceLogger;
    private readonly int _connectTimeoutMs;
    private readonly int _rwTimeoutMs;
    private readonly object _sync = new();

    private UdpClient? _udp;
    private byte _destNode;
    private byte _srcNode = 1;
    private byte _sid;
    private PlcConnectionState _state = PlcConnectionState.Disconnected;

    public OmronFinsPlcClient(long plcId, PlcEndpoint endpoint, int connectTimeoutMs, int rwTimeoutMs,
        ILogger<OmronFinsPlcClient> logger, IDeviceLogger deviceLogger)
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
    public bool IsConnected => _state == PlcConnectionState.Connected && _udp is not null;

    public event EventHandler<PlcConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        SetState(PlcConnectionState.Connecting);
        var sw = Stopwatch.StartNew();
        try
        {
            var udp = new UdpClient();
            // UDP 无连接握手，Connect 仅固定远端并触发本地端点分配，便于推导源节点号。
            udp.Connect(Endpoint.Host, Endpoint.Port);

            var destNode = LastOctet(Endpoint.Host, fallback: 1);
            var srcNode = LastOctet((udp.Client.LocalEndPoint as IPEndPoint)?.Address, fallback: destNode == 1 ? (byte)2 : (byte)1);

            lock (_sync)
            {
                _udp = udp;
                _destNode = destNode;
                _srcNode = srcNode;
            }

            // FINS/UDP 无握手：必须实际读一次 DM 确认设备可达，否则离线设备会被误判为在线，
            // 进而拖垮轮询（每个点位都等满读超时）。探活失败即视为连接失败。
            await ProbeAsync(ct);

            SetState(PlcConnectionState.Connected);
            _deviceLogger.Log(new DeviceLogEntry
            {
                DeviceType = DeviceType.Plc, DeviceId = PlcId, Action = DeviceAction.Connect,
                Request = $"{Endpoint.Host}:{Endpoint.Port} FINS DA1={destNode} SA1={srcNode}",
                Success = true, CostMs = (int)sw.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _udp?.Dispose();
                _udp = null;
            }
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

    /// <summary>连接探活：读 1 个 DM 字确认链路可达（不记设备流水，避免连接阶段噪声）。</summary>
    private async Task ProbeAsync(CancellationToken ct)
    {
        var (areaCode, offset) = ResolveAddress("D0");
        var body = new byte[]
        {
            areaCode,
            (byte)(offset >> 8), (byte)(offset & 0xFF),
            0x00,
            0x00, 0x01,
        };
        await SendAsync(0x01, 0x01, body, ct);
    }

    public Task DisconnectAsync()
    {
        lock (_sync)
        {
            _udp?.Close();
            _udp?.Dispose();
            _udp = null;
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
        if (_udp is null) return false;
        try
        {
            // 读 1 个 DM 字作为探活。
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
        var (areaCode, offset) = ResolveAddress(registerAddress);
        var sw = Stopwatch.StartNew();
        try
        {
            // Memory Area Read (0x0101)：区码 + 字地址(2) + 位(1)=0 + 字数(2)
            var body = new byte[]
            {
                areaCode,
                (byte)(offset >> 8), (byte)(offset & 0xFF),
                0x00,
                (byte)(length >> 8), (byte)(length & 0xFF),
            };
            var resp = await SendAsync(0x01, 0x01, body, ct);
            const int dataStart = 14; // header(10)+cmd(2)+endcode(2)
            if (resp.Length < dataStart + length * 2)
                throw new InvalidOperationException($"FINS 读响应数据不足（{resp.Length} 字节）");
            var result = new int[length];
            for (var i = 0; i < length; i++)
                result[i] = (resp[dataStart + i * 2] << 8) | resp[dataStart + i * 2 + 1];

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
        var (areaCode, offset) = ResolveAddress(registerAddress);
        var sw = Stopwatch.StartNew();
        try
        {
            // Memory Area Write (0x0102)：区码 + 字地址(2) + 位(1)=0 + 字数(2)=1 + 数据(2)
            var word = (ushort)value;
            var body = new byte[]
            {
                areaCode,
                (byte)(offset >> 8), (byte)(offset & 0xFF),
                0x00,
                0x00, 0x01,
                (byte)(word >> 8), (byte)(word & 0xFF),
            };
            await SendAsync(0x01, 0x02, body, ct);

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

    /// <summary>发送一帧 FINS 命令并返回完整响应（含已校验的结束码）。</summary>
    private async Task<byte[]> SendAsync(byte mrc, byte src, byte[] body, CancellationToken ct)
    {
        var udp = _udp ?? throw new InvalidOperationException($"PLC {PlcId} 未连接");
        byte sid;
        lock (_sync) { sid = unchecked(++_sid); }

        var frame = new byte[10 + 2 + body.Length];
        frame[0] = 0x80; // ICF：命令，需响应
        frame[1] = 0x00; // RSV
        frame[2] = 0x02; // GCT：网关计数
        frame[3] = 0x00; // DNA：目的网络号
        frame[4] = _destNode; // DA1：目的节点号
        frame[5] = 0x00; // DA2：目的单元号
        frame[6] = 0x00; // SNA：源网络号
        frame[7] = _srcNode; // SA1：源节点号
        frame[8] = 0x00; // SA2：源单元号
        frame[9] = sid; // SID
        frame[10] = mrc; // MRC：主命令码
        frame[11] = src; // SRC：子命令码
        Array.Copy(body, 0, frame, 12, body.Length);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_rwTimeoutMs);

        // 用带 CancellationToken 的重载：超时会真正取消底层 socket 操作，
        // 避免遗留未完成的 ReceiveAsync 任务在关闭时抛“未观察的 Task 异常”。
        await udp.SendAsync(frame.AsMemory(0, frame.Length), timeoutCts.Token);
        var recv = await udp.ReceiveAsync(timeoutCts.Token);
        var resp = recv.Buffer;

        if (resp.Length < 14)
            throw new InvalidOperationException($"FINS 响应过短（{resp.Length} 字节）");
        // 结束码 resp[12..13] 含 3 个状态标志位，必须剥离后再判成败：
        //   MRES bit7 = 网络中继错误；SRES bit7 = PLC 致命错误；SRES bit6 = PLC 非致命错误。
        // 其中「非致命错误」（如电池欠压）不影响本次读写结果，若一并当作失败会导致
        // 带该标志的正常响应被误判为连接失败，整机永远连不上。
        var mres = resp[12];
        var sres = resp[13];
        var relayError = (mres & 0x80) != 0;
        var pcFatalError = (sres & 0x80) != 0;
        var pcNonFatalError = (sres & 0x40) != 0;
        var realCode = ((mres & 0x7F) << 8) | (sres & 0x3F);
        if (relayError || pcFatalError || realCode != 0)
            throw new InvalidOperationException($"FINS 错误码 {mres:X2}{sres:X2}");
        if (pcNonFatalError)
            _logger.LogWarning("PLC {PlcId} 存在非致命错误（如电池欠压），通信正常但建议现场检查。", PlcId);
        return resp;
    }

    private static (byte AreaCode, int Offset) ResolveAddress(string registerAddress)
    {
        var (area, offset) = RegisterAddress.Parse(registerAddress);
        if (!WordAreaCodes.TryGetValue(area, out var code))
            throw new NotSupportedException($"FINS 暂不支持内存区 '{area}'（地址 {registerAddress}）");
        return (code, offset);
    }

    private static byte LastOctet(string host, byte fallback) =>
        IPAddress.TryParse(host, out var ip) ? LastOctet(ip, fallback) : fallback;

    private static byte LastOctet(IPAddress? ip, byte fallback)
    {
        if (ip is null) return fallback;
        var bytes = ip.MapToIPv4().GetAddressBytes();
        return bytes.Length == 4 && bytes[3] != 0 ? bytes[3] : fallback;
    }

    private void SetState(PlcConnectionState state, string? message = null)
    {
        if (_state == state) return;
        _state = state;
        ConnectionStateChanged?.Invoke(this, new PlcConnectionStateChangedEventArgs(PlcId, state, message));
    }

    public void Dispose()
    {
        _udp?.Dispose();
    }
}
