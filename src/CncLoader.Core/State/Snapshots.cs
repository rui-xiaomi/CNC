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

    /// <summary>读值是否仍在有效期（false=过期，应视为 unknown，勿当最新值参与状态判定）。</summary>
    public bool IsFresh(TimeSpan maxAge) => DateTime.UtcNow - ReadAtUtc <= maxAge;
}

/// <summary>一个加工位的合成状态快照。</summary>
public sealed record PositionStatus
{
    public required long EquipmentId { get; init; }
    public required long PositionId { get; init; }
    public required PositionState State { get; init; }
    /// <summary>当前绑定物料码（调度器透出；无件时为 null）。</summary>
    public string? MaterialId { get; init; }
    /// <summary>当前状态的补充说明；为空时 UI 使用标准状态名称。</summary>
    public string? StatusDetail { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>一台机台的机台级信号快照（门/安全）。</summary>
public sealed record MachineStatus
{
    public required long EquipmentId { get; init; }
    /// <summary>对应 PLC Id（失联监测按此告警；一机一 PLC）。</summary>
    public long PlcId { get; init; }
    /// <summary>门是否打开（DOOR=ON 视为开门）。</summary>
    public bool? DoorOpen { get; init; }
    /// <summary>机台是否安全（MACHINE_SAFE=ON）。</summary>
    public bool? Safe { get; init; }
    /// <summary>对应 PLC 是否在线。</summary>
    public bool PlcOnline { get; init; }
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>机台快照是否仍在有效期（false=轮询已停摆或链路已断，应按离线处理）。</summary>
    public bool IsFresh(TimeSpan maxAge) => DateTime.UtcNow - UpdatedAtUtc <= maxAge;
}
