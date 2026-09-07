using System.Net;
using System.Net.Sockets;
using CncLoader.Communication.Plc;
using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace CncLoader.Core.Tests.Communication;

/// <summary>P0-1：持续读超时才置 Faulted；单次 UDP 丢包不得整台下线。</summary>
[TestFixture]
public sealed class FinsLinkFaultTests
{
    [Test]
    public async Task FINS单次读超时_保持在线()
    {
        await using var server = new RespondOnceFinsServer();
        var endpoint = new PlcEndpoint("127.0.0.1", server.Port, "FINS");
        using var client = new OmronFinsPlcClient(1, endpoint, connectTimeoutMs: 2000, rwTimeoutMs: 200,
            NullLogger<OmronFinsPlcClient>.Instance, new NoopDeviceLogger(), linkFaultThreshold: 3);

        await client.ConnectAsync();
        Assert.That(client.IsConnected, Is.True);

        Assert.ThrowsAsync<TimeoutException>(async () => await client.ReadRegistersAsync("D1006", 1));

        Assert.Multiple(() =>
        {
            Assert.That(client.State, Is.EqualTo(PlcConnectionState.Connected));
            Assert.That(client.IsConnected, Is.True);
        });
    }

    [Test]
    public async Task FINS连续读超时达阈值_置Faulted且IsConnected为false()
    {
        await using var server = new RespondOnceFinsServer();
        var endpoint = new PlcEndpoint("127.0.0.1", server.Port, "FINS");
        using var client = new OmronFinsPlcClient(1, endpoint, connectTimeoutMs: 2000, rwTimeoutMs: 200,
            NullLogger<OmronFinsPlcClient>.Instance, new NoopDeviceLogger(), linkFaultThreshold: 3);

        await client.ConnectAsync();
        Assert.That(client.IsConnected, Is.True);

        Assert.ThrowsAsync<TimeoutException>(async () => await client.ReadRegistersAsync("D1006", 1));
        Assert.ThrowsAsync<TimeoutException>(async () => await client.ReadRegistersAsync("D1006", 1));
        Assert.That(client.IsConnected, Is.True, "第 2 次超时仍在线");
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
