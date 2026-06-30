using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Simulation;

/// <summary>
/// 进程内欧姆龙 FINS/UDP 模拟器（开发期无真机时验证 FINS 链路）。为每台虚拟 PLC 在环回地址各开一个
/// UDP 端口，按《测试机信号表》预置 DM 字。仅实现 Memory Area Read(0x0101)/Write(0x0102) 两条命令，
/// 与 OmronFinsPlcClient 配套，可端到端自测连接/读/写/心跳。
/// </summary>
public sealed class OmronFinsUdpSimulator : IDisposable
{
    private const byte PlcNode = 1; // 环回模拟，节点号固定 1（客户端对节点号不挑）。

    private readonly ILogger<OmronFinsUdpSimulator> _logger;
    private readonly IPAddress _bindAddress;
    private readonly ConcurrentDictionary<long, VirtualPlc> _plcs = new();
    private CancellationTokenSource? _cts;

    public OmronFinsUdpSimulator(string bindAddress, ILogger<OmronFinsUdpSimulator> logger)
    {
        _logger = logger;
        _bindAddress = IPAddress.Parse(bindAddress);
    }

    public bool IsRunning { get; private set; }

    /// <summary>登记一台虚拟 PLC。seed：DM 字偏移 → 初值（如 1006 → 2）。</summary>
    public void AddPlc(long plcId, int port, IReadOnlyDictionary<int, ushort> seed)
    {
        var store = new ConcurrentDictionary<int, ushort>();
        foreach (var (offset, value) in seed)
            store[offset] = value;
        _plcs[plcId] = new VirtualPlc(plcId, port, store);
    }

    public int GetPort(long plcId) =>
        _plcs.TryGetValue(plcId, out var p) ? p.Port : throw new KeyNotFoundException($"FINS 模拟器未登记 PLC {plcId}");

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;
        _cts = new CancellationTokenSource();

        foreach (var plc in _plcs.Values)
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(_bindAddress, plc.Port));
                plc.Socket = udp;
                plc.ListenTask = Task.Run(() => ServeAsync(plc, udp, _cts.Token));
                _logger.LogInformation("FINS 模拟器 PLC {PlcId} 监听 {Addr}:{Port}", plc.PlcId, _bindAddress, plc.Port);
            }
            catch (SocketException ex)
            {
                // 端口被占用（10048）：通常是同时开了多个程序实例。单台失败不影响其余台启动。
                _logger.LogWarning(ex, "FINS 模拟器 PLC {PlcId} 端口 {Port} 监听失败（可能已有程序实例占用，请勿同时运行多个实例）",
                    plc.PlcId, plc.Port);
            }
        }

        IsRunning = true;
        return Task.CompletedTask;
    }

    private async Task ServeAsync(VirtualPlc plc, UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult recv;
            try
            {
                recv = await udp.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FINS 模拟器 PLC {PlcId} 接收异常", plc.PlcId);
                continue;
            }

            try
            {
                var response = BuildResponse(plc, recv.Buffer);
                if (response is not null)
                    await udp.SendAsync(response, response.Length, recv.RemoteEndPoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FINS 模拟器 PLC {PlcId} 处理请求异常", plc.PlcId);
            }
        }
    }

    /// <summary>解析 FINS 请求并构造响应；非法/不支持的帧返回 null（不回复）。</summary>
    private static byte[]? BuildResponse(VirtualPlc plc, byte[] req)
    {
        if (req.Length < 12) return null;
        var clientNode = req[7];   // 请求源节点 SA1 → 响应回送目的
        var sid = req[9];
        var mrc = req[10];
        var src = req[11];

        // 仅支持内存区读/写（MRC=0x01）。
        if (mrc != 0x01) return ErrorResponse(clientNode, sid, mrc, src, 0x01, 0x04); // 不支持的命令

        if (src == 0x01) // Memory Area Read
        {
            if (req.Length < 18) return null;
            var addr = (req[13] << 8) | req[14];
            var count = (req[16] << 8) | req[17];
            var resp = new byte[14 + count * 2];
            WriteHeader(resp, clientNode, sid, mrc, src);
            for (var i = 0; i < count; i++)
            {
                plc.Store.TryGetValue(addr + i, out var w);
                resp[14 + i * 2] = (byte)(w >> 8);
                resp[14 + i * 2 + 1] = (byte)(w & 0xFF);
            }
            return resp;
        }

        if (src == 0x02) // Memory Area Write
        {
            if (req.Length < 18) return null;
            var addr = (req[13] << 8) | req[14];
            var count = (req[16] << 8) | req[17];
            for (var i = 0; i < count; i++)
            {
                var idx = 18 + i * 2;
                if (idx + 1 >= req.Length) break;
                plc.Store[addr + i] = (ushort)((req[idx] << 8) | req[idx + 1]);
            }
            var resp = new byte[14];
            WriteHeader(resp, clientNode, sid, mrc, src);
            return resp;
        }

        return ErrorResponse(clientNode, sid, mrc, src, 0x01, 0x04);
    }

    private static void WriteHeader(byte[] buf, byte clientNode, byte sid, byte mrc, byte src)
    {
        buf[0] = 0xC0; // ICF：响应帧
        buf[1] = 0x00; // RSV
        buf[2] = 0x02; // GCT
        buf[3] = 0x00; // DNA
        buf[4] = clientNode; // DA1：回送至请求方
        buf[5] = 0x00; // DA2
        buf[6] = 0x00; // SNA
        buf[7] = PlcNode; // SA1：本模拟 PLC
        buf[8] = 0x00; // SA2
        buf[9] = sid; // SID 回显
        buf[10] = mrc;
        buf[11] = src;
        buf[12] = 0x00; // 结束码主 0=成功
        buf[13] = 0x00; // 结束码次
    }

    private static byte[] ErrorResponse(byte clientNode, byte sid, byte mrc, byte src, byte mres, byte sres)
    {
        var resp = new byte[14];
        WriteHeader(resp, clientNode, sid, mrc, src);
        resp[12] = mres;
        resp[13] = sres;
        return resp;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        _cts?.Cancel();
        foreach (var plc in _plcs.Values)
        {
            try { plc.Socket?.Close(); } catch { /* ignore */ }
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

    private sealed class VirtualPlc(long plcId, int port, ConcurrentDictionary<int, ushort> store)
    {
        public long PlcId { get; } = plcId;
        public int Port { get; } = port;
        public ConcurrentDictionary<int, ushort> Store { get; } = store;
        public UdpClient? Socket { get; set; }
        public Task? ListenTask { get; set; }
    }
}
