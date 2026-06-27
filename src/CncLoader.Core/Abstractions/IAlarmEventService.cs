using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>告警事件：通信失败等写入 MAS_AUTO_ALARM_EVENT。</summary>
public interface IAlarmEventService
{
    event EventHandler<AlarmRow>? AlarmRaised;

    Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default);
    Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default);
}
