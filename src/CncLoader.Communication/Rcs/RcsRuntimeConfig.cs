using CncLoader.Common.Configuration;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Rcs;

/// <summary>进程内可变 RCS 连接配置（出站热更新；回调启动快照用于提示重启）。</summary>
public sealed class RcsRuntimeConfig : IRcsRuntimeConfig
{
    private readonly object _gate = new();
    private string _baseUrl;
    private string _clientCode;
    private readonly string _version;
    private readonly string _tokenCode;
    private int _requestTimeoutMs;
    private int _maxRetries;
    private string _callbackHost;
    private int _callbackPort;
    private int _pollIntervalMs;
    private string _bootCallbackHost;
    private int _bootCallbackPort;

    public RcsRuntimeConfig(IOptions<AppOptions> options)
    {
        var r = options.Value.Rcs;
        _baseUrl = r.BaseUrl;
        _clientCode = r.ClientCode;
        _version = r.Version;
        _tokenCode = r.TokenCode;
        _requestTimeoutMs = Math.Max(1000, r.RequestTimeoutMs);
        _maxRetries = Math.Max(1, r.MaxRetries);
        _callbackHost = r.CallbackHost;
        _callbackPort = r.CallbackPort;
        _pollIntervalMs = Math.Max(500, r.PollIntervalMs);
        _bootCallbackHost = _callbackHost;
        _bootCallbackPort = _callbackPort;
    }

    public string BaseUrl { get { lock (_gate) return _baseUrl; } }
    public string ClientCode { get { lock (_gate) return _clientCode; } }
    public string Version => _version;
    public string TokenCode => _tokenCode;
    public int RequestTimeoutMs { get { lock (_gate) return _requestTimeoutMs; } }
    public int MaxRetries { get { lock (_gate) return _maxRetries; } }
    public string CallbackHost { get { lock (_gate) return _callbackHost; } }
    public int CallbackPort { get { lock (_gate) return _callbackPort; } }
    public int PollIntervalMs { get { lock (_gate) return _pollIntervalMs; } }
    public string BootCallbackHost { get { lock (_gate) return _bootCallbackHost; } }
    public int BootCallbackPort { get { lock (_gate) return _bootCallbackPort; } }

    public void Apply(RcsConnectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            _baseUrl = config.BaseUrl.Trim();
            _clientCode = config.ClientCode.Trim();
            _callbackHost = string.IsNullOrWhiteSpace(config.CallbackHost) ? "0.0.0.0" : config.CallbackHost.Trim();
            _callbackPort = config.CallbackPort;
            _requestTimeoutMs = Math.Max(1000, config.RequestTimeoutMs);
            _maxRetries = Math.Max(1, config.MaxRetries);
            _pollIntervalMs = Math.Max(500, config.PollIntervalMs);
        }
    }

    public void CaptureBootCallback()
    {
        lock (_gate)
        {
            _bootCallbackHost = _callbackHost;
            _bootCallbackPort = _callbackPort;
        }
    }

    public RcsConnectionConfig Snapshot()
    {
        lock (_gate)
        {
            return new RcsConnectionConfig
            {
                BaseUrl = _baseUrl,
                ClientCode = _clientCode,
                CallbackHost = _callbackHost,
                CallbackPort = _callbackPort,
                RequestTimeoutMs = _requestTimeoutMs,
                MaxRetries = _maxRetries,
                PollIntervalMs = _pollIntervalMs
            };
        }
    }
}
