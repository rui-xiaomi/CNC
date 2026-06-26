namespace CncLoader.Core.Abstractions;

public enum DeviceType { Plc, Agv, Scan }

public enum DeviceAction { Read, Write, Connect, Disconnect, Heartbeat }

/// <summary>一条设备通信流水（对应 MAS_AUTO_DEVICE_LOG）。</summary>
public sealed record DeviceLogEntry
{
    public required DeviceType DeviceType { get; init; }
    public long? DeviceId { get; init; }
    public required DeviceAction Action { get; init; }
    public string? RegisterAddress { get; init; }
    public string? Request { get; init; }
    public string? Response { get; init; }
    public bool Success { get; init; } = true;
    public int? CostMs { get; init; }
    public string? Error { get; init; }
    public string? Author { get; init; }
}

/// <summary>
/// 设备通信流水记录器。Phase 1 可仅写文件日志；Phase 2 增加落库 MAS_AUTO_DEVICE_LOG。
/// </summary>
public interface IDeviceLogger
{
    void Log(DeviceLogEntry entry);
}
