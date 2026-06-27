using CncLoader.Core.Config;

namespace CncLoader.Core.Abstractions;

/// <summary>下拉选项（Id + 显示文本）。</summary>
public sealed record NamedOption(long Id, string DisplayName);

/// <summary>删除校验通用结果（引用数 + 是否可删 + 提示消息）。</summary>
public sealed record DeleteCheckResult(bool CanDelete, int Refs, string Message);

/// <summary>线体配置 CRUD 与列表。</summary>
public interface IWorkLineService
{
    Task<IReadOnlyList<WorkLineListItem>> GetAllAsync(CancellationToken ct = default);
    Task<WorkLineEditModel?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<long> SaveAsync(WorkLineEditModel model, string author, CancellationToken ct = default);
    /// <summary>删除前校验：被工序引用时禁止删除。</summary>
    Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default);
    /// <summary>软删线体（State='1'）。调用前应先 CheckDeleteAsync。</summary>
    Task DeleteAsync(long id, string author, CancellationToken ct = default);
}

/// <summary>工序配置 CRUD 与列表（按线体过滤）。</summary>
public interface ICraftworkService
{
    Task<IReadOnlyList<NamedOption>> GetWorkLineOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CraftworkListItem>> GetByLineAsync(long? workLineId, CancellationToken ct = default);
    Task<CraftworkEditModel?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<long> SaveAsync(CraftworkEditModel model, string author, CancellationToken ct = default);
    /// <summary>删除前校验：被机台引用时禁止删除。</summary>
    Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default);
    /// <summary>软删工序（State='1'）。调用前应先 CheckDeleteAsync。</summary>
    Task DeleteAsync(long id, string author, CancellationToken ct = default);
}

/// <summary>机台配置与详情（加工位 / 关联料架）。</summary>
public interface IEquipmentConfigService
{
    Task<IReadOnlyList<NamedOption>> GetCraftworkOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentListItem>> GetByCraftAsync(long? craftworkId, CancellationToken ct = default);
    Task<IReadOnlyList<PositionItem>> GetPositionsAsync(long equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentFrameBinding>> GetFrameBindingsAsync(long equipmentId, CancellationToken ct = default);

    /// <summary>PLC 下拉（含 IP）。</summary>
    Task<IReadOnlyList<NamedOption>> GetPlcOptionsAsync(CancellationToken ct = default);
    /// <summary>料架下拉（含识别码）。</summary>
    Task<IReadOnlyList<NamedOption>> GetFrameOptionsAsync(CancellationToken ct = default);
    /// <summary>下一个机台编号建议（如 EQ04）。</summary>
    Task<string> SuggestNextNoAsync(CancellationToken ct = default);
    /// <summary>新增机台并自动创建 2 个加工位，返回机台主键。</summary>
    Task<long> CreateEquipmentAsync(EquipmentCreateModel model, string author, CancellationToken ct = default);
    /// <summary>读取机台编辑数据（含 CraftworkId/PlcId）。</summary>
    Task<EquipmentEditModel?> GetByIdAsync(long equipmentId, CancellationToken ct = default);
    /// <summary>更新机台（不重建加工位，不动料架绑定；PlcId=0 表示不绑 PLC）。</summary>
    Task UpdateAsync(EquipmentEditModel model, string author, CancellationToken ct = default);
    /// <summary>读取机台当前上/下料架绑定。</summary>
    Task<EquipmentFrameBindingIds> GetFrameBindingIdsAsync(long equipmentId, CancellationToken ct = default);
    /// <summary>设置机台上/下料架绑定（null 表示清除该角色）。</summary>
    Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId, string author, CancellationToken ct = default);
    /// <summary>删除前校验：被点位映射或料架绑定引用时禁止删除；加工位自动级联软删。</summary>
    Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default);
    /// <summary>软删机台（State='1'）并级联软删其加工位。调用前应先 CheckDeleteAsync。</summary>
    Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default);
}

/// <summary>料架配置与槽位/电极追踪。</summary>
public interface IFrameService
{
    Task<IReadOnlyList<FrameListItem>> GetAllAsync(CancellationToken ct = default);
    Task<FrameDetail?> GetDetailAsync(long frameId, CancellationToken ct = default);
    /// <summary>新增料架并按 层×每层数 预建空槽位，返回料架主键。</summary>
    Task<long> CreateFrameAsync(FrameCreateModel model, string author, CancellationToken ct = default);
}
