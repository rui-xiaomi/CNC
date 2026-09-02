using CncLoader.Core.Rcs;
using CncLoader.Data;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P0-6 R8/R9/R11/R13：真实 <see cref="SlotAccountService.CorrectFromInventoryAsync"/> 批量盘点保护。
/// </summary>
[TestFixture]
public sealed class InventoryReservedSlotProtectionTests
{
    private const long FrameId = 7;
    private const string TaskSecret = "task-inv-secret-should-not-leak";

    [Test]
    public async Task R8_混合批次_Reserved槽完整快照不得变且须计冲突()
    {
        var slotA = SafeSlot(1, 1, 1, SlotStates.Empty, null);
        var slotB = ReservedSlot(2, 1, 2, "MAT-R", TaskSecret, "RSV_PUT");
        var beforeB = SnapshotOf(slotB);
        var store = new FakeBatchStore(slotA, slotB);
        var sut = CreateSut(store);

        var result = await sut.CorrectFromInventoryAsync(FrameId, 101, new[] { "MAT-NEW-A", "MAT-NEW-B" });

        var afterB = store.Committed.Single(s => s.SlotNo == 2);
        Assert.Multiple(() =>
        {
            Assert.That(store.Committed.Single(s => s.SlotNo == 1).SlotState, Is.EqualTo(SlotStates.Occupied));
            Assert.That(store.Committed.Single(s => s.SlotNo == 1).MaterialId, Is.EqualTo("MAT-NEW-A"));
            Assert.That(afterB.SlotState, Is.EqualTo(beforeB.SlotState), "Reserved 槽 STATE 不得变");
            Assert.That(afterB.MaterialId, Is.EqualTo(beforeB.MaterialId));
            Assert.That(afterB.Remark, Is.EqualTo(beforeB.Remark));
            Assert.That(afterB.BindSource, Is.EqualTo(beforeB.BindSource));
            Assert.That(afterB.BindTime, Is.EqualTo(beforeB.BindTime));
            Assert.That(result.UpdatedCount, Is.EqualTo(1));
            Assert.That(result.ReservationConflictCount, Is.EqualTo(1));
            Assert.That(result.ConflictSlots.Any(s => s.SlotNo == 2), Is.True);
            Assert.That(result.ConflictSlots.Select(s => s.Remark), Has.All.Null, "ConflictSlots 不泄露 taskId");
            Assert.That(store.CommitCount, Is.EqualTo(1), "只提交一次批量事务");
            Assert.That(store.ConditionalWriteCount, Is.EqualTo(1), "仅安全槽走条件写");
            Assert.That(store.ConditionalWriteSlotNos, Is.EquivalentTo(new[] { 1 }));
            Assert.That(store.UnconditionalSaveCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R9_结果计数_四类互斥可核对()
    {
        var slots = new[]
        {
            SafeSlot(1, 1, 1, SlotStates.Empty, null),
            SafeSlot(2, 1, 2, SlotStates.Occupied, "MAT-SAME"),
            ReservedSlot(3, 1, 3, "MAT-R", TaskSecret, "RSV_TAKE"),
        };
        var store = new FakeBatchStore(slots);
        var sut = CreateSut(store);

        var result = await sut.CorrectFromInventoryAsync(FrameId, 101,
            new[] { "MAT-A", "MAT-SAME", "MAT-SHOULD-SKIP", "MAT-MISSING" });

        Assert.Multiple(() =>
        {
            Assert.That(result.RequestedCount, Is.EqualTo(4));
            Assert.That(result.UpdatedCount, Is.EqualTo(1));
            Assert.That(result.UnchangedCount, Is.EqualTo(1));
            Assert.That(result.ReservationConflictCount, Is.EqualTo(1));
            Assert.That(result.NotFoundCount, Is.EqualTo(1));
            Assert.That(result.CategorizedTotal, Is.EqualTo(result.RequestedCount));
            Assert.That(store.Committed.Single(s => s.SlotNo == 3).SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(store.Committed.Single(s => s.SlotNo == 3).Remark, Is.EqualTo(TaskSecret));
            Assert.That(store.ConditionalWriteSlotNos, Does.Not.Contain(3), "Reserved 不调用写入");
        });
    }

    [Test]
    public async Task R11_全部Reserved_不得更新且不得纯成功计数()
    {
        var slots = new[]
        {
            ReservedSlot(1, 1, 1, "MAT-1", "task-a", "RSV_PUT"),
            ReservedSlot(2, 1, 2, "MAT-2", "task-b", "RSV_TAKE"),
        };
        var before = slots.Select(SnapshotOf).ToArray();
        var store = new FakeBatchStore(slots);
        var sut = CreateSut(store);

        var result = await sut.CorrectFromInventoryAsync(FrameId, 101, new[] { "X1", "X2" });

        Assert.Multiple(() =>
        {
            Assert.That(result.UpdatedCount, Is.EqualTo(0));
            Assert.That(result.ReservationConflictCount, Is.EqualTo(result.RequestedCount));
            for (var i = 0; i < before.Length; i++)
            {
                var cur = store.Committed.Single(s => s.SlotNo == before[i].SlotNo);
                Assert.That(cur.SlotState, Is.EqualTo(before[i].SlotState));
                Assert.That(cur.Remark, Is.EqualTo(before[i].Remark));
                Assert.That(cur.MaterialId, Is.EqualTo(before[i].MaterialId));
                Assert.That(cur.BindSource, Is.EqualTo(before[i].BindSource));
            }
            Assert.That(result.Succeeded, Is.False, "用户结果不能为纯 Success");
            Assert.That(store.CommitCount, Is.EqualTo(0));
            Assert.That(store.ConditionalWriteCount, Is.EqualTo(0));
            Assert.That(store.RollbackCount, Is.GreaterThanOrEqualTo(1));
        });
    }

    [Test]
    public async Task R13_取消时_暂存不得提交且不得部分Updated()
    {
        var store = new FakeBatchStore(
            SafeSlot(1, 1, 1, SlotStates.Empty, null),
            SafeSlot(2, 1, 2, SlotStates.Empty, null))
        {
            CancelOnCommit = true
        };
        var before = store.Committed.Select(SnapshotOf).ToArray();
        var sut = CreateSut(store);

        var result = await sut.CorrectFromInventoryAsync(FrameId, 101, new[] { "A", "B" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InventoryCorrectionStatus.Cancelled));
            Assert.That(result.UpdatedCount, Is.EqualTo(0));
            Assert.That(store.CommitCount, Is.EqualTo(0));
            Assert.That(store.RollbackCount, Is.EqualTo(1));
            Assert.That(store.Committed.Select(SnapshotOf), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task R13_数据库异常_整批回滚不留部分提交()
    {
        var store = new FakeBatchStore(
            SafeSlot(1, 1, 1, SlotStates.Empty, null),
            SafeSlot(2, 1, 2, SlotStates.Empty, null))
        {
            ThrowOnCommit = true
        };
        var before = store.Committed.Select(SnapshotOf).ToArray();
        var sut = CreateSut(store);

        var result = await sut.CorrectFromInventoryAsync(FrameId, 101, new[] { "A", "B" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InventoryCorrectionStatus.DatabaseError));
            Assert.That(result.UpdatedCount, Is.EqualTo(0));
            Assert.That(store.CommitCount, Is.EqualTo(0));
            Assert.That(store.RollbackCount, Is.EqualTo(1));
            Assert.That(store.Committed.Select(SnapshotOf), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task R13_成功提交_一次写入全部安全槽()
    {
        var store = new FakeBatchStore(
            SafeSlot(1, 1, 1, SlotStates.Empty, null),
            SafeSlot(2, 1, 2, SlotStates.Empty, null));
        var sut = CreateSut(store);

        var result = await sut.CorrectFromInventoryAsync(FrameId, 101, new[] { "A", "B" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InventoryCorrectionStatus.Completed));
            Assert.That(store.CommitCount, Is.EqualTo(1));
            Assert.That(store.RollbackCount, Is.EqualTo(0));
            Assert.That(store.ConditionalWriteCount, Is.EqualTo(2));
            Assert.That(store.Committed.All(s => s.SlotState == SlotStates.Occupied), Is.True);
            Assert.That(store.Committed.Select(s => s.MaterialId), Is.EquivalentTo(new[] { "A", "B" }));
        });
    }

    private static SlotAccountService CreateSut(FakeBatchStore store) =>
        new(new UnusedDbContextFactory(), store, NullLogger<SlotAccountService>.Instance);

    private static SlotRow SafeSlot(int slotNo, int layer, int pos, string state, string? material) => new()
    {
        Id = slotNo,
        FrameId = FrameId,
        SlotNo = slotNo,
        LayerNo = layer,
        PosInLayer = pos,
        SlotState = state,
        MaterialId = material,
        BindSource = state == SlotStates.Occupied ? "CONFIRMED" : null,
        BindTime = state == SlotStates.Occupied ? DateTime.Now.AddHours(-1) : null,
    };

    private static SlotRow ReservedSlot(int slotNo, int layer, int pos, string? material, string remark, string bindSource) => new()
    {
        Id = slotNo,
        FrameId = FrameId,
        SlotNo = slotNo,
        LayerNo = layer,
        PosInLayer = pos,
        SlotState = SlotStates.Reserved,
        MaterialId = material,
        Remark = remark,
        BindSource = bindSource,
        BindTime = DateTime.Now.AddMinutes(-5),
    };

    private static SlotMutationSnapshot SnapshotOf(SlotRow s) =>
        new(s.Id, s.FrameId, s.SlotNo, s.SlotState, s.MaterialId, s.Remark, s.BindSource, s.BindTime);

    private sealed class UnusedDbContextFactory : IDbContextFactory<CncDbContext>
    {
        public CncDbContext CreateDbContext() =>
            throw new InvalidOperationException("盘点批量测试路径不应使用 IDbContextFactory");

        public Task<CncDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("盘点批量测试路径不应使用 IDbContextFactory");
    }

    /// <summary>事务内条件写 + Commit/Rollback；区分 staged 与 committed。</summary>
    private sealed class FakeBatchStore : ISlotAccountStore
    {
        private readonly List<SlotRow> _committed;
        private readonly List<SlotRow> _txBuffer = [];
        private bool _inTx;

        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public int ConditionalWriteCount { get; private set; }
        public int UnconditionalSaveCount { get; private set; }
        public List<int> ConditionalWriteSlotNos { get; } = [];
        public bool CancelOnCommit { get; set; }
        public bool ThrowOnCommit { get; set; }
        public IReadOnlyList<SlotRow> Committed => _committed;

        public FakeBatchStore(params SlotRow[] initial) =>
            _committed = initial.Select(s => s.Clone()).ToList();

        public Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
            long frameId, int slotNo, string targetState, string? materialId,
            bool clearRemarkAndBindTime, DateTime updateTime, CancellationToken ct = default)
            => throw new NotSupportedException("本测试走 OpenAsync 批量会话");

        public Task<ISlotAccountSession> OpenAsync(CancellationToken ct = default)
        {
            _inTx = true;
            _txBuffer.Clear();
            _txBuffer.AddRange(_committed.Select(s => s.Clone()));
            return Task.FromResult<ISlotAccountSession>(new Session(this));
        }

        private sealed class Session(FakeBatchStore owner) : ISlotAccountSession
        {
            private bool _completed;

            public Task<SlotRow?> FindByFrameSlotAsync(long frameId, int slotNo, CancellationToken ct = default)
            {
                var src = owner._inTx ? owner._txBuffer : owner._committed;
                return Task.FromResult(src.FirstOrDefault(s => s.FrameId == frameId && s.SlotNo == slotNo)?.Clone());
            }

            public Task<IReadOnlyList<SlotRow>> FindByFrameOrderedAsync(long frameId, CancellationToken ct = default)
            {
                var src = owner._inTx ? owner._txBuffer : owner._committed;
                IReadOnlyList<SlotRow> rows = src
                    .Where(s => s.FrameId == frameId)
                    .OrderBy(s => s.LayerNo).ThenBy(s => s.PosInLayer)
                    .Select(s => s.Clone())
                    .ToList();
                return Task.FromResult(rows);
            }

            public Task<ExternalSlotWriteAttempt> TrySetExternalSlotAsync(
                long frameId, int slotNo, string targetState, string? materialId,
                bool clearRemarkAndBindTime, DateTime updateTime, CancellationToken ct = default,
                InventorySlotWriteExtras? extras = null)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(targetState.Trim(), SlotStates.Reserved, StringComparison.Ordinal))
                    return Task.FromResult(new ExternalSlotWriteAttempt(0, null, InvalidTargetState: true));

                owner.ConditionalWriteCount++;
                owner.ConditionalWriteSlotNos.Add(slotNo);

                var buf = owner._txBuffer;
                var idx = buf.FindIndex(s => s.FrameId == frameId && s.SlotNo == slotNo);
                if (idx < 0)
                    return Task.FromResult(new ExternalSlotWriteAttempt(0, null));

                var current = buf[idx];
                if (current.SlotState == SlotStates.Reserved)
                    return Task.FromResult(new ExternalSlotWriteAttempt(0, current.Clone()));

                var next = current.Clone();
                next.SlotState = targetState;
                next.MaterialId = materialId;
                next.UpdateTime = updateTime;
                if (clearRemarkAndBindTime)
                {
                    next.Remark = null;
                    next.BindTime = null;
                }
                if (extras is not null)
                {
                    next.LastVerifyTime = extras.LastVerifyTime;
                    if (extras.ApplyBindFields)
                    {
                        next.BindSource = extras.BindSource;
                        next.BindTime = extras.BindTime;
                    }
                }

                buf[idx] = next;
                return Task.FromResult(new ExternalSlotWriteAttempt(1, next.Clone()));
            }

            public Task CommitAsync(CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (owner.CancelOnCommit)
                    throw new OperationCanceledException(ct);
                if (owner.ThrowOnCommit)
                    throw new InvalidOperationException("simulated db failure");

                owner._committed.Clear();
                owner._committed.AddRange(owner._txBuffer.Select(s => s.Clone()));
                owner._inTx = false;
                owner.CommitCount++;
                _completed = true;
                return Task.CompletedTask;
            }

            public Task RollbackAsync(CancellationToken ct = default)
            {
                if (_completed) return Task.CompletedTask;
                owner._txBuffer.Clear();
                owner._inTx = false;
                owner.RollbackCount++;
                _completed = true;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                if (!_completed)
                    await RollbackAsync();
            }
        }
    }
}
