namespace CncLoader.Communication.Plc;

/// <summary>PLC 连接端点。一机一 PLC，各 IP 不同。</summary>
public sealed record PlcEndpoint(string Host, int Port);

public enum PlcConnectionState { Disconnected, Connecting, Connected, Faulted }

public sealed class PlcConnectionStateChangedEventArgs(long plcId, PlcConnectionState state, string? message = null) : EventArgs
{
    public long PlcId { get; } = plcId;
    public PlcConnectionState State { get; } = state;
    public string? Message { get; } = message;
}

/// <summary>
/// 单台 PLC 的连接。每台 PLC 一个独立实例，互不影响（一台断线只暂停其机台调度）。
/// 协议可替换（当前 Modbus TCP），业务层不感知具体协议。
/// </summary>
public interface IPlcClient : IDisposable
{
    long PlcId { get; }
    PlcEndpoint Endpoint { get; }
    PlcConnectionState State { get; }
    bool IsConnected { get; }

    event EventHandler<PlcConnectionStateChangedEventArgs>? ConnectionStateChanged;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    /// <summary>心跳：探测连接是否仍可用。</summary>
    Task<bool> HeartbeatAsync(CancellationToken ct = default);

    /// <summary>从寄存器地址（如 D1006）读取 length 个保持寄存器。</summary>
    Task<int[]> ReadRegistersAsync(string registerAddress, int length, CancellationToken ct = default);

    /// <summary>向寄存器地址写入单个值。</summary>
    Task WriteRegisterAsync(string registerAddress, int value, CancellationToken ct = default);
}
