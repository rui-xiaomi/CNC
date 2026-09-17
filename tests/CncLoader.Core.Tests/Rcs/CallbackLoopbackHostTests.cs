using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

/// <summary>本机回调探针：任意网卡 / 非法 Host / 环回一律落到 127.0.0.1。</summary>
[TestFixture]
public sealed class CallbackLoopbackHostTests
{
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("0.0.0.0")]
    [TestCase("::")]
    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    [TestCase("00.0.0.")]
    public void 任意网卡非法或环回_解析为IPv4环回(string? boundHost)
    {
        Assert.That(CallbackLoopbackHost.Resolve(boundHost), Is.EqualTo("127.0.0.1"));
    }

    [Test]
    public void 具体网卡地址_保持原值()
    {
        Assert.That(CallbackLoopbackHost.Resolve("192.168.1.20"), Is.EqualTo("192.168.1.20"));
    }
}
