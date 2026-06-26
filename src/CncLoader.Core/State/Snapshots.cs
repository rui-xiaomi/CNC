using CncLoader.Core.Signals;

namespace CncLoader.Core.State;

/// <summary>单个信号点的最新读值快照。</summary>
public sealed record SignalReading
{
    public required SignalKey Signal { get; init; }
    public long? PositionId { get; init; }
    public required string RegisterAddress { get; init; }
    public int RawValue { get; init; }
    /// <summary>按约定翻译的布尔值（ON=true）；非 On/Off 原始值为 null。</summary>
    public bool? On { get; init; }
    public DateTime ReadAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>一个加工位的合成状态快照。</summary>
public sealed record PositionStatus
{
    public required long EquipmentId { get; init; }
    public required long PositionId { get; init; }
    public required PositionState State { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>一台机台的机台级信号快照（门/安全）。</summary>
public sealed record MachineStatus
{
    public required long EquipmentId { get; init; }
    /// <summary>门是否打开（DOOR=ON 视为开门）。</summary>
    public bool? DoorOpen { get; init; }
    /// <summary>机台是否安全（MACHINE_SAFE=ON）。</summary>
    public bool? Safe { get; init; }
    /// <summary>对应 PLC 是否在线。</summary>
    public bool PlcOnline { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}
