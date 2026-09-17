using System.Net;
using System.Net.Sockets;
using CncLoader.Common.Configuration;
using CncLoader.Communication.Rcs;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static CncLoader.Core.Tests.Communication.RcsCallbackTestFakes;

namespace CncLoader.Core.Tests.Communication;

/// <summary>本机回调探针：未监听 / 成功 / 403 / 网络失败。HttpClient 只在 Communication。</summary>
[TestFixture]
public sealed class RcsCallbackHostProbeTests
{
    [Test]
    public async Task 未启动_返回未监听()
    {
        var host = CreateHost(FreePort(), allowedIps: new[] { "192.168.1.50" });

        var r = await host.ProbePushEndpointAsync();

        Assert.Multiple(() =>
        {
            Assert.That(r.Ok, Is.False);
            Assert.That(r.FailureKind, Is.EqualTo(RcsLocalCallbackProbeFailureKind.NotListening));
            Assert.That(r.Error, Is.Not.Empty);
        });
    }

    [Test]
    public async Task 已监听且白名单含现场IP_环回探针成功且空taskId不改任务态()
    {
        var processor = new RecordingProcessor();
        await using var host = await StartHostAsync(FreePort(), new[] { "192.168.1.50" }, processor);

        var r = await host.ProbePushEndpointAsync();

        Assert.Multiple(() =>
        {
            Assert.That(r.Ok, Is.True);
            Assert.That(r.FailureKind, Is.EqualTo(RcsLocalCallbackProbeFailureKind.None));
            Assert.That(r.HttpStatus, Is.EqualTo(200));
            Assert.That(r.AckBody, Does.Contain("taskId"));
            Assert.That(processor.PushCount, Is.EqualTo(1));
            Assert.That(processor.LastBody, Does.Contain("\"taskId\":\"\""));
            Assert.That(processor.LastBody, Does.Contain("callback-probe"));
        });
    }

    [Test]
    public async Task 白名单为空_failClosed返回403()
    {
        var processor = new RecordingProcessor();
        await using var host = await StartHostAsync(FreePort(), Array.Empty<string>(), processor);

        var r = await host.ProbePushEndpointAsync();

        Assert.Multiple(() =>
        {
            Assert.That(r.Ok, Is.False);
            Assert.That(r.FailureKind, Is.EqualTo(RcsLocalCallbackProbeFailureKind.Forbidden));
            Assert.That(r.HttpStatus, Is.EqualTo(403));
            Assert.That(processor.PushCount, Is.Zero, "403 不得进入处理器");
        });
    }

    [Test]
    public async Task 目标端口无监听_网络失败()
    {
        var r = await RcsLocalCallbackProber.PostPushAsync("127.0.0.1", FreePort());

        Assert.Multiple(() =>
        {
            Assert.That(r.Ok, Is.False);
            Assert.That(r.FailureKind, Is.EqualTo(RcsLocalCallbackProbeFailureKind.Unreachable));
            Assert.That(r.Error, Is.Not.Empty);
        });
    }

    private static async Task<RcsCallbackHost> StartHostAsync(
        int port, string[] allowedIps, IRcsCallbackProcessor? processor = null)
    {
        var host = CreateHost(port, allowedIps, processor);
        await host.StartAsync(CancellationToken.None);
        if (!host.IsListening)
            Assert.Fail($"回调宿主未监听：{host.ListenError}");
        return host;
    }

    private static RcsCallbackHost CreateHost(
        int port, string[] allowedIps, IRcsCallbackProcessor? processor = null)
        => new(
            new ProbeRuntime(port),
            processor ?? new RecordingProcessor(),
            new FakeAlarms(),
            Options.Create(new AppOptions
            {
                Rcs = new RcsOptions
                {
                    UseSimulator = true,
                    CallbackHost = "127.0.0.1",
                    CallbackPort = port,
                    CallbackAllowedRemoteIps = allowedIps
                }
            }),
            NullLogger<RcsCallbackHost>.Instance);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class ProbeRuntime : IRcsRuntimeConfig
    {
        public ProbeRuntime(int port)
        {
            CallbackPort = port;
            BootCallbackPort = port;
        }

        public string BaseUrl => "http://127.0.0.1:8090";
        public string ClientCode => "CNC";
        public string Version => "1";
        public string TokenCode => "t";
        public int RequestTimeoutMs => 3000;
        public int MaxRetries => 1;
        public string CallbackHost => "127.0.0.1";
        public int CallbackPort { get; }
        public int PollIntervalMs => 3000;
        public string BootCallbackHost => CallbackHost;
        public int BootCallbackPort { get; }
        public void Apply(RcsConnectionConfig config) { }
        public void CaptureBootCallback() { }
        public RcsConnectionConfig Snapshot() => new()
        {
            CallbackHost = CallbackHost,
            CallbackPort = CallbackPort
        };
    }

    private sealed class RecordingProcessor : IRcsCallbackProcessor
    {
        public int PushCount { get; private set; }
        public string? LastBody { get; private set; }

        public Task<string> HandlePushTaskStatusAsync(string rawBody, CancellationToken ct = default)
        {
            PushCount++;
            LastBody = rawBody;
            return Task.FromResult("""{"taskId":""}""");
        }

        public Task<string> HandleScanTaskStatusAsync(string rawBody, CancellationToken ct = default)
            => Task.FromResult("""{"taskId":""}""");

        public Task<string> HandleWarnCallbackAsync(string rawBody, CancellationToken ct = default)
            => Task.FromResult("""{"taskId":""}""");

        public void ForgetTask(string taskId) { }
    }
}
