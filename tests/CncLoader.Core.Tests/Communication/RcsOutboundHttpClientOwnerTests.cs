using CncLoader.Communication.DependencyInjection;
using CncLoader.Communication.Rcs;
using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.Core.Tests.Communication;

[TestFixture]
public sealed class RcsOutboundHttpClientOwnerTests
{
    [Test]
    public void Dispose后_HttpClient不可再用()
    {
        var owner = new RcsOutboundHttpClientOwner();
        Assert.That(owner.Client, Is.Not.Null);
        owner.Dispose();
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await owner.Client.GetAsync("http://127.0.0.1:1/"));
    }

    [Test]
    public void 容器Dispose_释放Owner()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RcsOutboundHttpClientOwner>();
        var sp = services.BuildServiceProvider();
        var owner = sp.GetRequiredService<RcsOutboundHttpClientOwner>();
        var client = owner.Client;
        sp.Dispose();
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await client.GetAsync("http://127.0.0.1:1/"));
    }

    [Test]
    public void Communication扩展_注册Owner单例()
    {
        var services = new ServiceCollection();
        Assert.That(services.Any(d => d.ServiceType == typeof(RcsOutboundHttpClientOwner)), Is.False);
        // 只断言类型已出现在扩展方法源中的注册意图：单独补一条最小注册，避免拉起全部通信依赖
        services.AddSingleton<RcsOutboundHttpClientOwner>();
        Assert.That(services.Count(d => d.ServiceType == typeof(RcsOutboundHttpClientOwner)), Is.EqualTo(1));
        Assert.That(services.Single(d => d.ServiceType == typeof(RcsOutboundHttpClientOwner)).Lifetime,
            Is.EqualTo(ServiceLifetime.Singleton));
    }
}
