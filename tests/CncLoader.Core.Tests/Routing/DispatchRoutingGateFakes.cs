using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using CncLoader.Data;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CncLoader.Core.Tests.Routing;

/// <summary>P0-5 R9–R15 共享调用序与权威配置 fake（每测试独立实例，无静态共享）。</summary>
internal static class DispatchGateEvents
{
    public const string RouteQueryPre = "RouteQueryPre";
    public const string RouteValidatePre = "RouteValidatePre";
    public const string CacheHit = "CacheHit";
    public const string ReserveStart = "ReserveStart";
    public const string ReserveCommit = "ReserveCommit";
    public const string DisableRoute = "DisableRoute";
    public const string RouteQueryFinal = "RouteQueryFinal";
    public const string RouteValidateFinal = "RouteValidateFinal";
    public const string Rollback = "Rollback";
    public const string RcsDispatch = "RcsDispatch";
    public const string PlcWrite = "PlcWrite";
}

internal sealed class CallTrace
{
    private readonly object _gate = new();
    private readonly List<string> _events = new();

    public IReadOnlyList<string> Events
    {
        get { lock (_gate) return _events.ToList(); }
    }

    public void Add(string name)
    {
        lock (_gate) _events.Add(name);
    }

    public int CountOf(string name)
    {
        lock (_gate) return _events.Count(e => e == name);
    }
}

internal sealed record AuthorityQueryRecord(
    int Sequence,
    long EquipmentId,
    string? EquipmentState,
    string? CraftState,
    string? WorkLineState,
    bool ReturnedActive);

/// <summary>可变权威配置 Store：线程安全 STATE 切换；每次 FindEquipment 记查询序与三层状态。</summary>
internal sealed class MutableEquipmentRoutingStore : IEquipmentRoutingStore
{
    private readonly object _gate = new();
    private readonly List<AuthorityQueryRecord> _queries = new();
    private int _seq;

    public List<EquipmentRoutingRow> Equipments { get; } = new();
    public List<CraftworkRoutingRow> Crafts { get; } = new();
    public List<WorkLineRoutingRow> WorkLines { get; } = new();
    public List<FrameBindRoutingRow> FrameBinds { get; } = new();
    public int FindFrameBindsCallCount { get; private set; }
    /// <summary>第 N 次 FindFrameBindsByEquipmentAsync 返回后，将匹配行 STATE 置 1（TOCTOU）。</summary>
    private readonly Dictionary<(long Eq, long Frame), int> _disableBindAfterFind = new();
    private readonly Dictionary<(long Eq, long Frame), int> _bindFindCounts = new();

    public IReadOnlyList<AuthorityQueryRecord> Queries
    {
        get { lock (_gate) return _queries.ToList(); }
    }

    public void ResetBindQueryCount() => FindFrameBindsCallCount = 0;

    public void SetFrameBindState(long equipmentId, long frameId, string state)
    {
        lock (_gate)
        {
            for (var i = 0; i < FrameBinds.Count; i++)
            {
                if (FrameBinds[i].EquipmentId != equipmentId || FrameBinds[i].FrameId != frameId)
                    continue;
                var b = FrameBinds[i];
                FrameBinds[i] = b with { State = state };
            }
        }
    }

    public void ClearFrameBinds()
    {
        lock (_gate) FrameBinds.Clear();
    }

    /// <summary>第 N 次（1-based）按机台查 Bind 返回后，禁用指定 (Eq,Frame) Bind。</summary>
    public void DisableFrameBindAfterFindCount(long equipmentId, long frameId, int findCount)
    {
        lock (_gate)
            _disableBindAfterFind[(equipmentId, frameId)] = findCount;
    }

    public void SeedActiveChain(
        long lineId = 10, string lineCode = "LINE-A",
        long craftId = 20, long craftNode = 1,
        long equipmentId = 30)
    {
        lock (_gate)
        {
            WorkLines.Add(new WorkLineRoutingRow(lineId, lineCode, "0"));
            Crafts.Add(new CraftworkRoutingRow(craftId, lineId, craftNode, "0"));
            Equipments.Add(new EquipmentRoutingRow(equipmentId, craftId, "0"));
        }
    }

