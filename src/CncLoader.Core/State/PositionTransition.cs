using CncLoader.Core.Rcs;

namespace CncLoader.Core.State;

/// <summary>
/// 加工位状态机的唯一决策面（文档 §7）：给定一次 tick 的输入快照，产出目标状态 + 有序动作清单。
/// 本类型不做任何 IO，也不持有可变状态——所有 PLC 读写、落账、告警、入队都由调用方按动作清单执行。
/// 这样「什么条件转什么态」只有这一处真相源，可用表驱动测试穷举；
/// 调度器只负责把动作翻译成 IO，不再自己决定状态。
/// </summary>
public static class PositionTransition
{
    /// <summary>工序间直送登记的存活上限：源任务非终态且超时即回收登记。</summary>
    public static readonly TimeSpan InboundHandoffTtl = TimeSpan.FromMinutes(10);

    /// <summary>源任务已 COMPLETED 后继续等 PLC 见料的宽限；超时只告警不清登记（防误退回中转）。</summary>
    public static readonly TimeSpan InboundHandoffCompletedGrace = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 推进一个加工位。机台级门（离线 / 不安全 / 开门）优先于位级状态机，且不触发任何动作。
    /// </summary>
    public static TransitionOutcome Decide(in PositionInputs input)
        => DecideMachineGate(input.PlcOnline, input.Safe, input.DoorOpen, input.Current) is PositionState gated
            ? TransitionOutcome.To(gated)
            : BuildOutcome(input, NextState(input));

    /// <summary>
    /// 机台级门：命中则整个位级状态机短路，且不执行任何动作。返回 null 表示放行到位级状态机。
    /// 调用方可先问这一步以跳过后续的 RCS 状态查询。
    /// </summary>
    public static PositionState? DecideMachineGate(
        bool plcOnline, bool? safe, bool doorOpen, PositionState current)
    {
        // 通信断 → 离线（最高优先，无法判定任何信号）
        if (!plcOnline) return PositionState.Offline;

        // 不安全/开门：
        //   已投入运行（非 Offline）→ 报警（安全底线：运行中安全掉线必须停下）；
        //   仍处 Offline（启动/未就绪，机台尚未上报安全信号）→ 保持 Offline 等就绪，不误 latch 粘滞告警。
        // 两者都不执行 Alarm 动作包：安全信号恢复后由粘滞的 Alarm 态补落告警与回滚。
        if (safe == false || doorOpen)
            return current == PositionState.Offline ? PositionState.Offline : PositionState.Alarm;

        return null;
    }

    /// <summary>
    /// 陈旧直送登记的回收决策。源任务状态需调用方现读，故与 <see cref="Decide"/> 分两段。
    /// </summary>
    public static InboundReclaim DecideInboundReclaim(
        bool sourceRowMissing, string? sourceTaskState, TimeSpan age)
    {
        // FAILED/CANCELED/缺失 → 清并回收
        if (sourceRowMissing || sourceTaskState is RcsTaskState.Failed or RcsTaskState.Canceled)
            return new InboundReclaim(InboundReclaimKind.Clear, $"STALE:{sourceTaskState ?? "missing"}");

        // COMPLETED 只等 PLC，超宽限仅告警不清（防误退回中转）
        if (sourceTaskState == RcsTaskState.Completed)
            return age > InboundHandoffCompletedGrace
                ? new InboundReclaim(InboundReclaimKind.WarnOverdue, null)
                : InboundReclaim.Keep;

        // 非终态超 TTL → 清
        return age > InboundHandoffTtl
            ? new InboundReclaim(InboundReclaimKind.Clear, "TTL")
            : InboundReclaim.Keep;
    }

    /// <summary>HasMat 复核不过时的告警归因文案（阈值耗尽 / 方向不符）。</summary>
    public static string HasMatAlarmReason(bool? freshHasMat, PositionPhase phase, int threshold)
        => freshHasMat is null
            ? $"HasMat 连续复核未知达到阈值 {threshold}"
            : phase == PositionPhase.Upload
                ? "RCS 报完成但 PLC 明确无料（复核不过）"
                : "RCS 报完成但 PLC 明确仍有料（复核不过）";

