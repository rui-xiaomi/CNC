using CncLoader.Common.Configuration;
using CncLoader.Communication.ExternalDevices;
using CncLoader.Communication.Logging;
using CncLoader.Communication.Plc;
using CncLoader.Communication.Polling;
using CncLoader.Communication.Rcs;
using CncLoader.Communication.Simulation;
using CncLoader.Communication.State;
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
        services.AddHostedService<DeviceLogPurgeService>();
        services.AddHostedService<OperationalLogPurgeService>();

        services.AddSingleton<IPlcClientFactory>(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            return new PlcClientFactory(
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IDeviceLogger>(),
                plc.ConnectTimeoutMs,
                plc.ReadWriteTimeoutMs,
                plc.LinkFaultThreshold);
        });

        services.AddSingleton<PlcConnectionManager>();
        services.AddSingleton<IPlcEndpointResolver, PlcEndpointResolver>();
        services.AddSingleton<IPlcConnectionService, PlcConnectionService>();
        services.AddSingleton<IPlcOperationService, PlcOperationService>();

        // Phase 5 外设测试：AGV 连通性 + 扫码枪监听（仅测试，不纳入调度/来料校验）
        services.AddSingleton<IAgvTestService, AgvTestService>();
        services.AddSingleton<IScanListenerService, ScanListenerService>();

        // Phase 4 RCS 对接：运行时配置 + 出站客户端（4 接口）+ 任务编排
        services.AddSingleton<IRcsRuntimeConfig, RcsRuntimeConfig>();
        // IRcsClient 刻意不进容器：受管派工门禁在 IRcsTaskService 内，容器里没有裸客户端可拿，
        // 「注入 IRcsClient 绕过门禁」在 UI/App/Data 里是编译错误（internal），在容器里也解析不到。
        // 单例 HttpClient 复用连接池；PooledConnectionLifetime 让 BaseUrl 热更新后能重新解析 DNS
        // （默认无生命周期的 Singleton HttpClient 会把首次解析的 IP 一直用到进程退出）。
        services.AddSingleton<IRcsTaskService>(sp => new RcsTaskService(
            new RcsClient(
                new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                }),
                sp.GetRequiredService<IRcsRuntimeConfig>(),
                sp.GetRequiredService<IRcsMessageLog>(),
                sp.GetRequiredService<ILogger<RcsClient>>()),
            sp.GetRequiredService<IRcsTaskStore>(),
            sp.GetRequiredService<IRcsMessageLog>(),
            sp.GetRequiredService<IRcsCallbackProcessor>(),
            sp.GetRequiredService<IManagedDispatchRouteResolver>(),
            sp.GetRequiredService<IRoutingAvailabilityValidator>(),
            sp.GetRequiredService<ILogger<RcsTaskService>>(),
            sp.GetRequiredService<ISlotAccountService>()));

        // 先从库加载连接配置，再启动回调宿主（保证 BootCallback* 已 Capture）
        services.AddHostedService<RcsConnectionBootstrapper>();

        // Phase 4 步骤②：回调处理器 + 内嵌 Kestrel 回调服务端（3 回调 + 幂等去重 + warnCallback 落 ALARM）
        services.AddSingleton<IRcsCallbackProcessor, RcsCallbackProcessor>();
        services.AddSingleton<RcsCallbackHost>();
        services.AddSingleton<IRcsCallbackListener>(sp => sp.GetRequiredService<RcsCallbackHost>());
        services.AddHostedService(sp => sp.GetRequiredService<RcsCallbackHost>());

        // Phase 4 步骤③：本机 RCS 模拟器（收任务→延时→按失败率/取消率回推 push/scan）。仅 UseSimulator=true 生效。
        services.AddHostedService<RcsSimulator>();

        // Phase 4 步骤④：任务跟踪器（queryTask 兜底轮询 + 11→5 映射 + 自动 redo + 取消工单告警）
        services.AddHostedService<RcsTaskTracker>();

        // Phase 4 步骤⑤：加工位状态机调度器（DISPATCHING/TRANSPORTING + LOADED双条件 + PLC复核 + 优先级队列 + 启动对账）
        services.AddSingleton<IDispatchQueue, PriorityDispatchQueue>();
        services.AddSingleton<IRouteResolver, RouteResolver>();
        services.AddSingleton<PositionScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<PositionScheduler>());
        services.AddSingleton<IPositionScheduler>(sp => sp.GetRequiredService<PositionScheduler>());

        // CNC 机台行为模拟器（PLC sim 之上叠加加工位节拍语义，演示用；现场关闭）
        services.AddSingleton<CncMachineSimulator>();
        services.AddHostedService(sp => sp.GetRequiredService<CncMachineSimulator>());
        services.AddSingleton<IPlcWriteHook>(sp => sp.GetRequiredService<CncMachineSimulator>());

        // Phase 4 步骤⑥b：换架任务对编排（先拉后送 + TXN_ID + redo/回滚/工单）+ 空托盘回收
        services.AddSingleton<IChangeFrameOrchestrator, ChangeFrameOrchestrator>();

        // Phase 4 步骤⑥c：盘点后台任务 + 水位监视器自动触发换架
        services.AddSingleton<IInventoryService, InventoryService>();
        services.AddHostedService<WaterMonitorService>();
        services.AddSingleton<IWaterMonitorService>(sp => sp.GetRequiredService<WaterMonitorService>());

        // 定期盘点后台调度（"盘点管家"，RCS 空闲时逐料架扫码核账）。默认关，需 InventoryAutoEnabled=true。
        services.AddHostedService<InventorySchedulerService>();

        services.AddSingleton(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            var bind = SimulatorLoopback.ResolvePlcBind(plc.UseSimulator, plc.SimulatorBindAddress);
            return new ModbusTcpSimulator(bind,
                sp.GetRequiredService<ILogger<ModbusTcpSimulator>>());
        });

        services.AddSingleton(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            var bind = SimulatorLoopback.ResolvePlcBind(plc.UseSimulator, plc.SimulatorBindAddress);
            return new OmronFinsUdpSimulator(bind,
                sp.GetRequiredService<ILogger<OmronFinsUdpSimulator>>());
        });

        // 两个模拟器都作为寄存器直写目标暴露，供 CncMachineSimulator 不依赖协议地驱动节拍。
        services.AddSingleton<ISimulatorRegisterStore>(sp => sp.GetRequiredService<ModbusTcpSimulator>());
        services.AddSingleton<ISimulatorRegisterStore>(sp => sp.GetRequiredService<OmronFinsUdpSimulator>());

        services.AddSingleton<IPlcPollingService>(sp =>
        {
            var plc = sp.GetRequiredService<IOptions<AppOptions>>().Value.Plc;
            return new PlcPollingService(
                sp.GetRequiredService<IPlcPointSource>(),
                sp.GetRequiredService<PlcConnectionManager>(),
                sp.GetRequiredService<ISignalStateStore>(),
                sp.GetRequiredService<ILogger<PlcPollingService>>(),
                plc.PollingIntervalMs,
                plc.PollingEnabled);
        });
        // 持续轮询循环随主机启动（信号仓单一数据源，调度器/UI 均依赖其实时刷新）。
        services.AddHostedService(sp => (PlcPollingService)sp.GetRequiredService<IPlcPollingService>());

        // PLC 失联监测：持续离线超阈值补大声告警（不负责重连，P0-5）。
        services.AddHostedService<PlcHealthMonitor>();
        services.AddHostedService<PlcReconnectService>();

        return services;
    }
}