    public void SeedNextEquipment(long nextEq, long nextCraftId, long nextNode, long lineId = 10)
    {
        lock (_gate)
        {
            if (!Crafts.Any(c => c.Id == nextCraftId))
                Crafts.Add(new CraftworkRoutingRow(nextCraftId, lineId, nextNode, "0"));
            Equipments.Add(new EquipmentRoutingRow(nextEq, nextCraftId, "0"));
        }
    }

    public void BindFrame(long equipmentId, long frameId, FrameRole role)
    {
        lock (_gate)
        {
            FrameBinds.Add(new FrameBindRoutingRow(
                FrameBinds.Count + 1, frameId, equipmentId, ((int)role).ToString(), "0"));
        }
    }

    public void SetEquipmentState(long equipmentId, string state)
    {
        lock (_gate)
        {
            var idx = Equipments.FindIndex(e => e.Id == equipmentId);
            if (idx < 0) return;
            var e = Equipments[idx];
            Equipments[idx] = e with { State = state };
        }
    }

    public void SetCraftState(long craftId, string state)
    {
        lock (_gate)
        {
            var idx = Crafts.FindIndex(c => c.Id == craftId);
            if (idx < 0) return;
            var c = Crafts[idx];
            Crafts[idx] = c with { State = state };
        }
    }

    public void SetWorkLineState(long lineId, string state)
    {
        lock (_gate)
        {
            var idx = WorkLines.FindIndex(l => l.Id == lineId);
            if (idx < 0) return;
            var l = WorkLines[idx];
            WorkLines[idx] = l with { State = state };
        }
    }

