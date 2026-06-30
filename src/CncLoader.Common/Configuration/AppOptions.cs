namespace CncLoader.Common.Configuration;

/// <summary>
/// 应用强类型配置根，绑定 appsettings.json 的 "App" 节。
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    public DatabaseOptions Database { get; set; } = new();
    public PlcOptions Plc { get; set; } = new();
    public OperatorOptions Operator { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
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
