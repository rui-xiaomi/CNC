using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.Data.DependencyInjection;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Data 层：CncDbContext 工厂（Pomelo MySQL）、点位来源、数据自检。
    /// 用工厂而非 scoped 上下文，便于单例后台服务安全使用。
    /// </summary>
    public static IServiceCollection AddCncData(this IServiceCollection services)
    {
        // 固定服务器版本，避免启动时 AutoDetect 阻塞连接。
        var serverVersion = new MySqlServerVersion(new Version(8, 0, 36));

        services.AddDbContextFactory<CncDbContext>((sp, options) =>
        {
            var connFactory = sp.GetRequiredService<IDatabaseConnectionFactory>();
            options.UseMySql(connFactory.BuildConnectionString(), serverVersion);
        });

        services.AddSingleton<IPlcPointSource, PlcPointSource>();
        services.AddSingleton<IDataHealthProbe, DataHealthProbe>();

        return services;
    }
}
