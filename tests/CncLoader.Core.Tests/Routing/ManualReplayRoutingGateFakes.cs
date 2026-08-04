using CncLoader.Common.Configuration;
using CncLoader.Common.Identity;
using CncLoader.Communication.Rcs;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.Data.Repositories;
using CncLoader.UI.ViewModels.Pages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 R16–R24 共享夹具：真实 <see cref="RcsTaskService"/> / <see cref="RcsViewModel"/> /
/// <see cref="RoutingAvailabilityValidator"/> + 可变权威 Store（每测独立实例）。
/// </summary>
internal static class ManualReplayRoutingCodes
{
    public const string FromCell = "CELL-FROM-30";
    public const string ToCell = "CELL-TO-40";
    public const string AmbiguousCell = "CELL-AMBIG";
    public const long SrcEq = 30;
    public const long DstEq = 40;
    public const long LineId = 10;
    public const long CraftId = 20;
    public const long DstCraftId = 21;
    public const string LineCode = "LINE-A";
}

internal sealed class CountingRoutingValidator : IRoutingAvailabilityValidator
{
    private readonly IRoutingAvailabilityValidator _inner;
    private int _calls;

    public CountingRoutingValidator(IRoutingAvailabilityValidator inner) => _inner = inner;

    public int CallCount => Volatile.Read(ref _calls);
    public List<DispatchRouteContext> Contexts { get; } = new();
    public List<RoutingAvailabilityResult> Results { get; } = new();

    public async Task<RoutingAvailabilityResult> ValidateAsync(
        DispatchRouteContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        Contexts.Add(context);
        var r = await _inner.ValidateAsync(context, ct);
        Results.Add(r);
        return r;
    }
}

internal sealed class FakeRcsHttpClient : IRcsClient
{
    public int TransitCount { get; private set; }
    public int ExcuteCount { get; private set; }
    public int CancelCount { get; private set; }
    public int QueryCount { get; private set; }
    public int SendCount => TransitCount + ExcuteCount;
    public List<string> TransitTaskIds { get; } = new();

    public Task<RcsResult> TransitTaskAsync(TransitTaskRequest req, CancellationToken ct = default)
    {
        TransitCount++;
        TransitTaskIds.Add(req.TaskId ?? "");
        return Task.FromResult(new RcsResult(true, 200, true, "ok", "{}", "{}", null, 1)
        {
            TaskId = req.TaskId
        });
    }

    public Task<RcsResult> ExcuteTaskAsync(ExcuteTaskRequest req, CancellationToken ct = default)
    {
        ExcuteCount++;
        return Task.FromResult(new RcsResult(true, 200, true, "ok", "{}", "{}", null, 1)
        {
            TaskId = req.TaskId
        });
    }

    public Task<RcsResult> CancelTaskAsync(CancelTaskRequest req, CancellationToken ct = default)
    {
        CancelCount++;
        return Task.FromResult(new RcsResult(true, 200, true, "ok", "{}", "{}", null, 1));
    }

    public Task<RcsResult> QueryTaskAsync(QueryTaskRequest req, CancellationToken ct = default)
    {
        QueryCount++;
        return Task.FromResult(new RcsResult(true, 200, true, "ok", "{}", "[]", null, 1));
    }
}

