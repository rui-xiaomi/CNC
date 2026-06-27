using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>PLC 连接管理（多 PLC 独立建链/断开）。</summary>
public interface IPlcConnectionService
{
    event EventHandler<long>? ConnectionChanged;

    IReadOnlyDictionary<long, PlcLinkState> GetLinkStates();
    bool IsConnected(long plcId);
    Task ConnectAsync(long plcId, CancellationToken ct = default);
    Task DisconnectAsync(long plcId, CancellationToken ct = default);
    Task ConnectAllAsync(CancellationToken ct = default);
    Task DisconnectAllAsync(CancellationToken ct = default);
    /// <summary>从 DB 重新登记端点（IP/端口变更后）。</summary>
    Task RefreshRegistrationAsync(long plcId, CancellationToken ct = default);
}