    public Task<EquipmentRoutingRow?> FindEquipmentAsync(long equipmentId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var eq = Equipments.FirstOrDefault(e => e.Id == equipmentId);
            string? craftState = null;
            string? lineState = null;
            var active = false;
            if (eq is not null)
            {
                var craft = Crafts.FirstOrDefault(c => c.Id == eq.CraftworkId);
                craftState = craft?.State;
                if (craft is not null)
                {
                    var line = WorkLines.FirstOrDefault(l => l.Id == craft.WorkLineId);
                    lineState = line?.State;
                    active = eq.State == "0" && craft.State == "0" && line?.State == "0";
                }
            }

            _queries.Add(new AuthorityQueryRecord(
                ++_seq, equipmentId, eq?.State, craftState, lineState, active));
            return Task.FromResult(eq);
        }
    }

    public Task<CraftworkRoutingRow?> FindCraftworkAsync(long craftworkId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(Crafts.FirstOrDefault(c => c.Id == craftworkId));
    }

    public Task<WorkLineRoutingRow?> FindWorkLineAsync(long workLineId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(WorkLines.FirstOrDefault(l => l.Id == workLineId));
    }

    public Task<IReadOnlyList<CraftworkRoutingRow>> FindCraftworksByWorkLineAsync(
        long workLineId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<CraftworkRoutingRow>>(
                Crafts.Where(c => c.WorkLineId == workLineId).ToList());
    }

    public Task<IReadOnlyList<EquipmentRoutingRow>> FindEquipmentsByCraftworkIdsAsync(
        IReadOnlyCollection<long> craftworkIds, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<EquipmentRoutingRow>>(
                Equipments.Where(e => craftworkIds.Contains(e.CraftworkId)).ToList());
    }

    /// <summary>测试注入：下次 FindFrameBinds 抛异常（fail-closed）。</summary>
    public Exception? ThrowOnNextFindFrameBinds { get; set; }

    public Task<IReadOnlyList<FrameBindRoutingRow>> FindFrameBindsByEquipmentAsync(
        long equipmentId, CancellationToken ct = default)
    {
        var toThrow = ThrowOnNextFindFrameBinds;
        if (toThrow is not null)
        {
            ThrowOnNextFindFrameBinds = null;
            FindFrameBindsCallCount++;
            throw toThrow;
        }

        lock (_gate)
        {
            FindFrameBindsCallCount++;
            var snapshot = FrameBinds.Where(b => b.EquipmentId == equipmentId).ToList();

            foreach (var key in _disableBindAfterFind.Keys.Where(k => k.Eq == equipmentId).ToList())
            {
                _bindFindCounts.TryGetValue(key, out var n);
                n++;
                _bindFindCounts[key] = n;
                if (n >= _disableBindAfterFind[key])
                {
                    for (var i = 0; i < FrameBinds.Count; i++)
                    {
                        if (FrameBinds[i].EquipmentId != key.Eq || FrameBinds[i].FrameId != key.Frame)
                            continue;
                        FrameBinds[i] = FrameBinds[i] with { State = "1" };
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<FrameBindRoutingRow>>(snapshot);
        }
    }
}

/// <summary>包装真实 EquipmentConfigService：按预记相位区分 Pre/Final 查询事件。</summary>
internal sealed class TracingEquipmentConfigService : IEquipmentConfigService
{
    private readonly EquipmentConfigService _inner;
    private readonly MutableEquipmentRoutingStore _store;
    private readonly CallTrace _trace;
    private int _reserveCommitted;

    public TracingEquipmentConfigService(
        MutableEquipmentRoutingStore store, CallTrace trace)
    {
        _store = store;
        _trace = trace;
        _inner = new EquipmentConfigService(new UnusedDbContextFactory(), store);
    }

    public void MarkReserveCommitted() => Interlocked.Exchange(ref _reserveCommitted, 1);

    public int AuthorityQueryCount => _store.Queries.Count;

    public Task<WorkLineRef?> GetWorkLineByEquipmentAsync(long equipmentId, CancellationToken ct = default)
    {
        var afterReserve = Volatile.Read(ref _reserveCommitted) == 1;
        _trace.Add(afterReserve
            ? DispatchGateEvents.RouteQueryFinal
            : DispatchGateEvents.RouteQueryPre);
        if (!afterReserve)
            _trace.Add(DispatchGateEvents.RouteValidatePre);
        else
            _trace.Add(DispatchGateEvents.RouteValidateFinal);
        return _inner.GetWorkLineByEquipmentAsync(equipmentId, ct);
    }

    public Task<IReadOnlyList<long>> GetNextProcessEquipmentsAsync(long equipmentId, CancellationToken ct = default)
        => _inner.GetNextProcessEquipmentsAsync(equipmentId, ct);

    public Task<bool> HasSubsequentProcessAsync(long equipmentId, CancellationToken ct = default)
        => _inner.HasSubsequentProcessAsync(equipmentId, ct);

    public Task<EquipmentFrameBindingIds> GetFrameBindingIdsAsync(long equipmentId, CancellationToken ct = default)
        => _inner.GetFrameBindingIdsAsync(equipmentId, ct);

    public Task<long?> GetFrameBindingByRoleAsync(long equipmentId, FrameRole role, CancellationToken ct = default)
        => _inner.GetFrameBindingByRoleAsync(equipmentId, role, ct);

    public Task<IReadOnlyList<NamedOption>> GetCraftworkOptionsAsync(CancellationToken ct = default)
        => _inner.GetCraftworkOptionsAsync(ct);
    public Task<IReadOnlyList<EquipmentListItem>> GetByCraftAsync(long? craftworkId, CancellationToken ct = default)
        => _inner.GetByCraftAsync(craftworkId, ct);
    public Task<IReadOnlyList<PositionItem>> GetPositionsAsync(long equipmentId, CancellationToken ct = default)
        => _inner.GetPositionsAsync(equipmentId, ct);
    public Task<IReadOnlyList<EquipmentFrameBinding>> GetFrameBindingsAsync(long equipmentId, CancellationToken ct = default)
        => _inner.GetFrameBindingsAsync(equipmentId, ct);
    public Task<IReadOnlyList<NamedOption>> GetPlcOptionsAsync(CancellationToken ct = default)
        => _inner.GetPlcOptionsAsync(ct);
    public Task<IReadOnlyList<NamedOption>> GetFrameOptionsAsync(CancellationToken ct = default)
        => _inner.GetFrameOptionsAsync(ct);
    public Task<string> SuggestNextNoAsync(CancellationToken ct = default) => _inner.SuggestNextNoAsync(ct);
    public Task<long> CreateEquipmentAsync(EquipmentCreateModel model, string author, CancellationToken ct = default)
        => _inner.CreateEquipmentAsync(model, author, ct);
    public Task<EquipmentEditModel?> GetByIdAsync(long equipmentId, CancellationToken ct = default)
        => _inner.GetByIdAsync(equipmentId, ct);
    public Task UpdateAsync(EquipmentEditModel model, string author, CancellationToken ct = default)
        => _inner.UpdateAsync(model, author, ct);
    public Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default)
        => _inner.GetBindingByFrameAsync(frameId, ct);
    public Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId, string author, CancellationToken ct = default)
        => _inner.SetFrameBindingAsync(equipmentId, uploadFrameId, downloadFrameId, author, ct);
    public Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default)
        => _inner.CheckDeleteAsync(equipmentId, ct);
    public Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default)
        => _inner.DeleteAsync(equipmentId, author, ct);
}

