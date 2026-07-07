namespace CncLoader.Core.Rcs;

/// <summary>
/// 加工记录服务（第四阶段⑦）：加工位 LOADED→PROCESSING 时记开始，PROCESSING→DONE_OK/NG 时记结果，
/// 写 MAS_AUTO_WORK_RECORD（关联任务/工件/加工位/耗时），供监控看板联动与产量统计。
/// </summary>
public interface IWorkRecordService
{
    /// <summary>加工开始：写 WORK_RECORD 一行（WORK_START_TIME=now，WORK_RESULT 空/进行中）。返回记录主键。
    /// 同一加工位若有未结束记录则先补结（防重启残留）。</summary>
    Task<long> RecordStartAsync(WorkRecordStartArgs args, CancellationToken ct = default);

    /// <summary>加工结束：按记录 Id 写 WORK_RESULT(0=OK/1=NG)+WORK_END_TIME+REMARK。耗时由 start/end 算。</summary>
    Task RecordResultAsync(long recordId, string result, string? remark, CancellationToken ct = default);

    /// <summary>按加工位找最近一条未结束记录（PROCESSING 中的），用于调度器关联。</summary>
    Task<WorkRecordRow?> FindOpenByPositionAsync(long equipmentId, long positionId, CancellationToken ct = default);

    /// <summary>最近 N 条加工记录（倒序），供监控看板/加工记录页。</summary>
    Task<IReadOnlyList<WorkRecordRow>> GetRecentAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>当班统计：OK 数 / NG 数 / 总数（基于今天 WORK_START_TIME）。</summary>
    Task<WorkShiftStats> GetShiftStatsAsync(CancellationToken ct = default);
}

/// <summary>加工开始入参。</summary>
public sealed record WorkRecordStartArgs
{
    public required long EquipmentId { get; init; }
    public required long PositionId { get; init; }
    public required string PositionCode { get; init; }
    public long WorkLineId { get; init; } = 1;
    public long? CraftworkId { get; init; }
    public string? ElectrodeId { get; init; }
    public string? MaterialCode { get; init; }
    /// <summary>关联 RCS 任务 ID（上料任务，溯源用）。</summary>
    public string? RcsTaskId { get; init; }
    public string? Author { get; init; }
}

/// <summary>加工记录展示行。</summary>
public sealed record WorkRecordRow(
    long Id,
    long EquipmentId,
    string PositionCode,
    string? ElectrodeId,
    DateTime? WorkStartTime,
    DateTime? WorkEndTime,
    string WorkResult,    // "" 进行中 / "0" OK / "1" NG / "2" 异常
    string? Remark,
    int? ElapsedSeconds);

/// <summary>当班统计。</summary>
public sealed record WorkShiftStats(int Ok, int Ng, int Total);
