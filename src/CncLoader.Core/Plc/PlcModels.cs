namespace CncLoader.Core.Plc;

public enum PlcLinkState { Disconnected, Connecting, Connected, Faulted }

/// <summary>PLC 列表行（含关联机台）。</summary>
public sealed record PlcListItem(
    long PlcId,
    string Name,
    string? EquipmentName,
    string? EquipmentNo,
    string Ip,
    int Port,
    string Protocol,
    PlcLinkState LinkState,
    bool IsConnected);

/// <summary>保存 PLC 配置。PlcId=0 表示新增（由调用方填入建议值或用户值后保存）。</summary>
public sealed record PlcEditModel(
    long PlcId,
    string Name,
    string Ip,
    int Port,
    string Protocol = "ModbusTCP")
{
    /// <summary>是否为新增模式（PlcId=0 视为新增，需唯一性校验与建议值）。</summary>
    public bool IsNew => PlcId == 0;

    /// <summary>对应机台 ID（一机一 PLC，反向绑定：把该机台的 PLC_ID 设为本 PLC）。
    /// null 或 0 表示不绑定；保存后由 BindEquipmentAsync 落库。</summary>
    public long? BoundEquipmentId { get; set; }
}

/// <summary>删除 PLC 前的引用校验结果。</summary>
public sealed record PlcDeleteCheckResult(
    bool CanDelete,
    int EquipmentRefs,
    int PointRefs,
    string Message);

/// <summary>
/// 点位映射行（含 DB 主键，供维护界面）。
/// 设为可变 class 以便 DataGrid 双向绑定行内编辑；保存时整行回写。
/// </summary>
public sealed class PlcPointRow
{
    public long Id { get; set; }
    public long PlcId { get; set; }
    public long EquipmentId { get; set; }
    public long? PositionId { get; set; }
    public string? PositionName { get; set; }
    public string SignalKey { get; set; } = "";
    public string SignalLabel { get; set; } = "";
    public bool IsWrite { get; set; }
    public string RegisterAddress { get; set; } = "";
    public string? IoAddress { get; set; }
    public int OnValue { get; set; } = 1;
    public int OffValue { get; set; } = 2;
    public int DataLength { get; set; } = 1;
}

/// <summary>读操作结果。</summary>
public sealed record PlcReadResult(
    long? PointId,
    string SignalLabel,
    string? PositionName,
    string RegisterAddress,
    int RawValue,
    string SemanticText,
    bool IsOn,
    int? CostMs,
    string? Error);

/// <summary>写操作结果。</summary>
public sealed record PlcWriteResult(
    string RegisterAddress,
    int WrittenValue,
    int? ReadBackValue,
    bool Verified,
    int CostMs,
    string? Error);

/// <summary>设备通信流水（UI 展示）。</summary>
public sealed record DeviceLogRow(
    long Id,
    DateTime Time,
    long? DeviceId,
    string Action,
    string? RegisterAddress,
    string? Request,
    string? Response,
    bool Success,
    int? CostMs,
    string? Author,
    string DisplayLine);

/// <summary>告警行（UI 展示）。</summary>
public sealed record AlarmRow(
    long Id,
    DateTime Time,
    string Level,
    string Message,
    string State);

public sealed record EquipmentOption(long Id, string DisplayName, long PlcId);

public sealed record WriteSignalOption(
    long PointId,
    string DisplayName,
    string RegisterAddress,
    long PlcId,
    long EquipmentId);
