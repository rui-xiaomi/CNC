using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CncLoader.Common.Configuration;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 出站 HTTP 客户端。封装公共字段、序列化、超时、网络级重试（指数退避 ≤ MaxRetries），
/// 每次调用双向报文落 <see cref="IRcsMessageLog"/>。
/// </summary>
public sealed class RcsClient : IRcsClient
{
    private const string TransitPath = "/api/ExternalInterfaces/transitTask";
    private const string ExcutePath = "/api/ExternalInterfaces/excuteTask";
    private const string CancelPath = "/api/ExternalInterfaces/cancelTask";
    private const string QueryPath = "/api/ExternalInterfaces/queryTask";

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly HttpClient _http;
    private readonly RcsOptions _options;
    private readonly IRcsMessageLog _msgLog;
    private readonly ILogger<RcsClient> _logger;

    public RcsClient(HttpClient http, IOptions<AppOptions> options, IRcsMessageLog msgLog, ILogger<RcsClient> logger)
    {
        _http = http;
        _options = options.Value.Rcs;
        _msgLog = msgLog;
        _logger = logger;
        _http.Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _options.RequestTimeoutMs));
    }

    public Task<RcsResult> TransitTaskAsync(TransitTaskRequest req, CancellationToken ct = default)
    {
        FillCommon(req);
        return SendAsync("transitTask", TransitPath, req, req.TaskId, ct);
    }

    public Task<RcsResult> ExcuteTaskAsync(ExcuteTaskRequest req, CancellationToken ct = default)
    {
        FillCommon(req);
        return SendAsync("excuteTask", ExcutePath, req, req.TaskId, ct);
    }

    public Task<RcsResult> CancelTaskAsync(CancelTaskRequest req, CancellationToken ct = default)
    {
        FillCommon(req);
        return SendAsync("cancelTask", CancelPath, req, req.TaskId, ct);
    }

    public Task<RcsResult> QueryTaskAsync(QueryTaskRequest req, CancellationToken ct = default)
        => SendAsync("queryTask", QueryPath, req, null, ct);

    /// <summary>填充出站公共字段（reqTime/clientCode/version/tokenCode）。</summary>
    private void FillCommon(RcsRequestBase req)
    {
        req.ReqTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        req.ClientCode = _options.ClientCode;
        req.Version = _options.Version;
        if (string.IsNullOrEmpty(req.TokenCode)) req.TokenCode = _options.TokenCode;
    }

    private async Task<RcsResult> SendAsync(string iface, string path, object payload, string? taskId, CancellationToken ct)
    {
        var url = CombineUrl(_options.BaseUrl, path);
        var body = JsonSerializer.Serialize(payload, payload.GetType(), JsonOpt);
        var maxAttempts = Math.Max(1, _options.MaxRetries);
        RcsResult last = RcsResult.Fail(body, "未发送");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(url, content, ct);
                var respBody = await resp.Content.ReadAsStringAsync(ct);
                sw.Stop();

                var (success, message) = ParseAck(respBody);
                var ok = resp.IsSuccessStatusCode;
                last = new RcsResult(ok, (int)resp.StatusCode, ok && success, message, body, respBody,
                    ok ? null : $"HTTP {(int)resp.StatusCode}", (int)sw.ElapsedMilliseconds);

                await LogAsync(iface, url, taskId, body, respBody, (int)sw.ElapsedMilliseconds, last.Ok, last.Error, ct);

                if (ok) return last; // 业务失败(Success=false)也算已送达，不重试
                // 非 2xx：网络/服务端错误 → 重试
            }
            catch (Exception ex)
            {
                sw.Stop();
                last = RcsResult.Fail(body, ex.Message, 0, (int)sw.ElapsedMilliseconds);
                await LogAsync(iface, url, taskId, body, null, (int)sw.ElapsedMilliseconds, false, ex.Message, ct);
                _logger.LogWarning(ex, "RCS {Iface} 第 {Attempt}/{Max} 次调用失败", iface, attempt, maxAttempts);
            }

            if (attempt < maxAttempts)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(300 * (1 << (attempt - 1))), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        return last;
    }

    private async Task LogAsync(string iface, string url, string? taskId, string reqBody, string? respBody,
        int costMs, bool success, string? error, CancellationToken ct)
    {
        try
        {
            await _msgLog.LogAsync(new RcsMsgEntry("OUT", iface, url, taskId, reqBody, respBody, costMs, success, error), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RCS 报文流水落库失败（不影响主流程）");
        }
    }

    /// <summary>解析应答外层 { Success, Message }；Success 兼容布尔与字符串"true"。</summary>
    private static (bool success, string? message) ParseAck(string? respBody)
    {
        if (string.IsNullOrWhiteSpace(respBody)) return (false, null);
        try
        {
            using var doc = JsonDocument.Parse(respBody);
            var root = doc.RootElement;
            // 查询接口成功报文用小写 success；下发接口用 Success
            var success = ReadBool(root, "Success") ?? ReadBool(root, "success") ?? false;
            string? message = null;
            if (root.TryGetProperty("Message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
            else if (root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String) message = m2.GetString();
            return (success, message);
        }
        catch
        {
            return (false, null);
        }
    }

    private static bool? ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
            _ => null
        };
    }

    private static string CombineUrl(string baseUrl, string path)
        => $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}
