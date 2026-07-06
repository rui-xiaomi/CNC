using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CncLoader.Common.Configuration;
using CncLoader.Core.Rcs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Simulation;

/// <summary>
/// 本机 RCS 模拟器（第四阶段③）。扮演 RCS 服务端：在 <see cref="RcsOptions.BaseUrl"/> 端口监听 4 个出站接口
/// （transitTask/excuteTask/cancelTask/queryTask），收到即应答 <c>{Success:true}</c>，随后按可配延时/失败率/取消率
/// 回推 <c>pushTaskStatus</c>（搬运/抓取）或 <c>scanTaskStatus</c>（识别）到本机回调服务端，形成端到端闭环。
/// 仅 <see cref="RcsOptions.UseSimulator"/>=true 时启用；开发/自测用，现场对接真实 RCS 时关闭。
/// </summary>
public sealed class RcsSimulator : IHostedService, IAsyncDisposable
{
    private enum SimKind { Transit, Grab, Identify }

    private sealed class SimTask
    {
        public required string TaskId { get; init; }
        public SimKind Kind { get; init; }
        public string? Code { get; init; }     // 识别：被扫料架编号（position[0].code）
        public int PosStart { get; init; } = 101;
        public int Count { get; init; } = 1;
        public volatile bool Canceled;
    }

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly RcsOptions _options;
    private readonly ILogger<RcsSimulator> _logger;
    private readonly ConcurrentDictionary<string, SimTask> _tasks = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _callbackHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    private WebApplication? _app;

    public RcsSimulator(IOptions<AppOptions> options, ILogger<RcsSimulator> logger)
    {
        _options = options.Value.Rcs;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.UseSimulator)
        {
            _logger.LogInformation("RCS 模拟器未启用（UseSimulator=false）。");
            return;
        }