internal sealed class TracingSlots : ISlotAccountService
{
    private readonly CallTrace _trace;
    private readonly TracingEquipmentConfigService? _equipment;
    private readonly Action? _disableOnReserveCommit;
    private readonly ConcurrentDictionary<string, byte> _reserved = new();

    public TracingSlots(
        CallTrace trace,
        long occupiedFrameId,
        long? putFrameId = null,
        TracingEquipmentConfigService? equipment = null,
        MutableEquipmentRoutingStore? store = null,
        Action? disableOnReserveCommit = null)
    {
        _trace = trace;
        OccupiedFrameId = occupiedFrameId;
        PutFrameId = putFrameId;
        _equipment = equipment;
        _ = store;
        _disableOnReserveCommit = disableOnReserveCommit;
    }

    public long OccupiedFrameId { get; }
    public long? PutFrameId { get; }
    public int ReserveTakeCount { get; private set; }
    public int ReservePutCount { get; private set; }
    public int RollbackTakeCount { get; private set; }
    public int RollbackPutCount { get; private set; }
    public int RollbackCount => RollbackTakeCount + RollbackPutCount;
    public bool RollbackTakeSucceeds { get; set; } = true;
    public bool RollbackPutSucceeds { get; set; } = true;
    public string? LastReservedTaskId { get; private set; }
    public string? LastRolledBackTaskId { get; private set; }

    public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
    {
        _trace.Add(DispatchGateEvents.ReserveStart);
        ReserveTakeCount++;
        LastReservedTaskId = taskId;
        _reserved[taskId] = 1;
        _trace.Add(DispatchGateEvents.ReserveCommit);
        _equipment?.MarkReserveCommitted();
        _disableOnReserveCommit?.Invoke();
        if (_disableOnReserveCommit is not null)
            _trace.Add(DispatchGateEvents.DisableRoute);
        return Task.FromResult<ReservedSlot?>(new ReservedSlot(frameId, 1, 1, 1, "MAT-1"));
    }

    public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
    {
        _trace.Add(DispatchGateEvents.ReserveStart);
        ReservePutCount++;
        LastReservedTaskId = taskId;
        _reserved[taskId] = 1;
        _trace.Add(DispatchGateEvents.ReserveCommit);
        _equipment?.MarkReserveCommitted();
        _disableOnReserveCommit?.Invoke();
        if (_disableOnReserveCommit is not null)
            _trace.Add(DispatchGateEvents.DisableRoute);
        return Task.FromResult<ReservedSlot?>(new ReservedSlot(frameId, 1, 1, 1, materialId));
    }

    public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
    {
        _trace.Add(DispatchGateEvents.Rollback);
        RollbackTakeCount++;
        LastRolledBackTaskId = taskId;
        if (RollbackTakeSucceeds) _reserved.TryRemove(taskId, out _);
        return Task.FromResult(RollbackTakeSucceeds);
    }

