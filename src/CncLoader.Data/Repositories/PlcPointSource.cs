using CncLoader.Core.Abstractions;
using CncLoader.Core.Signals;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 从 MAS_AUTO_PLC_POINT 读取点位定义并投影为领域 <see cref="PlcPointDefinition"/>。
/// 经 <see cref="IPlcPointRoutingStore"/> 短读；仅投影 STATE="0" 活动点位。
/// </summary>
public sealed class PlcPointSource : IPlcPointSource
{
    private readonly IPlcPointRoutingStore _routing;

    public PlcPointSource(IPlcPointRoutingStore routing) => _routing = routing;

    public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default)
        => QueryAsync(null, null, ct);

    public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default)
        => QueryAsync(plcId, null, ct);

    public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default)
        => QueryAsync(null, equipmentId, ct);

    private async Task<IReadOnlyList<PlcPointDefinition>> QueryAsync(long? plcId, long? equipmentId, CancellationToken ct)
    {
        var rows = await _routing.FindAsync(plcId, equipmentId, ct);
        var result = new List<PlcPointDefinition>();
        foreach (var p in rows)
        {
            if (p.State != "0") continue; // 行为保持：仅活动点位
            if (!SignalKeys.TryParse(p.SignalKey, out var key))
                continue; // 未知信号语义跳过
            result.Add(new PlcPointDefinition
            {
                PlcId = p.PlcId,
                EquipmentId = p.EquipmentId,
                PositionId = p.PositionId,
                Signal = key,
                IsWrite = p.Rw == "1",
                RegisterAddress = p.RegisterAddr,
                IoAddress = p.IoAddr,
                OnValue = p.OnValue,
                OffValue = p.OffValue,
                DataLength = p.DataLen <= 0 ? 1 : p.DataLen
            });
        }
        return result;
    }
}
