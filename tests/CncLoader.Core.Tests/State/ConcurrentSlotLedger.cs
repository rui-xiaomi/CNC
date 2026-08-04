using System.Collections.Concurrent;
using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// 线程安全槽位账 fake：同时作为外部条件写与 Reserve/Confirm/Rollback 原子状态存储。
/// 用 TCS gate 做确定性交错；记录线性化顺序。每个测试独立实例。
/// </summary>
internal sealed class ConcurrentSlotLedger : ISlotAccountStore
{
    private readonly object _gate = new();
    private readonly List<SlotRow> _slots;
    private readonly ConcurrentQueue<string> _trace = new();
    private List<SlotRow>? _txBuffer;
    private int _externalWriteCount;
    private int _reserveCommitCount;
    private int _commitCount;
    private int _rollbackCount;

    /// <summary>在条件 UPDATE 判定前等待（RunContinuationsAsynchronously）。</summary>
    public TaskCompletionSource? PauseBeforeExternalWrite { get; set; }

    /// <summary>单槽 TrySetExternalSlot 抛库异常（R20）。</summary>
    public bool ThrowOnExternalWrite { get; set; }

    /// <summary>模拟 affected=0 且重查非 Reserved、与目标不同（ConcurrencyConflict）。</summary>
    public bool SimulateConcurrencyConflict { get; set; }

    public IReadOnlyList<string> Trace => _trace.ToArray();
    public int ExternalWriteCount => Volatile.Read(ref _externalWriteCount);
    public int ReserveCommitCount => Volatile.Read(ref _reserveCommitCount);
    public int CommitCount => Volatile.Read(ref _commitCount);
    public int RollbackCount => Volatile.Read(ref _rollbackCount);
    public IReadOnlyList<SlotRow> Slots
    {
        get { lock (_gate) return _slots.Select(s => s.Clone()).ToList(); }
    }

    public ConcurrentSlotLedger(params SlotRow[] initial) =>
        _slots = initial.Select(s => s.Clone()).ToList();

    public void TraceEvent(string name) => _trace.Enqueue(name);

    public SlotRow Require(long frameId, int slotNo)
    {
        lock (_gate)
        {
            var s = _slots.FirstOrDefault(x => x.FrameId == frameId && x.SlotNo == slotNo)
                ?? throw new InvalidOperationException($"槽 {frameId}/{slotNo} 不存在");
            return s.Clone();
        }
    }

