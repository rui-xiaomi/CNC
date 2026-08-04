using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
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

        services.AddSingleton<IPlcPointRoutingStore, PlcPointRoutingStore>();
        services.AddSingleton<IPlcPointSource, PlcPointSource>();
        services.AddSingleton<IDataHealthProbe, DataHealthProbe>();

        services.AddSingleton<IDeviceLogStore, DeviceLogStore>();
        services.AddSingleton<IAlarmEventService, AlarmEventService>();
        services.AddSingleton<IPlcPointManagementService, PlcPointManagementService>();

        // Phase 3 配置管理：线体/工序/机台/料架
        services.AddSingleton<IWorkLineService, WorkLineService>();
        services.AddSingleton<ICraftworkService, CraftworkService>();
        services.AddSingleton<IEquipmentRoutingStore, EquipmentRoutingStore>();
        services.AddSingleton<IEquipmentConfigService, EquipmentConfigService>();
        services.AddSingleton<IRoutingAvailabilityValidator, RoutingAvailabilityValidator>();
        services.AddSingleton<IManagedDispatchRouteResolver, ManagedDispatchRouteResolver>();
        services.AddSingleton<IFrameStructureStore, FrameStructureStore>();
        services.AddSingleton<IFrameService, FrameService>();

        services.AddSingleton<IPlcCatalogService>(sp => new PlcCatalogService(
            sp.GetRequiredService<IDbContextFactory<CncDbContext>>(),
            () => sp.GetRequiredService<IPlcConnectionService>()));

        // Phase 4 RCS 对接：报文流水 / 任务落库 / 位置映射
        services.AddSingleton<IRcsMessageLog, RcsMessageLog>();
        services.AddSingleton<IRcsTaskStore, RcsTaskStore>();
        services.AddSingleton<ILocationMapRoutingStore, LocationMapRoutingStore>();
        services.AddSingleton<ILocationMapService, LocationMapService>();
        services.AddSingleton<IRcsConnectionConfigService, RcsConnectionConfigService>();

        // Phase 4 步骤⑥a：槽位账目（预记/落账/回滚 + 同架并发互斥 + 人工校正/反查）
        services.AddSingleton<ISlotAccountStore, SlotAccountStore>();
        services.AddSingleton<ISlotAccountService, SlotAccountService>();

        // Phase 4 步骤⑦：加工记录（WORK_RECORD 关联任务/工件/加工位/耗时）
        services.AddSingleton<IWorkRecordService, WorkRecordService>();

        return services;
    }
}
