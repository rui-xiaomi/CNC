using CncLoader.Common.Configuration;
using CncLoader.Communication.ExternalDevices;
using CncLoader.Communication.Logging;
using CncLoader.Communication.Plc;
using CncLoader.Communication.Polling;
using CncLoader.Communication.Rcs;
using CncLoader.Communication.Simulation;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Polling;
using CncLoader.Core.Rcs;
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
            return new PlcClientFactory(
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IDeviceLogger>(),
                plc.ConnectTimeoutMs,
                plc.ReadWriteTimeoutMs);
        });

        services.AddSingleton<PlcConnectionManager>();
        services.AddSingleton<IPlcEndpointResolver, PlcEndpointResolver>();
        services.AddSingleton<IPlcConnectionService, PlcConnectionService>();
        services.AddSingleton<IPlcOperationService, PlcOperationService>();

        // Phase 5 外设测试：AGV 连通性 + 扫码枪监听（仅测试，不纳入调度/来料校验）
        services.AddSingleton<IAgvTestService, AgvTestService>();
        services.AddSingleton<IScanListenerService, ScanListenerService>();

        // Phase 4 RCS 对接：出站客户端（4 接口）+ 任务编排
        services.AddSingleton<IRcsClient>(sp => new RcsClient(
            new System.Net.Http.HttpClient(),
            sp.GetRequiredService<IOptions<AppOptions>>(),
            sp.GetRequiredService<IRcsMessageLog>(),
            sp.GetRequiredService<ILogger<RcsClient>>()));
        services.AddSingleton<IRcsTaskService, RcsTaskService>();

        // Phase 4 步骤②：回调处理器 + 内嵌 Kestrel 回调服务端（3 回调 + 幂等去重 + warnCallback 落 ALARM）
        services.AddSingleton<IRcsCallbackProcessor, RcsCallbackProcessor>();
        services.AddHostedService<RcsCallbackHost>();

        // Phase 4 步骤③：本机 RCS 模拟器（收任务→延时→按失败率/取消率回推 push/scan）。仅 UseSimulator=true 生效。
        services.AddHostedService<RcsSimulator>();

        // Phase 4 步骤④：任务跟踪器（queryTask 兜底轮询 + 11→5 映射 + 自动 redo + 取消工单告警）
        services.AddHostedService<RcsTaskTracker>();

        services.AddSingleton(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            return new ModbusTcpSimulator(plc.SimulatorBindAddress,
                sp.GetRequiredService<ILogger<ModbusTcpSimulator>>());
        });

        services.AddSingleton(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            return new OmronFinsUdpSimulator(plc.SimulatorBindAddress,
                sp.GetRequiredService<ILogger<OmronFinsUdpSimulator>>());
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