internal sealed class MutableRcsTaskStore : IRcsTaskStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RcsTaskRow> _rows = new(StringComparer.Ordinal);
    private long _nextId = 1;

    public int CreateCount { get; private set; }
    public int IncrementRedoCount { get; private set; }
    public int SetDispatchedCount { get; private set; }
    public int UpdateStateCount { get; private set; }
    public List<RcsTaskRecord> Created { get; } = new();

    public void Seed(RcsTaskRow row)
    {
        lock (_gate) _rows[row.RcsTaskId!] = row;
    }

    public RcsTaskRow? Snapshot(string taskId)
    {
        lock (_gate) return _rows.TryGetValue(taskId, out var r) ? r : null;
    }

    public Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            CreateCount++;
            Created.Add(record);
            var id = _nextId++;
            _rows[record.RcsTaskId] = new RcsTaskRow(
                id, record.RcsTaskId, RcsTaskKindNames.ToDbKind(record.Kind),
                record.TaskType, RcsTaskState.Created, null, record.Priority,
                record.FromCode, record.ToCode, record.EquipmentId, record.PositionId,
                record.MaterialId, record.TxnId, record.ReqParam, 0, "0",
                DateTime.Now, null, null, null);
            return Task.FromResult(id);
        }
    }

    public Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            SetDispatchedCount++;
            if (_rows.TryGetValue(rcsTaskId, out var r))
                _rows[rcsTaskId] = r with { TaskState = RcsTaskState.Dispatched, DispatchTime = DateTime.Now };
            return Task.CompletedTask;
        }
    }

    public Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null,
        string? error = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            UpdateStateCount++;
            if (!_rows.TryGetValue(rcsTaskId, out var r)) return Task.FromResult(false);
            _rows[rcsTaskId] = r with
            {
                TaskState = taskState,
                RcsStatus = rcsStatus,
                ErrorMsg = error,
                FinishTime = RcsStatusMapper.IsTerminal(taskState) ? DateTime.Now : r.FinishTime
            };
            return Task.FromResult(true);
        }
    }

    public Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IncrementRedoCount++;
            if (_rows.TryGetValue(rcsTaskId, out var r))
                _rows[rcsTaskId] = r with
                {
                    RedoCount = r.RedoCount + 1,
                    TaskState = RcsTaskState.Dispatched,
                    ErrorMsg = null
                };
            return Task.CompletedTask;
        }
    }

    public Task<bool> TryIncrementRedoIfUnderAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(rcsTaskId, out var r) || r.RedoCount >= maxRedo)
                return Task.FromResult(false);
            _rows[rcsTaskId] = r with
            {
                RedoCount = r.RedoCount + 1,
                TaskState = RcsTaskState.Dispatched,
                ErrorMsg = null
            };
            return Task.FromResult(true);
        }
    }

    public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.TryGetValue(rcsTaskId, out var r) ? r : null);
    }

    public Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<RcsTaskRow>>(
                _rows.Values.OrderByDescending(x => x.Id).Take(limit).ToList());
    }

    public Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<string>>(
                _rows.Values
                    .Where(r => r.TaskState is RcsTaskState.Created or RcsTaskState.Dispatched
                        or RcsTaskState.Executing)
                    .Select(r => r.RcsTaskId!)
                    .ToList());
    }
}

internal sealed class TrackingCallbackProcessor : IRcsCallbackProcessor
{
    private readonly RcsCallbackProcessor? _real;
    public int ForgetCount { get; private set; }
    public List<string> Forgotten { get; } = new();
    public int PushCount { get; private set; }
    public int ScanCount { get; private set; }

    public TrackingCallbackProcessor(RcsCallbackProcessor? real = null) => _real = real;

    public async Task<string> HandlePushTaskStatusAsync(string rawBody, CancellationToken ct = default)
    {
        PushCount++;
        if (_real is not null) return await _real.HandlePushTaskStatusAsync(rawBody, ct);
        return "{\"taskId\":\"\"}";
    }

    public async Task<string> HandleScanTaskStatusAsync(string rawBody, CancellationToken ct = default)
    {
        ScanCount++;
        if (_real is not null) return await _real.HandleScanTaskStatusAsync(rawBody, ct);
        return "{\"taskId\":\"\"}";
    }

    public Task<string> HandleWarnCallbackAsync(string rawBody, CancellationToken ct = default)
        => _real?.HandleWarnCallbackAsync(rawBody, ct) ?? Task.FromResult("{\"taskId\":\"\"}");

    public void ForgetTask(string taskId)
    {
        ForgetCount++;
        Forgotten.Add(taskId);
        _real?.ForgetTask(taskId);
    }
}

internal sealed class FakeLocationMapForRouting : ILocationMapService, ILocationMapRoutingStore
{
    private readonly List<LocationMapRoutingRow> _rows = new();
    public int ResolveByCodeCount { get; private set; }
    public List<string> ResolvedCodes { get; } = new();

    public void Seed(LocationMapItem item, string state = "0")
    {
        _rows.Add(new LocationMapRoutingRow(
            item.Id, item.LocType, item.EquipmentId, item.PositionId, item.FrameId,
            item.LocName, item.RcsCode, item.RcsType, state));
    }