    public async Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
        long frameId, int slotNo, string targetState, string? materialId,
        bool clearRemarkAndBindTime, DateTime updateTime, CancellationToken ct = default)
    {
        if (string.Equals((targetState ?? string.Empty).Trim(), SlotStates.Reserved, StringComparison.Ordinal))
        {
            TraceEvent("InvalidTargetState");
            return new ExternalSlotWriteAttempt(0, null, InvalidTargetState: true);
        }

        TraceEvent("ExternalConditionalUpdateEnter");
        if (PauseBeforeExternalWrite is { } pause)
            await pause.Task.ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        if (ThrowOnExternalWrite)
            throw new InvalidOperationException("simulated db failure");

        lock (_gate)
        {
            Interlocked.Increment(ref _externalWriteCount);
            var src = _txBuffer ?? _slots;
            var idx = src.FindIndex(s => s.FrameId == frameId && s.SlotNo == slotNo);
            if (idx < 0)
            {
                TraceEvent("ExternalConditionalUpdate(affected=0)");
                TraceEvent("ClassifyNotFound");
                return new ExternalSlotWriteAttempt(0, null);
            }

            var current = src[idx];
            if (current.SlotState == SlotStates.Reserved)
            {
                TraceEvent("ExternalConditionalUpdate(affected=0)");
                TraceEvent("ClassifyReserved");
                return new ExternalSlotWriteAttempt(0, current.Clone());
            }

            if (SimulateConcurrencyConflict)
            {
                TraceEvent("ExternalConditionalUpdate(affected=0)");
                TraceEvent("ClassifyConcurrencyConflict");
                return new ExternalSlotWriteAttempt(0, current.Clone());
            }

            var alreadySame =
                current.SlotState == targetState
                && string.Equals(current.MaterialId, materialId, StringComparison.Ordinal)
                && (!clearRemarkAndBindTime || (current.Remark is null && current.BindTime is null));
            if (alreadySame)
            {
                TraceEvent("ExternalConditionalUpdate(affected=0)");
                TraceEvent("ClassifyUnchanged");
                return new ExternalSlotWriteAttempt(0, current.Clone());
            }

            var next = current.Clone();
            next.SlotState = targetState;
            next.MaterialId = materialId;
            next.UpdateTime = updateTime;
            if (clearRemarkAndBindTime)
            {
                next.Remark = null;
                next.BindTime = null;
            }

            src[idx] = next;
            TraceEvent("ExternalConditionalUpdate(affected=1)");
            return new ExternalSlotWriteAttempt(1, next.Clone());
        }
    }

    public Task<ISlotAccountSession> OpenAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _txBuffer = _slots.Select(s => s.Clone()).ToList();
            return Task.FromResult<ISlotAccountSession>(new Session(this));
        }
    }

    public Task<ReservedSlot?> ReservePutAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            TraceEvent("ReserveEnter");
            var candidates = _slots
                .Where(s => s.FrameId == frameId && s.SlotState == SlotStates.Empty)
                .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                .ToList();
            foreach (var slot in candidates)
            {
                if (slot.SlotState != SlotStates.Empty) continue;
                var now = DateTime.Now;
                slot.SlotState = SlotStates.Reserved;
                slot.MaterialId = materialId;
                slot.Remark = taskId;
                slot.BindSource = "RSV_PUT";
                slot.BindTime = now;
                Interlocked.Increment(ref _reserveCommitCount);
                TraceEvent("ReserveCommit");
                return Task.FromResult<ReservedSlot?>(
                    new ReservedSlot(frameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer, materialId));
            }

            TraceEvent("ReserveFailed");
            return Task.FromResult<ReservedSlot?>(null);
        }
    }

    public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            TraceEvent("ReserveEnter");
            var candidates = _slots
                .Where(s => s.FrameId == frameId && s.SlotState == SlotStates.Occupied)
                .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                .ToList();
            foreach (var slot in candidates)
            {
                if (slot.SlotState != SlotStates.Occupied) continue;
                var now = DateTime.Now;
                slot.SlotState = SlotStates.Reserved;
                slot.Remark = taskId;
                slot.BindSource = "RSV_TAKE";
                slot.BindTime = now;
                Interlocked.Increment(ref _reserveCommitCount);
                TraceEvent("ReserveCommit");
                return Task.FromResult<ReservedSlot?>(
                    new ReservedSlot(frameId, slot.SlotNo, slot.LayerNo, slot.PosInLayer, slot.MaterialId));
            }

            TraceEvent("ReserveFailed");
            return Task.FromResult<ReservedSlot?>(null);
        }
    }

    public Task<bool> ConfirmPutAsync(string taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            TraceEvent("ConfirmPutEnter");
            if (_slots.Any(s => s.Remark == taskId && s.SlotState == SlotStates.Occupied))
            {
                TraceEvent("ConfirmPutIdempotent");
                return Task.FromResult(true);
            }

            // 原子条件：Reserved + REMARK + BIND_SOURCE==RSV_PUT（与生产 ExecuteUpdate 对齐）
            var slot = _slots.FirstOrDefault(s =>
                s.Remark == taskId
                && s.SlotState == SlotStates.Reserved
                && s.BindSource == "RSV_PUT");
            if (slot is null)
            {
                TraceEvent("ConfirmPutRejected");
                return Task.FromResult(false);
            }

            slot.SlotState = SlotStates.Occupied;
            slot.BindSource = "CONFIRMED";
            slot.BindTime = DateTime.Now;
            TraceEvent("ConfirmPutCommit");
            return Task.FromResult(true);
        }
    }

    public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            TraceEvent("ConfirmTakeEnter");
            if (_slots.Any(s => s.Remark == taskId && s.SlotState == SlotStates.Empty))
            {
                TraceEvent("ConfirmTakeIdempotent");
                return Task.FromResult(true);
            }

            var slot = _slots.FirstOrDefault(s =>
                s.Remark == taskId
                && s.SlotState == SlotStates.Reserved
                && s.BindSource == "RSV_TAKE");
            if (slot is null)
            {
                TraceEvent("ConfirmTakeRejected");
                return Task.FromResult(false);
            }

            slot.SlotState = SlotStates.Empty;
            slot.MaterialId = null;
            slot.Remark = null;
            slot.BindTime = null;
            TraceEvent("ConfirmTakeCommit");
            return Task.FromResult(true);
        }
    }

    public Task<bool> RollbackPutAsync(string taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            TraceEvent("RollbackPutEnter");
            var slot = _slots.FirstOrDefault(s =>
                s.Remark == taskId
                && s.SlotState == SlotStates.Reserved
                && s.BindSource == "RSV_PUT");
            if (slot is null)
            {
                TraceEvent("RollbackPutRejected");
                return Task.FromResult(false);
            }

            slot.SlotState = SlotStates.Empty;
            slot.MaterialId = null;
            slot.Remark = null;
            slot.BindTime = null;
            TraceEvent("RollbackPutCommit");
            return Task.FromResult(true);
        }
    }

    public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            TraceEvent("RollbackTakeEnter");
            var slot = _slots.FirstOrDefault(s =>
                s.Remark == taskId
                && s.SlotState == SlotStates.Reserved
                && s.BindSource == "RSV_TAKE");
            if (slot is null)
            {
                TraceEvent("RollbackTakeRejected");
                return Task.FromResult(false);
            }

            slot.SlotState = SlotStates.Occupied;
            slot.Remark = null;
            slot.BindTime = null;
            TraceEvent("RollbackTakeCommit");
            return Task.FromResult(true);
        }
    }

    private sealed class Session(ConcurrentSlotLedger owner) : ISlotAccountSession
    {
        private bool _completed;

        public Task<SlotRow?> FindByFrameSlotAsync(long frameId, int slotNo, CancellationToken ct = default)
        {
            lock (owner._gate)
            {
                var src = owner._txBuffer ?? owner._slots;
                return Task.FromResult(src.FirstOrDefault(s => s.FrameId == frameId && s.SlotNo == slotNo)?.Clone());
            }
        }

        public Task<IReadOnlyList<SlotRow>> FindByFrameOrderedAsync(long frameId, CancellationToken ct = default)
        {
            lock (owner._gate)
            {
                owner.TraceEvent("ReadIdentity");
                var src = owner._txBuffer ?? owner._slots;
                IReadOnlyList<SlotRow> rows = src
                    .Where(s => s.FrameId == frameId)
                    .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                    .Select(s => s.Clone())
                    .ToList();
                return Task.FromResult(rows);
            }
        }

        public async Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
            long frameId, int slotNo, string targetState, string? materialId,
            bool clearRemarkAndBindTime, DateTime updateTime, CancellationToken ct = default,
            InventorySlotWriteExtras? extras = null)
        {
            if (string.Equals((targetState ?? string.Empty).Trim(), SlotStates.Reserved, StringComparison.Ordinal))
            {
                owner.TraceEvent("InvalidTargetState");
                return new ExternalSlotWriteAttempt(0, null, InvalidTargetState: true);
            }

            owner.TraceEvent("ExternalConditionalUpdateEnter");
            if (owner.PauseBeforeExternalWrite is { } pause)
                await pause.Task.ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (owner.ThrowOnExternalWrite)
                throw new InvalidOperationException("simulated db failure");

            lock (owner._gate)
            {
                Interlocked.Increment(ref owner._externalWriteCount);
                // WHERE 条件按已提交态评估（模拟他连接 Reserve 已提交；与 InnoDB 条件 UPDATE 可见性对齐）。
                var committed = owner._slots.FirstOrDefault(s => s.FrameId == frameId && s.SlotNo == slotNo);
                if (committed is null)
                {
                    owner.TraceEvent("ExternalConditionalUpdate(affected=0)");
                    owner.TraceEvent("ClassifyNotFound");
                    return new ExternalSlotWriteAttempt(0, null);
                }

                if (committed.SlotState == SlotStates.Reserved)
                {
                    owner.TraceEvent("ExternalConditionalUpdate(affected=0)");
                    owner.TraceEvent("ClassifyReserved");
                    return new ExternalSlotWriteAttempt(0, committed.Clone());
                }

                if (owner.SimulateConcurrencyConflict)
                {
                    owner.TraceEvent("ExternalConditionalUpdate(affected=0)");
                    owner.TraceEvent("ClassifyConcurrencyConflict");
                    return new ExternalSlotWriteAttempt(0, committed.Clone());
                }

                var writeTarget = owner._txBuffer ?? owner._slots;
                var idx = writeTarget.FindIndex(s => s.FrameId == frameId && s.SlotNo == slotNo);
                if (idx < 0)
                {
                    owner.TraceEvent("ExternalConditionalUpdate(affected=0)");
                    owner.TraceEvent("ClassifyNotFound");
                    return new ExternalSlotWriteAttempt(0, null);
                }

                var current = writeTarget[idx];
                var alreadySame =
                    current.SlotState == targetState
                    && string.Equals(current.MaterialId, materialId, StringComparison.Ordinal);
                if (alreadySame)
                {
                    owner.TraceEvent("ExternalConditionalUpdate(affected=0)");
                    owner.TraceEvent("ClassifyUnchanged");
                    return new ExternalSlotWriteAttempt(0, current.Clone());
                }

                var next = current.Clone();
                next.SlotState = targetState;
                next.MaterialId = materialId;
                next.UpdateTime = updateTime;
                if (extras is not null)
                {
                    next.LastVerifyTime = extras.LastVerifyTime;
                    if (extras.ApplyBindFields)
                    {
                        next.BindSource = extras.BindSource;
                        next.BindTime = extras.BindTime;
                    }
                }

                writeTarget[idx] = next;
                owner.TraceEvent("ExternalConditionalUpdate(affected=1)");
                return new ExternalSlotWriteAttempt(1, next.Clone());
            }
        }

        public Task CommitAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (owner._gate)
            {
                if (owner._txBuffer is null) return Task.CompletedTask;
                owner._slots.Clear();
                owner._slots.AddRange(owner._txBuffer.Select(s => s.Clone()));
                owner._txBuffer = null;
                Interlocked.Increment(ref owner._commitCount);
                owner.TraceEvent("Commit");
                _completed = true;
                return Task.CompletedTask;
            }
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            lock (owner._gate)
            {
                if (_completed) return Task.CompletedTask;
                owner._txBuffer = null;
                Interlocked.Increment(ref owner._rollbackCount);
                owner.TraceEvent("Rollback");
                _completed = true;
                return Task.CompletedTask;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
                await RollbackAsync();
        }
    }
}
