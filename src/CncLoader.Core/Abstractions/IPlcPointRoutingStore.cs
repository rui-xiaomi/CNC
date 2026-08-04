namespace CncLoader.Core.Abstractions;

/// <summary>
/// PLC 点位原始读取接缝：返回全部匹配行（含 STATE），活动过滤由 <see cref="IPlcPointSource"/> 决定。
/// </summary>
public interface IPlcPointRoutingStore
{
    Task<IReadOnlyList<PlcPointRoutingRow>> FindAsync(
        long? plcId, long? equipmentId, CancellationToken ct = default);
}

/// <summary>点位路由快照（STATE 原样）。</summary>
public sealed record PlcPointRoutingRow(
    long Id,
    long PlcId,
    long EquipmentId,
    long? PositionId,
    string SignalKey,
    string Rw,
    string RegisterAddr,
    string? IoAddr,
    int OnValue,
    int OffValue,
    int DataLen,
    string State);
