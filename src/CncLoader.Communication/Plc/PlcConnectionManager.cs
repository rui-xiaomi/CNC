using System.Collections.Concurrent;
using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Plc;

/// <summary>创建单台 PLC 客户端。便于注入日志/流水，并支持协议替换。</summary>
public interface IPlcClientFactory
{
    IPlcClient Create(long plcId, PlcEndpoint endpoint);
}

/// <summary>按端点协议分流创建具体 PLC 客户端（Modbus TCP / 欧姆龙 FINS）。</summary>
public sealed class PlcClientFactory : IPlcClientFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDeviceLogger _deviceLogger;
    private readonly int _connectTimeoutMs;
    private readonly int _rwTimeoutMs;

    public PlcClientFactory(ILoggerFactory loggerFactory, IDeviceLogger deviceLogger,
        int connectTimeoutMs, int rwTimeoutMs)
    {
        _loggerFactory = loggerFactory;
        _deviceLogger = deviceLogger;
        _connectTimeoutMs = connectTimeoutMs;
        _rwTimeoutMs = rwTimeoutMs;
    }

    public IPlcClient Create(long plcId, PlcEndpoint endpoint) =>
        endpoint.Protocol.Contains("FINS", StringComparison.OrdinalIgnoreCase)
            ? new OmronFinsPlcClient(plcId, endpoint, _connectTimeoutMs, _rwTimeoutMs,
                _loggerFactory.CreateLogger<OmronFinsPlcClient>(), _deviceLogger)
            : new NModbusPlcClient(plcId, endpoint, _connectTimeoutMs, _rwTimeoutMs,
                _loggerFactory.CreateLogger<NModbusPlcClient>(), _deviceLogger);
}

/// <summary>
/// 按 PLC_ID 管理多条独立连接，互不影响（一台断线只暂停其机台调度）。
/// </summary>
public sealed class PlcConnectionManager : IDisposable
{
    private readonly IPlcClientFactory _factory;
    private readonly ILogger<PlcConnectionManager> _logger;
    private readonly ConcurrentDictionary<long, IPlcClient> _clients = new();

    public PlcConnectionManager(IPlcClientFactory factory, ILogger<PlcConnectionManager> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public event EventHandler<PlcConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>登记一台 PLC（不立即连接）。重复登记会替换旧实例。</summary>
    public IPlcClient Register(long plcId, PlcEndpoint endpoint)
    {
        if (_clients.TryRemove(plcId, out var old))
        {
            old.ConnectionStateChanged -= OnClientStateChanged;
            old.Dispose();
        }
        var client = _factory.Create(plcId, endpoint);
        client.ConnectionStateChanged += OnClientStateChanged;
        _clients[plcId] = client;
        return client;
    }

    public IPlcClient? Get(long plcId) => _clients.TryGetValue(plcId, out var c) ? c : null;

    public IReadOnlyCollection<IPlcClient> All => _clients.Values.ToArray();

    public IReadOnlyDictionary<long, bool> ConnectionStates =>
        _clients.ToDictionary(kv => kv.Key, kv => kv.Value.IsConnected);

    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        foreach (var client in _clients.Values)
        {
            try { await client.ConnectAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "PLC {PlcId} 连接失败", client.PlcId); }
        }
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var client in _clients.Values)
        {
            try { await client.DisconnectAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "PLC {PlcId} 断开失败", client.PlcId); }
        }
    }

    private void OnClientStateChanged(object? sender, PlcConnectionStateChangedEventArgs e)
        => ConnectionStateChanged?.Invoke(this, e);

    public void Dispose()
    {
        foreach (var c in _clients.Values)
        {
            c.ConnectionStateChanged -= OnClientStateChanged;
            c.Dispose();
        }
        _clients.Clear();
    }
}
