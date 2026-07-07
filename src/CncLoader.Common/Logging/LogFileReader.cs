using System.Text;
using System.Text.RegularExpressions;
using CncLoader.Common.Configuration;
using Microsoft.Extensions.Options;

namespace CncLoader.Common.Logging;

/// <summary>应用运行日志一行（解析自 Serilog 文件）。</summary>
public sealed record LogLine(DateTime? Time, string Level, string Text);

/// <summary>读取 Serilog 落地的应用日志文件（logs/cncloader-*.log），供 UI「日志/告警」页展示。</summary>
public interface ILogFileReader
{
    /// <summary>读最新日志文件尾部若干行；minLevel 非空时仅返回不低于该级别的行。返回按时间倒序（最新在前）。</summary>
    Task<IReadOnlyList<LogLine>> TailAsync(int maxLines = 500, string? minLevel = null, CancellationToken ct = default);
}

/// <summary>
/// 默认实现：定位 <see cref="LoggingOptions.Directory"/> 下最新 cncloader-*.log，
/// 以 FileShare.ReadWrite 打开（Serilog 正持有该文件），解析行首时间戳/级别（异常续行并入上一条）。
/// </summary>
public sealed class LogFileReader : ILogFileReader
{
    // 匹配 outputTemplate "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] ..."
    private static readonly Regex HeadRegex = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(?<lvl>\w{3})\] (?<msg>.*)$",
        RegexOptions.Compiled);

    // Serilog 级别严重度（u3 缩写）：低→高
    private static readonly Dictionary<string, int> LevelRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VRB"] = 0, ["DBG"] = 1, ["INF"] = 2, ["WRN"] = 3, ["ERR"] = 4, ["FTL"] = 5
    };

    private readonly string _logDir;

    public LogFileReader(IOptions<AppOptions> options)
    {
        var dir = options.Value.Logging.Directory;
        _logDir = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
    }

    public async Task<IReadOnlyList<LogLine>> TailAsync(int maxLines = 500, string? minLevel = null, CancellationToken ct = default)
    {
        var file = FindLatestLog();
        if (file is null) return Array.Empty<LogLine>();

        var raw = await ReadAllLinesSharedAsync(file, ct);
        var parsed = ParseEntries(raw);

        if (!string.IsNullOrWhiteSpace(minLevel) && LevelRank.TryGetValue(minLevel, out var min))
            parsed = parsed.Where(l => LevelRank.TryGetValue(l.Level, out var r) && r >= min).ToList();

        // 尾部 maxLines，倒序（最新在前）
        var tail = parsed.Count > maxLines ? parsed.Skip(parsed.Count - maxLines).ToList() : parsed;
        tail.Reverse();
        return tail;
    }

    private string? FindLatestLog()
    {
        if (!Directory.Exists(_logDir)) return null;
        return Directory.EnumerateFiles(_logDir, "cncloader-*.log")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    private static async Task<List<string>> ReadAllLinesSharedAsync(string path, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
            lines.Add(line);
        return lines;
    }

    private static List<LogLine> ParseEntries(IReadOnlyList<string> lines)
    {
        var result = new List<LogLine>();
        foreach (var line in lines)
        {
            var m = HeadRegex.Match(line);
            if (m.Success)
            {
                DateTime? ts = DateTime.TryParse(m.Groups["ts"].Value, out var t) ? t : null;
                result.Add(new LogLine(ts, m.Groups["lvl"].Value.ToUpperInvariant(), m.Groups["msg"].Value));
            }
            else if (result.Count > 0)
            {
                // 异常堆栈等续行：并入上一条
                var prev = result[^1];
                result[^1] = prev with { Text = prev.Text + Environment.NewLine + line };
            }
            // 无法解析且无上一条：丢弃（罕见）
        }
        return result;
    }
}
