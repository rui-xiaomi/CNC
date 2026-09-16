using System.IO;
using System.Net;
using System.Net.Http;
using CncLoader.Communication.Rcs;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Rcs;

/// <summary>P0-2：创建类请求发出后超时/断连/5xx 为「结果未知」，不得重发；请求未发出（建连失败）可重发。</summary>
[TestFixture]
public sealed class RcsClientOutcomeTests
{
    [Test]
    public async Task 建连失败_请求未发出_允许重试直至成功()
    {
        var handler = new ScriptedHandler(
            _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"),
            _ => Respond(HttpStatusCode.OK));
        var client = Create(handler);

        var r = await client.TransitTaskAsync(new TransitTaskRequest { TaskId = "T-1" });

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True);
            Assert.That(handler.Calls, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task 发出后超时_结果未知_不得重发()
    {
        var handler = new ScriptedHandler(
            _ => throw new TaskCanceledException("timeout"),
            _ => Respond(HttpStatusCode.OK));
        var client = Create(handler);

        var r = await client.ExcuteTaskAsync(new ExcuteTaskRequest { TaskId = "T-2" });

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.False);
            Assert.That(r.FailureKind, Is.EqualTo(RcsFailureKind.OutcomeUnknown));
            Assert.That(handler.Calls, Is.EqualTo(1), "RCS 可能已建任务，重发同 taskId 可能被判重复而误报失败");
        });
    }

    [Test]
    public async Task 响应中途断开_结果未知_不得重发()
    {
        var handler = new ScriptedHandler(
            _ => throw new HttpRequestException("connection reset", new IOException("reset")),
            _ => Respond(HttpStatusCode.OK));
        var client = Create(handler);

        var r = await client.TransitTaskAsync(new TransitTaskRequest { TaskId = "T-3" });

        Assert.Multiple(() =>
        {
            Assert.That(r.FailureKind, Is.EqualTo(RcsFailureKind.OutcomeUnknown));
            Assert.That(handler.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task 创建类5xx为结果未知_4xx为明确失败_均不重发()
    {
        var h5 = new ScriptedHandler(_ => Respond(HttpStatusCode.BadGateway), _ => Respond(HttpStatusCode.OK));
        var h4 = new ScriptedHandler(_ => Respond(HttpStatusCode.BadRequest), _ => Respond(HttpStatusCode.OK));

        var r5 = await Create(h5).TransitTaskAsync(new TransitTaskRequest { TaskId = "T-5" });
        var r4 = await Create(h4).TransitTaskAsync(new TransitTaskRequest { TaskId = "T-4" });

        Assert.Multiple(() =>
        {
            Assert.That(r5.FailureKind, Is.EqualTo(RcsFailureKind.OutcomeUnknown));
            Assert.That(h5.Calls, Is.EqualTo(1));
            Assert.That(r4.Success, Is.False);
            Assert.That(r4.FailureKind, Is.Not.EqualTo(RcsFailureKind.OutcomeUnknown));
            Assert.That(h4.Calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task 查询类保持网络级重试()
    {
        var handler = new ScriptedHandler(
            _ => throw new TaskCanceledException("timeout"),
            _ => Respond(HttpStatusCode.OK));
        var client = Create(handler);

        var r = await client.QueryTaskAsync(QueryTaskRequest.ForLocalIds(new[] { "T-Q" }));

        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True);
            Assert.That(handler.Calls, Is.EqualTo(2));
        });
    }

    private static RcsClient Create(HttpMessageHandler handler)
        => new(new HttpClient(handler), new Runtime(), new NoopMsgLog(), NullLogger<RcsClient>.Instance);

    private static HttpResponseMessage Respond(HttpStatusCode code)
        => new(code) { Content = new StringContent("{\"Success\":true,\"Message\":\"ok\"}") };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _steps;

        public ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) => _steps = steps;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = _steps[Math.Min(Calls, _steps.Length - 1)];
            Calls++;
            return Task.FromResult(step(request));
        }
    }

    private sealed class Runtime : IRcsRuntimeConfig
    {
        public string BaseUrl => "http://127.0.0.1:8090";
        public string ClientCode => "CNC";
        public string Version => "1.0.0";
        public string TokenCode => "0";
        public int RequestTimeoutMs => 1000;
        public int MaxRetries => 3;
        public string CallbackHost => "127.0.0.1";
        public int CallbackPort => 9080;
        public int PollIntervalMs => 3000;
        public string BootCallbackHost => CallbackHost;
        public int BootCallbackPort => CallbackPort;
        public void Apply(RcsConnectionConfig config) { }
        public void CaptureBootCallback() { }
        public RcsConnectionConfig Snapshot() => new()
        {
            BaseUrl = BaseUrl, ClientCode = ClientCode, CallbackHost = CallbackHost,
            CallbackPort = CallbackPort, RequestTimeoutMs = RequestTimeoutMs,
            MaxRetries = MaxRetries, PollIntervalMs = PollIntervalMs
        };
    }

    private sealed class NoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
