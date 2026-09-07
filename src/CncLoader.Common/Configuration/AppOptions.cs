namespace CncLoader.Common.Configuration;

/// <summary>
/// 应用强类型配置根，绑定 appsettings.json 的 "App" 节。
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    public DatabaseOptions Database { get; set; } = new();
    public PlcOptions Plc { get; set; } = new();
    public RcsOptions Rcs { get; set; } = new();
    public OperatorOptions Operator { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
}

/// <summary>RCS 调度系统对接配置（第四阶段）。地址等亦可存 MAS_AUTO_WORKLINE_AGV，此处为默认/兜底。</summary>
public sealed class RcsOptions
{
    /// <summary>RCS 出站基址，如 http://192.168.1.50:8080。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8090";

    /// <summary>本系统标识（请求公共字段 clientCode）。</summary>
    public string ClientCode { get; set; } = "CNC";

    /// <summary>协议版本（须与 RCS 一致，否则失败）。</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>令牌（预留字段）。</summary>
    public string TokenCode { get; set; } = "0";

    /// <summary>出站 HTTP 超时（毫秒）。</summary>
    public int RequestTimeoutMs { get; set; } = 10000;

    /// <summary>网络级失败重试次数（指数退避）。</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>内嵌回调服务端监听 IP（第四阶段②启用）。</summary>
    public string CallbackHost { get; set; } = "0.0.0.0";

    /// <summary>内嵌回调服务端监听端口。</summary>
    public int CallbackPort { get; set; } = 9080;

    /// <summary>兜底轮询间隔（毫秒，2~5s）。</summary>
    public int PollIntervalMs { get; set; } = 3000;

    /// <summary>是否启用任务跟踪器（回调主通道 + queryTask 兜底轮询 + 自动 redo + 取消工单）。第四阶段④启用。</summary>
    public bool TrackerEnabled { get; set; } = true;

    /// <summary>任务失败自动 redo 上限（同 taskId 幂等重发；超出告警人工）。</summary>
    public int MaxAutoRedo { get; set; } = 3;

    /// <summary>加工位状态机调度器循环间隔（毫秒，默认 500ms）。主循环逐个串行驱动加工位。</summary>
    public int SchedulerIntervalMs { get; set; } = 500;

    /// <summary>
    /// 启动对账失败后的自动重试间隔（毫秒）。默认 5000；必须 &gt;0（Options 启动校验，禁止静默降级）。
    /// </summary>
    public int ReconcileRetryIntervalMs { get; set; } = 5000;

    /// <summary>RCS 完成后 HasMat fresh 连续读取未知的告警阈值。</summary>
    public int HasMatRecheckFailThreshold { get; set; } = 6;

    /// <summary>是否启用加工位状态机调度器。关闭时不对账、不开自动派工，看板显示「调度器未启用」；轮询不再合成工位态。</summary>
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>是否启用料架水位监视器自动换架（第四阶段⑥c）。默认关：料架→机台角色反查（GetBindingByFrameAsync）落地前，
    /// 自动换架会误触发到错误机台/角色，故需显式开启。</summary>
    public bool WaterMonitorEnabled { get; set; }

    /// <summary>料架水位阈值（剩余空槽 ≤ 此值视为"接近满"，提前触发换架；勿等全满）。</summary>
    public int WaterFullThreshold { get; set; } = 2;

    /// <summary>满架缓存区命名点（LOCATION_MAP LOC_NAME），换架时旧架拉到此 cell。</summary>
    public string FullBufferArea { get; set; } = "FULL_BUFFER";

    /// <summary>空架缓存区命名点（换架时空架拉走/新架送来）。</summary>
    public string EmptyBufferArea { get; set; } = "EMPTY_BUFFER";

    /// <summary>托盘回收区命名点（空托盘回收任务终点）。</summary>
    public string PalletReturnArea { get; set; } = "PALLET_RETURN";

    /// <summary>是否启用定期后台盘点（班前/班后自动扫码核账）。默认关：盘点期间搬运任务排队，需运维明确开启。</summary>
    public bool InventoryAutoEnabled { get; set; }

