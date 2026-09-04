using CncLoader.Common.DependencyInjection;
using CncLoader.Communication.DependencyInjection;
using CncLoader.Core.Abstractions;
using CncLoader.Core.DependencyInjection;
using CncLoader.Core.Rcs;
using CncLoader.Data.DependencyInjection;
using CncLoader.UI.DependencyInjection;
using CncLoader.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.Core.Tests.UI;

/// <summary>
/// 组合根真实解析：按 App.xaml.cs 的顺序注册后逐个 Resolve。
/// 只审计描述符不够——「ViewModel 加了构造参数但忘了注册接缝」这类错只在 Resolve 时才暴露。
/// 不建连接、不起 HostedService，仅构造对象图。
/// </summary>
[TestFixture]
public sealed class CompositionRootResolveTests
{
    private ServiceProvider _provider = null!;

    [OneTimeSetUp]
    public void BuildContainer()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:Database:Server"] = "localhost",
                ["App:Database:Database"] = "cnc_auto",
                ["App:Database:User"] = "root",
                ["App:Database:Password"] = "placeholder",
                ["App:Rcs:BaseUrl"] = "http://127.0.0.1:5050",
                ["App:Rcs:ReconcileRetryIntervalMs"] = "5000",
                ["App:Rcs:HasMatRecheckFailThreshold"] = "6"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddCncCommon(configuration);
        services.AddCncCore();
        services.AddCncData();
        services.AddCncCommunication();
        services.AddCncUi();

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = false // HostedService 的构造在 Host 启动时才需要
        });
    }

    [OneTimeTearDown]
    public void Dispose() => _provider.Dispose();

    [TestCase(typeof(IUserNotificationService))]
    [TestCase(typeof(IUiDispatcher))]
    [TestCase(typeof(IDialogService))]
    [TestCase(typeof(IRcsTaskService))]
    public void Seam_Resolves(Type service)
    {
        Assert.That(_provider.GetService(service), Is.Not.Null, $"{service.Name} 未注册或依赖不全");
    }

    [Test]
    public void RcsClient_IsNotResolvableFromContainer()
    {
        // 结构性门禁：容器里拿不到裸出站客户端，新增服务无法绕过 IRcsTaskService 的受管派工门禁
        var clientType = typeof(IRcsTaskService).Assembly.GetType("CncLoader.Core.Rcs.IRcsClient")
                         ?? typeof(RcsResult).Assembly.GetType("CncLoader.Core.Rcs.IRcsClient");
        Assert.That(clientType, Is.Not.Null, "IRcsClient 类型应仍存在（只是 internal）");
        Assert.That(_provider.GetService(clientType!), Is.Null,
            "IRcsClient 不得可从容器解析");
    }
}
