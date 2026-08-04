using System.IO;
using System.Text;
using CncLoader.Communication.Rcs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Communication.RcsCallbackTestFakes;

namespace CncLoader.Core.Tests.Communication;

/// <summary>
/// P0-4 第三组：Host ACK / HTTP 200 契约（真实 ACK 工厂 + IResult.ExecuteAsync，不启 Kestrel）。
/// </summary>
[TestFixture]
public sealed class RcsCallbackHostAckTests
{
    private const string TaskId = "LINE-MV-20260804130000-0001";

    private static readonly string PushBody =
        "{\"taskId\":\"" + TaskId + "\",\"data\":{\"system\":{\"error_code\":0,\"msg\":\"ok\"}}}";

    [Test]
    public async Task 持久化成功_Host映射HTTP200_ContentType与ACK_JSON一致()
    {
        var store = new FakeTaskStore();
        var sut = CreateProcessor(store, new FakeAlarms());

        var ack = await sut.HandlePushTaskStatusAsync(PushBody);
        var (status, contentType, body) = await ExecuteAckAsync(ack);

        Assert.Multiple(() =>
        {
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            Assert.That(status, Is.EqualTo(StatusCodes.Status200OK));
            Assert.That(contentType, Is.EqualTo(RcsCallbackAckResults.JsonContentType)
                .Or.EqualTo("application/json; charset=utf-8"));
            Assert.That(body, Is.EqualTo(ack));
            Assert.That(body, Does.Contain("\"taskId\":\"" + TaskId + "\""));
        });
    }

    [Test]
    public async Task 持久化失败_仍映射HTTP200_ACK格式不变_同key可重试()
    {
        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => throw new InvalidOperationException("测试：落库失败"));
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var sut = CreateProcessor(store, new FakeAlarms());

        var ackFail = await sut.HandlePushTaskStatusAsync(PushBody);
        var (status, contentType, body) = await ExecuteAckAsync(ackFail);

        var ackRetry = await sut.HandlePushTaskStatusAsync(PushBody);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(StatusCodes.Status200OK),
                "Failed 内部结果仍由 Host 映射为兼容 HTTP 200");
            Assert.That(contentType, Is.EqualTo(RcsCallbackAckResults.JsonContentType)
                .Or.EqualTo("application/json; charset=utf-8"));
            Assert.That(body, Is.EqualTo(ackFail));
            Assert.That(body, Does.Contain("\"taskId\":\"" + TaskId + "\""));
            Assert.That(ackRetry, Is.EqualTo(ackFail), "ACK body 格式不变");
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                "失败未进 final seen，同 key 仍可重试");
        });
    }

    private static async Task<(int StatusCode, string? ContentType, string Body)> ExecuteAckAsync(string ackBody)
    {
        var result = RcsCallbackAckResults.FromAckBody(ackBody);
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return (ctx.Response.StatusCode, ctx.Response.ContentType, body);
    }
}
