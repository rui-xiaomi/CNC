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
    /// <summary>可选顺序探针；null 时无副作用。</summary>
    public List<string>? OrderSink { get; set; }

    public void Reset()
    {
        Volatile.Write(ref _calls, 0);
        Contexts.Clear();
        Results.Clear();
    }

    public async Task<RoutingAvailabilityResult> ValidateAsync(
        DispatchRouteContext context, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        Contexts.Add(context);
        OrderSink?.Add("Validate");
        var r = await _inner.ValidateAsync(context, ct);
        Results.Add(r);
        return r;
    }
}

/// <summary>计数包装真实 Resolver（PalletReturn / 类型化门禁断言发送边界次数）。</summary>
internal sealed class CountingManagedRouteResolver : IManagedDispatchRouteResolver
{
    private readonly IManagedDispatchRouteResolver _inner;
    private int _calls;

    public CountingManagedRouteResolver(IManagedDispatchRouteResolver inner) => _inner = inner;

    public int CallCount => Volatile.Read(ref _calls);
    public List<(string? From, string? To)> Args { get; } = new();
    /// <summary>可选顺序探针；null 时无副作用。</summary>
    public List<string>? OrderSink { get; set; }

    public void Reset()
    {
        Volatile.Write(ref _calls, 0);
        Args.Clear();
    }

    public async Task<ManagedDispatchRouteResult> ResolveAsync(
        string? fromCode, string? toCode, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        Args.Add((fromCode, toCode));
        OrderSink?.Add("Resolve");
        return await _inner.ResolveAsync(fromCode, toCode, ct);
    }
}

internal sealed class FakeRcsHttpClient : IRcsClient
{
    public int TransitCount { get; private set; }
    public int ExcuteCount { get; private set; }
    public int CancelCount { get; private set; }
    public int QueryCount { get; private set; }
    public string? LastQueryAtBaseUrl { get; private set; }
    public int SendCount => TransitCount + ExcuteCount;
    public List<string> TransitTaskIds { get; } = new();
    /// <summary>可选顺序探针（Grab/Identify/Inventory RED）；null 时无副作用。</summary>
    public List<string>? OrderSink { get; set; }
    /// <summary>下一次 Transit 返回失败（非路由；FailureKind=SendFailed）。</summary>
    public bool FailNextTransit { get; set; }

    public Task<RcsResult> TransitTaskAsync(TransitTaskRequest req, CancellationToken ct = default)
    {
        TransitCount++;
        TransitTaskIds.Add(req.TaskId ?? "");
        OrderSink?.Add("RcsTransit");
        if (FailNextTransit)
        {
            FailNextTransit = false;
            return Task.FromResult(RcsResult.Fail(req.TaskId ?? "", "rcs-send-failed"));
        }
        return Task.FromResult(new RcsResult(true, 200, true, "ok", "{}", "{}", null, 1)
        {
            TaskId = req.TaskId
        });
    }

    public Task<RcsResult> ExcuteTaskAsync(ExcuteTaskRequest req, CancellationToken ct = default)
    {
        ExcuteCount++;
        OrderSink?.Add("RcsExcute");
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

    public Task<RcsResult> QueryTaskAtAsync(QueryTaskRequest req, RcsConnectionConfig probe, CancellationToken ct = default)
    {
        LastQueryAtBaseUrl = probe.BaseUrl;
        return QueryTaskAsync(req, ct);
    }
}

internal sealed class MutableRcsTaskStore : IRcsTaskStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RcsTaskRow> _rows = new(StringComparer.Ordinal);
    private long _nextId = 1;

    public int CreateCount { get; private set; }
    public int IncrementRedoCount { get; private set; }
    public int TryClaimCallCount { get; private set; }
    public int TryClaimSuccessCount { get; private set; }
    /// <summary>兼容旧断言名：等同 <see cref="TryClaimCallCount"/>。</summary>
    public int TryIncrementCallCount => TryClaimCallCount;
    /// <summary>兼容旧断言名：等同 <see cref="TryClaimSuccessCount"/>。</summary>
    public int TryIncrementSuccessCount => TryClaimSuccessCount;
    public int SetDispatchedCount { get; private set; }
    public int UpdateStateCount { get; private set; }
    public List<RcsTaskRecord> Created { get; } = new();
    /// <summary>可选顺序探针（Grab/Identify/Inventory RED）；null 时无副作用。</summary>
    public List<string>? OrderSink { get; set; }
    /// <summary>下次 TryClaimAutoRedo 抛异常（fail-closed）。</summary>
    public Exception? ThrowOnNextTryClaim { get; set; }
    /// <summary>兼容旧名。</summary>
    public Exception? ThrowOnNextTryIncrement
    {
        get => ThrowOnNextTryClaim;
        set => ThrowOnNextTryClaim = value;
    }
    /// <summary>
    /// 可选异步门闩：在真正 Claim 前 await（供并发/TOCTOU 确定性交错）；返回后继续原逻辑。
    /// </summary>
    public Func<string, int, Task>? BeforeTryClaimAsync { get; set; }
    /// <summary>兼容旧名。</summary>
    public Func<string, int, Task>? BeforeTryIncrementAsync
    {
        get => BeforeTryClaimAsync;
        set => BeforeTryClaimAsync = value;
    }

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
            OrderSink?.Add("CreateTask");
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