    /// <summary>候选目标态：只看当前态与输入信号，副作用与二段判定交给 <see cref="BuildOutcome"/>。</summary>
    private static PositionState NextState(in PositionInputs i)
    {
        switch (i.Current)
        {
            case PositionState.Offline:
                return PositionState.WaitLoad;
            case PositionState.Alarm:
                // Alarm 粘滞：只能经人工 ResetAlarm 恢复，状态机不自动离开（安全底线）
                return PositionState.Alarm;
            case PositionState.WaitLoad:
                // 有料且未启动 → 可能是重启后 PLC 有料但无任务（§6.3 账实不符），保持 WaitLoad 等对账/人工。
                // 转 Dispatching 由动作清单（请求上料 → 单一调度消费者下发）间接完成。
                return PositionState.WaitLoad;
            case PositionState.Dispatching:
                if (i.RcsState == RcsTaskState.Canceled) return PositionState.Alarm;
                if (i.RcsState == RcsTaskState.Failed) return PositionState.Dispatching; // tracker 自动 redo
                if (i.RcsState == RcsTaskState.Completed) return PositionState.Transporting; // 复核见动作清单
                if (i.RcsState == RcsTaskState.Dispatched) return PositionState.Transporting;
                return PositionState.Dispatching;
            case PositionState.Transporting:
                if (i.RcsState == RcsTaskState.Canceled) return PositionState.Alarm;
                // COMPLETED → 复核交动作清单（需 fresh PLC 读，避免信号仓滞后）
                if (i.RcsState == RcsTaskState.Failed) return PositionState.Transporting; // redo
                return PositionState.Transporting;
            case PositionState.Loaded:
                return PositionState.Loaded; // 写启动成功后转 Processing，见动作清单
            case PositionState.Processing:
                if (i.Ok == true) return PositionState.DoneOk;
                if (i.Ng == true) return PositionState.DoneNg;
                return PositionState.Processing;
            case PositionState.DoneOk:
            case PositionState.DoneNg:
                return i.Current; // 入下料队后转 Dispatching，见动作清单
            case PositionState.Unloaded:
                return PositionState.Unloaded; // 写复位后转 WaitLoad，见动作清单
            default:
                return PositionState.WaitLoad;
        }
    }

    private static TransitionOutcome BuildOutcome(in PositionInputs i, PositionState next)
    {
        switch (next)
        {
            case PositionState.WaitLoad:
                return WaitLoadOutcome(i);

            case PositionState.Transporting:
                if (i.RcsState != RcsTaskState.Completed) return TransitionOutcome.To(next);
                if (i.Phase is null)
                {
                    return TransitionOutcome.To(PositionState.Alarm)
                        with { AlarmReason = "RCS 报完成但任务阶段未知" };
                }
                // 目标态由 fresh 复核结果决定（Hold 留 Transporting / Confirmed 进 Loaded|Unloaded / Alarm）
                return new TransitionOutcome
                {
                    Target = PositionState.Transporting,
                    Actions = new[] { new PositionAction(PositionActionKind.RecheckHasMatFresh) }
                };

            case PositionState.Loaded:
            {
                // 上料到位：先源料架取料落账（物料已到位），再写启动、开加工记录。
                // 落账先于写启动：写启动失败时物料已在机台，账目已落账，告警回滚才是 no-op（P1-6）。
                var actions = new List<PositionAction>();
                if (i.HasCurrentTask) actions.Add(new PositionAction(PositionActionKind.ConfirmTake));
                actions.Add(PositionAction.WriteTestStart(1));
                actions.Add(new PositionAction(PositionActionKind.RecordWorkStart));
                return new TransitionOutcome
                {
                    Target = PositionState.Processing,
                    Actions = actions,
                    OnActionFailure = PositionState.Alarm
                };
            }

            case PositionState.Unloaded:
            {
                // 下料到位：先入库料架落账（件已到料架），再写复位、清件。
                // 落账先于写复位：写复位失败时件已在料架，账目已落账，告警回滚才是 no-op（P1-6）。
                var actions = new List<PositionAction>();
                if (i.HasCurrentTask) actions.Add(new PositionAction(PositionActionKind.ConfirmPut));
                actions.Add(PositionAction.WriteTestStart(2));
                actions.Add(new PositionAction(PositionActionKind.ClearCurrentItem));
                return new TransitionOutcome
                {
                    Target = PositionState.WaitLoad,
                    Actions = actions,
                    OnActionFailure = PositionState.Alarm
                };
            }

            case PositionState.Alarm:
                // 首次进 Alarm 才落告警包（回滚预记 + 清交接 + 落库）；粘滞期间不重复
                return i.AlarmAlreadyRaised
                    ? TransitionOutcome.To(PositionState.Alarm)
                    : new TransitionOutcome
                    {
                        Target = PositionState.Alarm,
                        Actions = new[] { new PositionAction(PositionActionKind.RaiseAlarm) }
                    };

            case PositionState.DoneOk:
            case PositionState.DoneNg:
                return DoneOutcome(i, next);

            default:
                return TransitionOutcome.To(next);
        }
    }

