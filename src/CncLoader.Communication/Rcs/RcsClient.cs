using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 出站 HTTP 客户端。封装公共字段、序列化、超时、网络级重试（指数退避 ≤ MaxRetries），
/// 每次调用双向报文落 <see cref="IRcsMessageLog"/>。地址/超时/重试读 <see cref="IRcsRuntimeConfig"/>（可热更新）。
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
    private readonly IRcsRuntimeConfig _runtime;
    private readonly IRcsMessageLog _msgLog;
    private readonly ILogger<RcsClient> _logger;

    public RcsClient(HttpClient http, IRcsRuntimeConfig runtime, IRcsMessageLog msgLog, ILogger<RcsClient> logger)
    {
        _http = http;
        _runtime = runtime;
        _msgLog = msgLog;
        _logger = logger;
        // 单次请求用 CTS 控超时；HttpClient 超时放宽避免抢先取消。
        _http.Timeout = Timeout.InfiniteTimeSpan;
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

    private void FillCommon(RcsRequestBase req)
    {
        req.ReqTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        req.ClientCode = _runtime.ClientCode;
        req.Version = _runtime.Version;
        if (string.IsNullOrEmpty(req.TokenCode)) req.TokenCode = _runtime.TokenCode;
    }

    private async Task<RcsResult> SendAsync(string iface, string path, object payload, string? taskId, CancellationToken ct)
    {
        var url = CombineUrl(_runtime.BaseUrl, path);
        var body = JsonSerializer.Serialize(payload, payload.GetType(), JsonOpt);
        var maxAttempts = Math.Max(1, _runtime.MaxRetries);
        var timeoutMs = Math.Max(1000, _runtime.RequestTimeoutMs);
        RcsResult last = RcsResult.Fail(body, "未发送");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeoutMs);
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(url, content, timeoutCts.Token);
                var respBody = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                sw.Stop();

                var (success, message) = RcsAckParser.Parse(respBody);
                var ok = resp.IsSuccessStatusCode;
                last = new RcsResult(ok, (int)resp.StatusCode, ok && success, message, body, respBody,
                    ok ? null : $"HTTP {(int)resp.StatusCode}", (int)sw.ElapsedMilliseconds);

                await LogAsync(iface, url, taskId, body, respBody, (int)sw.ElapsedMilliseconds, last.Ok, last.Error, ct);

                if (ok) return last;
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

    private static string CombineUrl(string baseUrl, string path)
        => $"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
}
