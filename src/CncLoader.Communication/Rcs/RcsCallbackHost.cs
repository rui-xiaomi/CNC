using System.Net;
using System.Text;
using CncLoader.Core.Rcs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 回调服务端（内嵌 Kestrel 自宿主）。监听 <c>RcsOptions.CallbackHost:CallbackPort</c>，
/// 提供 <c>/externalApi/pushTaskStatus|scanTaskStatus|warnCallback</c> 三个 POST 端点，
/// 收到即交 <see cref="IRcsCallbackProcessor"/> 处理并应答 <c>{"taskId":"..."}</c>。
/// 作为 <see cref="IHostedService"/> 随主机启动；端口占用等启动失败仅记日志，不影响主程序。
/// </summary>
public sealed class RcsCallbackHost : IHostedService, IRcsCallbackListener, IAsyncDisposable
{
    private readonly IRcsRuntimeConfig _runtime;
    private readonly IRcsCallbackProcessor _processor;
    private readonly ILogger<RcsCallbackHost> _logger;
    private WebApplication? _app;
    private volatile bool _isListening;
    private volatile string? _listenError;
    private string _boundHost = "0.0.0.0";
    private int _boundPort = 9080;

    public RcsCallbackHost(IRcsRuntimeConfig runtime, IRcsCallbackProcessor processor, ILogger<RcsCallbackHost> logger)
    {
        _runtime = runtime;
        _processor = processor;
        _logger = logger;
    }

    public bool IsListening => _isListening;
    public string? ListenError => _listenError;
    public string BoundHost => _boundHost;
    public int BoundPort => _boundPort;

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
            // 端口被占用（常因开多个实例）等启动失败：仅记日志，不阻断主程序。
            _isListening = false;
            _listenError = ex.Message;
            _logger.LogError(ex, "RCS 回调服务启动失败（端口 {Port} 可能被占用）", port);
            _app = null;
        }
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