    public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default)
    {
        _trace.Add(DispatchGateEvents.Rollback);
        RollbackPutCount++;
        LastRolledBackTaskId = taskId;
        if (RollbackPutSucceeds) _reserved.TryRemove(taskId, out _);
        return Task.FromResult(RollbackPutSucceeds);
    }

    public bool HasActiveReservation(string taskId) => _reserved.ContainsKey(taskId);

    public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default)
    {
        if (frameId == OccupiedFrameId)
            return Task.FromResult(new FrameOccupancy(10, 2, 0, 8));
        if (PutFrameId is long pf && frameId == pf)
            return Task.FromResult(new FrameOccupancy(10, 0, 0, 10));
        return Task.FromResult(new FrameOccupancy(10, 0, 0, 10));
    }

    public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
        => Task.FromResult(0);
    public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
        IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
    public int ConfirmPutCount { get; private set; }
    public int ConfirmTakeCount { get; private set; }
    public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default)
    {
        ConfirmPutCount++;
        return Task.FromResult(false);
    }
    public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default)
    {
        ConfirmTakeCount++;
        return Task.FromResult(false);
    }
    public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
        => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.NotFound, frameId, slotNo, null));
    public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default)
        => Task.FromResult<SlotLocation?>(null);
    public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
        => Task.FromResult(InventoryCorrectionResult.Empty());
    public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
}

internal sealed class TracingTaskService : IRcsTaskService
{
    private readonly CallTrace _trace;
    public TracingTaskService(CallTrace trace) => _trace = trace;

    public int DispatchTransitCount { get; private set; }
    public string? LastTaskId { get; private set; }
    public string? LastTaskType { get; private set; }
    public TransitDispatchArgs? LastArgs { get; private set; }

    public Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
    {
        _trace.Add(DispatchGateEvents.RcsDispatch);
        DispatchTransitCount++;
        LastTaskId = args.TaskId;
        LastTaskType = args.TaskType;
        LastArgs = args;
        return Task.FromResult(new RcsResult(true, 200, true, "ok", "", "{}", null, 1)
        {
            TaskId = args.TaskId ?? "missing"
        });
    }

