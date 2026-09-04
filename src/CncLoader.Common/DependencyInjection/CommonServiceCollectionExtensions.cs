using CncLoader.Common.Configuration;
using CncLoader.Common.Identity;
using CncLoader.Common.Logging;
using CncLoader.Common.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.Common.DependencyInjection;

public static class CommonServiceCollectionExtensions
{
    /// <summary>注册 Common 层基础设施：配置绑定、加解密、操作人、连接串工厂。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static IServiceCollection AddCncCommon(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AppOptions>()
            .Bind(configuration.GetSection(AppOptions.SectionName))
            .Validate(options => options.Rcs.HasMatRecheckFailThreshold > 0,
                "App:Rcs:HasMatRecheckFailThreshold 必须大于 0。")
            .Validate(options => options.Rcs.ReconcileRetryIntervalMs > 0,
                "App:Rcs:ReconcileRetryIntervalMs 必须大于 0。")
            .Validate(options => options.Plc.PollingIntervalMs > 0,
                "App:Plc:PollingIntervalMs 必须大于 0（0 会导致轮询循环紧转）。")
            .ValidateOnStart();

        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<ICurrentUser, CurrentUser>();
        services.AddSingleton<IDatabaseConnectionFactory, DatabaseConnectionFactory>();
        services.AddSingleton<ILogFileReader, LogFileReader>();

        return services;
    }
}
