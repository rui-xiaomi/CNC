using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>告警事件：通信失败等写入 MAS_AUTO_ALARM_EVENT。</summary>
public interface IAlarmEventService
{
    event EventHandler<AlarmRow>? AlarmRaised;

    /// <summary>告警集合变化（标记已处理/全部删除等使未处理数变动时触发），供角标即时刷新。</summary>
    event EventHandler? AlarmsChanged;

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

    /// <summary>告警明细查询（日志/告警页用）。unhandledOnly=true 仅返回未处理（STATE='0'）。</summary>
    Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default);

    /// <summary>标记告警为已处理（STATE='1'）。</summary>
    Task MarkHandledAsync(long id, string author, CancellationToken ct = default);

    /// <summary>物理删除全部告警（清空 MAS_AUTO_ALARM_EVENT）。返回删除行数。不可恢复。</summary>
    Task<int> DeleteAllAsync(CancellationToken ct = default);

    /// <summary>未处理告警数（STATE='0'），供标题栏角标展示。</summary>
    Task<int> GetUnhandledCountAsync(CancellationToken ct = default);
}