    public void SetStateByCode(string rcsCode, bool remove, string? state = null)
    {
        if (remove)
        {
            _rows.RemoveAll(i => i.RcsCode == rcsCode);
            return;
        }
        if (state is null) return;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].RcsCode != rcsCode) continue;
            var r = _rows[i];
            _rows[i] = r with { State = state };
        }
    }

    public Task<IReadOnlyList<LocationMapItem>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LocationMapItem>>(_rows.Select(ToItem).ToList());

    public Task<long> SaveAsync(LocationMapItem item, string? author = null, CancellationToken ct = default)
        => Task.FromResult(item.Id > 0 ? item.Id : 1L);

    public Task DeleteAsync(long id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<LocationMapItem?> ResolvePositionAsync(long equipmentId, long? positionId, string rcsType, CancellationToken ct = default)
        => Task.FromResult(_rows
            .Where(i => i.State == "0" && i.EquipmentId == equipmentId
                        && i.PositionId == positionId && i.RcsType == rcsType)
            .Select(ToItem).FirstOrDefault());

    public Task<LocationMapItem?> ResolveFrameAsync(long frameId, string rcsType, CancellationToken ct = default)
        => Task.FromResult(_rows
            .Where(i => i.State == "0" && i.FrameId == frameId && i.RcsType == rcsType)
            .Select(ToItem).FirstOrDefault());

    public Task<LocationMapItem?> ResolveAreaAsync(string locName, CancellationToken ct = default)
        => Task.FromResult(_rows
            .Where(i => i.State == "0" && i.LocName == locName)
            .Select(ToItem).FirstOrDefault());

    public Task<LocationMapItem?> ResolveByRcsCodeAsync(string rcsCode, CancellationToken ct = default)
    {
        ResolveByCodeCount++;
        ResolvedCodes.Add(rcsCode);
        var hits = _rows.Where(i => i.State == "0" && i.RcsCode == rcsCode).Select(ToItem).ToList();
        if (hits.Count != 1) return Task.FromResult<LocationMapItem?>(null);
        return Task.FromResult<LocationMapItem?>(hits[0]);
    }

    public Task<IReadOnlyList<LocationMapRoutingRow>> FindByPositionAsync(
        long equipmentId, long? positionId, string rcsType, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(_rows
            .Where(x => x.RcsType == rcsType && x.EquipmentId == equipmentId
                        && (positionId == null ? x.PositionId == null : x.PositionId == positionId))
            .ToList());

    public Task<IReadOnlyList<LocationMapRoutingRow>> FindByFrameAsync(
        long frameId, string rcsType, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(_rows
            .Where(x => x.RcsType == rcsType && x.FrameId == frameId).ToList());

    public Task<IReadOnlyList<LocationMapRoutingRow>> FindByAreaAsync(
        string locName, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(_rows
            .Where(x => x.LocType == "AREA" && x.LocName == locName).ToList());

    public Task<IReadOnlyList<LocationMapRoutingRow>> FindByRcsCodeAsync(
        string rcsCode, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(_rows
            .Where(x => x.RcsCode == rcsCode).ToList());

    private static LocationMapItem ToItem(LocationMapRoutingRow r) => new()
    {
        Id = r.Id,
        LocType = r.LocType,
        EquipmentId = r.EquipmentId,
        PositionId = r.PositionId,
        FrameId = r.FrameId,
        LocName = r.LocName,
        RcsCode = r.RcsCode,
        RcsType = r.RcsType
    };
}

internal sealed class TrackingSlotsForClosure : ISlotAccountService
{
    public int ConfirmCount { get; private set; }
    public int ConfirmTakeCount { get; private set; }
    public int RollbackCount { get; private set; }
    public int RollbackTakeCount { get; private set; }
    public int ReserveCount { get; private set; }
    public List<string> ConfirmedTaskIds { get; } = new();
    public List<string> RolledBackTaskIds { get; } = new();

    public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default)
    {
        ConfirmCount++;
        ConfirmedTaskIds.Add(taskId);
        return Task.FromResult(true);
    }

    public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default)
    {
        ConfirmTakeCount++;
        ConfirmedTaskIds.Add(taskId);
        return Task.FromResult(true);
    }

    public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default)
    {
        RollbackCount++;
        RolledBackTaskIds.Add(taskId);
        return Task.FromResult(true);
    }

    public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
    {
        RollbackTakeCount++;
        RolledBackTaskIds.Add(taskId);
        return Task.FromResult(true);
    }

    public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
    {
        ReserveCount++;
        return Task.FromResult<ReservedSlot?>(new ReservedSlot(frameId, 1, 1, 1, materialId));
    }

    public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
    {
        ReserveCount++;
        return Task.FromResult<ReservedSlot?>(new ReservedSlot(frameId, 1, 1, 1, "M"));
    }

    public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
        => Task.FromResult(0);
    public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
        IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
    public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default)
        => Task.FromResult(new FrameOccupancy(10, 0, 0, 10));
    public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
        => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.Updated, frameId, slotNo, null));
    public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default)
        => Task.FromResult<SlotLocation?>(null);
    public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
        => Task.FromResult(InventoryCorrectionResult.Empty());
    public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
}

