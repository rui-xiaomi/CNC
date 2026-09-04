using CncLoader.Core.Signals;

namespace CncLoader.Core.Abstractions;

/// <summary>
/// 点位定义来源（MAS_AUTO_PLC_POINT）。由 Data 层实现，供轮询中枢按机台/PLC 取点位。
/// </summary>
public interface IPlcPointSource
{
    /// <summary>取全部启用点位。</summary>
    Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default);

    /// <summary>取某 PLC 的全部点位。</summary>
    Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default);

    /// <summary>取某机台的全部点位。</summary>
    Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default);

    /// <summary>点位增删改后失效进程内缓存（PlcPointManagementService 在 CRUD 后调用）。</summary>
    void Invalidate();
}