    public async Task<AutoRedoClaimResult> TryClaimAutoRedoAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
    {
        TryClaimCallCount++;
        OrderSink?.Add("TryClaim");

        if (ThrowOnNextTryClaim is { } ex)
        {
            ThrowOnNextTryClaim = null;
            throw ex;
        }

        if (BeforeTryClaimAsync is not null)
            await BeforeTryClaimAsync(rcsTaskId, maxRedo);

        lock (_gate)
        {
            if (!_rows.TryGetValue(rcsTaskId, out var r))
                return AutoRedoClaimResult.NotFound;
            if (r.RedoCount >= maxRedo)
                return AutoRedoClaimResult.LimitReached;
            if (!AutoRedoClaimRules.IsClaimableState(r.TaskState))
                return AutoRedoClaimResult.NotClaimable;

            _rows[rcsTaskId] = r with
            {
                RedoCount = r.RedoCount + 1,
                TaskState = RcsTaskState.Dispatched,
                ErrorMsg = null
            };
            TryClaimSuccessCount++;
            return AutoRedoClaimResult.Claimed;
        }
    }

    public void ResetTryIncrementCounters()
    {
        TryClaimCallCount = 0;
        TryClaimSuccessCount = 0;
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
    private readonly Dictionary<string, int> _findCounts = new(StringComparer.Ordinal);
    /// <summary>某 RcsCode 被 FindByRcsCodeAsync 命中达到该次数后（含本次），将该码 STATE 置为 1。</summary>
    private readonly Dictionary<string, int> _disableAfterFindCount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _disableBeforeReturnFindCount = new(StringComparer.Ordinal);

    public int ResolveByCodeCount { get; private set; }
    public int ResolveAreaCallCount { get; private set; }
    public int ResolveFrameCallCount { get; private set; }
    public int FindByRcsCodeCallCount { get; private set; }
    public List<string> ResolvedCodes { get; } = new();
    public List<string> FindByRcsCodeArgs { get; } = new();
    public List<string> ResolveAreaArgs { get; } = new();
    public List<(long FrameId, string RcsType)> ResolveFrameArgs { get; } = new();
    /// <summary>某 LocName 被 ResolveAreaAsync 命中达到该次数后（含本次返回后），将该角色全部行 STATE 置 1。</summary>
    private readonly Dictionary<string, int> _disableAreaAfterResolveCount = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _resolveAreaCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Count, string NewName)> _mutateLocNameAfterResolveArea =
        new(StringComparer.Ordinal);
    /// <summary>某 FrameId 被 ResolveFrameAsync 命中达到该次数后（含本次返回后），将该 Frame 全部 Map 行 STATE 置 1。</summary>
    private readonly Dictionary<long, int> _disableFrameAfterResolveCount = new();
    private readonly Dictionary<long, int> _resolveFrameCounts = new();

