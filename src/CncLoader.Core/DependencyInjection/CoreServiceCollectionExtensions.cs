using CncLoader.Core.State;
using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    /// <summary>注册 Core 领域服务：状态仓（单例，单一数据源）、状态合成器。</summary>
    public static IServiceCollection AddCncCore(this IServiceCollection services)
    {
        services.AddSingleton<ISignalStateStore, SignalStateStore>();
        services.AddSingleton<IStatusSynthesizer, StatusSynthesizer>();
        return services;
    }
}
