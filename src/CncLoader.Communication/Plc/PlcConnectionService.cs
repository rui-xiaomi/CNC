using CncLoader.Communication.Plc;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Signals;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Plc;

public sealed class PlcConnectionService : IPlcConnectionService
{
    private readonly PlcConnectionManager _manager;
    private readonly IPlcCatalogService _catalog;
    private readonly IPlcEndpointResolver _endpoints;
    private readonly IAlarmEventService _alarms;
    private readonly ILogger<PlcConnectionService> _logger;

    public PlcConnectionService(PlcConnectionManager manager, IPlcCatalogService catalog,
        IPlcEndpointResolver endpoints, IAlarmEventService alarms, ILogger<PlcConnectionService> logger)
    {
        _manager = manager;
        _catalog = catalog;
        _endpoints = endpoints;
        _alarms = alarms;
        _logger = logger;
        _manager.ConnectionStateChanged += OnClientStateChanged;
    }

    public event EventHandler<long>? ConnectionChanged;

    public IReadOnlyDictionary<long, PlcLinkState> GetLinkStates() =>
        _manager.All.ToDictionary(c => c.PlcId, c => MapState(c.State));

    public bool IsConnected(long plcId) => _manager.Get(plcId)?.IsConnected ?? false;

    public async Task ConnectAsync(long plcId, CancellationToken ct = default)
    {
        await EnsureRegisteredAsync(plcId, ct);
        var client = _manager.Get(plcId)!;
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PLC {PlcId} 连接失败", plcId);
            await _alarms.RaisePlcAlarmAsync(plcId, $"PLC#{plcId} 连接失败：{ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync(long plcId, CancellationToken ct = default)
    {
        var client = _manager.Get(plcId);
        if (client is null) return;
        await client.DisconnectAsync();
        ConnectionChanged?.Invoke(this, plcId);
    }

    public Task ConnectAllAsync(CancellationToken ct = default) => _manager.ConnectAllAsync(ct);

    public Task DisconnectAllAsync(CancellationToken ct = default) => _manager.DisconnectAllAsync();

    public async Task RefreshRegistrationAsync(long plcId, CancellationToken ct = default)
    {
        var wasConnected = IsConnected(plcId);
        if (_manager.Get(plcId) is not null)
            await DisconnectAsync(plcId, ct);
        await EnsureRegisteredAsync(plcId, ct, force: true);
        if (wasConnected)
            await ConnectAsync(plcId, ct);
    }

    private async Task EnsureRegisteredAsync(long plcId, CancellationToken ct, bool force = false)
    {
        if (!force && _manager.Get(plcId) is not null) return;
        var plc = await _catalog.GetByIdAsync(plcId, ct)
            ?? throw new InvalidOperationException($"PLC#{plcId} 不存在");
        var endpoint = _endpoints.Resolve(plcId, plc.Ip, plc.Port, plc.Protocol);
        _manager.Register(plcId, endpoint);
    }

    private void OnClientStateChanged(object? sender, PlcConnectionStateChangedEventArgs e)
    {
        ConnectionChanged?.Invoke(this, e.PlcId);
        if (e.State is PlcConnectionState.Faulted)
            _ = _alarms.RaisePlcAlarmAsync(e.PlcId, e.Message ?? "PLC 连接异常");
    }

    private static PlcLinkState MapState(PlcConnectionState s) => s switch
    {
        PlcConnectionState.Connected => PlcLinkState.Connected,
        PlcConnectionState.Connecting => PlcLinkState.Connecting,
        PlcConnectionState.Faulted => PlcLinkState.Faulted,
        _ => PlcLinkState.Disconnected
    };
}

/// <summary>解析 PLC 连接端点（模拟器模式映射到环回端口）。</summary>
public interface IPlcEndpointResolver
{
    PlcEndpoint Resolve(long plcId, string dbIp, int dbPort, string protocol = "ModbusTCP");
}
