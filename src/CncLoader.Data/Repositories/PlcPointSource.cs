using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Signals;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 从 MAS_AUTO_PLC_POINT 读取点位定义并投影为领域 <see cref="PlcPointDefinition"/>。
/// 经 <see cref="IPlcPointRoutingStore"/> 短读；仅投影 STATE="0" 活动点位。
/// </summary>
public sealed class PlcPointSource : IPlcPointSource
{
    /// <summary>全量点位缓存 TTL：轮询每 500ms 调 GetAllAsync，缓存后降到 TTL 内一次，避免每拍全表查库。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly IPlcPointRoutingStore _routing;
    private readonly object _gate = new();
    private IReadOnlyList<PlcPointDefinition>? _allCache;
    private DateTime _allCacheAt = DateTime.MinValue;

    public PlcPointSource(IPlcPointRoutingStore routing) => _routing = routing;

    public void Invalidate()
    {
        lock (_gate)
        {
            _allCache = null;
            _allCacheAt = DateTime.MinValue;
        }
    }

    public async Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_allCache is not null && DateTime.UtcNow - _allCacheAt < CacheTtl)
                return _allCache;
        }

        var all = await QueryAsync(null, null, ct);
        lock (_gate)
        {
            _allCache = all;
            _allCacheAt = DateTime.UtcNow;
        }
        return all;
    }

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
            if (!ConfigActivity.IsActive(p.State)) continue; // 行为保持：仅活动点位
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
