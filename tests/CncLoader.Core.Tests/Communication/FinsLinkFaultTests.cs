using System.Net;
using System.Net.Sockets;
using CncLoader.Communication.Plc;
using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CncLoader.Core.Tests.Communication;

/// <summary>P0-1：FINS/UDP 断链后读超时，必须置 Faulted 使 IsConnected 翻 false，否则轮询拿陈旧信号继续派工。</summary>
[TestFixture]
public sealed class FinsLinkFaultTests
{
    [Test]
    public async Task FINS读超时_链路故障_置Faulted且IsConnected为false()
    {
        // 服务端只回应连接探活（D0 读），之后静默：模拟真机断链/断电后的读超时。
        await using var server = new RespondOnceFinsServer();
        var endpoint = new PlcEndpoint("127.0.0.1", server.Port, "FINS");
        using var client = new OmronFinsPlcClient(1, endpoint, connectTimeoutMs: 2000, rwTimeoutMs: 300,
            NullLogger<OmronFinsPlcClient>.Instance, new NoopDeviceLogger());

        await client.ConnectAsync();
        Assert.That(client.IsConnected, Is.True, "连接探活成功后应在线");

        Assert.ThrowsAsync<TimeoutException>(async () => await client.ReadRegistersAsync("D1006", 1));

        Assert.Multiple(() =>
        {
            Assert.That(client.State, Is.EqualTo(PlcConnectionState.Faulted));
            Assert.That(client.IsConnected, Is.False);
        });
    }

    private sealed class NoopDeviceLogger : IDeviceLogger
    {
        public void Log(DeviceLogEntry entry) { }
    }

    /// <summary>只回应第一帧（连接探活），之后静默，触发后续读超时。</summary>
    private sealed class RespondOnceFinsServer : IAsyncDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly Task _loop;
        private int _responded;

        public RespondOnceFinsServer() => _loop = Task.Run(LoopAsync);

        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        private async Task LoopAsync()
        {
            try
            {
                while (true)
                {
                    var recv = await _udp.ReceiveAsync();
                    if (Interlocked.Exchange(ref _responded, 1) == 0)
                    {
                        var resp = BuildReadOk(recv.Buffer);
                        if (resp is not null)
                            await _udp.SendAsync(resp, resp.Length, recv.RemoteEndPoint);
                    }
                }
            }
            catch (Exception)
            {
                // 服务端关闭
            }
        }

        private static byte[]? BuildReadOk(byte[] req)
        {
            if (req.Length < 18) return null;
            var sid = req[9];
            var count = (req[16] << 8) | req[17];
            var resp = new byte[14 + count * 2];
            resp[0] = 0xC0;
            resp[2] = 0x02;
            resp[4] = req[7]; // DNA = 客户端节点号
            resp[7] = 0x01;
            resp[9] = sid;
            resp[10] = 0x01;
            resp[11] = req[11];
            // resp[12..13] = 0x0000 结束码（正常）
            return resp;
        }

        public async ValueTask DisposeAsync()
        {
            _udp.Dispose();
            try { await _loop; } catch { /* ignore */ }
        }
    }
}
