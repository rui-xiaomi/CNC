using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// 状态机唯一真相源 <see cref="PositionTransition"/> 的表驱动覆盖：零 fake、零 IO。
/// 这些断言以前只能经调度器 + 10 余个 fake 间接推，或根本推不到。
/// </summary>
[TestFixture]
public sealed class PositionTransitionTests
{
    private static PositionInputs Online(PositionState current) => new()
    {
        Current = current,
        PlcOnline = true,
        Safe = true,
        DoorOpen = false
    };

    // ─── 机台级门优先于位级状态机 ──────────────────────────────────────────

    [TestCase(PositionState.WaitLoad)]
    [TestCase(PositionState.Processing)]
    [TestCase(PositionState.Transporting)]
    [TestCase(PositionState.Alarm)]
    public void PlcOffline_AlwaysOffline_WithoutActions(PositionState current)
    {
        var outcome = PositionTransition.Decide(Online(current) with { PlcOnline = false });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Offline));
        Assert.That(outcome.Actions, Is.Empty, "离线短路不得执行任何副作用");
    }

    [TestCase(false, true, TestName = "机台不安全")]
    [TestCase(true, false, TestName = "开门")]
    public void UnsafeOrDoorOpen_WhenRunning_AlarmsWithoutAlarmPackage(bool safe, bool doorClosed)
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Processing) with
        {
            Safe = safe,
            DoorOpen = !doorClosed
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Alarm));
        Assert.That(outcome.Actions, Is.Empty,
            "安全跳闸当轮不落告警包；粘滞的 Alarm 态在信号恢复后补落，避免每 tick 重复回滚");
    }

    [Test]
    public void UnsafeWhileStillOffline_StaysOffline_NoFalseLatch()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Offline) with { Safe = false });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Offline),
            "启动未就绪时机台尚未上报安全信号，不得误 latch 粘滞告警");
    }

    // ─── Alarm 粘滞 ────────────────────────────────────────────────────────

    [TestCase(null)]
    [TestCase(RcsTaskState.Completed)]
    [TestCase(RcsTaskState.Dispatched)]
    [TestCase(RcsTaskState.Failed)]
    public void Alarm_IsLatched_RegardlessOfSignalsOrRcsState(string? rcsState)
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Alarm) with
        {
            RcsState = rcsState,
            HasMat = false,
            AllowLoad = true,
            Ok = true,
            AlarmAlreadyRaised = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Alarm),
            "Alarm 只能经人工 ResetAlarm 退出，状态机不自动离开");
        Assert.That(outcome.Actions, Is.Empty, "粘滞期间不重复落告警包");
    }

    [Test]
    public void Alarm_FirstEntry_EmitsAlarmPackageOnce()
    {
        var first = PositionTransition.Decide(Online(PositionState.Alarm) with { AlarmAlreadyRaised = false });
        var second = PositionTransition.Decide(Online(PositionState.Alarm) with { AlarmAlreadyRaised = true });

        Assert.That(Kinds(first), Is.EqualTo(new[] { PositionActionKind.RaiseAlarm }));
        Assert.That(second.Actions, Is.Empty);
    }

    [Test]
    public void DispatchingOrTransporting_RcsCanceled_GoesAlarm()
    {
        foreach (var from in new[] { PositionState.Dispatching, PositionState.Transporting })
        {
            var outcome = PositionTransition.Decide(Online(from) with
            {
                RcsState = RcsTaskState.Canceled,
                AlarmAlreadyRaised = false
            });
            Assert.That(outcome.Target, Is.EqualTo(PositionState.Alarm), $"{from} + CANCELED 应报警");
            Assert.That(Kinds(outcome), Does.Contain(PositionActionKind.RaiseAlarm));
        }
    }

    // ─── Dispatching / Transporting 的 RCS 映射 ────────────────────────────

    [TestCase(RcsTaskState.Dispatched, PositionState.Transporting)]
    [TestCase(RcsTaskState.Completed, PositionState.Transporting)]
    [TestCase(RcsTaskState.Failed, PositionState.Dispatching)]
    [TestCase(null, PositionState.Dispatching)]
    public void Dispatching_MapsRcsState(string? rcsState, PositionState expected)
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Dispatching) with
        {
            RcsState = rcsState,
            Phase = PositionPhase.Upload
        });

        Assert.That(outcome.Target, Is.EqualTo(expected));
    }

    // ─── HasMat 复核（未知 fail-closed 的入口） ────────────────────────────

    [Test]
    public void Transporting_RcsCompleted_WithKnownPhase_EmitsFreshRecheck()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Transporting) with
        {
            RcsState = RcsTaskState.Completed,
            Phase = PositionPhase.Upload,
            HasCurrentTask = true
        });

        Assert.That(Kinds(outcome), Is.EqualTo(new[] { PositionActionKind.RecheckHasMatFresh }),
            "COMPLETED 必须现读 PLC 复核，不得只凭信号仓放行");
        Assert.That(outcome.Target, Is.EqualTo(PositionState.Transporting),
            "落点由复核结果覆盖，默认保持 TRANSPORTING（fail-closed）");
    }

    [Test]
    public void Transporting_RcsCompleted_PhaseUnknown_AlarmsWithReason()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Transporting) with
        {
            RcsState = RcsTaskState.Completed,
            Phase = null
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Alarm));
        Assert.That(outcome.AlarmReason, Is.EqualTo("RCS 报完成但任务阶段未知"));
        Assert.That(Kinds(outcome), Does.Not.Contain(PositionActionKind.RecheckHasMatFresh));
    }

    [TestCase(null, PositionPhase.Upload, "HasMat 连续复核未知达到阈值 5")]
    [TestCase(false, PositionPhase.Upload, "RCS 报完成但 PLC 明确无料（复核不过）")]
    [TestCase(true, PositionPhase.Unload, "RCS 报完成但 PLC 明确仍有料（复核不过）")]
    public void HasMatAlarmReason_DistinguishesUnknownFromMismatch(
        bool? fresh, PositionPhase phase, string expected)
    {
        Assert.That(PositionTransition.HasMatAlarmReason(fresh, phase, 5), Is.EqualTo(expected));
    }

    // ─── Loaded / Unloaded：写 PLC 失败即中止后续落账 ──────────────────────

    [Test]
    public void Loaded_SettlesSlotBeforeWriteTestStart_AndFallsToAlarmOnFailure()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Loaded) with
        {
            HasCurrentTask = true,
            Phase = PositionPhase.Upload
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Processing));
        Assert.That(Kinds(outcome), Is.EqualTo(new[]
        {
            PositionActionKind.ConfirmTake,
            PositionActionKind.WriteTestStart,
            PositionActionKind.RecordWorkStart
        }), "取料落账必须先于写启动（写启动失败时账目已落账，告警回滚才是 no-op）");
        Assert.That(outcome.Actions[1].TestStartValue, Is.EqualTo(1));
        Assert.That(outcome.OnActionFailure, Is.EqualTo(PositionState.Alarm));
    }

    [Test]
    public void Loaded_WithoutCurrentTask_SkipsSlotSettlement()
    {
        // 工序间交接件无取料预记
        var outcome = PositionTransition.Decide(Online(PositionState.Loaded) with { HasCurrentTask = false });

        Assert.That(Kinds(outcome), Is.EqualTo(new[]
        {
            PositionActionKind.WriteTestStart,
            PositionActionKind.RecordWorkStart
        }));
    }

    [Test]
    public void Unloaded_SettlesThenResetsThenClearsItem()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Unloaded) with
        {
            HasCurrentTask = true,
            Phase = PositionPhase.Unload
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.WaitLoad));
        Assert.That(Kinds(outcome), Is.EqualTo(new[]
        {
            PositionActionKind.ConfirmPut,
            PositionActionKind.WriteTestStart,
            PositionActionKind.ClearCurrentItem
        }));
        Assert.That(outcome.Actions[1].TestStartValue, Is.EqualTo(2));
        Assert.That(outcome.OnActionFailure, Is.EqualTo(PositionState.Alarm));
    }

    // ─── Processing → Done*，Done* → 下料入队 ──────────────────────────────

    [Test]
    public void Processing_NoResultYet_StaysProcessing()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Processing) with
        {
            Ok = false, Ng = false, Phase = PositionPhase.Upload, HasCurrentTask = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Processing));
        Assert.That(outcome.Actions, Is.Empty);
    }

    [TestCase(true, false, true, TestName = "OK 出结果同 tick 入下料队")]
    [TestCase(false, true, false, TestName = "NG 出结果同 tick 入下料队")]
    public void Processing_ResultSignal_EnqueuesUnloadInSameTick(bool ok, bool ng, bool expectOk)
    {
        // Processing → Done* → 入下料队 → Dispatching 在同一 tick 内走完
        var outcome = PositionTransition.Decide(Online(PositionState.Processing) with
        {
            Ok = ok, Ng = ng, Phase = PositionPhase.Upload, HasCurrentTask = true,
            HasOpenWorkRecord = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Dispatching));
        Assert.That(Kinds(outcome), Is.EqualTo(new[]
        {
            PositionActionKind.RecordWorkResult,
            PositionActionKind.EnqueueUnload
        }));
        Assert.That(outcome.Actions[0].IsOk, Is.EqualTo(expectOk), "加工结果须与信号一致");
        Assert.That(outcome.Actions[1].IsOk, Is.EqualTo(expectOk), "下料分流须与结果一致（NG 走 NG 架）");
    }

    [TestCase(PositionState.DoneOk, true)]
    [TestCase(PositionState.DoneNg, false)]
    public void Done_EnqueuesUnloadWithResult_AndFallsToAlarmOnFailure(PositionState done, bool expectOk)
    {
        var outcome = PositionTransition.Decide(Online(done) with
        {
            Phase = PositionPhase.Upload,
            HasOpenWorkRecord = true,
            HasCurrentTask = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Dispatching));
        Assert.That(Kinds(outcome), Is.EqualTo(new[]
        {
            PositionActionKind.RecordWorkResult,
            PositionActionKind.EnqueueUnload
        }), "先写加工结果再入下料队");
        Assert.That(outcome.Actions[0].IsOk, Is.EqualTo(expectOk));
        Assert.That(outcome.Actions[1].IsOk, Is.EqualTo(expectOk));
        Assert.That(outcome.OnActionFailure, Is.EqualTo(PositionState.Alarm));
    }

    [Test]
    public void Done_WhenAutoDispatchPaused_KeepsDoneAndClearsWorkRecord()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.DoneOk) with
        {
            Phase = PositionPhase.Upload,
            HasOpenWorkRecord = true,
            AutoDispatchPaused = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.DoneOk),
            "暂停派工时保持 Done*，恢复后再入下料队");
        Assert.That(Kinds(outcome), Is.EqualTo(new[]
        {
            PositionActionKind.RecordWorkResult,
            PositionActionKind.ClearWorkRecord
        }), "结果仍要落，但清 WorkRecordId 防每 tick 重复写");
        Assert.That(Kinds(outcome), Does.Not.Contain(PositionActionKind.EnqueueUnload));
    }

    [Test]
    public void Done_WhenAlreadyUnloading_DoesNothing()
    {
        // 阶段已是 Unload 说明本件的下料已触发过，不得重复入队
        var outcome = PositionTransition.Decide(Online(PositionState.DoneOk) with
        {
            Phase = PositionPhase.Unload,
            HasOpenWorkRecord = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.DoneOk));
        Assert.That(outcome.Actions, Is.Empty);
    }

    // ─── WaitLoad：直送交接与请求上料 ──────────────────────────────────────

    [Test]
    public void WaitLoad_MaterialPresentWithDispatchedHandoff_ConsumesAndGoesLoaded()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.WaitLoad) with
        {
            HasMat = true,
            InboundPresent = true,
            InboundDispatched = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.Loaded));
        Assert.That(Kinds(outcome), Is.EqualTo(new[] { PositionActionKind.ConsumeInboundHandoff }));
    }

    [Test]
    public void WaitLoad_PendingHandoffNotYetDispatched_MustNotBeConsumed()
    {
        // ADR-0002：预登记先为 Pending，RCS 下发成功后才转 Dispatched，目标工位不得提前消费
        var outcome = PositionTransition.Decide(Online(PositionState.WaitLoad) with
        {
            HasMat = true,
            InboundPresent = true,
            InboundDispatched = false
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.WaitLoad));
        Assert.That(outcome.Actions, Is.Empty);
    }

    [Test]
    public void WaitLoad_NoMaterialWithHandoff_ChecksStaleness()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.WaitLoad) with
        {
            HasMat = false,
            AllowLoad = true,
            InboundPresent = true,
            InboundDispatched = true
        });

        Assert.That(Kinds(outcome), Is.EqualTo(new[]
        {
            PositionActionKind.ReclaimStaleInboundHandoff,
            PositionActionKind.RequestUploadIfNoInbound
        }), "先判陈旧回收，再（在登记确实已清时）请求上料");
    }

    [Test]
    public void WaitLoad_IdleAndAllowed_RequestsUpload()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.WaitLoad) with
        {
            HasMat = false, AllowLoad = true
        });

        Assert.That(Kinds(outcome), Is.EqualTo(new[] { PositionActionKind.RequestUploadIfNoInbound }));
    }

    [TestCase(null, true, false, false, TestName = "HasMat 未知不得请求上料")]
    [TestCase(true, true, false, false, TestName = "已有料不请求上料")]
    [TestCase(false, false, false, false, TestName = "不允许上料时不请求")]
    [TestCase(false, true, true, false, TestName = "已有在途任务不请求")]
    [TestCase(false, true, false, true, TestName = "暂停自动派工时不请求")]
    public void WaitLoad_DoesNotRequestUpload(bool? hasMat, bool allowLoad, bool hasTask, bool paused)
    {
        var outcome = PositionTransition.Decide(Online(PositionState.WaitLoad) with
        {
            HasMat = hasMat,
            AllowLoad = allowLoad,
            HasCurrentTask = hasTask,
            AutoDispatchPaused = paused
        });

        Assert.That(Kinds(outcome), Does.Not.Contain(PositionActionKind.RequestUploadIfNoInbound));
    }

    [Test]
    public void WaitLoad_MaterialPresentWithoutHandoff_StaysWaitingForReconcileOrOperator()
    {
        // §6.3 账实不符：重启后 PLC 有料但无任务，保持 WaitLoad 等对账/人工，不擅自请求上料
        var outcome = PositionTransition.Decide(Online(PositionState.WaitLoad) with
        {
            HasMat = true, AllowLoad = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.WaitLoad));
        Assert.That(outcome.Actions, Is.Empty);
    }

    // ─── 陈旧直送登记回收 ──────────────────────────────────────────────────

    [TestCase(RcsTaskState.Failed, "STALE:FAILED")]
    [TestCase(RcsTaskState.Canceled, "STALE:CANCELED")]
    public void InboundReclaim_SourceAbandoned_ClearsImmediately(string state, string expectedReason)
    {
        var decision = PositionTransition.DecideInboundReclaim(false, state, TimeSpan.Zero);

        Assert.That(decision.Kind, Is.EqualTo(InboundReclaimKind.Clear));
        Assert.That(decision.Reason, Is.EqualTo(expectedReason));
    }

    [Test]
    public void InboundReclaim_SourceRowMissing_ClearsImmediately()
    {
        var decision = PositionTransition.DecideInboundReclaim(true, null, TimeSpan.Zero);

        Assert.That(decision.Kind, Is.EqualTo(InboundReclaimKind.Clear));
        Assert.That(decision.Reason, Is.EqualTo("STALE:missing"));
    }

    [Test]
    public void InboundReclaim_SourceCompleted_NeverClears_OnlyWarnsAfterGrace()
    {
        var within = PositionTransition.DecideInboundReclaim(
            false, RcsTaskState.Completed, PositionTransition.InboundHandoffCompletedGrace);
        var beyond = PositionTransition.DecideInboundReclaim(
            false, RcsTaskState.Completed,
            PositionTransition.InboundHandoffCompletedGrace + TimeSpan.FromMinutes(1));

        Assert.That(within.Kind, Is.EqualTo(InboundReclaimKind.Keep));
        Assert.That(beyond.Kind, Is.EqualTo(InboundReclaimKind.WarnOverdue),
            "COMPLETED 只等 PLC，超宽限仅告警不清登记（防误退回中转丢件）");
    }

    [Test]
    public void InboundReclaim_SourceInFlight_ClearsOnlyAfterTtl()
    {
        var within = PositionTransition.DecideInboundReclaim(
            false, RcsTaskState.Dispatched, PositionTransition.InboundHandoffTtl);
        var beyond = PositionTransition.DecideInboundReclaim(
            false, RcsTaskState.Dispatched,
            PositionTransition.InboundHandoffTtl + TimeSpan.FromMinutes(1));

        Assert.That(within.Kind, Is.EqualTo(InboundReclaimKind.Keep));
        Assert.That(beyond.Kind, Is.EqualTo(InboundReclaimKind.Clear));
        Assert.That(beyond.Reason, Is.EqualTo("TTL"));
    }

    // ─── Offline 复位 ──────────────────────────────────────────────────────

    [Test]
    public void Offline_WhenBackOnlineAndSafe_ReturnsToWaitLoad()
    {
        var outcome = PositionTransition.Decide(Online(PositionState.Offline) with
        {
            HasMat = false, AllowLoad = true
        });

        Assert.That(outcome.Target, Is.EqualTo(PositionState.WaitLoad));
    }

    private static PositionActionKind[] Kinds(TransitionOutcome outcome)
        => outcome.Actions.Select(a => a.Kind).ToArray();
}