    public Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default)
        => Task.FromResult(RcsResult.Fail("", "noop"));
    public Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default)
        => Task.FromResult(RcsResult.Fail("", "noop"));
    public Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default)
        => Task.FromResult(new RcsResult(true, 200, true, "ok", "", "{}", null, 1) { TaskId = rcsTaskId });
    public Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default)
        => Task.FromResult(RcsResult.Fail("", "noop"));
    public Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default)
        => Task.FromResult(RcsResult.Fail("", "noop"));
    public Task<RcsResult> AutoRedispatchAsync(string rcsTaskId, int maxRedoCount, CancellationToken ct = default)
        => Task.FromResult(RcsResult.Fail("", "noop"));
    public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default)
        => Task.FromResult(RcsResult.Fail("", "noop"));
    public Task<IReadOnlyList<RcsTaskRow>> GetRecentTasksAsync(int limit = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
    public Task<IReadOnlyList<RcsMsgRow>> GetRecentMessagesAsync(int limit = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    public Task<IReadOnlyList<RcsMsgRow>> QueryMessagesAsync(RcsMsgQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<RcsResult> DispatchPalletReturnAsync(long equipmentId, long? positionId, string fromCode, string toCode,
        long workLineId, string lineCode, string author, CancellationToken ct = default)
        => Task.FromResult(RcsResult.Fail("", "noop"));
}

internal sealed class TracingPlcOps : IPlcOperationService
{
    private readonly CallTrace _trace;
    public TracingPlcOps(CallTrace trace) => _trace = trace;
    public int WriteCount { get; private set; }

    public Task<IReadOnlyList<PlcReadResult>> ReadPointsAsync(long plcId, bool readOnlySignals = true, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlcReadResult>>(Array.Empty<PlcReadResult>());
    public Task<PlcReadResult> ReadRegisterAsync(long plcId, string registerAddress, int length, CancellationToken ct = default)
        => Task.FromResult(new PlcReadResult(null, "", null, registerAddress, 0, "", false, null, null));
    public Task<PlcWriteResult> WriteWithConfirmAsync(long plcId, string registerAddress, int value, string author, CancellationToken ct = default)
    {
        _trace.Add(DispatchGateEvents.PlcWrite);
        WriteCount++;
        return Task.FromResult(new PlcWriteResult(registerAddress, value, null, true, 0, null));
    }
    public Task<PlcWriteResult> VerifyWriteAsync(long plcId, string registerAddress, int expectedValue, CancellationToken ct = default)
        => Task.FromResult(new PlcWriteResult(registerAddress, expectedValue, null, true, 0, null));
}

/// <summary>
/// 路由 fake：默认全部解析成功，返回的编码刻意不使用 <c>FRAME-{id}</c> 形状——
/// 那是生产 <c>RouteResolver.ResolveFrameCellAsync</c> 明令禁止生成的假码，fake 不得示范。
/// 需要走「缺 LOCATION_MAP 拒发」路径时把对应 <c>Missing*</c> 置 true，解析即返回 null。
/// </summary>
internal sealed class FixedRoutes : IRouteResolver
{
    public bool MissingUpload { get; set; }
    public bool MissingUnload { get; set; }
    public bool MissingPositionCell { get; set; }
    public bool MissingFrameCell { get; set; }

    public Task<(string from, string to)?> ResolveUploadAsync(long equipmentId, long positionId, CancellationToken ct = default)
        => Task.FromResult(MissingUpload ? null : ((string, string)?)("FROM-A", "TO-B"));
    public Task<(string from, string to)?> ResolveUnloadAsync(long equipmentId, long positionId, CancellationToken ct = default)
        => Task.FromResult(MissingUnload ? null : ((string, string)?)("POS-CELL", "UNLOAD-CELL"));
    public Task<string?> ResolvePositionCellAsync(long equipmentId, long positionId, CancellationToken ct = default)
        => Task.FromResult(MissingPositionCell ? null : $"CELL-EQ{equipmentId}-P{positionId}");
    public Task<string?> ResolveFrameCellAsync(long frameId, CancellationToken ct = default)
        => Task.FromResult(MissingFrameCell ? null : $"RACK-CELL-{frameId}");
}

internal sealed class UnusedDbContextFactory : IDbContextFactory<CncDbContext>
{
    public CncDbContext CreateDbContext()
        => throw new InvalidOperationException("派工门禁 RED 不得走 IDbContextFactory");
    public Task<CncDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("派工门禁 RED 不得走 IDbContextFactory");
}

internal sealed class EmptyPoints : IPlcPointSource
{
    public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PlcPointDefinition>>(Array.Empty<PlcPointDefinition>());
    public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default)
        => GetAllAsync(ct);
    public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default)
        => GetAllAsync(ct);
    public void Invalidate() { }
}

internal sealed class NoopAlarms : IAlarmEventService
{
#pragma warning disable CS0067
    public event EventHandler<AlarmRow>? AlarmRaised;
    public event EventHandler? AlarmsChanged;
#pragma warning restore CS0067
    public int RaiseCount { get; private set; }
    public Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default)
    {
        RaiseCount++;
        return Task.CompletedTask;
    }
    public Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent, string? taskCode, CancellationToken ct = default)
    {
        RaiseCount++;
        return Task.FromResult(0L);
    }
    public Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default)
    {
        RaiseCount++;
        return Task.FromResult(0L);
    }
    public int NotFoundCount { get; private set; }
    public string? LastNotFoundReason { get; private set; }
    public Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default)
    {
        RaiseCount++;
        NotFoundCount++;
        LastNotFoundReason = reason;
        return Task.FromResult(0L);
    }
    public Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, string? reason = null, CancellationToken ct = default)
    {
        RaiseCount++;
        return Task.FromResult(0L);
    }
    public Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
    public Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
    public Task MarkHandledAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> DeleteAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> GetUnhandledCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class NoopWorkRecords : IWorkRecordService
{
    public Task<long> RecordStartAsync(WorkRecordStartArgs args, CancellationToken ct = default) => Task.FromResult(0L);
    public Task RecordResultAsync(long recordId, string result, string? remark, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkRecordRow?> FindOpenByPositionAsync(long equipmentId, long positionId, CancellationToken ct = default)
        => Task.FromResult<WorkRecordRow?>(null);
    public Task<IReadOnlyList<WorkRecordRow>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkRecordRow>>(Array.Empty<WorkRecordRow>());
    public Task<WorkShiftStats> GetShiftStatsAsync(CancellationToken ct = default) => Task.FromResult(new WorkShiftStats(0, 0, 0));
}

internal sealed class NoopTaskStore : IRcsTaskStore
{
    public Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default) => Task.FromResult(0L);
    public Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null, string? error = null, CancellationToken ct = default)
        => Task.FromResult(false);
    public Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<AutoRedoClaimResult> TryClaimAutoRedoAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
        => Task.FromResult(AutoRedoClaimResult.NotClaimable);
    public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
        => Task.FromResult<RcsTaskRow?>(null);
    public Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
    public Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

