using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>告警事件：通信失败等写入 MAS_AUTO_ALARM_EVENT。</summary>
public interface IAlarmEventService
{
    event EventHandler<AlarmRow>? AlarmRaised;

    Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default);

    /// <summary>RCS warnCallback 严重告警落库（ALARM_TYPE=RCS_WARN，级别默认严重）。返回新建告警行 Id。</summary>
    Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent,
        string? taskCode, CancellationToken ct = default);

    /// <summary>RCS 任务取消 → 人工工单告警（ALARM_TYPE=RCS_CANCELED，级别严重，含 taskId 供 UI 联动）。</summary>
    Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>RCS 查无此任务告警（ALARM_TYPE=RCS_NOT_FOUND，级别警告）——轮询发现 RCS 侧无此 taskId 时人工介入。</summary>
    Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, CancellationToken ct = default);

    /// <summary>RCS 任务自动重做已达上限告警（ALARM_TYPE=RCS_REDO_LIMIT，级别严重）——需人工介入。</summary>
    Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default);

    Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default);
}
