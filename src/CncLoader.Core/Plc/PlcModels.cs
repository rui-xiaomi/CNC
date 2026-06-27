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

/// <summary>保存 PLC 配置。</summary>
public sealed record PlcEditModel(
    long PlcId,
    string Name,
    string Ip,
    int Port,
    string Protocol = "ModbusTCP");

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
