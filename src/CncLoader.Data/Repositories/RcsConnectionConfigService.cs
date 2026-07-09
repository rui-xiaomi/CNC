using CncLoader.Core.Rcs;
using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CncLoader.Data.Repositories;

/// <summary>RCS 连接配置仓储（MAS_AUTO_WORKLINE_AGV）。</summary>
public sealed class RcsConnectionConfigService : IRcsConnectionConfigService
{
    private readonly IDbContextFactory<CncDbContext> _dbFactory;
    private readonly ILogger<RcsConnectionConfigService> _logger;

    public RcsConnectionConfigService(IDbContextFactory<CncDbContext> dbFactory, ILogger<RcsConnectionConfigService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<RcsConnectionConfig?> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Agvs.AsNoTracking()
            .Where(a => a.State == "0")
            .OrderBy(a => a.Id)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : Map(row);
    }

    public async Task<RcsConnectionConfig?> GetByWorkLineAgvIdAsync(long agvId, CancellationToken ct = default)
    {
        if (agvId > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.Agvs.AsNoTracking()
                .Where(a => a.State == "0" && a.AgvId == agvId)
                .OrderBy(a => a.Id)
                .FirstOrDefaultAsync(ct);
            if (row is not null) return Map(row);
        }
        return await GetAsync(ct);
    }

    public async Task<RcsConnectionConfig> SaveAsync(RcsConnectionConfig config, string author, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
            throw new ArgumentException("RCS 地址不能为空。", nameof(config));
        if (string.IsNullOrWhiteSpace(config.ClientCode))
            throw new ArgumentException("clientCode 不能为空。", nameof(config));
        if (config.CallbackPort is < 1 or > 65535)
            throw new ArgumentException("回调端口无效。", nameof(config));
        if (config.RequestTimeoutMs < 1000)
            throw new ArgumentException("超时至少 1000ms。", nameof(config));
        if (config.MaxRetries < 1)
            throw new ArgumentException("重试次数至少为 1。", nameof(config));
        if (config.PollIntervalMs < 500)
            throw new ArgumentException("轮询间隔至少 500ms。", nameof(config));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        WorkLineAgv? row = null;
        if (config.Id > 0)
            row = await db.Agvs.FirstOrDefaultAsync(a => a.Id == config.Id, ct);
        if (row is null && config.AgvId > 0)
            row = await db.Agvs.Where(a => a.State == "0" && a.AgvId == config.AgvId).OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
        if (row is null)
            row = await db.Agvs.Where(a => a.State == "0").OrderBy(a => a.Id).FirstOrDefaultAsync(ct);

        var now = DateTime.Now;
        if (row is null)
        {
            row = new WorkLineAgv
            {
                AgvId = config.AgvId > 0 ? config.AgvId : 1,
                AgvName = "RCS",
                AgvCorrespondWay = "HTTP",
                AgvComputerIp = TryHostFromUrl(config.BaseUrl) ?? "127.0.0.1",
                AgvComputerPort = TryPortFromUrl(config.BaseUrl),
                State = "0",
                Author = Truncate(author, 15),
                UpdateTime = now
            };
            ApplyRcs(row, config);
            db.Agvs.Add(row);
            _logger.LogInformation("新建 RCS 连接配置行 AGV_ID={AgvId}", row.AgvId);
        }
        else
        {
            ApplyRcs(row, config);
            row.AgvComputerIp = TryHostFromUrl(config.BaseUrl) ?? row.AgvComputerIp;
            row.AgvComputerPort = TryPortFromUrl(config.BaseUrl) ?? row.AgvComputerPort;
            row.Author = Truncate(author, 15);
            row.UpdateTime = now;
            _logger.LogInformation("更新 RCS 连接配置 Id={Id}", row.Id);
        }

        await db.SaveChangesAsync(ct);
        return Map(row);
    }

    private static void ApplyRcs(WorkLineAgv row, RcsConnectionConfig c)
    {
        row.RcsBaseUrl = c.BaseUrl.Trim();
        row.RcsClientCode = c.ClientCode.Trim();
        row.RcsCallbackHost = NormalizeCallbackHost(c.CallbackHost);
        row.RcsCallbackPort = c.CallbackPort;
        row.RcsTimeoutMs = c.RequestTimeoutMs;
        row.RcsMaxRetries = c.MaxRetries;
        row.RcsPollIntervalMs = c.PollIntervalMs;
    }

    private static RcsConnectionConfig Map(WorkLineAgv row) => new()
    {
        Id = row.Id,
        AgvId = row.AgvId,
        BaseUrl = string.IsNullOrWhiteSpace(row.RcsBaseUrl)
            ? BuildUrl(row.AgvComputerIp, row.AgvComputerPort)
            : row.RcsBaseUrl!,
        ClientCode = string.IsNullOrWhiteSpace(row.RcsClientCode) ? "CNC" : row.RcsClientCode!,
        CallbackHost = NormalizeCallbackHost(row.RcsCallbackHost),
        CallbackPort = row.RcsCallbackPort is > 0 and <= 65535 ? row.RcsCallbackPort.Value : 9080,
        RequestTimeoutMs = row.RcsTimeoutMs is >= 1000 ? row.RcsTimeoutMs.Value : 10000,
        MaxRetries = row.RcsMaxRetries is >= 1 ? row.RcsMaxRetries.Value : 3,
        PollIntervalMs = row.RcsPollIntervalMs is >= 500 ? row.RcsPollIntervalMs.Value : 3000
    };

    /// <summary>合法 IP 规范化；非法/空 → 0.0.0.0（任意网卡）。</summary>
    private static string NormalizeCallbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "0.0.0.0";
        if (!System.Net.IPAddress.TryParse(host.Trim(), out var ip)) return "0.0.0.0";
        if (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any))
            return "0.0.0.0";
        return ip.ToString();
    }

    private static string BuildUrl(string? ip, int? port)
    {
        var host = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
        var p = port is > 0 ? port.Value : 8090;
        return $"http://{host}:{p}";
    }

    private static string? TryHostFromUrl(string url)
    {
        try { return new Uri(url).Host; }
        catch { return null; }
    }

    private static int? TryPortFromUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            return u.IsDefaultPort ? (u.Scheme == "https" ? 443 : 80) : u.Port;
        }
        catch { return null; }
    }

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
