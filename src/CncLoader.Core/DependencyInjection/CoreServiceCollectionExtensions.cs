using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    /// <summary>注册 Core 领域服务：状态仓（单例，单一数据源）、状态合成器、RCS 回调事件总线。</summary>
    public static IServiceCollection AddCncCore(this IServiceCollection services)
    {
        services.AddSingleton<ISignalStateStore, SignalStateStore>();
        services.AddSingleton<IStatusSynthesizer, StatusSynthesizer>();

        // RCS 回调事件总线（单例，单一数据源；处理器 Raise*，跟踪器/UI 订阅）
        services.AddSingleton<RcsCallbackNotifier>();
        services.AddSingleton<IRcsCallbackNotifier>(sp => sp.GetRequiredService<RcsCallbackNotifier>());

        return services;
    }
}