internal sealed class FakeNotifyCounter : IUserNotificationService
{
    public int SuccessCount { get; private set; }
    public int WarningCount { get; private set; }
    public int ErrorCount { get; private set; }
    public int InfoCount { get; private set; }
    public List<string> All { get; } = new();

    public void Success(string message) { SuccessCount++; All.Add(message); }
    public void Warning(string message) { WarningCount++; All.Add(message); }
    public void Error(string message) { ErrorCount++; All.Add(message); }
    public void Info(string message) { InfoCount++; All.Add(message); }

    /// <summary>多步场景（如 R19 第二次派工）前清零，避免累加计数污染断言。</summary>
    public void Reset()
    {
        SuccessCount = 0;
        WarningCount = 0;
        ErrorCount = 0;
        InfoCount = 0;
        All.Clear();
    }
}

/// <summary>手动/重发门禁测试宿主：真实 Service + ViewModel + Resolver/Validator。</summary>
internal sealed class ManualReplayHarness
{
    public required MutableEquipmentRoutingStore Store { get; init; }
    public required FakeLocationMapForRouting LocationMap { get; init; }
    public required FakeRcsHttpClient Client { get; init; }
    public required MutableRcsTaskStore TaskStore { get; init; }
    public required TrackingCallbackProcessor Callbacks { get; init; }
    public required CountingRoutingValidator Validator { get; init; }
    public required IManagedDispatchRouteResolver RouteResolver { get; init; }
    public required RcsTaskService TaskService { get; init; }
    public required RcsViewModel ViewModel { get; init; }
    public required TrackingSlotsForClosure Slots { get; init; }
    public required TracingPlcOps Plc { get; init; }
    public required FakeNotifyCounter Notify { get; init; }
    public required CallTrace Trace { get; init; }