    private static TransitionOutcome WaitLoadOutcome(in PositionInputs i)
    {
        // 工序间直接交接：上游 OK 件已送入本工位 cell，PLC 见料 + 有已下发的待入库登记 → 直接进 Loaded
        if (i.HasMat == true && i.InboundPresent && i.InboundDispatched)
        {
            return new TransitionOutcome
            {
                Target = PositionState.Loaded,
                Actions = new[] { new PositionAction(PositionActionKind.ConsumeInboundHandoff) }
            };
        }

        var actions = new List<PositionAction>();

        // 无料但有在途登记 → 现读源任务状态判断是否陈旧（见 DecideInboundReclaim）
        if (i.HasMat != true && i.InboundPresent)
            actions.Add(new PositionAction(PositionActionKind.ReclaimStaleInboundHandoff));

        // Layer 1：工位不自己查料/选槽，只标记"请求上料"，由单一调度消费者统一决策。
        // 有在途直送登记时不请求自取（与中转回流互斥）——该判定放在动作执行时点，
        // 因为上一动作可能刚回收掉陈旧登记。暂停自动派工时不置请求，恢复后下一 tick 再评估。
        if (!i.AutoDispatchPaused && i.AllowLoad == true && i.HasMat == false && !i.HasCurrentTask)
            actions.Add(new PositionAction(PositionActionKind.RequestUploadIfNoInbound));

        return new TransitionOutcome { Target = PositionState.WaitLoad, Actions = actions };
    }

    private static TransitionOutcome DoneOutcome(in PositionInputs i, PositionState next)
    {
        // 出结果 → 下料（仅一次：阶段还是 Upload 时触发）+ 写加工结果
        if (i.Phase != PositionPhase.Upload) return TransitionOutcome.To(next);

        var isOk = next == PositionState.DoneOk;
        var actions = new List<PositionAction>();
        if (i.HasOpenWorkRecord) actions.Add(PositionAction.RecordWorkResult(isOk));

        if (i.AutoDispatchPaused)
        {
            // 保持 Done*，恢复自动派工后再入下料队；清零 WorkRecordId 避免每 tick 重复写结果日志
            if (i.HasOpenWorkRecord) actions.Add(new PositionAction(PositionActionKind.ClearWorkRecord));
            return new TransitionOutcome { Target = next, Actions = actions };
        }

        actions.Add(PositionAction.EnqueueUnload(isOk));
        return new TransitionOutcome
        {
            Target = PositionState.Dispatching,
            Actions = actions,
            OnActionFailure = PositionState.Alarm
        };
    }
}

