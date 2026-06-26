namespace CncLoader.Core.State;

/// <summary>
/// 线程安全、可观察的信号状态仓（单一数据源）。中央轮询中枢写入，各页/状态机订阅。
/// </summary>
public interface ISignalStateStore
{
    /// <summary>任一加工位状态变化时触发。</summary>
    event EventHandler<PositionStatus>? PositionChanged;
    /// <summary>任一机台级状态变化时触发。</summary>
    event EventHandler<MachineStatus>? MachineChanged;

    void UpdatePosition(PositionStatus status);
    void UpdateMachine(MachineStatus status);
    void UpdateReading(long equipmentId, SignalReading reading);

    PositionStatus? GetPosition(long equipmentId, long positionId);
    MachineStatus? GetMachine(long equipmentId);
    IReadOnlyCollection<PositionStatus> GetAllPositions();
    IReadOnlyCollection<MachineStatus> GetAllMachines();
    /// <summary>取某机台的全部最新信号读值（按 信号+加工位 唯一）。</summary>
    IReadOnlyCollection<SignalReading> GetReadings(long equipmentId);
}
