using System.Net;
using System.Net.Sockets;
using CncLoader.Communication.Plc;
using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Communication;

/// <summary>
/// 共物理 PLC：多台逻辑 FINS 客户端打同一 IP:端口时，真机通常只接受一笔在途命令。
/// 本测试用「忙时丢第二包」的 UDP 服务复现现场超时，断言客户端必须串行化该端点。
/// </summary>
[TestFixture]
public sealed class FinsSharedEndpointSerializationTests
{
    [Test]
    public async Task 两台逻辑PLC共物理端点_并发读不得超时()
    {
        await using var server = new SingleFlightFinsServer(busyMs: 40);
        var endpoint = new PlcEndpoint("127.0.0.1", server.Port, "FINS");

        using var a = CreateClient(1, endpoint);
        using var b = CreateClient(2, endpoint);
        await a.ConnectAsync();
        await b.ConnectAsync();

        var tasks = new List<Task<int[]>>(32);
        for (var i = 0; i < 16; i++)
        {
            tasks.Add(a.ReadRegistersAsync("D1006", 1));
            tasks.Add(b.ReadRegistersAsync("D1200", 1));
        }

        int[][] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Assert.Fail($"共物理端点并发读失败（服务端丢包 {server.Dropped}）：{ex.GetType().Name} {ex.Message}");
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(server.Dropped, Is.EqualTo(0), "客户端应串行化同一端点，服务端不得丢包");
            Assert.That(results, Has.Length.EqualTo(32));
            Assert.That(results.All(r => r.Length == 1), Is.True);
        });
    }

    private static OmronFinsPlcClient CreateClient(long plcId, PlcEndpoint endpoint) =>
        new(plcId, endpoint, connectTimeoutMs: 2000, rwTimeoutMs: 400,
            NullLogger<OmronFinsPlcClient>.Instance, new NoopDeviceLogger());

    private sealed class NoopDeviceLogger : IDeviceLogger
    {
        public void Log(DeviceLogEntry entry) { }
    }

    /// <summary>在途一笔：处理期间到达的第二帧直接丢弃，模拟欧姆龙 FINS/UDP 真机。</summary>
    private sealed class SingleFlightFinsServer : IAsyncDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _inflight;
        private int _dropped;

        public SingleFlightFinsServer(int busyMs)
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            _loop = Task.Run(() => ServeAsync(busyMs, _cts.Token));
        }

        public int Port { get; }
        public int Dropped => Volatile.Read(ref _dropped);

        private async Task ServeAsync(int busyMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult recv;
                try { recv = await _udp.ReceiveAsync(ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }

                if (Interlocked.CompareExchange(ref _inflight, 1, 0) != 0)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }

                _ = ReplyAsync(recv, busyMs);
            }
        }

        private async Task ReplyAsync(UdpReceiveResult recv, int busyMs)
        {
            try
            {
                await Task.Delay(busyMs);
                var resp = BuildReadOk(recv.Buffer);
                if (resp is not null)
                    await _udp.SendAsync(resp, resp.Length, recv.RemoteEndPoint);
            }
            finally
            {
                Interlocked.Exchange(ref _inflight, 0);
            }
        }

        private static byte[]? BuildReadOk(byte[] req)
        {
            if (req.Length < 18) return null;
            var clientNode = req[7];
            var sid = req[9];
            var count = (req[16] << 8) | req[17];
            var resp = new byte[14 + count * 2];
            resp[0] = 0xC0;
            resp[2] = 0x02;
            resp[4] = clientNode;
            resp[7] = 0x01;
            resp[9] = sid;
            resp[10] = 0x01;
            resp[11] = req[11];
            return resp;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _udp.Dispose(); } catch { /* ignore */ }
            try { await _loop; } catch { /* ignore */ }
            _cts.Dispose();
        }
    }
}
