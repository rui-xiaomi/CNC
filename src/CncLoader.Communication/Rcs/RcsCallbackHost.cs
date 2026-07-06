using System.Net;
using System.Text;
using CncLoader.Common.Configuration;
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
/// 作为 <see cref="IHostedService"/> 随主机启动；端口占用等启动失败仅记日志，不影响主程序。
/// </summary>
public sealed class RcsCallbackHost : IHostedService, IAsyncDisposable
{
    private readonly RcsOptions _options;
    private readonly IRcsCallbackProcessor _processor;
    private readonly ILogger<RcsCallbackHost> _logger;
    private WebApplication? _app;

    public RcsCallbackHost(IOptions<AppOptions> options, IRcsCallbackProcessor processor, ILogger<RcsCallbackHost> logger)
    {
        _options = options.Value.Rcs;
        _processor = processor;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            // 回调宿主为无人值守后台服务，关闭其自带日志提供程序，避免与主程序 Serilog 双写噪音。
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (IPAddress.TryParse(_options.CallbackHost, out var ip))
                    k.Listen(ip, _options.CallbackPort);
                else
                    k.ListenAnyIP(_options.CallbackPort);
            });

            var app = builder.Build();
            MapEndpoints(app);
            _app = app;

            await app.StartAsync(cancellationToken);
            _logger.LogInformation("RCS 回调服务已启动，监听 http://{Host}:{Port}/externalApi/",
                _options.CallbackHost, _options.CallbackPort);
        }
        catch (Exception ex)
        {
            // 端口被占用（常因开多个实例）等启动失败：仅记日志，不阻断主程序。
            _logger.LogError(ex, "RCS 回调服务启动失败（端口 {Port} 可能被占用）", _options.CallbackPort);
            _app = null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null) return;
        try { await _app.StopAsync(cancellationToken); }
        catch (Exception ex) { _logger.LogWarning(ex, "RCS 回调服务停止异常"); }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapPost(RcsCallbackInterfaces.PushTaskStatusPath, async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            var ack = await _processor.HandlePushTaskStatusAsync(raw, ctx.RequestAborted);
            return Results.Content(ack, "application/json; charset=utf-8");
        });

        app.MapPost(RcsCallbackInterfaces.ScanTaskStatusPath, async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            var ack = await _processor.HandleScanTaskStatusAsync(raw, ctx.RequestAborted);
            return Results.Content(ack, "application/json; charset=utf-8");
        });

        app.MapPost(RcsCallbackInterfaces.WarnCallbackPath, async (HttpContext ctx) =>
        {
            var raw = await ReadBodyAsync(ctx);
            var ack = await _processor.HandleWarnCallbackAsync(raw, ctx.RequestAborted);
            return Results.Content(ack, "application/json; charset=utf-8");
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
    }
}