/// <summary>测试用精简 Validator：仅经 IEquipmentConfigService.GetWorkLine 判定（供非门禁专项夹具）。</summary>
internal sealed class WorkLineOnlyRoutingValidator : IRoutingAvailabilityValidator
{
    private readonly IEquipmentConfigService _equipment;
    public WorkLineOnlyRoutingValidator(IEquipmentConfigService equipment) => _equipment = equipment;

    public async Task<RoutingAvailabilityResult> ValidateAsync(
        DispatchRouteContext context, CancellationToken ct = default)
    {
        var line = await _equipment.GetWorkLineByEquipmentAsync(context.SourceEquipmentId, ct);
        if (line is null)
        {
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.NotFound, "WorkLine", null, "源路由不可用");
        }
        if (context.DestEquipmentId.IsApplicable)
        {
            if (context.DestEquipmentId.IsMissing)
            {
                return RoutingAvailabilityResult.Unavailable(
                    RoutingUnavailableReason.NotFound, "Equipment", null, "目标缺失");
            }
            var dest = await _equipment.GetWorkLineByEquipmentAsync(context.DestEquipmentId.Id!.Value, ct);
            if (dest is null)
            {
                return RoutingAvailabilityResult.Unavailable(
                    RoutingUnavailableReason.NotFound, "Equipment", context.DestEquipmentId.Id,
                    "目标路由不可用");
            }
        }
        return RoutingAvailabilityResult.Available(line);
    }
}

internal static class DispatchGateHarness
{
    public const long Eq = 30;
    public const long Pos = 1;
    public const long UploadFrame = 50;
    public const long DownloadFrame = 60;
    public const long LineId = 10;
    public const long CraftId = 20;
    public const string LineCode = "LINE-A";

    public static PositionScheduler CreateScheduler(
        IEquipmentConfigService equipment,
        ISlotAccountService slots,
        IRcsTaskService tasks,
        IPlcOperationService plc,
        SignalStateStore? store = null,
        IDispatchQueue? queue = null,
        IAlarmEventService? alarms = null,
        IEquipmentRoutingStore? routingStore = null,
        IRoutingAvailabilityValidator? validator = null,
        IRouteResolver? routes = null,
        IPlcPointSource? points = null,
        IRcsTaskStore? taskStore = null,
        int hasMatRecheckFailThreshold = 6)
    {
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions
            {
                SchedulerEnabled = false,
                ReconcileRetryIntervalMs = 5000,
                HasMatRecheckFailThreshold = hasMatRecheckFailThreshold
            }
        });
        var v = validator ?? new RoutingAvailabilityValidator(
            routingStore ?? throw new ArgumentNullException(nameof(routingStore),
                "须提供 routingStore 或 validator"),
            equipment,
            new FakeFrameRoutingStore(),
            NullLogger<RoutingAvailabilityValidator>.Instance);
        return new PositionScheduler(
            store ?? new SignalStateStore(),
            tasks,
            taskStore ?? new NoopTaskStore(),
            points ?? new EmptyPoints(),
            plc,
            routes ?? new FixedRoutes(),
            queue ?? new PriorityDispatchQueue(),
            alarms ?? new NoopAlarms(),
            options,
            NullLogger<PositionScheduler>.Instance,
            new NoopWorkRecords(),
            equipment,
            slots,
            v);
    }
}
