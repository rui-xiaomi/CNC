using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using CncLoader.Core.Tests.Routing;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// 动作执行器的副作用顺序契约：跑真实 <c>DrivePositionCoreAsync</c>（机台门 → Decide → 动作执行 → SetState）。
/// 此前这条主推进路径零覆盖，安全红线（写启动失败即 Alarm、失败不落账、安全跳闸不重复回滚）无回归网。
/// </summary>
[TestFixture]
public sealed class PositionDriveExecutorTests
{
    private const long Eq = 30;
    private const long Pos = 1;
    private const long PlcId = 7;
    private const string TestStartAddr = "D2000";
    private const string HasMatAddr = "D1000";

    // ─── 机台级门 ──────────────────────────────────────────────────────────

    [Test]
    public async Task PlcOffline_GoesOffline_WithoutTouchingPlcOrSlots()
    {
        var h = await BuildAsync();
        h.SetMachine(plcOnline: false);
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Processing, "T-1", PositionPhase.Upload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(PositionState.Offline));
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0));
            Assert.That(h.Slots.RollbackCount, Is.EqualTo(0));
            Assert.That(h.Alarms.RaiseCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task MachineUnsafeWhileRunning_AlarmsButDoesNotRollbackOrRaiseThatTick()
    {
        var h = await BuildAsync();
        h.SetMachine(plcOnline: true, safe: false);
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Processing, "T-1", PositionPhase.Upload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);
        var ctx = h.Scheduler.ProbeGetContext(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(PositionState.Alarm), "运行中安全掉线必须停下");
            Assert.That(ctx.AlarmRaised, Is.False, "安全跳闸当轮不落告警包");
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(0));
            Assert.That(h.Alarms.RaiseCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task MachineUnsafeRecovers_ThenAlarmPackageIsRaisedExactlyOnce()
    {
        var h = await BuildAsync();
        h.SetMachine(plcOnline: true, safe: false);
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Processing, "T-1", PositionPhase.Upload);
        await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        // 安全恢复：Alarm 粘滞，此时才补落告警包
        h.SetMachine(plcOnline: true, safe: true);
        await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);
        var afterFirst = h.Alarms.RaiseCount;
        var rollbackAfterFirst = h.Slots.RollbackTakeCount;

        // 再跑两轮：粘滞不得重复告警
        await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);
        await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(afterFirst, Is.EqualTo(1), "恢复后补落一次告警");
            Assert.That(rollbackAfterFirst, Is.EqualTo(1), "上料方向回滚取料预记");
            Assert.That(h.Alarms.RaiseCount, Is.EqualTo(1), "Alarm 粘滞期间不得每 tick 重复告警");
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(1), "也不得重复回滚");
            Assert.That(h.Scheduler.ProbeGetContext(Eq, Pos).State, Is.EqualTo(PositionState.Alarm));
        });
    }

    // ─── 写 POS_TEST_START 失败即中止后续落账 ──────────────────────────────

    [Test]
    public async Task Loaded_TestStartWriteSucceeds_SettlesTakeThenProcessing()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.Plc.WriteVerified = true;
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Loaded, "T-1", PositionPhase.Upload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(PositionState.Processing));
            Assert.That(h.Plc.LastWrittenValue, Is.EqualTo(1), "上料到位写 POS_TEST_START=1");
            Assert.That(h.Slots.ConfirmTakeCount, Is.EqualTo(1), "写启动成功后才取料落账");
        });
    }

    [Test]
    public async Task Loaded_TestStartWriteFails_AlarmsAndMustNotSettleSlot()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.Plc.WriteVerified = false;
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Loaded, "T-1", PositionPhase.Upload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);
        var rollbackSameTick = h.Slots.RollbackTakeCount;

        // 告警包（回滚预记 + 落库）由下一 tick 的粘滞 Alarm 态补落
        await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(PositionState.Alarm));
            Assert.That(h.Slots.ConfirmTakeCount, Is.EqualTo(0),
                "写启动失败必须中止后续动作，绝不能先落账再报警");
            Assert.That(rollbackSameTick, Is.EqualTo(0), "转 Alarm 当轮不落告警包");
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(1), "下一 tick 补落：回滚取料预记");
            Assert.That(h.Alarms.RaiseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Unloaded_TestStartWriteFails_AlarmsAndMustNotSettleSlotOrClearItem()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.Plc.WriteVerified = false;
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Unloaded, "T-2", PositionPhase.Unload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);
        var ctx = h.Scheduler.ProbeGetContext(Eq, Pos);

        // 告警包由下一 tick 的粘滞 Alarm 态补落
        await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(PositionState.Alarm));
            Assert.That(h.Slots.ConfirmPutCount, Is.EqualTo(0));
            Assert.That(ctx.CurrentTaskId, Is.EqualTo("T-2"), "写复位失败不得清件，否则丢账");
            Assert.That(h.Slots.RollbackPutCount, Is.EqualTo(1), "下料方向回滚入库预记");
        });
    }

    [Test]
    public async Task Unloaded_TestStartWriteSucceeds_SettlesPutAndClearsItem()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.Plc.WriteVerified = true;
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Unloaded, "T-2", PositionPhase.Unload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);
        var ctx = h.Scheduler.ProbeGetContext(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(PositionState.WaitLoad));
            Assert.That(h.Plc.LastWrittenValue, Is.EqualTo(2), "下料到位写 POS_TEST_START=2");
            Assert.That(h.Slots.ConfirmPutCount, Is.EqualTo(1));
            Assert.That(ctx.CurrentTaskId, Is.Null, "件已离开本工位");
            Assert.That(ctx.AlarmRaised, Is.False, "回到 WaitLoad 时清报警标记");
        });
    }

    // ─── Transporting + COMPLETED 的 HasMat 复核（fail-closed） ────────────

    [Test]
    public async Task Transporting_Completed_HasMatUnknown_HoldsTransportingNotLoaded()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.Tasks.SetState("T-1", RcsTaskState.Completed);
        h.Plc.HasMatError = "read timeout"; // 读失败 → 未知
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Transporting, "T-1", PositionPhase.Upload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(PositionState.Transporting),
                "HasMat 未知不得当已上料放行（fail-closed）");
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0), "未复核通过不得写 POS_TEST_START");
            Assert.That(h.Slots.ConfirmTakeCount, Is.EqualTo(0), "未复核通过不得落账");
        });
    }

    [Test]
    public async Task Transporting_Completed_HasMatUnknownRepeatedly_EventuallyAlarms()
    {
        var h = await BuildAsync(hasMatRecheckFailThreshold: 3);
        h.SetMachine();
        h.Tasks.SetState("T-1", RcsTaskState.Completed);
        h.Plc.HasMatError = "read timeout";
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Transporting, "T-1", PositionPhase.Upload);

        var states = new List<PositionState>();
        for (var i = 0; i < 3; i++)
            states.Add(await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos));

        Assert.Multiple(() =>
        {
            Assert.That(states[0], Is.EqualTo(PositionState.Transporting));
            Assert.That(states[1], Is.EqualTo(PositionState.Transporting));
            Assert.That(states[2], Is.EqualTo(PositionState.Alarm), "连续未知达阈值转 Alarm");
            Assert.That(h.Plc.WriteCount, Is.EqualTo(0));
            Assert.That(h.Slots.ConfirmTakeCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Transporting_Completed_HasMatConfirmed_ProceedsToLoaded()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.Tasks.SetState("T-1", RcsTaskState.Completed);
        h.Plc.HasMatRawValue = 1; // OnValue=1 → 有料
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Transporting, "T-1", PositionPhase.Upload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.That(state, Is.EqualTo(PositionState.Loaded),
            "复核确认有料才进 LOADED；本 tick 不写启动（下一 tick 由 Loaded 分支写）");
    }

    [Test]
    public async Task Transporting_Completed_HasMatContradicts_AlarmsImmediately()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.Tasks.SetState("T-1", RcsTaskState.Completed);
        h.Plc.HasMatRawValue = 2; // OffValue → 明确无料，与上料完成矛盾
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.Transporting, "T-1", PositionPhase.Upload);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        // 告警包由下一 tick 的粘滞 Alarm 态补落
        await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(state, Is.EqualTo(PositionState.Alarm), "PLC 明确不符即报警，不等阈值");
            Assert.That(h.Slots.ConfirmTakeCount, Is.EqualTo(0), "复核不过绝不落账");
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(1), "回滚取料预记（假完成/未到位）");
        });
    }

    // ─── WaitLoad 请求上料 ────────────────────────────────────────────────

    [Test]
    public async Task WaitLoad_IdleAndAllowed_MarksUploadRequested()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.SetPositionSignals(hasMat: false, allowLoad: true);
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.WaitLoad);

        var state = await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.That(state, Is.EqualTo(PositionState.WaitLoad));
        Assert.That(h.Scheduler.ProbeUploadRequested(Eq, Pos), Is.True);
    }

    [Test]
    public async Task WaitLoad_HasMaterialWithoutTask_DoesNotRequestUpload()
    {
        var h = await BuildAsync();
        h.SetMachine();
        h.SetPositionSignals(hasMat: true, allowLoad: true);
        h.Scheduler.ProbeSetContext(Eq, Pos, PositionState.WaitLoad);

        await h.Scheduler.ProbeDrivePositionOnceAsync(Eq, Pos);

        Assert.That(h.Scheduler.ProbeUploadRequested(Eq, Pos), Is.False,
            "§6.3 账实不符：有料无任务须等对账/人工，不得擅自请求上料");
    }

    // ─── 夹具 ─────────────────────────────────────────────────────────────

    private static async Task<Harness> BuildAsync(int hasMatRecheckFailThreshold = 5)
    {
        var trace = new CallTrace();
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(DispatchGateHarness.LineId, DispatchGateHarness.LineCode,
            DispatchGateHarness.CraftId, 1, Eq);
        store.BindFrame(Eq, DispatchGateHarness.UploadFrame, FrameRole.Upload);

        var equipment = new TracingEquipmentConfigService(store, trace);
        var slots = new TracingSlots(trace, DispatchGateHarness.UploadFrame,
            equipment: equipment, store: store);
        var tasks = new TracingTaskService(trace);
        var plc = new ScriptedPlcOps();
        var alarms = new NoopAlarms();
        var signals = new SignalStateStore();
        var taskStore = new ScriptedTaskStore();

        var scheduler = DispatchGateHarness.CreateScheduler(
            equipment, slots, tasks, plc,
            store: signals, alarms: alarms, routingStore: store,
            points: new SinglePositionPoints(), taskStore: taskStore,
            hasMatRecheckFailThreshold: hasMatRecheckFailThreshold);

        // 装载写点位 / HasMat 点位，动作执行器才能真正走 PLC
        await scheduler.ProbeLoadPositionCacheAsync();

        return new Harness(scheduler, signals, slots, plc, alarms, taskStore);
    }

    private sealed record Harness(
        PositionScheduler Scheduler,
        SignalStateStore Signals,
        TracingSlots Slots,
        ScriptedPlcOps Plc,
        NoopAlarms Alarms,
        ScriptedTaskStore Tasks)
    {
        public void SetMachine(bool plcOnline = true, bool safe = true, bool doorOpen = false)
            => Signals.UpdateMachine(new MachineStatus
            {
                EquipmentId = Eq, PlcOnline = plcOnline, Safe = safe, DoorOpen = doorOpen
            });

        public void SetPositionSignals(bool? hasMat = null, bool? allowLoad = null)
        {
            if (hasMat is not null)
                Signals.UpdateReading(Eq, new SignalReading
                {
                    Signal = SignalKey.PosHasMat, PositionId = Pos,
                    RegisterAddress = HasMatAddr, RawValue = hasMat.Value ? 1 : 2, On = hasMat
                });
            if (allowLoad is not null)
                Signals.UpdateReading(Eq, new SignalReading
                {
                    Signal = SignalKey.PosAllowLoad, PositionId = Pos,
                    RegisterAddress = "D1001", RawValue = allowLoad.Value ? 1 : 2, On = allowLoad
                });
        }
    }

    /// <summary>单加工位点位表：一个 POS_TEST_START 写点 + 一个 HasMat 读点。</summary>
    private sealed class SinglePositionPoints : IPlcPointSource
    {
        private static readonly PlcPointDefinition[] Points =
        {
            new()
            {
                PlcId = PlcId, EquipmentId = Eq, PositionId = Pos,
                Signal = SignalKey.PosTestStart, IsWrite = true, RegisterAddress = TestStartAddr
            },
            new()
            {
                PlcId = PlcId, EquipmentId = Eq, PositionId = Pos,
                Signal = SignalKey.PosHasMat, IsWrite = false, RegisterAddress = HasMatAddr,
                OnValue = 1, OffValue = 2
            }
        };

        public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcPointDefinition>>(Points);
        public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default)
            => GetAllAsync(ct);
        public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default)
            => GetAllAsync(ct);
    }

    /// <summary>可编排的 PLC 操作：控制写确认结果与 fresh HasMat 读值/错误。</summary>
    private sealed class ScriptedPlcOps : IPlcOperationService
    {
        public bool WriteVerified { get; set; } = true;
        public int WriteCount { get; private set; }
        public int? LastWrittenValue { get; private set; }
        public int HasMatRawValue { get; set; } = 1;
        public string? HasMatError { get; set; }

        public Task<IReadOnlyList<PlcReadResult>> ReadPointsAsync(long plcId, bool readOnlySignals = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcReadResult>>(Array.Empty<PlcReadResult>());

        public Task<PlcReadResult> ReadRegisterAsync(long plcId, string registerAddress, int length, CancellationToken ct = default)
            => Task.FromResult(new PlcReadResult(null, "HasMat", null, registerAddress,
                HasMatRawValue, "", HasMatRawValue == 1, 0, HasMatError));

        public Task<PlcWriteResult> WriteWithConfirmAsync(long plcId, string registerAddress, int value, string author, CancellationToken ct = default)
        {
            WriteCount++;
            LastWrittenValue = value;
            return Task.FromResult(new PlcWriteResult(registerAddress, value, null, WriteVerified, 0,
                WriteVerified ? null : "write not verified"));
        }

        public Task<PlcWriteResult> VerifyWriteAsync(long plcId, string registerAddress, int expectedValue, CancellationToken ct = default)
            => Task.FromResult(new PlcWriteResult(registerAddress, expectedValue, null, WriteVerified, 0, null));
    }

    /// <summary>可编排的 RCS 任务库：按 taskId 给定 TaskState。</summary>
    internal sealed class ScriptedTaskStore : IRcsTaskStore
    {
        private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);

        public void SetState(string taskId, string taskState) => _states[taskId] = taskState;

        public Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult(_states.TryGetValue(rcsTaskId, out var state)
                ? new RcsTaskRow(1, rcsTaskId, "transit", "0", state, null, 5,
                    "FROM", "TO", Eq, Pos, null, null, null, 0, "0", DateTime.Now, null, null, null)
                : null);

        public Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default) => Task.FromResult(0L);
        public Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null, string? error = null, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AutoRedoClaimResult> TryClaimAutoRedoAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
            => Task.FromResult(AutoRedoClaimResult.NotClaimable);
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
        public Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
