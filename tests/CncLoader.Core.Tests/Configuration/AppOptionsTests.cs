using CncLoader.Common.Configuration;
using CncLoader.Common.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace CncLoader.Core.Tests.Configuration;

[TestFixture]
[SupportedOSPlatform("windows")]
public sealed class AppOptionsTests
{
    [Test]
    public void 配置缺失时复核失败阈值应为六次()
    {
        Assert.That(new RcsOptions().HasMatRecheckFailThreshold, Is.EqualTo(6));
    }

    [Test]
    public void 复核失败阈值小于等于零时启动校验应失败()
    {
        var values = new Dictionary<string, string?>
        {
            ["App:Rcs:HasMatRecheckFailThreshold"] = "0"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddCncCommon(configuration))
            .Build();

        Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Test]
    public void 配置缺失时对账重试间隔应为五千毫秒()
    {
        Assert.That(new RcsOptions().ReconcileRetryIntervalMs, Is.EqualTo(5000));
    }

    [Test]
    public void 对账重试间隔小于等于零时启动校验应失败([Values(0, -1)] int invalid)
    {
        var values = new Dictionary<string, string?>
        {
            ["App:Rcs:ReconcileRetryIntervalMs"] = invalid.ToString()
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddCncCommon(configuration))
            .Build();

        Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Test]
    public async Task 对账重试间隔正数时启动校验应通过()
    {
        var values = new Dictionary<string, string?>
        {
            ["App:Rcs:ReconcileRetryIntervalMs"] = "100"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddCncCommon(configuration))
            .Build();

        await host.StartAsync();
        try
        {
            var options = host.Services.GetRequiredService<IOptions<AppOptions>>().Value;
            Assert.That(options.Rcs.ReconcileRetryIntervalMs, Is.EqualTo(100));
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
