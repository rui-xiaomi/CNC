using CncLoader.Core.Rcs;
using CncLoader.Data;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P0-6 第一组：真实 <see cref="SlotAccountService.SetSlotAsync"/> 单槽外部写保护（R1–R6）。
/// </summary>
[TestFixture]
public sealed class SlotAccountReservedProtectionTests
{
    [Test]
    public async Task R1_Reserved人工校正为有料_不得覆盖状态与预记字段()
    {
        var initial = ReservedSlot(
            remark: "task-test-001",
            bindSource: "RSV_PUT",
            materialId: "MAT-PUT-001");
        var (sut, store, _) = CreateSut(initial);
        var before = initial.Clone();

        var result = await sut.SetSlotAsync(
            initial.FrameId, initial.SlotNo, "MAT-MANUAL-99", SlotStates.Occupied, "tester");

        var after = store.RequireSlot();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.ReservationConflict));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(after.Remark, Is.EqualTo(before.Remark));
            Assert.That(after.BindSource, Is.EqualTo(before.BindSource));
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(after.BindTime, Is.EqualTo(before.BindTime));
            Assert.That(after.UpdateTime, Is.EqualTo(before.UpdateTime));
            Assert.That(store.AtomicUpdateCount, Is.EqualTo(1));
            Assert.That(store.SessionSaveCount, Is.EqualTo(0), "不得走旧 Apply/Save 路径");
        });
    }

    [Test]
    public async Task R2_Reserved清槽_不得清空状态与预记字段()
    {
        var initial = ReservedSlot(
            remark: "task-test-002",
            bindSource: "RSV_TAKE",
            materialId: "MAT-TAKE-001");
        var (sut, store, _) = CreateSut(initial);
        var before = initial.Clone();

        var result = await sut.SetSlotAsync(
            initial.FrameId, initial.SlotNo, null, SlotStates.Empty, "tester");

        var after = store.RequireSlot();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.ReservationConflict));
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(after.Remark, Is.EqualTo(before.Remark), "清槽不得清空 REMARK/taskId");
            Assert.That(after.BindSource, Is.EqualTo(before.BindSource));
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(after.BindTime, Is.EqualTo(before.BindTime));
            Assert.That(store.AtomicUpdateCount, Is.EqualTo(1));
            Assert.That(store.SessionSaveCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R3_Reserved且REMARK为空_仍拒绝且记数据不一致Warning()
    {
        var initial = ReservedSlot(
            remark: null,
            bindSource: "ILLEGAL",
            materialId: "MAT-ODD-001");
        var (sut, store, logger) = CreateSut(initial);
        var before = initial.Clone();

        var result = await sut.SetSlotAsync(
            initial.FrameId, initial.SlotNo, null, SlotStates.Empty, "tester");

        var after = store.RequireSlot();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.ReservationConflict));
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(after.Remark, Is.EqualTo(before.Remark));
            Assert.That(after.BindSource, Is.EqualTo(before.BindSource));
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(after.BindTime, Is.EqualTo(before.BindTime));
            Assert.That(store.SessionSaveCount, Is.EqualTo(0));
            Assert.That(logger.WarningCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(logger.Warnings.Any(m =>
                    m.Contains("不一致", StringComparison.Ordinal) ||
                    m.Contains("异常预记", StringComparison.Ordinal) ||
                    m.Contains("REMARK", StringComparison.Ordinal)),
                Is.True);
        });
    }

    [Test]
    public async Task R4_非Reserved正常校正_应按现有语义更新并保存()
    {
        var initial = new SlotRow
        {
            Id = 40,
            FrameId = 7,
            SlotNo = 4,
            LayerNo = 1,
            PosInLayer = 4,
            SlotState = SlotStates.Empty,
            MaterialId = null,
            Remark = null,
            BindSource = null,
            BindTime = null,
            LastVerifyTime = null,
            UpdateTime = new DateTime(2026, 8, 1, 8, 0, 0),
        };
        var before = initial.Clone();
        var (sut, store, _) = CreateSut(initial);

        var result = await sut.SetSlotAsync(
            initial.FrameId, initial.SlotNo, "MAT-OK-001", SlotStates.Occupied, "tester");

        var after = store.RequireSlot();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.Updated));
            Assert.That(result.Succeeded, Is.True);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Occupied));
            Assert.That(after.MaterialId, Is.EqualTo("MAT-OK-001"));
            Assert.That(after.Remark, Is.Null, "非 Empty 路径不应动 REMARK");
            Assert.That(after.BindSource, Is.Null, "SetSlot 不写 BIND_SOURCE");
            Assert.That(after.BindTime, Is.Null);
            Assert.That(after.UpdateTime, Is.Not.Null);
            Assert.That(after.UpdateTime, Is.Not.EqualTo(before.UpdateTime));
            Assert.That(store.AtomicUpdateCount, Is.EqualTo(1));
            Assert.That(store.SessionSaveCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R5_槽位不存在_返回NotFound且不保存()
    {
        var (sut, store, _) = CreateSut(initial: null);

        var result = await sut.SetSlotAsync(99, 1, "MAT-X", SlotStates.Occupied, "tester");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.NotFound));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(store.HasSlot, Is.False, "不得创建新槽");
            Assert.That(store.AtomicUpdateCount, Is.EqualTo(1));
            Assert.That(store.SessionSaveCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R_人工目标Reserved_非Reserved槽_返回InvalidTargetState且不调用Store()
    {
        var initial = new SlotRow
        {
            Id = 60, FrameId = 7, SlotNo = 6, LayerNo = 1, PosInLayer = 6,
            SlotState = SlotStates.Empty, MaterialId = null,
            UpdateTime = new DateTime(2026, 8, 1, 8, 0, 0),
        };
        var before = initial.Clone();
        var (sut, store, logger) = CreateSut(initial);

        var result = await sut.SetSlotAsync(
            initial.FrameId, initial.SlotNo, "MAT-X", SlotStates.Reserved, "tester");

        var after = store.RequireSlot();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.InvalidTargetState));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Message, Does.Contain("预记状态只能由派工流程创建"));
            Assert.That(store.AtomicUpdateCount, Is.EqualTo(0), "Service 前端拦截，不得调 Store");
            Assert.That(store.AppliedMutationCount, Is.EqualTo(0));
            Assert.That(after.SlotState, Is.EqualTo(before.SlotState));
            Assert.That(after.Remark, Is.EqualTo(before.Remark));
            Assert.That(after.BindSource, Is.EqualTo(before.BindSource));
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(logger.WarningCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public async Task R_人工目标Reserved_当前已Reserved_仍InvalidTargetState且不写库()
    {
        var initial = ReservedSlot("task-keep", "RSV_PUT", "MAT-R");
        var before = initial.Clone();
        var (sut, store, _) = CreateSut(initial);

        var result = await sut.SetSlotAsync(
            initial.FrameId, initial.SlotNo, null, SlotStates.Reserved, "tester");

        var after = store.RequireSlot();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.InvalidTargetState));
            Assert.That(store.AtomicUpdateCount, Is.EqualTo(0));
            Assert.That(after.SlotState, Is.EqualTo(before.SlotState));
            Assert.That(after.Remark, Is.EqualTo(before.Remark));
            Assert.That(after.BindSource, Is.EqualTo(before.BindSource));
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(after.BindTime, Is.EqualTo(before.BindTime));
        });
    }

    [Test]
    public async Task R_Store直接收到目标Reserved_不ExecuteUpdate()
    {
        var initial = new SlotRow
        {
            Id = 61, FrameId = 7, SlotNo = 7, LayerNo = 1, PosInLayer = 7,
            SlotState = SlotStates.Empty,
        };
        var before = initial.Clone();
        var store = new FakeSlotAccountStore(initial);

        var attempt = await store.TrySetExternalSlotAsync(
            7, 7, SlotStates.Reserved, "MAT", clearRemarkAndBindTime: false, DateTime.Now);

        var after = store.RequireSlot();
        Assert.Multiple(() =>
        {
            Assert.That(attempt.InvalidTargetState, Is.True);
            Assert.That(attempt.AffectedRows, Is.EqualTo(0));
            Assert.That(store.AtomicUpdateCount, Is.EqualTo(0));
            Assert.That(store.AppliedMutationCount, Is.EqualTo(0));
            Assert.That(after.SlotState, Is.EqualTo(before.SlotState));
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
        });
    }

    [Test]
    public async Task R_专用ReservePut仍可创建合法预记完整所有权()
    {
        var ledger = new ConcurrentSlotLedger(new SlotRow
        {
            Id = 1, FrameId = 7, SlotNo = 1, LayerNo = 1, PosInLayer = 1,
            SlotState = SlotStates.Empty,
        });
        var sut = new SlotAccountService(new UnusedDbContextFactory(), ledger, new CapturingLogger());

        var reserved = await sut.ReserveAsync(7, "task-legal", "MAT-LEGAL");
        var after = ledger.Require(7, 1);

        Assert.Multiple(() =>
        {
            Assert.That(reserved, Is.Not.Null);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(after.Remark, Is.EqualTo("task-legal"));
            Assert.That(after.BindSource, Is.EqualTo("RSV_PUT"));
            Assert.That(after.MaterialId, Is.EqualTo("MAT-LEGAL"));
            Assert.That(after.BindTime, Is.Not.Null);
        });
    }

    [Test]
    public async Task R6_非Reserved且已等于目标_返回Unchanged且不重复保存()
    {
        var updateTime = new DateTime(2026, 8, 2, 9, 0, 0);
        var initial = new SlotRow
        {
            Id = 50,
            FrameId = 7,
            SlotNo = 5,
            LayerNo = 1,
            PosInLayer = 5,
            SlotState = SlotStates.Occupied,
            MaterialId = "MAT-SAME",
            Remark = "keep-remark",
            BindSource = "MANUAL",
            BindTime = new DateTime(2026, 8, 1, 8, 0, 0),
            LastVerifyTime = null,
            UpdateTime = updateTime,
        };
        var (sut, store, _) = CreateSut(initial);
        var before = initial.Clone();

        var result = await sut.SetSlotAsync(
            initial.FrameId, initial.SlotNo, "MAT-SAME", SlotStates.Occupied, "tester");

        var after = store.RequireSlot();
        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.Unchanged));
            Assert.That(result.Succeeded, Is.True);
            Assert.That(after.SlotState, Is.EqualTo(before.SlotState));
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(after.Remark, Is.EqualTo(before.Remark));
            Assert.That(after.BindSource, Is.EqualTo(before.BindSource));
            Assert.That(after.BindTime, Is.EqualTo(before.BindTime));
            Assert.That(after.UpdateTime, Is.EqualTo(before.UpdateTime), "Unchanged 不得改 UPDATETIME");
            Assert.That(store.AtomicUpdateCount, Is.EqualTo(1));
            Assert.That(store.AppliedMutationCount, Is.EqualTo(0), "值已相同不得写库");
            Assert.That(store.SessionSaveCount, Is.EqualTo(0));
        });
    }

    private static SlotRow ReservedSlot(string? remark, string? bindSource, string? materialId) => new()
    {
        Id = 10,
        FrameId = 7,
        SlotNo = 2,
        LayerNo = 1,
        PosInLayer = 2,
        SlotState = SlotStates.Reserved,
        MaterialId = materialId,
        Remark = remark,
        BindSource = bindSource,
        BindTime = new DateTime(2026, 8, 4, 10, 0, 0),
        LastVerifyTime = new DateTime(2026, 8, 3, 12, 0, 0),
        UpdateTime = new DateTime(2026, 8, 4, 10, 0, 0),
    };

    private static (SlotAccountService sut, FakeSlotAccountStore store, CapturingLogger logger) CreateSut(SlotRow? initial)
    {
        var store = new FakeSlotAccountStore(initial);
        var logger = new CapturingLogger();
        var sut = new SlotAccountService(new UnusedDbContextFactory(), store, logger);
        return (sut, store, logger);
    }

    private sealed class UnusedDbContextFactory : IDbContextFactory<CncDbContext>
    {
        public CncDbContext CreateDbContext() =>
            throw new InvalidOperationException("SetSlot 测试路径不应使用 IDbContextFactory");

        public Task<CncDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SetSlot 测试路径不应使用 IDbContextFactory");
    }

    /// <summary>模拟原子条件更新：Reserved → affected=0 且快照不变；非 Reserved 值不同才写入。</summary>
    private sealed class FakeSlotAccountStore : ISlotAccountStore
    {
        private SlotRow? _slot;

        public int AtomicUpdateCount { get; private set; }
        public int AppliedMutationCount { get; private set; }
        public int SessionSaveCount { get; private set; }
        /// <summary>模拟 TOCTOU：条件更新 affected=0，但重查仍为非 Reserved 且与目标不同。</summary>
        public bool SimulateConcurrencyConflict { get; set; }
        public bool HasSlot => _slot is not null;

        public FakeSlotAccountStore(SlotRow? initial) => _slot = initial?.Clone();

        public SlotRow RequireSlot() => _slot ?? throw new InvalidOperationException("槽位不存在");

        public Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
            long frameId,
            int slotNo,
            string targetState,
            string? materialId,
            bool clearRemarkAndBindTime,
            DateTime updateTime,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(targetState.Trim(), SlotStates.Reserved, StringComparison.Ordinal))
                return Task.FromResult(new ExternalSlotWriteAttempt(0, null, InvalidTargetState: true));

            AtomicUpdateCount++;

            if (_slot is null || _slot.FrameId != frameId || _slot.SlotNo != slotNo)
                return Task.FromResult(new ExternalSlotWriteAttempt(0, null));

            if (_slot.SlotState == SlotStates.Reserved)
                return Task.FromResult(new ExternalSlotWriteAttempt(0, _slot.Clone()));

            if (SimulateConcurrencyConflict)
                return Task.FromResult(new ExternalSlotWriteAttempt(0, _slot.Clone()));

            var alreadySame =
                _slot.SlotState == targetState
                && string.Equals(_slot.MaterialId, materialId, StringComparison.Ordinal)
                && (!clearRemarkAndBindTime || (_slot.Remark is null && _slot.BindTime is null));
            if (alreadySame)
                return Task.FromResult(new ExternalSlotWriteAttempt(0, _slot.Clone()));

            _slot.SlotState = targetState;
            _slot.MaterialId = materialId;
            if (clearRemarkAndBindTime)
            {
                _slot.Remark = null;
                _slot.BindTime = null;
            }
            _slot.UpdateTime = updateTime;
            AppliedMutationCount++;
            return Task.FromResult(new ExternalSlotWriteAttempt(1, _slot.Clone()));
        }

        public Task<ISlotAccountSession> OpenAsync(CancellationToken ct = default) =>
            Task.FromResult<ISlotAccountSession>(new Session(this));

        private sealed class Session(FakeSlotAccountStore owner) : ISlotAccountSession
        {
            public Task<SlotRow?> FindByFrameSlotAsync(long frameId, int slotNo, CancellationToken ct = default)
                => Task.FromResult(owner._slot is { } s && s.FrameId == frameId && s.SlotNo == slotNo ? s.Clone() : null);

            public Task<IReadOnlyList<SlotRow>> FindByFrameOrderedAsync(long frameId, CancellationToken ct = default)
            {
                if (owner._slot is { } s && s.FrameId == frameId)
                    return Task.FromResult<IReadOnlyList<SlotRow>>(new[] { s.Clone() });
                return Task.FromResult<IReadOnlyList<SlotRow>>(Array.Empty<SlotRow>());
            }

            public Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
                long frameId, int slotNo, string targetState, string? materialId,
                bool clearRemarkAndBindTime, DateTime updateTime, CancellationToken ct = default,
                InventorySlotWriteExtras? extras = null)
                => owner.TrySetExternalSlotAsync(
                    frameId, slotNo, targetState, materialId, clearRemarkAndBindTime, updateTime, ct);

            public Task CommitAsync(CancellationToken ct = default)
            {
                owner.SessionSaveCount++;
                return Task.CompletedTask;
            }

            public Task RollbackAsync(CancellationToken ct = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger : ILogger<SlotAccountService>
    {
        public List<string> Warnings { get; } = [];
        public int WarningCount => Warnings.Count;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            Warnings.Add(formatter(state, exception));
        }
    }
}
