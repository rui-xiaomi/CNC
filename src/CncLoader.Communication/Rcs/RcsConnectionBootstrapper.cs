using CncLoader.Core.Rcs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Rcs;

/// <summary>启动时从库加载 RCS 连接配置覆盖运行时；无行则按当前运行时种子落库。</summary>
public sealed class RcsConnectionBootstrapper : IHostedService
{
    private readonly IRcsConnectionConfigService _store;
    private readonly IRcsRuntimeConfig _runtime;
    private readonly ILogger<RcsConnectionBootstrapper> _logger;

    public RcsConnectionBootstrapper(
        IRcsConnectionConfigService store,
        IRcsRuntimeConfig runtime,
        ILogger<RcsConnectionBootstrapper> logger)
    {
        _store = store;
        _runtime = runtime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _store.GetAsync(cancellationToken);
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.BaseUrl))
            {
                _runtime.Apply(existing);
                _runtime.CaptureBootCallback();
                _logger.LogInformation("RCS 连接配置已从库加载：{Url} client={Code} 回调={Host}:{Port}",
                    existing.BaseUrl, existing.ClientCode, existing.CallbackHost, existing.CallbackPort);
                return;
            }

            var seed = _runtime.Snapshot();
            var saved = await _store.SaveAsync(seed, "bootstrap", cancellationToken);
            _runtime.Apply(saved);
            _runtime.CaptureBootCallback();
            _logger.LogInformation("RCS 连接配置库无行，已按 appsettings 种子落库 Id={Id}", saved.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RCS 连接配置加载失败，继续使用 appsettings 默认值");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
