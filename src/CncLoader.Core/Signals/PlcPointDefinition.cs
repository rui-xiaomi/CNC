namespace CncLoader.Core.Signals;

/// <summary>
/// 一条 PLC 点位定义（MAS_AUTO_PLC_POINT 的领域投影）。
/// 信号地址不硬编码，全部来自此映射，新增机台只需新增点位即可纳入轮询。
/// </summary>
public sealed record PlcPointDefinition
{
    public required long PlcId { get; init; }
    public required long EquipmentId { get; init; }
    /// <summary>加工位 ID；机台级信号（门/安全）为 null。</summary>
    public long? PositionId { get; init; }
    public required SignalKey Signal { get; init; }
    /// <summary>true=写，false=读。</summary>
    public required bool IsWrite { get; init; }
    /// <summary>寄存器地址，如 "D1006"（读写主键依据）。</summary>
    public required string RegisterAddress { get; init; }
    /// <summary>IO 位地址，如 "I0.3"（仅展示）。</summary>
    public string? IoAddress { get; init; }
    public int OnValue { get; init; } = SignalConventions.DefaultOnValue;
    public int OffValue { get; init; } = SignalConventions.DefaultOffValue;
    public int DataLength { get; init; } = 1;
}
