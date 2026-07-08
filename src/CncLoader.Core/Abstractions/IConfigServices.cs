using CncLoader.Core.Config;
using CncLoader.Core.Rcs;

namespace CncLoader.Core.Abstractions;

/// <summary>下拉选项（Id + 显示文本）。</summary>
public sealed record NamedOption(long Id, string DisplayName);

/// <summary>料架-机台绑定反查结果（机台 + 该料架在此机台的角色）。</summary>
public sealed record FrameBindingInfo(long EquipmentId, FrameRole Role);

/// <summary>删除校验通用结果（引用数 + 是否可删 + 提示消息）。</summary>
public sealed record DeleteCheckResult(bool CanDelete, int Refs, string Message);

/// <summary>机台所属线体引用（线体主键 + 线体编码），供 RCS 任务 taskId 前缀与落库线体使用。</summary>
public sealed record WorkLineRef(long WorkLineId, string LineCode);

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
    /// <summary>反查某料架绑定的全部机台+角色（一架两用可返回多条）。供水位监视器精确定位换架目标。</summary>
    Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default);
    /// <summary>取机台指定角色（上料/下料/中转/NG）绑定的料架 ID；无则 null。供路由分流与槽位账定位目标料架。</summary>
    Task<long?> GetFrameBindingByRoleAsync(long equipmentId, FrameRole role, CancellationToken ct = default);
    /// <summary>求同线下一道工序（CraftworkNode 次大者）的全部启用机台 ID；本机为末道工序或无下一工序则返回空。供 OK 件工序间流转路由。</summary>
    Task<IReadOnlyList<long>> GetNextProcessEquipmentsAsync(long equipmentId, CancellationToken ct = default);
    /// <summary>反查机台所属线体（机台→工序→线体）。找不到返回 null。</summary>
    Task<WorkLineRef?> GetWorkLineByEquipmentAsync(long equipmentId, CancellationToken ct = default);
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

    /// <summary>可绑定的机台下拉。</summary>
    Task<IReadOnlyList<NamedOption>> GetEquipmentOptionsAsync(CancellationToken ct = default);
    /// <summary>把料架绑定给某机台的某角色（roleCode "0"上料/"1"下料/"2"中转/"3"NG）。
    /// 一机一角色一料架：先清该机台该角色的旧绑定，再绑本料架。</summary>
    Task BindEquipmentAsync(long frameId, long equipmentId, string roleCode, string author, CancellationToken ct = default);
    /// <summary>解绑（按绑定行主键删除）。</summary>
    Task UnbindAsync(long bindId, string author, CancellationToken ct = default);

    /// <summary>读取料架编辑数据。</summary>
    Task<FrameEditModel?> GetFrameForEditAsync(long id, CancellationToken ct = default);
    /// <summary>更新料架（名称/编码/识别码；层数或每层槽数变化时重建空槽——有料/占用则拒绝）。</summary>
    Task UpdateFrameAsync(FrameEditModel model, string author, CancellationToken ct = default);
    /// <summary>删除前校验：被机台绑定或仍有占用槽位时禁止删除。</summary>
    Task<DeleteCheckResult> CheckDeleteFrameAsync(long id, CancellationToken ct = default);
    /// <summary>删除料架（软删 STATE='1'）。</summary>
    Task DeleteFrameAsync(long id, string author, CancellationToken ct = default);
}
