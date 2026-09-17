using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Communication.State;

/// <summary>加工位可变运行态。主循环与派工回填须经 per-position gate 串行读写。</summary>
internal sealed class PositionContext
{
    public long EquipmentId { get; init; }
    public long PositionId { get; init; }
    public PositionState State { get; set; } = PositionState.Offline;
    public string? CurrentTaskId { get; set; }
    public PositionPhase? Phase { get; set; }
    public long WorkRecordId { get; set; }
    public bool AlarmRaised { get; set; }
    public HasMatRecheckTracker HasMatRecheck { get; } = new();
    public string? StatusDetail { get; set; }
    public string? AlarmReason { get; set; }
    public string? MaterialId { get; set; }
    public bool UploadRequested { get; set; }
    public DateTime? WaitLoadSince { get; set; }
    public DateTime? StateEnteredAt { get; set; }
    public DateTime? TaskBoundAt { get; set; }
    public bool? LastTestOk { get; set; }
}

/// <summary>上料入队决策：入队 / 料架无料等待 / 失败告警。</summary>
internal enum UploadDecision { Queued, WaitMaterial, Failed }

internal readonly record struct UploadPlan(UploadDecision Decision, long? SourceFrameId, string? From, string? To);

/// <summary>下料终点决策：终点 cell + 终点类型 + 目标料架/机台工位。</summary>
internal readonly record struct UnloadDecision(
    string ToCell, UnloadTarget Target, long? DestFrameId, long? DestEquipmentId, long? DestPositionId);

internal sealed record UnloadReservation(ReservedSlot? Slot = null, string? SlotCell = null);

/// <summary>工序间直接交接登记：上游把 OK 件送入下游 cell 后，下游见料即接。</summary>
internal sealed record InboundHandoff(
    string? SourceTaskId,
    string? MaterialId,
    DateTime CreatedUtc,
    bool IsDispatched = true);

/// <summary>待目标工位闸内执行的交接清理请求。</summary>
internal sealed record InboundClearRequest(string SourceTaskId, string Reason);

/// <summary>动作执行结果：是否成功，以及是否由该动作接管落点状态。</summary>
internal readonly record struct ActionResult(bool Succeeded, PositionState? NextOverride)
{
    public static readonly ActionResult Ok = new(true, null);
    public static readonly ActionResult Failed = new(false, null);
    public static ActionResult MoveTo(PositionState state) => new(true, state);
}
