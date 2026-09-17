using System.Collections.Concurrent;
using System.Net;
using System.Text;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 回调服务端（内嵌 Kestrel 自宿主）。监听 <c>RcsOptions.CallbackHost:CallbackPort</c>，
/// 提供 <c>/externalApi/pushTaskStatus|scanTaskStatus|warnCallback</c> 三个 POST 端点，
/// 收到即交 <see cref="IRcsCallbackProcessor"/> 处理并应答 <c>{"taskId":"..."}</c>。
/// 来源须在 <c>RcsOptions.CallbackAllowedRemoteIps</c> 内，否则 403（白名单为空 fail-closed 拒绝全部来源）。
/// 作为 <see cref="IHostedService"/> 随主机启动；端口占用等启动失败记日志并落严重告警（不阻断主程序，但操作员须知晓回调不可用）。
/// </summary>
public sealed class RcsCallbackHost : IHostedService, IRcsCallbackListener, IAsyncDisposable
{
    private static readonly TimeSpan RejectLogThrottle = TimeSpan.FromSeconds(30);

    private readonly IRcsRuntimeConfig _runtime;
    private readonly IRcsCallbackProcessor _processor;
    private readonly IAlarmEventService _alarms;
    private readonly ILogger<RcsCallbackHost> _logger;
    private readonly bool _useSimulator;
    private readonly IReadOnlyList<IPAddress> _allowedSources;
    private readonly IReadOnlyList<string> _invalidSources;
    /// <summary>拒绝日志限频（按来源 IP），防伪造请求刷爆日志。</summary>
    private readonly ConcurrentDictionary<string, DateTime> _rejectLogStamp = new();
    private WebApplication? _app;
    private volatile bool _isListening;
    private volatile string? _listenError;
    private string _boundHost = "0.0.0.0";
    private int _boundPort = 9080;

    public RcsCallbackHost(IRcsRuntimeConfig runtime, IRcsCallbackProcessor processor, IAlarmEventService alarms,
        IOptions<AppOptions> options, ILogger<RcsCallbackHost> logger)
    {
        _runtime = runtime;
        _processor = processor;
        _alarms = alarms;
        _logger = logger;
        var rcs = options.Value.Rcs;
        _useSimulator = rcs.UseSimulator;
        var (allowed, invalid) = CallbackSourceFilter.Parse(rcs.CallbackAllowedRemoteIps);
        // 现场白名单通常只有 RCS IP；本机「测试本机监听」与模拟器回推都走 127.0.0.1，必须补环回。
        _allowedSources = CallbackSourceFilter.EnsureLocalProbeAllowed(allowed);
        _invalidSources = invalid;
    }

    public bool IsListening => _isListening;
    public string? ListenError => _listenError;
    public string BoundHost => _boundHost;
    public int BoundPort => _boundPort;