    /// <summary>定期盘点间隔（分钟）。到点且 RCS 空闲时逐料架发起 identifyQR。</summary>
    public int InventoryIntervalMinutes { get; set; } = 60;

    /// <summary>是否启用本机 RCS 模拟器（第四阶段③启用）。默认 false（fail-closed）：现场配置缺失时不进模拟器，
    /// 演示/自测场景在 appsettings.json 显式设 true。true 时出站 BaseUrl 强制改写 127.0.0.1，忽略库内现场地址。</summary>
    public bool UseSimulator { get; set; }

    /// <summary>模拟器：收到任务后回推结果前的延时下限（毫秒）。</summary>
    public int SimulatorMinDelayMs { get; set; } = 1500;

    /// <summary>模拟器：回推结果前的延时上限（毫秒）。</summary>
    public int SimulatorMaxDelayMs { get; set; } = 4000;

    /// <summary>模拟器：任务失败率 0~1（命中则回推 error_code=1）。</summary>
    public double SimulatorFailureRate { get; set; }

    /// <summary>模拟器：任务自发取消率 0~1（命中则回推 error_code=9）。</summary>
    public double SimulatorCancelRate { get; set; }

    /// <summary>CNC 机台模拟器：检测出 NG 的概率 0~1（默认 0=全 OK）。命中则置 POS_NG=ON 走 NG 分流，用于演示 NG→NG架。</summary>
    public double SimulatorNgRate { get; set; }

    /// <summary>CNC 机台模拟器：上料 RCS 报完成时不置 HasMat=ON（默认 false）。用于演示「复核不过 → Alarm 粘滞」安全底线；改后需重启。</summary>
    public bool SimulatorSkipMaterialArrival { get; set; }

    /// <summary>
    /// 工位上下料 RCS 动词。Grab=excuteTask/grabTask；Transit=transitTask。
    /// 默认 Grab（本现场复合车）。改后须重启，禁止热切。换架/空托盘不受此项影响。
    /// </summary>
    public string LoadUnloadVerb { get; set; } = RcsLoadUnloadVerbs.Grab;
}

/// <summary>工位上下料能力档取值。</summary>
public static class RcsLoadUnloadVerbs
{
    public const string Grab = "Grab";
    public const string Transit = "Transit";

    public static bool IsDefined(string? value)
        => IsGrab(value) || IsTransit(value);

    public static bool IsGrab(string? value)
        => string.Equals(value, Grab, StringComparison.OrdinalIgnoreCase);

    public static bool IsTransit(string? value)
        => string.Equals(value, Transit, StringComparison.OrdinalIgnoreCase);
}

/// <summary>数据库连接配置。Password 可为明文（开发）或 DPAPI 密文（PasswordProtected=true）。</summary>
public sealed class DatabaseOptions
{
    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "cnc_auto";
    public string User { get; set; } = "root";

    /// <summary>口令：当 PasswordProtected=true 时为 DPAPI 密文（Base64），否则为明文。</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>口令是否为 DPAPI 密文。生产应为 true，避免明文落盘。</summary>
    public bool PasswordProtected { get; set; }

    /// <summary>附加连接参数。</summary>
    public string ExtraParameters { get; set; } = "CharSet=utf8mb4;Allow User Variables=true;";
}

/// <summary>PLC 通信默认参数。</summary>
public sealed class PlcOptions
{
    public int DefaultPort { get; set; } = 502;

    /// <summary>欧姆龙 FINS/UDP 默认端口（DB 未填端口时使用）。</summary>
    public int FinsDefaultPort { get; set; } = 9600;

    public int PollingIntervalMs { get; set; } = 500;

    /// <summary>PLC 信号读值有效期（毫秒）。超过此值的机台快照/读值视为过期（unknown），
    /// 调度器按 fail-closed 处理（不再派工）。默认 2500 = PollingIntervalMs × 5。
    /// 实际判定用 <see cref="EffectiveSignalMaxAgeMs"/>，避免读写超时比有效期更长时误判失联。</summary>
    public int SignalMaxAgeMs { get; set; } = 2500;

