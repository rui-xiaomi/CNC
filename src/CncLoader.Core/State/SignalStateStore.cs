using System.Collections.Concurrent;
using CncLoader.Core.Signals;

namespace CncLoader.Core.State;

/// <summary>
/// 基于 ConcurrentDictionary 的线程安全状态仓实现。变化时发布事件（观察者模式）。
/// </summary>
public sealed class SignalStateStore : ISignalStateStore
{
    private readonly ConcurrentDictionary<(long Eq, long Pos), PositionStatus> _positions = new();
    private readonly ConcurrentDictionary<long, MachineStatus> _machines = new();
    private readonly ConcurrentDictionary<(long Eq, SignalKey Sig, long Pos), SignalReading> _readings = new();

    public event EventHandler<PositionStatus>? PositionChanged;
    public event EventHandler<MachineStatus>? MachineChanged;

    public void UpdatePosition(PositionStatus status)
    {
        var key = (status.EquipmentId, status.PositionId);
        var prev = _positions.TryGetValue(key, out var p) ? p : null;
        _positions[key] = status;
        if (prev is null || prev.State != status.State)
            PositionChanged?.Invoke(this, status);
    }

    public void UpdateMachine(MachineStatus status)
    {
        var prev = _machines.TryGetValue(status.EquipmentId, out var m) ? m : null;
        _machines[status.EquipmentId] = status;
        if (prev is null || prev.DoorOpen != status.DoorOpen || prev.Safe != status.Safe || prev.PlcOnline != status.PlcOnline)
            MachineChanged?.Invoke(this, status);
    }

    public void UpdateReading(long equipmentId, SignalReading reading)
    {
        var key = (equipmentId, reading.Signal, reading.PositionId ?? 0L);
        _readings[key] = reading;
    }

    public PositionStatus? GetPosition(long equipmentId, long positionId) =>
        _positions.TryGetValue((equipmentId, positionId), out var s) ? s : null;

    public MachineStatus? GetMachine(long equipmentId) =>
        _machines.TryGetValue(equipmentId, out var m) ? m : null;

    public IReadOnlyCollection<PositionStatus> GetAllPositions() => _positions.Values.ToArray();

    public IReadOnlyCollection<MachineStatus> GetAllMachines() => _machines.Values.ToArray();

    public IReadOnlyCollection<SignalReading> GetReadings(long equipmentId) =>
        _readings.Where(kv => kv.Key.Eq == equipmentId).Select(kv => kv.Value).ToArray();
}