    public static ManualReplayHarness Create(bool seedActiveRoute = true)
    {
        var store = new MutableEquipmentRoutingStore();
        var loc = new FakeLocationMapForRouting();
        if (seedActiveRoute)
        {
            store.SeedActiveChain(
                ManualReplayRoutingCodes.LineId, ManualReplayRoutingCodes.LineCode,
                ManualReplayRoutingCodes.CraftId, craftNode: 1,
                ManualReplayRoutingCodes.SrcEq);
            store.SeedNextEquipment(
                ManualReplayRoutingCodes.DstEq, ManualReplayRoutingCodes.DstCraftId,
                nextNode: 2, ManualReplayRoutingCodes.LineId);
            store.BindFrame(ManualReplayRoutingCodes.SrcEq, 50, FrameRole.Upload);
            store.BindFrame(ManualReplayRoutingCodes.DstEq, 60, FrameRole.Unload);
            loc.Seed(new LocationMapItem
            {
                Id = 1, LocType = "POSITION", EquipmentId = ManualReplayRoutingCodes.SrcEq,
                PositionId = 1, RcsCode = ManualReplayRoutingCodes.FromCell, RcsType = "cell"
            });
            loc.Seed(new LocationMapItem
            {
                Id = 2, LocType = "POSITION", EquipmentId = ManualReplayRoutingCodes.DstEq,
                PositionId = 1, RcsCode = ManualReplayRoutingCodes.ToCell, RcsType = "cell"
            });
        }

        var trace = new CallTrace();
        var routingEquipment = new TracingEquipmentConfigService(store, trace);
        // UI 列表方法不得走 UnusedDbContextFactory，避免 InitializeAsync 竞态改写 StatusMessage
        var equipment = new UiSafeEquipmentConfigService(routingEquipment);
        var validator = new CountingRoutingValidator(
            new RoutingAvailabilityValidator(
                store, routingEquipment, NullLogger<RoutingAvailabilityValidator>.Instance));
        var resolver = new ManagedDispatchRouteResolver(
            loc, NullLogger<ManagedDispatchRouteResolver>.Instance);

        var client = new FakeRcsHttpClient();
        var taskStore = new MutableRcsTaskStore();
        var callbacks = new TrackingCallbackProcessor();
        var taskService = new RcsTaskService(
            client, taskStore, new NoopMsgLog(), callbacks,
            resolver, validator,
            NullLogger<RcsTaskService>.Instance);

        var plc = new TracingPlcOps(trace);
        var slots = new TrackingSlotsForClosure();
        var notify = new FakeNotifyCounter();
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions
            {
                UseSimulator = true,
                SchedulerEnabled = false,
                PalletReturnArea = "托盘回收区"
            }
        });

        // headless：挂接通知接缝，避免 Growl 无视觉树 NRE 假红
        RcsViewModel.TestNotifications = notify;

        var vm = new RcsViewModel(
            taskService,
            loc,
            new StubWorkLines(),
            equipment,
            new StubFrames(),
            new RcsCallbackNotifier(),
            new StubChangeFrame(),
            new StubConnConfig(),
            new StubRuntime(),
            new StubCallbackListener(),
            new StubScheduler(),
            new StubUser(),
            resolver,
            validator,
            options);

        return new ManualReplayHarness
        {
            Store = store,
            LocationMap = loc,
            Client = client,
            TaskStore = taskStore,
            Callbacks = callbacks,
            Validator = validator,
            RouteResolver = resolver,
            TaskService = taskService,
            ViewModel = vm,
            Slots = slots,
            Plc = plc,
            Notify = notify,
            Trace = trace
        };
    }

    public RcsTaskRow SeedHistoricalTask(
        string taskId = "LINE-A-MV-20260804120000-0001",
        string state = RcsTaskState.Failed,
        string? error = "prev-fail",
        int redoCount = 1)
    {
        var row = new RcsTaskRow(
            99, taskId, "transit", "2", state, "failed", 5,
            ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell,
            ManualReplayRoutingCodes.SrcEq, 1, "MAT-1", null, null,
            redoCount, "0", DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(-4),
            DateTime.Now.AddMinutes(-3), error);
        TaskStore.Seed(row);
        return row;
    }

    private sealed class NoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    }

    private sealed class StubWorkLines : IWorkLineService
    {
#pragma warning disable CS0067
        public event EventHandler? WorkLinesChanged;
#pragma warning restore CS0067
        public Task<IReadOnlyList<WorkLineListItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkLineListItem>>(new[]
            {
                new WorkLineListItem(ManualReplayRoutingCodes.LineId, ManualReplayRoutingCodes.LineCode, "线A", false, true, 1)
            });
        public Task<WorkLineEditModel?> GetByIdAsync(long id, CancellationToken ct = default)
            => Task.FromResult<WorkLineEditModel?>(null);
        public Task<long> SaveAsync(WorkLineEditModel model, string author, CancellationToken ct = default)
            => Task.FromResult(1L);
        public Task<DeleteCheckResult> CheckDeleteAsync(long id, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubFrames : IFrameService
    {
        public Task<IReadOnlyList<FrameListItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameListItem>>(Array.Empty<FrameListItem>());
        public Task<FrameDetail?> GetDetailAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<FrameDetail?>(null);
        public Task<long> CreateFrameAsync(FrameCreateModel model, string author, CancellationToken ct = default)
            => Task.FromResult(1L);
        public Task<IReadOnlyList<NamedOption>> GetEquipmentOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task BindEquipmentAsync(long frameId, long equipmentId, string roleCode, string author, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task UnbindAsync(long bindId, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<FrameEditModel?> GetFrameForEditAsync(long id, CancellationToken ct = default)
            => Task.FromResult<FrameEditModel?>(null);
        public Task UpdateFrameAsync(FrameEditModel model, string author, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<DeleteCheckResult> CheckDeleteFrameAsync(long id, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteFrameAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubChangeFrame : IChangeFrameOrchestrator
    {
#pragma warning disable CS0067
        public event EventHandler<ChangeFrameProgressEvent>? ProgressChanged;
#pragma warning restore CS0067
        public Task<string> ChangeFrameAsync(long equipmentId, FrameRole role, string author, CancellationToken ct = default)
            => Task.FromResult("txn");
        public IReadOnlyList<ChangeFrameProgressEvent> GetActiveTransactions()
            => Array.Empty<ChangeFrameProgressEvent>();
    }

    private sealed class StubConnConfig : IRcsConnectionConfigService
    {
        public Task<RcsConnectionConfig?> GetAsync(CancellationToken ct = default)
            => Task.FromResult<RcsConnectionConfig?>(null);
        public Task<RcsConnectionConfig?> GetByWorkLineAgvIdAsync(long agvId, CancellationToken ct = default)
            => Task.FromResult<RcsConnectionConfig?>(null);
        public Task<RcsConnectionConfig> SaveAsync(RcsConnectionConfig config, string author, CancellationToken ct = default)
            => Task.FromResult(config);
    }

    private sealed class StubRuntime : IRcsRuntimeConfig
    {
        public string BaseUrl => "http://127.0.0.1:8090";
        public string ClientCode => "CNC";
        public string Version => "1";
        public string TokenCode => "t";
        public int RequestTimeoutMs => 10000;
        public int MaxRetries => 1;
        public string CallbackHost => "0.0.0.0";
        public int CallbackPort => 9080;
        public int PollIntervalMs => 3000;
        public string BootCallbackHost => CallbackHost;
        public int BootCallbackPort => CallbackPort;
        public void Apply(RcsConnectionConfig config) { }
        public void CaptureBootCallback() { }
        public RcsConnectionConfig Snapshot() => new()
        {
            BaseUrl = BaseUrl, ClientCode = ClientCode, CallbackHost = CallbackHost,
            CallbackPort = CallbackPort, RequestTimeoutMs = RequestTimeoutMs,
            MaxRetries = MaxRetries, PollIntervalMs = PollIntervalMs
        };
    }

    private sealed class StubCallbackListener : IRcsCallbackListener
    {
        public bool IsListening => false;
        public string BoundHost => "127.0.0.1";
        public int BoundPort => 0;
        public string? ListenError => null;
    }

    private sealed class StubScheduler : IPositionScheduler
    {
        public bool IsReconciled => true;
        public ReconciliationState ReconciliationState => ReconciliationState.Succeeded;
        public string? ReconciliationFailureReason => null;
        public bool IsAutoDispatchPaused { get; private set; }
#pragma warning disable CS0067
        public event EventHandler? Reconciled;
        public event EventHandler<ReconciliationSnapshot>? ReconciliationStateChanged;
#pragma warning restore CS0067
        public void SetAutoDispatchPaused(bool paused) => IsAutoDispatchPaused = paused;
        public Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default)
            => Task.CompletedTask;
        public void InvalidateFrameBindingCache(long? equipmentId = null) { }
    }

    private sealed class StubUser : ICurrentUser
    {
        public string Name => "tester";
    }
}

/// <summary>路由委托真实 TracingEquipment；CRUD/下拉返回空，避免测试撞 DB factory。</summary>
internal sealed class UiSafeEquipmentConfigService : IEquipmentConfigService
{
    private readonly IEquipmentConfigService _routing;
    public UiSafeEquipmentConfigService(IEquipmentConfigService routing) => _routing = routing;

    public Task<WorkLineRef?> GetWorkLineByEquipmentAsync(long equipmentId, CancellationToken ct = default)
        => _routing.GetWorkLineByEquipmentAsync(equipmentId, ct);
    public Task<IReadOnlyList<long>> GetNextProcessEquipmentsAsync(long equipmentId, CancellationToken ct = default)
        => _routing.GetNextProcessEquipmentsAsync(equipmentId, ct);
    public Task<bool> HasSubsequentProcessAsync(long equipmentId, CancellationToken ct = default)
        => _routing.HasSubsequentProcessAsync(equipmentId, ct);
    public Task<EquipmentFrameBindingIds> GetFrameBindingIdsAsync(long equipmentId, CancellationToken ct = default)
        => _routing.GetFrameBindingIdsAsync(equipmentId, ct);
    public Task<long?> GetFrameBindingByRoleAsync(long equipmentId, FrameRole role, CancellationToken ct = default)
        => _routing.GetFrameBindingByRoleAsync(equipmentId, role, ct);

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
    public Task<long> CreateEquipmentAsync(EquipmentCreateModel model, string author, CancellationToken ct = default)
        => Task.FromResult(1L);
    public Task<EquipmentEditModel?> GetByIdAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult<EquipmentEditModel?>(null);
    public Task UpdateAsync(EquipmentEditModel model, string author, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FrameBindingInfo>>(Array.Empty<FrameBindingInfo>());
    public Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId, string author, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult(new DeleteCheckResult(true, 0, ""));
    public Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default)
        => Task.CompletedTask;
}