    public void Seed(LocationMapItem item, string state = "0")
    {
        // 禁止给 AREA/FRAME「顺便」补 EquipmentId：按调用方传入原样入账
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

    /// <summary>第 N 次（1-based）FindByRcsCode 命中该码时，在返回后将该码全部行 STATE 置 1（供 Pre+Final TOCTOU）。</summary>
    public void DisableAfterFindCount(string rcsCode, int findCount)
        => _disableAfterFindCount[rcsCode] = findCount;

    /// <summary>第 N 次 Find 在快照前将该码 STATE 置 1（供 Grab/Identify Final-only 单次 Resolve 即拒）。</summary>
    public void DisableBeforeReturnOnFindCount(string rcsCode, int findCount)
        => _disableBeforeReturnFindCount[rcsCode] = findCount;

    /// <summary>
    /// 第 N 次 ResolveAreaAsync(locName) 仍返回活动快照，返回后将该 LocName 全部行 STATE=1
    ///（供 UI Pre 解析 To 后、Service Final 前禁用）。
    /// </summary>
    public void DisableAreaAfterResolveCount(string locName, int resolveCount)
        => _disableAreaAfterResolveCount[locName] = resolveCount;

    /// <summary>
    /// 第 N 次 ResolveFrameAsync(frameId,*) 仍返回活动快照，返回后将该 FrameId 全部 Map 行 STATE=1
    ///（供 Inventory 业务 Pre 后、Identify Service Final 前禁用）。
    /// </summary>
    public void DisableFrameMapAfterResolveCount(long frameId, int resolveCount)
        => _disableFrameAfterResolveCount[frameId] = resolveCount;

    /// <summary>按 RcsCode 改写 LocName（供换架 AREA 角色/类型 TOCTOU）。</summary>
    public void SetLocNameByCode(string rcsCode, string locName)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].RcsCode != rcsCode) continue;
            _rows[i] = _rows[i] with { LocName = locName };
        }
    }

    /// <summary>按 RcsCode 改写 LocType（供错误端点类型拒发）。</summary>
    public void SetLocTypeByCode(string rcsCode, string locType)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].RcsCode != rcsCode) continue;
            _rows[i] = _rows[i] with { LocType = locType };
        }
    }

    /// <summary>
    /// 第 N 次 ResolveAreaAsync 仍返回活动快照，返回后将该 LocName 行的 LocName 改为 newLocName
    ///（STATE 保持活动，供 Final 角色校验）。
    /// </summary>
    public void MutateLocNameAfterResolveAreaCount(string locName, int resolveCount, string newLocName)
        => _mutateLocNameAfterResolveArea[locName] = (resolveCount, newLocName);

    public IReadOnlyList<LocationMapRoutingRow> SnapshotByCode(string rcsCode)
        => _rows.Where(x => x.RcsCode == rcsCode).ToList();

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
    {
        ResolveFrameCallCount++;
        ResolveFrameArgs.Add((frameId, rcsType));
        _resolveFrameCounts.TryGetValue(frameId, out var n);
        n++;
        _resolveFrameCounts[frameId] = n;

        var hit = _rows
            .Where(i => i.State == "0" && i.FrameId == frameId && i.RcsType == rcsType)
            .Select(ToItem).FirstOrDefault();

        if (_disableFrameAfterResolveCount.TryGetValue(frameId, out var after) && n >= after)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].FrameId != frameId) continue;
                _rows[i] = _rows[i] with { State = "1" };
            }
        }

        return Task.FromResult(hit);
    }

    public Task<LocationMapItem?> ResolveAreaAsync(string locName, CancellationToken ct = default)
    {
        ResolveAreaCallCount++;
        ResolveAreaArgs.Add(locName);
        _resolveAreaCounts.TryGetValue(locName, out var n);
        n++;
        _resolveAreaCounts[locName] = n;

        var hit = _rows
            .Where(i => i.State == "0" && i.LocName == locName)
            .Select(ToItem).FirstOrDefault();

        if (_disableAreaAfterResolveCount.TryGetValue(locName, out var after) && n >= after)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].LocName != locName) continue;
                _rows[i] = _rows[i] with { State = "1" };
            }
        }

        if (_mutateLocNameAfterResolveArea.TryGetValue(locName, out var mut) && n >= mut.Count)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].LocName != locName) continue;
                _rows[i] = _rows[i] with { LocName = mut.NewName };
            }
        }

        return Task.FromResult(hit);
    }

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
    {
        FindByRcsCodeCallCount++;
        FindByRcsCodeArgs.Add(rcsCode);
        _findCounts.TryGetValue(rcsCode, out var n);
        n++;
        _findCounts[rcsCode] = n;

        // Final-only：第 N 次在快照前禁用，使单次 Resolve 即读到 STATE=1
        if (_disableBeforeReturnFindCount.TryGetValue(rcsCode, out var before) && n >= before)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].RcsCode != rcsCode) continue;
                _rows[i] = _rows[i] with { State = "1" };
            }
        }

        // Pre+Final：先快照再禁用，第 after 次仍返回禁用前状态
        var hits = _rows.Where(x => x.RcsCode == rcsCode).ToList();
        if (_disableAfterFindCount.TryGetValue(rcsCode, out var after) && n >= after)
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].RcsCode != rcsCode) continue;
                _rows[i] = _rows[i] with { State = "1" };
            }
        }

        return Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(hits);
    }

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

    /// <summary>二次确认的应答（默认确认，便于沿用「真实 RCS 模式下点了确定」的既有断言）。</summary>
    public bool ConfirmAnswer { get; set; } = true;
    public int ConfirmCount { get; private set; }
    public List<string> Confirmations { get; } = new();
    public int AlertCount { get; private set; }

    public bool Confirm(string message, string title)
    {
        ConfirmCount++;
        Confirmations.Add(message);
        return ConfirmAnswer;
    }

    public void Alert(string message, string title)
    {
        AlertCount++;
        All.Add(message);
    }

    /// <summary>多步场景（如 R19 第二次派工）前清零，避免累加计数污染断言。</summary>
    public void Reset()
    {
        SuccessCount = 0;
        WarningCount = 0;
        ErrorCount = 0;
        InfoCount = 0;
        ConfirmCount = 0;
        AlertCount = 0;
        Confirmations.Clear();
        All.Clear();
    }
}

