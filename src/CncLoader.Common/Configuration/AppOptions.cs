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

    /// <summary>加工位状态机调度器循环间隔（毫秒，默认 500ms）。每加工位并行驱动。</summary>
    public int SchedulerIntervalMs { get; set; } = 500;

    /// <summary>是否启用加工位状态机调度器（第四阶段⑤）。关闭时工位态由轮询合成回退。</summary>
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

    /// <summary>是否启用本机 RCS 模拟器（第四阶段③启用）。</summary>
    public bool UseSimulator { get; set; } = true;

    /// <summary>模拟器：收到任务后回推结果前的延时下限（毫秒）。</summary>
    public int SimulatorMinDelayMs { get; set; } = 1500;

    /// <summary>模拟器：回推结果前的延时上限（毫秒）。</summary>
    public int SimulatorMaxDelayMs { get; set; } = 4000;

    /// <summary>模拟器：任务失败率 0~1（命中则回推 error_code=1）。</summary>
    public double SimulatorFailureRate { get; set; }

    /// <summary>模拟器：任务自发取消率 0~1（命中则回推 error_code=9）。</summary>
    public double SimulatorCancelRate { get; set; }
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

    /// <summary>是否启用持续轮询循环（信号仓周期刷新）。设 false 可停掉自动轮询做手动单步调试；
    /// 启动自检的单轮读与 PLC 页手动读不受影响。默认 true。</summary>
    public bool PollingEnabled { get; set; } = true;

    public int ConnectTimeoutMs { get; set; } = 3000;
    public int ReadWriteTimeoutMs { get; set; } = 2000;
    public int MaxReconnectAttempts { get; set; } = 3;

    /// <summary>是否在启动时启用内置 Modbus TCP 模拟器（开发期无真机时使用）。</summary>
    public bool UseSimulator { get; set; } = true;

    /// <summary>模拟器监听 IP（环回）。</summary>
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
}
