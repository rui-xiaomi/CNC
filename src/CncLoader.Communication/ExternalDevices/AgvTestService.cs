using System.Net.Sockets;
using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.ExternalDevices;

/// <summary>AGV 连通性测试：HTTP REST 模式发 GET 到 /api/agv/status；Socket 模式 TCP 连一次。</summary>
public sealed class AgvTestService : IAgvTestService
{
    private readonly ILogger<AgvTestService> _logger;
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public AgvTestService(ILogger<AgvTestService> logger) => _logger = logger;

    public async Task<AgvTestResult> TestConnectionAsync(AgvTestConfig config, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (config.CommWay == AgvCommWay.Socket)
            {
                // Socket 模式：解析 host:port，TCP 连一次即视为连通
                var ep = ParseHostPort(config.Endpoint, 8080);
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(ep.host, ep.port, ct);
                sw.Stop();
                _logger.LogInformation("AGV Socket 测试连通 {Host}:{Port} {Ms}ms", ep.host, ep.port, sw.ElapsedMilliseconds);
                return new AgvTestResult(true, 0, (int)sw.ElapsedMilliseconds,
                    $"TCP {ep.host}:{ep.port} connected", null);
            }

            // HTTP REST 模式：GET endpoint（默认补 /api/agv/status）
            var url = config.Endpoint.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "CncLoader-AGV-Test/1.0");

            using var resp = await HttpClient.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            sw.Stop();
            _logger.LogInformation("AGV HTTP 测试连通 {Url} -> {Code} {Ms}ms", url, (int)resp.StatusCode, sw.ElapsedMilliseconds);
            return new AgvTestResult(resp.IsSuccessStatusCode, (int)resp.StatusCode,
                (int)sw.ElapsedMilliseconds, Truncate(body, 800), null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "AGV 测试失败 {Endpoint}", config.Endpoint);
            return new AgvTestResult(false, 0, (int)sw.ElapsedMilliseconds, null, ex.Message);
        }
    }

    private static (string host, int port) ParseHostPort(string s, int defaultPort)
    {
        // 接受 host:port 或 http://host:port/path
        var raw = s.Trim();
        var idx = raw.IndexOf("://", StringComparison.Ordinal);
        if (idx >= 0) raw = raw[(idx + 3)..];
        var slash = raw.IndexOf('/');
        if (slash >= 0) raw = raw[..slash];
        var colon = raw.LastIndexOf(':');
        if (colon > 0 && int.TryParse(raw[(colon + 1)..], out var p))
            return (raw[..colon], p);
        return (raw, defaultPort);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