/// <summary>手动/重发门禁测试宿主：真实 Service + ViewModel + Resolver/Validator。</summary>
internal sealed class ManualReplayHarness
{
    public required MutableEquipmentRoutingStore Store { get; init; }
    public required FakeLocationMapForRouting LocationMap { get; init; }
    public required FakeFrameRoutingStore Frames { get; init; }
    public required FakeRcsHttpClient Client { get; init; }
    public required MutableRcsTaskStore TaskStore { get; init; }
    public required TrackingCallbackProcessor Callbacks { get; init; }
    public required CountingRoutingValidator Validator { get; init; }
    public required CountingManagedRouteResolver RouteResolver { get; init; }
    public required RcsTaskService TaskService { get; init; }
    public required RcsViewModel ViewModel { get; init; }
    public required StubRuntime Runtime { get; init; }
    public required TrackingSlotsForClosure Slots { get; init; }
    public required TracingPlcOps Plc { get; init; }
    public required FakeNotifyCounter Notify { get; init; }
    public required CallTrace Trace { get; init; }
    public required StubChangeFrame ChangeFrame { get; init; }

    public static ManualReplayHarness Create(bool seedActiveRoute = true)
        => CreateCore(seedActiveRoute, seedTypedPalletReturn: false, palletReturnArea: "托盘回收区");

    /// <summary>空托盘回收 RED 夹具：类型化 AREA/FRAME/POSITION 种子 + 英文 PALLET_RETURN。</summary>
    public static ManualReplayHarness CreateForPalletReturn()
        => CreateCore(seedActiveRoute: false, seedTypedPalletReturn: true,
            palletReturnArea: TypedEndpointSeedShapes.LocPalletReturn);

