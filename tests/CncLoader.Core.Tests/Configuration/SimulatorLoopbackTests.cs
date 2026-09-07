using CncLoader.Common.Configuration;
using CncLoader.Communication.Rcs;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Options;

namespace CncLoader.Core.Tests.Configuration;

[TestFixture]
public sealed class SimulatorLoopbackTests
{
    [Test]
    public void ToLocalRcsBaseUrl_现场地址_只改主机保留端口()
    {
        Assert.That(SimulatorLoopback.ToLocalRcsBaseUrl("http://192.168.250.13:5050"),
            Is.EqualTo("http://127.0.0.1:5050"));
    }

    [Test]
    public void ToLocalRcsBaseUrl_本机8090_保持不变()
    {
        Assert.That(SimulatorLoopback.ToLocalRcsBaseUrl("http://127.0.0.1:8090"),
            Is.EqualTo("http://127.0.0.1:8090"));
    }

    [Test]
    public void ToLocalRcsBaseUrl_空值_回落到8090()
    {
        Assert.That(SimulatorLoopback.ToLocalRcsBaseUrl(null),
            Is.EqualTo("http://127.0.0.1:8090"));
    }

    [Test]
    public void ResolvePlcBind_模拟器_非环回强制127()
    {
        Assert.That(SimulatorLoopback.ResolvePlcBind(true, "192.168.250.1"),
            Is.EqualTo("127.0.0.1"));
    }

    [Test]
    public void ResolvePlcBind_真机_保留配置()
    {
        Assert.That(SimulatorLoopback.ResolvePlcBind(false, "192.168.250.1"),
            Is.EqualTo("192.168.250.1"));
    }

    [Test]
    public void RcsRuntimeConfig_模拟器Apply现场地址_出站仍本机()
    {
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions
            {
                UseSimulator = true,
                BaseUrl = "http://127.0.0.1:8090",
                ClientCode = "WMS"
            }
        });
        var runtime = new RcsRuntimeConfig(options);

        runtime.Apply(new RcsConnectionConfig
        {
            BaseUrl = "http://192.168.250.13:5050",
            ClientCode = "WMS",
            CallbackHost = "0.0.0.0",
            CallbackPort = 9080
        });

        Assert.That(runtime.BaseUrl, Is.EqualTo("http://127.0.0.1:8090"));
        Assert.That(runtime.ClientCode, Is.EqualTo("WMS"));
    }

    [Test]
    public void RcsRuntimeConfig_真机Apply_使用库地址()
    {
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions
            {
                UseSimulator = false,
                BaseUrl = "http://127.0.0.1:8090",
                ClientCode = "WMS"
            }
        });
        var runtime = new RcsRuntimeConfig(options);

        runtime.Apply(new RcsConnectionConfig
        {
            BaseUrl = "http://192.168.250.13:5050",
            ClientCode = "WMS",
            CallbackHost = "0.0.0.0",
            CallbackPort = 9080
        });

        Assert.That(runtime.BaseUrl, Is.EqualTo("http://192.168.250.13:5050"));
    }
}