/// <summary>一次 tick 的加工位输入快照。布尔为 null 表示该信号未知/未读到。</summary>
public readonly record struct PositionInputs
{
    public PositionState Current { get; init; }
    public bool PlcOnline { get; init; }
    /// <summary>机台安全信号；未读到时调用方按 true（不阻塞）传入，与历史行为一致。</summary>
    public bool? Safe { get; init; }
    public bool DoorOpen { get; init; }
    public bool? HasMat { get; init; }
    public bool? AllowLoad { get; init; }
    public bool? Ok { get; init; }
    public bool? Ng { get; init; }
    /// <summary>当前绑定任务的 RCS 状态；无绑定任务时为 null。</summary>
    public string? RcsState { get; init; }
    public PositionPhase? Phase { get; init; }
    public bool HasCurrentTask { get; init; }
    public bool AutoDispatchPaused { get; init; }
    /// <summary>本工位是否有工序间直送登记。</summary>
    public bool InboundPresent { get; init; }
    /// <summary>直送登记是否已 RCS 下发成功（Pending 登记不得被目标工位提前消费，见 ADR-0002）。</summary>
    public bool InboundDispatched { get; init; }
    /// <summary>是否有未结的加工记录（WorkRecordId &gt; 0）。</summary>
    public bool HasOpenWorkRecord { get; init; }
    /// <summary>Alarm 告警包是否已落过（粘滞期间不重复落库）。</summary>
    public bool AlarmAlreadyRaised { get; init; }
}

/// <summary>状态机产出：目标态 + 有序动作清单。</summary>
public readonly record struct TransitionOutcome
{
    public TransitionOutcome()
    {
    }

    public PositionState Target { get; init; }

    public IReadOnlyList<PositionAction> Actions { get; init; } = Array.Empty<PositionAction>();

    /// <summary>
    /// 可失败动作（写 PLC、入下料队）失败时的落点。为 null 表示清单内没有可失败动作。
    /// 动作失败即中止后续动作。
    /// </summary>
    public PositionState? OnActionFailure { get; init; }

    /// <summary>需要写入告警原因时的文案。</summary>
    public string? AlarmReason { get; init; }

    public static TransitionOutcome To(PositionState target) => new() { Target = target };
}

public enum PositionActionKind
{
    /// <summary>取用已下发的直送登记：清登记、绑定源任务与物料码、阶段置上料。</summary>
    ConsumeInboundHandoff,
    /// <summary>现读源任务状态，按 <see cref="PositionTransition.DecideInboundReclaim"/> 回收或告警。</summary>
    ReclaimStaleInboundHandoff,
    /// <summary>若此刻仍无直送登记，则标记"请求上料"。</summary>
    RequestUploadIfNoInbound,
    /// <summary>fresh 读 HasMat 并交复核跟踪器定态（目标态由复核结果覆盖）。</summary>
    RecheckHasMatFresh,
    /// <summary>写 POS_TEST_START（可失败）。</summary>
    WriteTestStart,
    /// <summary>上料取料落账。</summary>
    ConfirmTake,
    /// <summary>下料入库落账。</summary>
    ConfirmPut,
    /// <summary>开加工记录。</summary>
    RecordWorkStart,
    /// <summary>写加工结果。</summary>
    RecordWorkResult,
    /// <summary>清 WorkRecordId（暂停派工时防重复写结果）。</summary>
    ClearWorkRecord,
    /// <summary>清当前件：任务号、阶段、物料码。</summary>
    ClearCurrentItem,
    /// <summary>入下料队（可失败）。</summary>
    EnqueueUnload,
    /// <summary>首次进 Alarm 的告警包：按方向回滚预记、清交接登记、落库告警。</summary>
    RaiseAlarm
}

/// <summary>一个待执行的副作用。</summary>
public readonly record struct PositionAction(
    PositionActionKind Kind,
    int TestStartValue = 0,
    bool IsOk = false)
{
    public static PositionAction WriteTestStart(int value)
        => new(PositionActionKind.WriteTestStart, TestStartValue: value);

    public static PositionAction RecordWorkResult(bool isOk)
        => new(PositionActionKind.RecordWorkResult, IsOk: isOk);

    public static PositionAction EnqueueUnload(bool isOk)
        => new(PositionActionKind.EnqueueUnload, IsOk: isOk);
}

public enum InboundReclaimKind
{
    /// <summary>保留登记继续等。</summary>
    Keep,
    /// <summary>回收登记（物料退回中转架或告警）。</summary>
    Clear,
    /// <summary>源任务已完成但久未见料：只告警，不清登记。</summary>
    WarnOverdue
}

public readonly record struct InboundReclaim(InboundReclaimKind Kind, string? Reason)
{
    public static readonly InboundReclaim Keep = new(InboundReclaimKind.Keep, null);
}