    public Task<RcsLocalCallbackProbeResult> ProbePushEndpointAsync(CancellationToken ct = default)
    {
        if (!_isListening)
            return Task.FromResult(RcsLocalCallbackProbeResult.NotListening(_listenError));

        var host = CallbackLoopbackHost.Resolve(_boundHost);
        return RcsLocalCallbackProber.PostPushAsync(host, _boundPort, ct);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var host = _runtime.BootCallbackHost;
        var port = _runtime.BootCallbackPort;
        _boundHost = host;
        _boundPort = port;
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            // 回调宿主为无人值守后台服务，关闭其自带日志提供程序，避免与主程序 Serilog 双写噪音。
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                // 回调报文为小 JSON：限制请求体与请求头时长，防大包/慢速请求耗尽内存与连接（P0-4）。
                k.Limits.MaxRequestBodySize = 1024 * 1024;
                k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                if (IPAddress.TryParse(host, out var ip))
                {
                    k.Listen(ip, port);
                    // 规范化（如 00.0.0.0 → 0.0.0.0），避免测试回调/模拟器拿脏 Host 去连。
                    _boundHost = ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
                        ? "0.0.0.0"
                        : ip.ToString();
                }
                else
                {
                    // 非法 Host（如误填 00.0.0.）回退任意网卡，Bound 记为 0.0.0.0。
                    k.ListenAnyIP(port);
                    _boundHost = "0.0.0.0";
                    _logger.LogWarning("回调 Host「{Host}」非法，已回退监听 0.0.0.0:{Port}", host, port);
                }
            });

            var app = builder.Build();
            MapEndpoints(app);
            _app = app;

            await app.StartAsync(cancellationToken);
            _isListening = true;
            _listenError = null;
            _logger.LogInformation("RCS 回调服务已启动，监听 http://{Host}:{Port}/externalApi/",
                _boundHost, port);
        }
        catch (Exception ex)
        {
            // 端口被占用（常因开多个实例）等启动失败：仅记日志，不阻断主程序，但落严重告警让操作员立刻知晓回调不可用（P2-6）。
            _isListening = false;
            _listenError = ex.Message;
            _logger.LogError(ex, "RCS 回调服务启动失败（端口 {Port} 可能被占用）", port);
            _app = null;
            try
            {
                await _alarms.RaiseRcsWarnAsync("CALLBACK",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    $"RCS 回调服务启动失败（端口 {port} 可能被占用），任务状态/扫码/告警将无法回推，请检查端口后重启客户端", null,
                    CancellationToken.None);
            }
            catch (Exception alarmEx)
            {
                _logger.LogWarning(alarmEx, "回调启动失败告警落库失败");
            }
            return;
        }

        await WarnIfSourceFilterWeakAsync(port);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null) return;
        try { await _app.StopAsync(cancellationToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "RCS 回调服务停止异常"); }
        finally
        {
            _isListening = false;
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        // 来源白名单：非名单来源 403，不进处理器、不落库（防伪造回调改写槽位账 / 触发自动 redo；P0-4）。
        app.Use(async (HttpContext ctx, RequestDelegate next) =>
        {
            var remote = ctx.Connection.RemoteIpAddress;
            if (CallbackSourceFilter.IsRejected(remote, _allowedSources))
            {
                LogRejectedThrottled(remote, ctx.Request.Path);
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(ctx);
        });

        app.MapPost(RcsCallbackInterfaces.PushTaskStatusPath, async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            var ack = await _processor.HandlePushTaskStatusAsync(raw, ctx.RequestAborted);
            return RcsCallbackAckResults.FromAckBody(ack);
        });

        app.MapPost(RcsCallbackInterfaces.ScanTaskStatusPath, async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            var ack = await _processor.HandleScanTaskStatusAsync(raw, ctx.RequestAborted);
            return RcsCallbackAckResults.FromAckBody(ack);
        });

        app.MapPost(RcsCallbackInterfaces.WarnCallbackPath, async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            var ack = await _processor.HandleWarnCallbackAsync(raw, ctx.RequestAborted);
            return RcsCallbackAckResults.FromAckBody(ack);
        });
    }

    /// <summary>白名单为空（fail-closed 拒全部）或含非法项时，启动即记错误日志并落严重告警（模拟器模式只记日志）。</summary>
    private async Task WarnIfSourceFilterWeakAsync(int port)
    {
        var problems = new List<string>();
        if (_invalidSources.Count > 0)
            problems.Add($"RCS 回调来源白名单含非法项 [{string.Join(", ", _invalidSources)}]，已忽略，请改为点分 IPv4 或 IPv6");
        if (_allowedSources.Count == 0)
            problems.Add($"RCS 回调来源白名单为空，端口 {port} 的回调已 fail-closed 拒绝全部来源，请配置 App:Rcs:CallbackAllowedRemoteIps");

        if (problems.Count == 0)
        {
            _logger.LogInformation("RCS 回调来源白名单：{Ips}", string.Join(", ", _allowedSources));
            return;
        }

        var message = string.Join("；", problems);
        _logger.LogError("{Msg}", message);
        if (_useSimulator) return;
        try
        {
            await _alarms.RaiseRcsWarnAsync("CALLBACK", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                message, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "回调来源白名单告警落库失败");
        }
    }

    private void LogRejectedThrottled(IPAddress? remote, PathString path)
    {
        var key = remote?.ToString() ?? "unknown";
        var now = DateTime.UtcNow;
        if (_rejectLogStamp.TryGetValue(key, out var last) && now - last < RejectLogThrottle) return;
        _rejectLogStamp[key] = now;
        _logger.LogWarning("拒绝非白名单来源的 RCS 回调：{Remote} {Path}（30s 内同来源不再记录）", key, path.Value);
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ctx.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try { await _app.DisposeAsync(); }
            catch { /* 忽略释放异常 */ }
            _app = null;
        }
        _isListening = false;
    }
}