        int listenPort;
        try { listenPort = new Uri(_options.BaseUrl).Port; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RCS 模拟器无法从 BaseUrl 解析端口：{BaseUrl}", _options.BaseUrl);
            return;
        }

        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(listenPort));
            var app = builder.Build();
            MapEndpoints(app);
            _app = app;
            await app.StartAsync(cancellationToken);
            _logger.LogInformation("RCS 模拟器已启动，监听 :{Port}（回推至 {CbHost}:{CbPort}，延时 {Min}~{Max}ms 失败率 {Fail:P0} 取消率 {Cancel:P0}）",
                listenPort, CallbackHost(), _options.CallbackPort,
                _options.SimulatorMinDelayMs, _options.SimulatorMaxDelayMs,
                _options.SimulatorFailureRate, _options.SimulatorCancelRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RCS 模拟器启动失败（端口 {Port} 可能被占用）", listenPort);
            _app = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        if (_app is null) return;
        try { await _app.StopAsync(cancellationToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "RCS 模拟器停止异常"); }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/ExternalInterfaces/transitTask", async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            AcceptTask(raw, SimKind.Transit);
            return Results.Content(Ack("搬运任务已接受"), "application/json; charset=utf-8");
        });

        app.MapPost("/api/ExternalInterfaces/excuteTask", async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            var kind = ReadString(raw, "taskType") == "identifyQR" ? SimKind.Identify : SimKind.Grab;
            AcceptTask(raw, kind);
            return Results.Content(Ack("定制任务已接受"), "application/json; charset=utf-8");
        });

        app.MapPost("/api/ExternalInterfaces/cancelTask", async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            var taskId = ReadString(raw, "taskId");
            if (!string.IsNullOrWhiteSpace(taskId) && _tasks.TryGetValue(taskId, out var t))
            {
                t.Canceled = true;
                _logger.LogInformation("RCS 模拟器收到取消 {TaskId}", taskId);
            }
            return Results.Content(Ack("取消已接受"), "application/json; charset=utf-8");
        });

        app.MapPost("/api/ExternalInterfaces/queryTask", async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            // 查询兜底：按 condition IN 的 taskId 列表从内存表回 status（已回推完成的返回 completed，否则 underway）。
            var asked = ParseQueryTaskIds(raw);
            var items = new List<object>();
            foreach (var id in asked)
            {
                if (_tasks.TryGetValue(id, out var t))
                    items.Add(new { id = id, status = t.Canceled ? "canceled" : "underway" });
                else
                    items.Add(new { id = id, status = "completed" }); // 已回推完成已从内存表移除 → 视为 completed
            }
            var body = JsonSerializer.Serialize(new
            {
                pageIndex = 1, totalPages = 1, pageSize = items.Count, items,
                hasPreviousPage = false, hasNextPage = false, data = (object?)null,
                success = true, message = (string?)null
            }, JsonOpt);
            return Results.Content(body, "application/json; charset=utf-8");
        });
    }

    private void AcceptTask(string raw, SimKind kind)
    {
        var taskId = ReadString(raw, "taskId");
        if (string.IsNullOrWhiteSpace(taskId))
        {
            _logger.LogWarning("RCS 模拟器收到无 taskId 的任务，忽略。");
            return;
        }

        var (posStart, count) = kind == SimKind.Identify ? ParseIdentifyParam(ReadString(raw, "param")) : (101, 1);
        var task = new SimTask
        {
            TaskId = taskId,
            Kind = kind,
            Code = ReadFirstPositionCode(raw),
            PosStart = posStart,
            Count = count
        };
        _tasks[taskId] = task;
        _logger.LogInformation("RCS 模拟器受理 {Kind} {TaskId}", kind, taskId);
        ScheduleCallback(task);
    }

    private void ScheduleCallback(SimTask task)
    {
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(RandomDelay(), _cts.Token); }
            catch (OperationCanceledException) { return; }

            int errorCode;
            if (task.Canceled) errorCode = RcsErrorCode.Cancel;
            else if (Roll(_options.SimulatorFailureRate)) errorCode = RcsErrorCode.Error;
            else if (Roll(_options.SimulatorCancelRate)) errorCode = RcsErrorCode.Cancel;
            else errorCode = RcsErrorCode.Success;

            try
            {
                if (task.Kind == SimKind.Identify) await SendScanAsync(task, errorCode);
                else await SendPushAsync(task, errorCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RCS 模拟器回推 {TaskId} 失败", task.TaskId);
            }
            finally
            {
                _tasks.TryRemove(task.TaskId, out _);
            }
        });
    }

    private async Task SendPushAsync(SimTask task, int errorCode)
    {
        var payload = new
        {
            reqTime = Now(),
            clientCode = "RCS",
            tokenCode = "0",
            taskId = task.TaskId,
            version = _options.Version,
            data = new { system = new { error_code = errorCode, msg = MsgFor(errorCode) } }
        };
        await PostCallbackAsync(RcsCallbackInterfaces.PushTaskStatusPath, payload, task.TaskId, errorCode);
    }

    private async Task SendScanAsync(SimTask task, int errorCode)
    {
        var products = new List<string>();
        if (errorCode == RcsErrorCode.Success)
            for (var i = 0; i < Math.Max(1, task.Count); i++)
                products.Add($"SIM{task.Code}-{task.PosStart + i}");

        var payload = new
        {
            reqTime = Now(),
            clientCode = "RCS",
            tokenCode = "0",
            version = _options.Version,
            taskId = task.TaskId,
            data = new
            {
                system = new { error_code = errorCode, msg = MsgFor(errorCode) },
                code = task.Code ?? "",
                products
            }
        };
        await PostCallbackAsync(RcsCallbackInterfaces.ScanTaskStatusPath, payload, task.TaskId, errorCode);
    }

    private async Task PostCallbackAsync(string path, object payload, string taskId, int errorCode)
    {
        var url = $"http://{CallbackHost()}:{_options.CallbackPort}{path}";
        var body = JsonSerializer.Serialize(payload, JsonOpt);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _callbackHttp.PostAsync(url, content, _cts.Token);
        _logger.LogInformation("RCS 模拟器回推 {Path} {TaskId} error_code={Code} → HTTP {Status}",
            path, taskId, errorCode, (int)resp.StatusCode);
    }

    /// <summary>回推目标 host：回调宿主可能绑定 0.0.0.0，回推走本机环回。</summary>
    private string CallbackHost()
        => _options.CallbackHost is "0.0.0.0" or "" or null ? "127.0.0.1" : _options.CallbackHost;

    private int RandomDelay()
    {
        var min = Math.Max(0, _options.SimulatorMinDelayMs);
        var max = Math.Max(min, _options.SimulatorMaxDelayMs);
        return min == max ? min : Random.Shared.Next(min, max + 1);
    }

    private static bool Roll(double rate) => rate > 0 && Random.Shared.NextDouble() < rate;

    private static string MsgFor(int errorCode) => errorCode switch
    {
        RcsErrorCode.Success => "模拟完成",
        RcsErrorCode.Cancel => "模拟取消",
        _ => "模拟失败"
    };

    private static (int posStart, int count) ParseIdentifyParam(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return (101, 1);
        var parts = param.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var posStart = parts.Length > 0 && int.TryParse(parts[0], out var p) ? p : 101;
        var count = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 1;
        return (posStart, count);
    }

    private static string? ReadFirstPositionCode(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Array)
                foreach (var p in pos.EnumerateArray())
                    if (p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                        return c.GetString();
        }
        catch { /* 容错：无 position 返回 null */ }
        return null;
    }

    private static string? ReadString(string raw, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>解析 queryTask 请求 condition.conditions 中 IN 的 taskId 列表（逗号分隔）。</summary>
    private static IReadOnlyList<string> ParseQueryTaskIds(string raw)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("condition", out var cond)) return list;
            if (!cond.TryGetProperty("conditions", out var conds) || conds.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in conds.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var op = c.TryGetProperty("operator", out var opEl) && opEl.ValueKind == JsonValueKind.String ? opEl.GetString() : null;
                if (!string.Equals(op, "IN", StringComparison.OrdinalIgnoreCase)) continue;
                if (c.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.String)
                    foreach (var part in vEl.GetString()?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>())
                        list.Add(part);
            }
        }
        catch { /* 容错：返回已收集部分 */ }
        return list;
    }

    private static string Ack(string message)
        => JsonSerializer.Serialize(new { Success = true, Message = message, Data = (object?)null }, JsonOpt);

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ctx.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _callbackHttp.Dispose();
        if (_app is not null)
        {
            try { await _app.DisposeAsync(); } catch { /* 忽略 */ }
            _app = null;
        }
        _cts.Dispose();
    }
}
