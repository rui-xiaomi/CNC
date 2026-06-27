using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>PLC 配置 CRUD 与列表查询。</summary>
public interface IPlcCatalogService
{
    Task<IReadOnlyList<PlcListItem>> GetAllAsync(CancellationToken ct = default);
    Task<PlcEditModel?> GetByIdAsync(long plcId, CancellationToken ct = default);
    Task SaveAsync(PlcEditModel model, string author, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentOption>> GetEquipmentsAsync(CancellationToken ct = default);

    /// <summary>建议下一个 PLC_ID（max(PLC_ID)+1，空表返回 1）。</summary>
    Task<long> SuggestNextPlcIdAsync(CancellationToken ct = default);

    /// <summary>删除前校验：被机台/点位引用时禁止删除。</summary>
    Task<PlcDeleteCheckResult> CheckDeleteAsync(long plcId, CancellationToken ct = default);

    /// <summary>软删 PLC（State='1'）。调用前应先 CheckDeleteAsync。</summary>
    Task DeleteAsync(long plcId, string author, CancellationToken ct = default);

    /// <summary>绑定机台到 PLC（一机一 PLC，反向绑定）。
    /// 清所有原绑到本 PLC 的机台 PlcId，再把目标机台的 PlcId 设为 plcId。
    /// equipmentId 为 null/0 表示解除本 PLC 的所有机台绑定。</summary>
    Task BindEquipmentAsync(long plcId, long? equipmentId, string author, CancellationToken ct = default);
}
