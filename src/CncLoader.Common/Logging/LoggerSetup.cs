using CncLoader.Common.Configuration;
using Serilog;
using Serilog.Events;

namespace CncLoader.Common.Logging;

/// <summary>
/// Serilog 装配：按天滚动文件 + 控制台/Debug。通信流水另有 <c>DeviceLog</c> 子记录器。
/// </summary>
public static class LoggerSetup
{
    /// <summary>根据配置创建 Serilog Logger。</summary>
    public static Serilog.ILogger Create(LoggingOptions options, string baseDirectory)
    {
        var level = ParseLevel(options.MinimumLevel);
        var logDir = Path.IsPathRooted(options.Directory)
            ? options.Directory
            : Path.Combine(baseDirectory, options.Directory);
        Directory.CreateDirectory(logDir);

        return new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(logDir, "cncloader-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static LogEventLevel ParseLevel(string value) =>
        Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var lvl) ? lvl : LogEventLevel.Information;
}
