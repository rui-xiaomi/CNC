using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>设备通信流水：落库 + 查询 + 实时通知。</summary>
public interface IDeviceLogStore
{
    event EventHandler<DeviceLogRow>? LogAppended;

    /// <summary>追加一条流水（写操作意图须在下发前调用）。</summary>
    Task<long> AppendAsync(DeviceLogEntry entry, CancellationToken ct = default);
    Task UpdateAsync(long id, string? response, bool success, int? costMs, string? error, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceLogRow>> GetRecentAsync(long? deviceId, int limit = 50, CancellationToken ct = default);
}
