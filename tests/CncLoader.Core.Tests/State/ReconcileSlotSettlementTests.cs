using CncLoader.Common.Configuration;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using CncLoader.Core.Tests.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CncLoader.Core.Tests.State;

/// <summary>启动对账①b：HasMat 未知时不得撤预记、不得放工位再派工。</summary>
[TestFixture]
public sealed class ReconcileSlotSettlementTests
{
    private const long Eq = 30;
    private const long Pos = 1;
    private const string TaskId = "T-HOLD-1";

    [Test]
    public async Task 对账1b_上料已完成但HasMat未知_预记保留且工位不回WaitLoad()
    {
        var slots = new CountingSlots();
        slots.SeedReservation(TaskId);
        var store = new MemoryTaskStore();
        store.Add(Row(TaskId, taskType: "0", RcsTaskState.Executing, Eq, Pos));
        var tasks = new QueryTaskService("""{"items":[{"id":"T-HOLD-1","status":"completed"}]}""");
        var scheduler = CreateScheduler(store, tasks, slots);

        var round = await scheduler.ProbeReconcileAsync();
        var ctx = scheduler.ProbeGetContext(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(round.Succeeded, Is.True);
            Assert.That(slots.RollbackTakeCount, Is.EqualTo(0), "未知不得回滚取料预记");
            Assert.That(slots.ConfirmTakeCount, Is.EqualTo(0), "未知不得落账");
            Assert.That(slots.HasReservation(TaskId), Is.True);
            Assert.That(ctx.CurrentTaskId, Is.EqualTo(TaskId), "工位须仍绑该任务");
            Assert.That(ctx.State, Is.Not.EqualTo(PositionState.WaitLoad), "不得放回可再派工");
            Assert.That(ctx.AlarmRaised, Is.False, "未知不得粘滞报警");
        });
    }

    [Test]
    public async Task 对账1b_上料已完成且PLC有料_取料落账并进Loaded()
    {
        var slots = new CountingSlots();
        slots.SeedReservation(TaskId);
        var store = new MemoryTaskStore();
        store.Add(Row(TaskId, taskType: "0", RcsTaskState.Executing, Eq, Pos));
        var tasks = new QueryTaskService("""{"items":[{"id":"T-HOLD-1","status":"completed"}]}""");
        var plc = new FixedHasMatPlc(hasMat: true);
        var scheduler = CreateScheduler(store, tasks, slots, plc, withHasMatPoint: true);

        var round = await scheduler.ProbeReconcileAsync();
        var ctx = scheduler.ProbeGetContext(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(round.Succeeded, Is.True);
            Assert.That(slots.ConfirmTakeCount, Is.EqualTo(1));
            Assert.That(slots.RollbackTakeCount, Is.EqualTo(0));
            Assert.That(slots.HasReservation(TaskId), Is.False);
            Assert.That(ctx.State, Is.EqualTo(PositionState.Loaded));
            Assert.That(ctx.CurrentTaskId, Is.EqualTo(TaskId));
        });
    }

    [Test]
    public async Task 对账1b_上料已完成但PLC明确无料_回滚并Alarm()
    {
        var slots = new CountingSlots();
        slots.SeedReservation(TaskId);
        var store = new MemoryTaskStore();
        store.Add(Row(TaskId, taskType: "0", RcsTaskState.Executing, Eq, Pos));
        var tasks = new QueryTaskService("""{"items":[{"id":"T-HOLD-1","status":"completed"}]}""");
        var plc = new FixedHasMatPlc(hasMat: false);
        var scheduler = CreateScheduler(store, tasks, slots, plc, withHasMatPoint: true);

        var round = await scheduler.ProbeReconcileAsync();
        var ctx = scheduler.ProbeGetContext(Eq, Pos);

        Assert.Multiple(() =>
        {
            Assert.That(round.Succeeded, Is.True);
            Assert.That(slots.RollbackTakeCount, Is.EqualTo(1));
            Assert.That(slots.ConfirmTakeCount, Is.EqualTo(0));
            Assert.That(ctx.State, Is.EqualTo(PositionState.Alarm));
            Assert.That(ctx.CurrentTaskId, Is.Null);
        });
    }

    private static PositionScheduler CreateScheduler(
        IRcsTaskStore taskStore,
        IRcsTaskService taskSvc,
        ISlotAccountService slots,
        IPlcOperationService? plc = null,
        bool withHasMatPoint = false)
    {
        var equipment = new StubEquipment();
        IPlcPointSource points = withHasMatPoint ? new HasMatPoints() : new EmptyPoints();
        return new PositionScheduler(
            new SignalStateStore(),
            taskSvc,
            taskStore,
            points,
            plc ?? new FixedHasMatPlc(hasMat: null),
            new FixedRoutes(),
            new PriorityDispatchQueue(),
            new NoopAlarms(),
            Options.Create(new AppOptions
            {
                Rcs = new RcsOptions { SchedulerEnabled = true, SchedulerIntervalMs = 10_000 }
            }),
            NullLogger<PositionScheduler>.Instance,
            new NoopWorkRecords(),
            equipment,
            slots,
            new WorkLineOnlyRoutingValidator(equipment));
    }

    private static RcsTaskRow Row(string taskId, string taskType, string state, long? eq, long? pos)
        => new(1, taskId, "TR", taskType, state, null, 5, "FROM", "TO", eq, pos, "M-1",
            null, null, 0, "0", DateTime.Now, null, null, null);

    private sealed class MemoryTaskStore : IRcsTaskStore
    {
        private readonly Dictionary<string, RcsTaskRow> _rows = new(StringComparer.Ordinal);

        public void Add(RcsTaskRow row) => _rows[row.RcsTaskId!] = row;

        public Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                _rows.Values.Where(r => r.TaskState is RcsTaskState.Created or RcsTaskState.Dispatched or RcsTaskState.Executing)
                    .Select(r => r.RcsTaskId!)
                    .ToList());

        public Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult(_rows.TryGetValue(rcsTaskId, out var row) ? row : null);

        public Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null, string? error = null, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(rcsTaskId, out var row)) return Task.FromResult(false);
            _rows[rcsTaskId] = row with { TaskState = taskState, RcsStatus = rcsStatus, ErrorMsg = error };
            return Task.FromResult(true);
        }

        public Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default) => Task.FromResult(1L);
        public Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AutoRedoClaimResult> TryClaimAutoRedoAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
            => Task.FromResult(AutoRedoClaimResult.NotClaimable);
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(_rows.Values.ToList());
    }

    private sealed class QueryTaskService : IRcsTaskService
    {
        private readonly string _raw;
        public QueryTaskService(string raw) => _raw = raw;

        public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default)
            => Task.FromResult(new RcsResult(true, 200, true, null, "", _raw, null, 0));

        public Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "stub"));
        public Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default) => Fail();
        public Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default) => Fail();
        public Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> AutoRedispatchAsync(string rcsTaskId, int maxRedoCount, CancellationToken ct = default) => Fail();
        public Task<RcsResult> DispatchPalletReturnAsync(long equipmentId, long? positionId, string fromCode, string toCode,
            long workLineId, string lineCode, string author, CancellationToken ct = default) => Fail();
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentTasksAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentMessagesAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryMessagesAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;

        private static Task<RcsResult> Fail() => Task.FromResult(RcsResult.Fail("", "stub"));
    }

    private sealed class CountingSlots : ISlotAccountService
    {
        private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
        public int ConfirmTakeCount { get; private set; }
        public int RollbackTakeCount { get; private set; }

        public void SeedReservation(string taskId) => _reserved.Add(taskId);
        public bool HasReservation(string taskId) => _reserved.Contains(taskId);

        public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default)
        {
            ConfirmTakeCount++;
            _reserved.Remove(taskId);
            return Task.FromResult(true);
        }

        public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
        {
            RollbackTakeCount++;
            _reserved.Remove(taskId);
            return Task.FromResult(true);
        }

        public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
            IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
        public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default) => Task.FromResult<ReservedSlot?>(null);
        public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default) => Task.FromResult<ReservedSlot?>(null);
        public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default) => Task.FromResult(new FrameOccupancy(0, 0, 0, 0));
        public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
            => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.Updated, frameId, slotNo, null));
        public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default) => Task.FromResult<SlotLocation?>(null);
        public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
            => Task.FromResult(InventoryCorrectionResult.Empty());
        public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
    }

    private sealed class FixedHasMatPlc : IPlcOperationService
    {
        private readonly bool? _hasMat;
        public FixedHasMatPlc(bool? hasMat) => _hasMat = hasMat;

        public Task<PlcReadResult> ReadRegisterAsync(long plcId, string registerAddress, int length, CancellationToken ct = default)
        {
            if (_hasMat is null)
                return Task.FromResult(new PlcReadResult(null, "", null, registerAddress, 0, "", false, null, "未连接"));
            var raw = _hasMat.Value ? 1 : 0;
            return Task.FromResult(new PlcReadResult(null, "", null, registerAddress, raw, "", raw == 1, null, null));
        }

        public Task<IReadOnlyList<PlcReadResult>> ReadPointsAsync(long plcId, bool readOnlySignals = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcReadResult>>(Array.Empty<PlcReadResult>());
        public Task<PlcWriteResult> WriteWithConfirmAsync(long plcId, string registerAddress, int value, string author, CancellationToken ct = default)
            => Task.FromResult(new PlcWriteResult(registerAddress, value, null, true, 0, null));
        public Task<PlcWriteResult> VerifyWriteAsync(long plcId, string registerAddress, int expectedValue, CancellationToken ct = default)
            => Task.FromResult(new PlcWriteResult(registerAddress, expectedValue, null, true, 0, null));
    }

    private sealed class HasMatPoints : IPlcPointSource
    {
        public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcPointDefinition>>(
            [
                new PlcPointDefinition
                {
                    PlcId = 1, EquipmentId = Eq, PositionId = Pos,
                    Signal = SignalKey.PosHasMat, RegisterAddress = "D100",
                    OnValue = 1, OffValue = 0, IsWrite = false, DataLength = 1
                }
            ]);

        public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default) => GetAllAsync(ct);
        public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default) => GetAllAsync(ct);
    }

    private sealed class StubEquipment : IEquipmentConfigService
    {
        public Task<WorkLineRef?> GetWorkLineByEquipmentAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<WorkLineRef?>(new WorkLineRef(1, "LINE"));
        public Task<EquipmentFrameBindingIds> GetFrameBindingIdsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult(new EquipmentFrameBindingIds(null, null));
        public Task<long?> GetFrameBindingByRoleAsync(long equipmentId, FrameRole role, CancellationToken ct = default)
            => Task.FromResult<long?>(null);
        public Task<IReadOnlyList<long>> GetNextProcessEquipmentsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        public Task<bool> HasSubsequentProcessAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<IReadOnlyList<NamedOption>> GetCraftworkOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task<IReadOnlyList<EquipmentListItem>> GetByCraftAsync(long? craftworkId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentListItem>>(Array.Empty<EquipmentListItem>());
        public Task<IReadOnlyList<PositionItem>> GetPositionsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PositionItem>>(Array.Empty<PositionItem>());
        public Task<IReadOnlyList<EquipmentFrameBinding>> GetFrameBindingsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentFrameBinding>>(Array.Empty<EquipmentFrameBinding>());
        public Task<IReadOnlyList<NamedOption>> GetPlcOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task<IReadOnlyList<NamedOption>> GetFrameOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task<string> SuggestNextNoAsync(CancellationToken ct = default) => Task.FromResult("EQ01");
        public Task<long> CreateEquipmentAsync(EquipmentCreateModel model, string author, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<EquipmentEditModel?> GetByIdAsync(long equipmentId, CancellationToken ct = default) => Task.FromResult<EquipmentEditModel?>(null);
        public Task UpdateAsync(EquipmentEditModel model, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameBindingInfo>>(Array.Empty<FrameBindingInfo>());
        public Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default) => Task.CompletedTask;
    }
}