    /// <summary>连续读超时多少次才把该 PLC 标 Faulted。默认 3；单次 UDP 丢包不断整台。</summary>
    public int LinkFaultThreshold { get; set; } = 3;

    /// <summary>Faulted 后是否自动重连。人工 Disconnect 不重连。</summary>
    public bool AutoReconnectEnabled { get; set; } = true;

    /// <summary>自动重连初始间隔（毫秒）。失败后倍增，上限 <see cref="AutoReconnectMaxDelayMs"/>。</summary>
    public int AutoReconnectInitialDelayMs { get; set; } = 2000;

    /// <summary>自动重连最大间隔（毫秒）。</summary>
    public int AutoReconnectMaxDelayMs { get; set; } = 30000;

    /// <summary>信号有效期下限：至少覆盖一次读写超时 + 三轮轮询，避免等超时过程中被当成失联。</summary>
    public int EffectiveSignalMaxAgeMs
    {
        get
        {
            var configured = SignalMaxAgeMs > 0 ? SignalMaxAgeMs : 2500;
            var floor = Math.Max(0, ReadWriteTimeoutMs) + Math.Max(0, PollingIntervalMs) * 3;
            return Math.Max(configured, floor);
        }
    }

    /// <summary>PLC 持续失联多少毫秒后触发失联告警（PlcHealthMonitor）。默认 30000（30s）。
    /// 设 ≤0 停用失联监测。失联期间工位已 Offline 停派工，本项只补「大声告警」。</summary>
    public int OfflineAlarmAfterMs { get; set; } = 30000;

    /// <summary>是否启用持续轮询循环（信号仓周期刷新）。设 false 可停掉自动轮询做手动单步调试；
    /// 启动自检的单轮读与 PLC 页手动读不受影响。默认 true。</summary>
    public bool PollingEnabled { get; set; } = true;

    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ReadWriteTimeoutMs { get; set; } = 2000;

    /// <summary>是否在启动时启用内置 Modbus TCP 模拟器（开发期无真机时使用）。默认 false（fail-closed）：
    /// 现场配置缺失时不进模拟器，演示/自测场景在 appsettings.json 显式设 true。</summary>
    public bool UseSimulator { get; set; }

    /// <summary>模拟器监听 IP。UseSimulator=true 时非环回值会被强制改写为 127.0.0.1。</summary>
    public string SimulatorBindAddress { get; set; } = "127.0.0.1";
}

/// <summary>操作人配置（轻量，不建用户表）。</summary>
public sealed class OperatorOptions
{
    /// <summary>显式操作人名；留空则取本机登录用户名。</summary>
    public string? Name { get; set; }
}

/// <summary>日志配置。</summary>
public sealed class LoggingOptions
{
    public string Directory { get; set; } = "logs";
    public string MinimumLevel { get; set; } = "Information";
    public int RetainedFileCountLimit { get; set; } = 31;

    /// <summary>
    /// 成功的 PLC 读/连/断/心跳是否落库 MAS_AUTO_DEVICE_LOG。
    /// 默认 false：轮询读只写文件（Debug），库表仅保留写操作与失败记录，避免现场库无限膨胀。
    /// </summary>
    public bool PersistSuccessfulReads { get; set; }

    /// <summary>设备流水表保留天数；≤0 表示不自动清理。默认 14。</summary>
    public int DeviceLogRetentionDays { get; set; } = 14;

    /// <summary>RCS 报文流水（MAS_AUTO_RCS_MSG_LOG，TEXT 大字段）保留天数；≤0 不清理。默认 30。</summary>
    public int RcsMsgLogRetentionDays { get; set; } = 30;

    /// <summary>告警表（MAS_AUTO_ALARM_EVENT）保留天数；≤0 不清理。默认 30。</summary>
    public int AlarmEventRetentionDays { get; set; } = 30;
}
