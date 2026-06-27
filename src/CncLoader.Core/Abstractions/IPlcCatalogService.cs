using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>PLC 配置 CRUD 与列表查询。</summary>
public interface IPlcCatalogService
{
    Task<IReadOnlyList<PlcListItem>> GetAllAsync(CancellationToken ct = default);
    Task<PlcEditModel?> GetByIdAsync(long plcId, CancellationToken ct = default);
    Task SaveAsync(PlcEditModel model, string author, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentOption>> GetEquipmentsAsync(CancellationToken ct = default);
}
