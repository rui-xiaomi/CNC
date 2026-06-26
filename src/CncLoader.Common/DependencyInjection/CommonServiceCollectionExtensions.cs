using CncLoader.Common.Configuration;
using CncLoader.Common.Identity;
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
            .ValidateOnStart();

        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<ICurrentUser, CurrentUser>();
        services.AddSingleton<IDatabaseConnectionFactory, DatabaseConnectionFactory>();

        return services;
    }
}
