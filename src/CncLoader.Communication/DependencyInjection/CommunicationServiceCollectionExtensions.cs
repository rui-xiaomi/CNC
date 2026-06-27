using CncLoader.Common.Configuration;
using CncLoader.Communication.Logging;
using CncLoader.Communication.Plc;
using CncLoader.Communication.Polling;
using CncLoader.Communication.Simulation;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Polling;
using CncLoader.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.DependencyInjection;

public static class CommunicationServiceCollectionExtensions
{
    /// <summary>注册通信层：设备流水、PLC 客户端工厂、连接管理器、模拟器、轮询中枢。</summary>
    public static IServiceCollection AddCncCommunication(this IServiceCollection services)
    {
        services.AddSingleton<IDeviceLogger, CompositeDeviceLogger>();

        services.AddSingleton<IPlcClientFactory>(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            return new NModbusPlcClientFactory(
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IDeviceLogger>(),
                plc.ConnectTimeoutMs,
                plc.ReadWriteTimeoutMs);
        });

        services.AddSingleton<PlcConnectionManager>();
        services.AddSingleton<IPlcEndpointResolver, PlcEndpointResolver>();
        services.AddSingleton<IPlcConnectionService, PlcConnectionService>();
        services.AddSingleton<IPlcOperationService, PlcOperationService>();

        services.AddSingleton(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            return new ModbusTcpSimulator(plc.SimulatorBindAddress,
                sp.GetRequiredService<ILogger<ModbusTcpSimulator>>());
        });

        services.AddSingleton<IPlcPollingService>(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            return new PlcPollingService(
                sp.GetRequiredService<IPlcPointSource>(),
                sp.GetRequiredService<PlcConnectionManager>(),
                sp.GetRequiredService<ISignalStateStore>(),
                sp.GetRequiredService<IStatusSynthesizer>(),
                sp.GetRequiredService<ILogger<PlcPollingService>>(),
                plc.PollingIntervalMs);
        });

        return services;
    }
}
