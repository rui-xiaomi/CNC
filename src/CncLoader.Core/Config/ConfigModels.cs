namespace CncLoader.Core.Config;

// 配置管理模块（Phase 3）展示/编辑 DTO。
// 列表项为只读 record；编辑项为可变 class，便于表单双向绑定后整体回写。

// ===== 线体 =====

/// <summary>线体列表行（对应原型「线体列表」表格列：编码/名称/扫码枪/AGV/状态）。</summary>
public sealed record WorkLineListItem(
    long Id,
    string Code,
    string Name,
    bool ScanEnabled,
    string AgvText,
    bool Enabled);

/// <summary>线体编辑表单。</summary>
public sealed class WorkLineEditModel
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Computer { get; set; }
    public string? ComputerIp { get; set; }
    public long? PlanNum { get; set; }
    public bool ScanEnabled { get; set; }
    public bool Enabled { get; set; } = true;
}

// ===== 工序 =====

/// <summary>工序列表行（排序/编号/名称/类型/所属线体/自动发送/优先级/状态）。</summary>
public sealed record CraftworkListItem(
    long Id,
    long Sort,
    string No,
    string Name,
    bool IsQuality,
    string TypeText,
    string LineName,
    bool AutoSend,
    long Prior,
    bool Enabled);

/// <summary>工序编辑表单。</summary>
public sealed class CraftworkEditModel
{
    public long Id { get; set; }
    public long WorkLineId { get; set; }
    public string No { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsQuality { get; set; } = true;
    public long Sort { get; set; }
    public long Prior { get; set; }
    public bool AutoSend { get; set; }
    public bool Enabled { get; set; } = true;
}

// ===== 机台 =====

/// <summary>机台列表行（编号/名称/编码/类型/所属工序/所属 PLC/状态）。</summary>
public sealed record EquipmentListItem(
    long Id,
    string No,
    string Name,
    string Code,
    string TypeText,
    string CraftName,
    string PlcText,
    bool Enabled);

/// <summary>加工位行（机台详情：名称/编码/状态）。</summary>
public sealed record PositionItem(
    string Name,
    string Code,
    string StateText);

/// <summary>新增机台（保存时自动建 2 个加工位、绑定独立 PLC）。</summary>
public sealed class EquipmentCreateModel
{
    public long CraftworkId { get; set; }
    public string No { get; set; } = "";
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Type { get; set; } = "检测";
    public long PlcId { get; set; }
}

/// <summary>编辑机台（No 为业务编号，编辑时只读；PlcId=0 表示不绑 PLC）。</summary>
public sealed class EquipmentEditModel
{
    public long Id { get; set; }
    public long CraftworkId { get; set; }
    public string No { get; set; } = "";
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Type { get; set; } = "检测";
    public long PlcId { get; set; }
}

/// <summary>机台当前关联料架（上/下料架的料架 ID，未配置为 null）。</summary>
public sealed record EquipmentFrameBindingIds(long? UploadFrameId, long? DownloadFrameId);

/// <summary>机台关联料架（上/下料架各一行）。</summary>
public sealed record EquipmentFrameBinding(
    bool IsUpload,
    string RoleText,
    string FrameDisplay);

// ===== 料架 =====

/// <summary>料架列表行（名称/识别码/布局/槽位/占用）。</summary>
public sealed record FrameListItem(
    long Id,
    string Name,
    string IdentifyCode,
    string LayoutText,
    int SlotTotal,
    int Occupied);

/// <summary>料架-机台绑定行（机台 + 角色：0上料/1下料/2中转/3NG）。BindId 供解绑，EquipmentId/RoleCode 供编辑。</summary>
public sealed record FrameBindRow(
    long BindId,
    long EquipmentId,
    string EquipmentDisplay,
    bool IsUpload,
    string RoleCode,
    string RoleText);

/// <summary>槽位（层 + 层内位 + 电极绑定 + 状态），按层分组渲染。
/// SlotState：0=空 1=占用 2=锁定 3=预记（第四阶段⑥a 槽位账目）。</summary>
public sealed record SlotItem(
    int SlotNo,
    int LayerNo,
    int PosInLayer,
    string Label,
    string? ElectrodeId,
    string SlotState,
    bool Occupied);

/// <summary>新增料架（保存时按 层×每层数 预建空槽位）。</summary>
public sealed class FrameCreateModel
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string IdentifyCode { get; set; } = "";
    public int LayerTotal { get; set; } = 1;
    public int SlotsPerLayer { get; set; } = 1;
}

/// <summary>编辑料架（名称/编码/识别码 + 层数/每层槽数）。改层数/槽数时若有料需先清空。</summary>
public sealed class FrameEditModel
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string IdentifyCode { get; set; } = "";
    public int LayerTotal { get; set; } = 1;
    public int SlotsPerLayer { get; set; } = 1;
}

/// <summary>料架明细：绑定关系 + 槽位（按层分组）+ 入库统计。</summary>
public sealed record FrameDetail(
    long FrameId,
    string Name,
    string IdentifyCode,
    int LayerTotal,
    int SlotsPerLayer,
    int SlotTotal,
    int Occupied,
    IReadOnlyList<FrameBindRow> Bindings,
    IReadOnlyList<SlotItem> Slots);
