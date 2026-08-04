using CncLoader.Core.Rcs;
using CncLoader.Data;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P0-6 R17/R18/R20：真实 SlotAccountService Confirm/Rollback 所有权与 affected=0 分类。
/// 无既有等价真实 Service 接线测试可复用（P0-2 仅测 Dispatcher 协调器）。
/// </summary>
[TestFixture]
public sealed class SlotStateTransitionProtectionTests
{
    private const long FrameId = 80;

    [Test]
    public async Task R17_Reserved正确taskId与方向_ConfirmPut成功进入占用并保留REMARK()
    {
        var ledger = new ConcurrentSlotLedger(ReservedPut(1, "task-ok", "MAT-1"));
        var before = ledger.Require(FrameId, 1);
        var sut = CreateSut(ledger);

        var ok = await sut.ConfirmAsync("task-ok");
        var after = ledger.Require(FrameId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Occupied));
            Assert.That(after.BindSource, Is.EqualTo("CONFIRMED"));
            Assert.That(after.Remark, Is.EqualTo("task-ok"), "PUT Confirm 保留 REMARK");
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(ledger.ExternalWriteCount, Is.EqualTo(0), "不得走通用 SetSlot/外部写");
        });
    }

    [Test]
    public async Task R17_Reserved正确taskId_RollbackPut成功恢复Empty且不被外部防护误拦()
    {
        var ledger = new ConcurrentSlotLedger(ReservedPut(2, "task-rb", "MAT-2"));
        var sut = CreateSut(ledger);

        var ok = await sut.RollbackAsync("task-rb");
        var after = ledger.Require(FrameId, 2);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Empty));
            Assert.That(after.Remark, Is.Null);
            Assert.That(after.MaterialId, Is.Null);
            Assert.That(ledger.ExternalWriteCount, Is.EqualTo(0));
            Assert.That(ledger.Trace.Contains("RollbackPutCommit"), Is.True);
        });
    }

    [Test]
    public async Task R17_Reserved正确taskId_ConfirmTake与RollbackTake合法路径()
    {
        var ledger = new ConcurrentSlotLedger(ReservedTake(3, "task-take", "MAT-T"));
        var sut = CreateSut(ledger);

        var confirmed = await sut.ConfirmTakeAsync("task-take");
        Assert.That(confirmed, Is.True);
        Assert.That(ledger.Require(FrameId, 3).SlotState, Is.EqualTo(SlotStates.Empty));

        var ledger2 = new ConcurrentSlotLedger(ReservedTake(4, "task-take-rb", "MAT-T2"));
        var sut2 = CreateSut(ledger2);
        var rolled = await sut2.RollbackTakeAsync("task-take-rb");
        var after = ledger2.Require(FrameId, 4);

        Assert.Multiple(() =>
        {
            Assert.That(rolled, Is.True);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Occupied));
            Assert.That(after.MaterialId, Is.EqualTo("MAT-T2"));
            Assert.That(after.Remark, Is.Null);
            Assert.That(ledger2.ExternalWriteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R18_taskId不匹配的Confirm与Rollback全部拒绝且快照不变()
    {
        var ledger = new ConcurrentSlotLedger(ReservedPut(5, "task-owner", "MAT-5"));
        var before = ledger.Require(FrameId, 5);
        var sut = CreateSut(ledger);

        var c = await sut.ConfirmAsync("task-other");
        var r = await sut.RollbackAsync("task-other");
        var after = ledger.Require(FrameId, 5);

        Assert.Multiple(() =>
        {
            Assert.That(c, Is.False);
            Assert.That(r, Is.False);
            Assert.That(after.SlotState, Is.EqualTo(before.SlotState));
            Assert.That(after.Remark, Is.EqualTo(before.Remark));
            Assert.That(after.BindSource, Is.EqualTo(before.BindSource));
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(ledger.ExternalWriteCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R18_非Reserved调用ConfirmRollback须拒绝()
    {
        var ledger = new ConcurrentSlotLedger(new SlotRow
        {
            Id = 6, FrameId = FrameId, SlotNo = 6, LayerNo = 1, PosInLayer = 6,
            SlotState = SlotStates.Occupied, MaterialId = "MAT-6", Remark = "task-occ", BindSource = "CONFIRMED"
        });
        var before = ledger.Require(FrameId, 6);
        var sut = CreateSut(ledger);

        // Confirm 对已 Occupied+同 remark 为幂等 true（生产契约）；Rollback 要求 Reserved
        var rollback = await sut.RollbackAsync("task-occ");
        var after = ledger.Require(FrameId, 6);

        Assert.Multiple(() =>
        {
            Assert.That(rollback, Is.False);
            Assert.That(after.SlotState, Is.EqualTo(before.SlotState));
            Assert.That(after.Remark, Is.EqualTo(before.Remark));
        });

        var emptyLedger = new ConcurrentSlotLedger(new SlotRow
        {
            Id = 7, FrameId = FrameId, SlotNo = 7, LayerNo = 1, PosInLayer = 7,
            SlotState = SlotStates.Empty, Remark = null
        });
        var emptySut = CreateSut(emptyLedger);
        Assert.That(await emptySut.ConfirmAsync("no-such"), Is.False);
        Assert.That(await emptySut.RollbackAsync("no-such"), Is.False);
    }

    [Test]
    public async Task R18_BIND_SOURCE方向不匹配_ConfirmPut不得落账RSV_TAKE()
    {
        var ledger = new ConcurrentSlotLedger(ReservedTake(8, "task-dir", "MAT-DIR"));
        var before = ledger.Require(FrameId, 8);
        var sut = CreateSut(ledger);

        var ok = await sut.ConfirmAsync("task-dir");
        var after = ledger.Require(FrameId, 8);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False, "方向不匹配应拒绝");
            AssertSnapshotUnchanged(before, after);
            Assert.That(ledger.ExternalWriteCount, Is.EqualTo(0));
            Assert.That(ledger.Trace, Does.Contain("ConfirmPutRejected"));
            Assert.That(ledger.Trace, Does.Not.Contain("ConfirmPutCommit"));
        });
    }

    [Test]
    public async Task R18_BIND_SOURCE方向不匹配_ConfirmTake不得清空RSV_PUT()
    {
        var ledger = new ConcurrentSlotLedger(ReservedPut(9, "task-dir2", "MAT-DIR2"));
        var before = ledger.Require(FrameId, 9);
        var sut = CreateSut(ledger);

        var ok = await sut.ConfirmTakeAsync("task-dir2");
        var after = ledger.Require(FrameId, 9);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False, "方向不匹配应拒绝");
            AssertSnapshotUnchanged(before, after);
            Assert.That(ledger.Trace, Does.Contain("ConfirmTakeRejected"));
            Assert.That(ledger.Trace, Does.Not.Contain("ConfirmTakeCommit"));
        });
    }

    [Test]
    public async Task R18_BIND_SOURCE方向不匹配_RollbackPut不得回滚RSV_TAKE()
    {
        var ledger = new ConcurrentSlotLedger(ReservedTake(10, "task-rb-dir", "MAT-RB"));
        var before = ledger.Require(FrameId, 10);
        var sut = CreateSut(ledger);

        var ok = await sut.RollbackAsync("task-rb-dir");
        var after = ledger.Require(FrameId, 10);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            AssertSnapshotUnchanged(before, after);
            Assert.That(ledger.Trace, Does.Contain("RollbackPutRejected"));
            Assert.That(ledger.Trace, Does.Not.Contain("RollbackPutCommit"));
        });
    }

    [Test]
    public async Task R18_BIND_SOURCE方向不匹配_RollbackTake不得回滚RSV_PUT()
    {
        var ledger = new ConcurrentSlotLedger(ReservedPut(11, "task-rb-dir2", "MAT-RB2"));
        var before = ledger.Require(FrameId, 11);
        var sut = CreateSut(ledger);

        var ok = await sut.RollbackTakeAsync("task-rb-dir2");
        var after = ledger.Require(FrameId, 11);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            AssertSnapshotUnchanged(before, after);
            Assert.That(ledger.Trace, Does.Contain("RollbackTakeRejected"));
            Assert.That(ledger.Trace, Does.Not.Contain("RollbackTakeCommit"));
        });
    }

    [Test]
    public async Task R18_ConfirmPut正确方向_RSV_PUT完整字段转换()
    {
        var ledger = new ConcurrentSlotLedger(ReservedPut(12, "task-put-ok", "MAT-PUT"));
        var before = ledger.Require(FrameId, 12);
        var sut = CreateSut(ledger);

        var ok = await sut.ConfirmAsync("task-put-ok");
        var after = ledger.Require(FrameId, 12);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Occupied));
            Assert.That(after.BindSource, Is.EqualTo("CONFIRMED"));
            Assert.That(after.Remark, Is.EqualTo("task-put-ok"), "PUT Confirm 保留 REMARK");
            Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
            Assert.That(after.BindTime, Is.Not.Null);
        });
    }

    [Test]
    public async Task R18_ConfirmTake正确方向_RSV_TAKE完整字段转换()
    {
        var ledger = new ConcurrentSlotLedger(ReservedTake(13, "task-take-ok", "MAT-TAKE"));
        var sut = CreateSut(ledger);

        var ok = await sut.ConfirmTakeAsync("task-take-ok");
        var after = ledger.Require(FrameId, 13);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Empty));
            Assert.That(after.MaterialId, Is.Null);
            Assert.That(after.Remark, Is.Null);
            Assert.That(after.BindTime, Is.Null);
        });
    }

    [Test]
    public async Task R20_单槽affected0_Reserved_NotFound_Unchanged_Concurrency_Db_Cancel分类()
    {
        // Reserved
        {
            var ledger = new ConcurrentSlotLedger(ReservedPut(1, "t", "M"));
            var sut = CreateSut(ledger);
            var r = await sut.SetSlotAsync(FrameId, 1, "X", SlotStates.Occupied, "a");
            Assert.That(r.Status, Is.EqualTo(SlotMutationStatus.ReservationConflict));
            Assert.That(r.Succeeded, Is.False);
            Assert.That(ledger.ExternalWriteCount, Is.EqualTo(1));
        }

        // NotFound
        {
            var ledger = new ConcurrentSlotLedger();
            var sut = CreateSut(ledger);
            var r = await sut.SetSlotAsync(FrameId, 99, "X", SlotStates.Occupied, "a");
            Assert.That(r.Status, Is.EqualTo(SlotMutationStatus.NotFound));
            Assert.That(r.Succeeded, Is.False);
        }

        // Unchanged
        {
            var ledger = new ConcurrentSlotLedger(new SlotRow
            {
                Id = 2, FrameId = FrameId, SlotNo = 2, LayerNo = 1, PosInLayer = 2,
                SlotState = SlotStates.Occupied, MaterialId = "SAME"
            });
            var sut = CreateSut(ledger);
            var r = await sut.SetSlotAsync(FrameId, 2, "SAME", SlotStates.Occupied, "a");
            Assert.That(r.Status, Is.EqualTo(SlotMutationStatus.Unchanged));
            Assert.That(r.Succeeded, Is.True);
        }

        // ConcurrencyConflict：affected=0 且非 Reserved、与目标不同；禁止第二次无条件写
        {
            var ledger = new ConcurrentSlotLedger(new SlotRow
            {
                Id = 3, FrameId = FrameId, SlotNo = 3, LayerNo = 1, PosInLayer = 3,
                SlotState = SlotStates.Empty, MaterialId = null
            })
            { SimulateConcurrencyConflict = true };
            var before = ledger.Require(FrameId, 3);
            var sut = CreateSut(ledger);
            var r = await sut.SetSlotAsync(FrameId, 3, "NEW", SlotStates.Occupied, "a");
            Assert.Multiple(() =>
            {
                Assert.That(r.Status, Is.EqualTo(SlotMutationStatus.ConcurrencyConflict));
                Assert.That(r.Succeeded, Is.False);
                Assert.That(ledger.Require(FrameId, 3).SlotState, Is.EqualTo(before.SlotState));
                Assert.That(ledger.ExternalWriteCount, Is.EqualTo(1), "不得无限重试/第二次无条件 UPDATE");
            });
        }

        // DatabaseError
        {
            var ledger = new ConcurrentSlotLedger(Empty(4)) { ThrowOnExternalWrite = true };
            var sut = CreateSut(ledger);
            var r = await sut.SetSlotAsync(FrameId, 4, "X", SlotStates.Occupied, "a");
            Assert.That(r.Status, Is.EqualTo(SlotMutationStatus.DatabaseError));
            Assert.That(r.Succeeded, Is.False);
        }

        // Cancelled
        {
            var ledger = new ConcurrentSlotLedger(Empty(5));
            var sut = CreateSut(ledger);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var r = await sut.SetSlotAsync(FrameId, 5, "X", SlotStates.Occupied, "a", cts.Token);
            Assert.That(r.Status, Is.EqualTo(SlotMutationStatus.Cancelled));
            Assert.That(r.Succeeded, Is.False);
        }
    }

    [Test]
    public async Task R20_批量affected0分类互斥且冲突不得Success()
    {
        var slots = new[]
        {
            Empty(1),
            new SlotRow
            {
                Id = 2, FrameId = FrameId, SlotNo = 2, LayerNo = 1, PosInLayer = 2,
                SlotState = SlotStates.Occupied, MaterialId = "SAME", BindSource = "CONFIRMED"
            },
            ReservedPut(3, "task-b", "MAT-R"),
        };
        var ledger = new ConcurrentSlotLedger(slots) { SimulateConcurrencyConflict = false };
        var sut = CreateSut(ledger);

        // 第 4 个 product → NotFound；槽1更新；槽2 Unchanged；槽3 Reserved 冲突
        var result = await sut.CorrectFromInventoryAsync(FrameId, 101,
            new[] { "MAT-A", "SAME", "SHOULD-SKIP", "MISSING" });

        Assert.Multiple(() =>
        {
            Assert.That(result.UpdatedCount, Is.EqualTo(1));
            Assert.That(result.UnchangedCount, Is.EqualTo(1));
            Assert.That(result.ReservationConflictCount, Is.EqualTo(1));
            Assert.That(result.NotFoundCount, Is.EqualTo(1));
            Assert.That(result.CategorizedTotal, Is.EqualTo(result.RequestedCount));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(ledger.Require(FrameId, 3).SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(ledger.Require(FrameId, 3).Remark, Is.EqualTo("task-b"));
        });

        // 批量 ConcurrencyConflict（孔位 101 → 层1位1，须与 Empty 槽坐标对齐）
        var ledgerCc = new ConcurrentSlotLedger(Empty(1)) { SimulateConcurrencyConflict = true };
        var sutCc = CreateSut(ledgerCc);
        var cc = await sutCc.CorrectFromInventoryAsync(FrameId, 101, new[] { "X" });
        Assert.Multiple(() =>
        {
            Assert.That(cc.ConcurrencyConflictCount, Is.EqualTo(1));
            Assert.That(cc.UpdatedCount, Is.EqualTo(0));
            Assert.That(cc.Succeeded, Is.False);
            Assert.That(ledgerCc.ExternalWriteCount, Is.EqualTo(1));
            Assert.That(ledgerCc.CommitCount, Is.EqualTo(0));
            Assert.That(ledgerCc.ExternalWriteCount, Is.EqualTo(1), "分类后不得第二次无条件写");
        });
    }

    private static void AssertSnapshotUnchanged(SlotRow before, SlotRow after)
    {
        Assert.That(after.SlotState, Is.EqualTo(before.SlotState));
        Assert.That(after.Remark, Is.EqualTo(before.Remark));
        Assert.That(after.BindSource, Is.EqualTo(before.BindSource));
        Assert.That(after.MaterialId, Is.EqualTo(before.MaterialId));
        Assert.That(after.BindTime, Is.EqualTo(before.BindTime));
    }

    private static SlotAccountService CreateSut(ConcurrentSlotLedger ledger) =>
        new(new UnusedDbContextFactory(), ledger, NullLogger<SlotAccountService>.Instance);

    private static SlotRow Empty(int slotNo) => new()
    {
        Id = slotNo, FrameId = FrameId, SlotNo = slotNo, LayerNo = 1, PosInLayer = slotNo,
        SlotState = SlotStates.Empty
    };

    private static SlotRow ReservedPut(int slotNo, string taskId, string material) => new()
    {
        Id = slotNo, FrameId = FrameId, SlotNo = slotNo, LayerNo = 1, PosInLayer = slotNo,
        SlotState = SlotStates.Reserved, MaterialId = material, Remark = taskId,
        BindSource = "RSV_PUT", BindTime = DateTime.Now.AddMinutes(-1)
    };

    private static SlotRow ReservedTake(int slotNo, string taskId, string material) => new()
    {
        Id = slotNo, FrameId = FrameId, SlotNo = slotNo, LayerNo = 1, PosInLayer = slotNo,
        SlotState = SlotStates.Reserved, MaterialId = material, Remark = taskId,
        BindSource = "RSV_TAKE", BindTime = DateTime.Now.AddMinutes(-1)
    };

    private sealed class UnusedDbContextFactory : IDbContextFactory<CncDbContext>
    {
        public CncDbContext CreateDbContext() =>
            throw new InvalidOperationException("状态机测试不应走 IDbContextFactory");

        public Task<CncDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("状态机测试不应走 IDbContextFactory");
    }
}