    private static ManualReplayHarness CreateCore(
        bool seedActiveRoute, bool seedTypedPalletReturn, string palletReturnArea)
    {
        var store = new MutableEquipmentRoutingStore();
        var loc = new FakeLocationMapForRouting();
        var frames = new FakeFrameRoutingStore();
        frames.Seed(50);
        frames.Seed(60);

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

        if (seedTypedPalletReturn)
        {
            TypedEndpointSeedShapes.SeedStandardAreas(loc);
            loc.Seed(TypedEndpointSeedShapes.PositionCellMap());
            loc.Seed(TypedEndpointSeedShapes.FrameShelf());
            loc.Seed(TypedEndpointSeedShapes.FrameCell());
            TypedEndpointSeedShapes.SeedActiveEquipmentChain(store);
            frames.Seed(TypedEndpointSeedShapes.FrameIdTransit);
            frames.Seed(TypedEndpointSeedShapes.FrameIdDownload);
        }

        var trace = new CallTrace();
        var routingEquipment = new TracingEquipmentConfigService(store, trace);
        // UI 列表方法不得走 UnusedDbContextFactory，避免 InitializeAsync 竞态改写 StatusMessage
        var equipment = new UiSafeEquipmentConfigService(routingEquipment);
        var validator = new CountingRoutingValidator(
            new RoutingAvailabilityValidator(
                store, routingEquipment, frames, NullLogger<RoutingAvailabilityValidator>.Instance));
        var resolver = new CountingManagedRouteResolver(
            new ManagedDispatchRouteResolver(
                loc, frames, NullLogger<ManagedDispatchRouteResolver>.Instance));

        var client = new FakeRcsHttpClient();
        var taskStore = new MutableRcsTaskStore();
        var callbacks = new TrackingCallbackProcessor();
        var slots = new TrackingSlotsForClosure();
        var taskService = new RcsTaskService(
            client, taskStore, new NoopMsgLog(), callbacks,
            resolver, validator,
            NullLogger<RcsTaskService>.Instance, slots);

        var plc = new TracingPlcOps(trace);
        var notify = new FakeNotifyCounter();
        var runtime = new StubRuntime();
        var changeFrame = new StubChangeFrame();
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions
            {
                UseSimulator = true,
                SchedulerEnabled = false,
                PalletReturnArea = palletReturnArea
            }
        });

        // headless：通知与 UI 线程都走注入的接缝，避免 Growl / Dispatcher 无视觉树 NRE 假红
        var vm = new RcsViewModel(
            taskService,
            loc,
            new StubWorkLines(),
            equipment,
            new StubFrames(),
            new RcsCallbackNotifier(),
            changeFrame,
            new StubConnConfig(),
            runtime,
            new StubCallbackListener(),
            new StubScheduler(),
            new StubUser(),
            resolver,
            validator,
            notify,
            new UI.ImmediateUiDispatcher(),
            options);

        return new ManualReplayHarness
        {
            Store = store,
            LocationMap = loc,
            Frames = frames,
            Client = client,
            TaskStore = taskStore,
            Callbacks = callbacks,
            Validator = validator,
            RouteResolver = resolver,
            TaskService = taskService,
            ViewModel = vm,
            Runtime = runtime,
            Slots = slots,
            Plc = plc,
            Notify = notify,
            Trace = trace,
            ChangeFrame = changeFrame
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
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
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

    internal sealed class StubChangeFrame : IChangeFrameOrchestrator
    {
#pragma warning disable CS0067
        public event EventHandler<ChangeFrameProgressEvent>? ProgressChanged;
#pragma warning restore CS0067
        public int CallCount { get; private set; }
        public Task<string> ChangeFrameAsync(long equipmentId, FrameRole role, string author, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult("txn");
        }
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

    internal sealed class StubRuntime : IRcsRuntimeConfig
    {
        public string BaseUrl { get; private set; } = "http://127.0.0.1:8090";
        public string ClientCode { get; private set; } = "CNC";
        public string Version => "1";
        public string TokenCode => "t";
        public int RequestTimeoutMs => 10000;
        public int MaxRetries => 1;
        public string CallbackHost => "0.0.0.0";
        public int CallbackPort => 9080;
        public int PollIntervalMs => 3000;
        public string BootCallbackHost => CallbackHost;
        public int BootCallbackPort => CallbackPort;
        public int ApplyCount { get; private set; }

        public void Apply(RcsConnectionConfig config)
        {
            ApplyCount++;
            BaseUrl = config.BaseUrl;
            ClientCode = config.ClientCode;
        }

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

/// <summary>
/// Inventory RED 用：GetWorkLine 走真实路由 Store；GetBindingByFrame 读内存 FrameBinds，
/// 避免 EquipmentConfigService 直查 DB（UnusedDbContextFactory）。
/// </summary>
internal sealed class StoreBackedFrameBindEquipment : IEquipmentConfigService
{
    private readonly TracingEquipmentConfigService _routing;
    private readonly MutableEquipmentRoutingStore _store;

    public StoreBackedFrameBindEquipment(MutableEquipmentRoutingStore store, CallTrace? trace = null)
    {
        _store = store;
        _routing = new TracingEquipmentConfigService(store, trace ?? new CallTrace());
    }

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

    public Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default)
    {
        var binds = _store.FrameBinds
            .Where(b => b.FrameId == frameId && b.State == "0")
            .Select(b =>
            {
                var role = int.TryParse(b.FrameRole, out var n) && Enum.IsDefined(typeof(FrameRole), n)
                    ? (FrameRole)n
                    : FrameRole.Transit;
                return new FrameBindingInfo(b.EquipmentId, role);
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<FrameBindingInfo>>(binds);
    }

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
    public Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId, string author, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default)
        => Task.FromResult(new DeleteCheckResult(true, 0, ""));
    public Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>空派工队列（Inventory 互斥检查）。</summary>
internal sealed class EmptyDispatchQueue : IDispatchQueue
{
    public int Count => 0;
    public void Enqueue(DispatchItem item) { }
    public DispatchItem? Dequeue() => null;
    public void Clear() { }
}

/// <summary>无在途换架（Inventory 互斥检查）。</summary>
internal sealed class IdleChangeFrameOrchestrator : IChangeFrameOrchestrator
{
#pragma warning disable CS0067
    public event EventHandler<ChangeFrameProgressEvent>? ProgressChanged;
#pragma warning restore CS0067
    public Task<string> ChangeFrameAsync(long equipmentId, FrameRole role, string author, CancellationToken ct = default)
        => Task.FromResult("txn");
    public IReadOnlyList<ChangeFrameProgressEvent> GetActiveTransactions()
        => Array.Empty<ChangeFrameProgressEvent>();
}

/// <summary>换架/盘点夹具用空调度器（仅 InvalidateFrameBindingCache）。</summary>
internal sealed class IdlePositionScheduler : IPositionScheduler
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
