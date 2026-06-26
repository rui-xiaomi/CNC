using CncLoader.Core.Abstractions;
using CncLoader.Core.Signals;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

/// <summary>
/// 从 MAS_AUTO_PLC_POINT 读取点位定义并投影为领域 <see cref="PlcPointDefinition"/>。
/// 使用 DbContext 工厂创建短生命周期上下文，可被单例轮询中枢安全调用。
/// </summary>
public sealed class PlcPointSource : IPlcPointSource
{
    private readonly IDbContextFactory<CncDbContext> _factory;

    public PlcPointSource(IDbContextFactory<CncDbContext> factory) => _factory = factory;

    public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default)
        => QueryAsync(null, null, ct);

    public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default)
        => QueryAsync(plcId, null, ct);

    public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default)
        => QueryAsync(null, equipmentId, ct);

    private async Task<IReadOnlyList<PlcPointDefinition>> QueryAsync(long? plcId, long? equipmentId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.PlcPoints.AsNoTracking().Where(p => p.State == "0");
        if (plcId.HasValue) query = query.Where(p => p.PlcId == plcId.Value);
        if (equipmentId.HasValue) query = query.Where(p => p.EquipmentId == equipmentId.Value);

        var rows = await query.ToListAsync(ct);
        var result = new List<PlcPointDefinition>(rows.Count);
        foreach (var p in rows)
        {
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
