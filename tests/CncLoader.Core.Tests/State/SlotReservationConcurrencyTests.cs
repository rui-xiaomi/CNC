using CncLoader.Core.Rcs;
using CncLoader.Data;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P0-6 R14–R16：真实 SlotAccountService 路径上的确定性交错（TCS gate，无 Sleep）。
/// </summary>
[TestFixture]
public sealed class SlotReservationConcurrencyTests
{
    private const long FrameId = 70;

    [Test]
    public async Task R14_盘点条件写前Reserve先成功_affected0且Reserved完整保持()
    {
        var slot = EmptySlot(1, 1, 1);
        var ledger = new ConcurrentSlotLedger(slot);
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ledger.PauseBeforeExternalWrite = pause;
        var sut = CreateSut(ledger);

        var correctTask = Task.Run(() =>
            sut.CorrectFromInventoryAsync(FrameId, 101, new[] { "MAT-INV-OVERWRITE" }));

        await WaitUntilAsync(() => ledger.Trace.Contains("ExternalConditionalUpdateEnter"), TimeSpan.FromSeconds(5));

        var reserved = await sut.ReserveAsync(FrameId, "task-r14", "MAT-RSV");
        Assert.That(reserved, Is.Not.Null, "Reserve 须先成功");

        pause.TrySetResult();
        InventoryCorrectionResult correction;
        try
        {
            correction = await correctTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pause.TrySetResult();
        }

        var after = ledger.Require(FrameId, 1);
        TestContext.Out.WriteLine("R14 线性化: " + string.Join(" → ", ledger.Trace));

        Assert.Multiple(() =>
        {
            Assert.That(correction.ReservationConflictCount, Is.EqualTo(1));
            Assert.That(correction.UpdatedCount, Is.EqualTo(0), "冲突槽不得计入 Updated");
            Assert.That(correction.Succeeded, Is.False);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(after.Remark, Is.EqualTo("task-r14"));
            Assert.That(after.BindSource, Is.EqualTo("RSV_PUT"));
            Assert.That(after.MaterialId, Is.EqualTo("MAT-RSV"));
            Assert.That(ledger.ReserveCommitCount, Is.EqualTo(1));
            Assert.That(ledger.CommitCount, Is.EqualTo(0));
            Assert.That(HasOrder(ledger.Trace,
                "ReadIdentity", "ReserveCommit", "ExternalConditionalUpdate(affected=0)", "ClassifyReserved"), Is.True,
                "线性化须为 ReadIdentity→ReserveCommit→ExternalConditionalUpdate(affected=0)→ClassifyReserved；实际=" +
                string.Join(" → ", ledger.Trace));
        });
    }

    [Test]
    public async Task R15A_外部校正先成功使不满足Reserve前置_Reserve须拒绝()
    {
        var ledger = new ConcurrentSlotLedger(EmptySlot(1, 1, 1));
        var sut = CreateSut(ledger);

        var correction = await sut.CorrectFromInventoryAsync(FrameId, 101, new[] { "MAT-OCCUPIED-NOW" });
        Assert.That(correction.UpdatedCount, Is.EqualTo(1));

        var reserved = await sut.ReserveAsync(FrameId, "task-r15a", "MAT-SHOULD-FAIL");
        var after = ledger.Require(FrameId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(reserved, Is.Null, "校正后为 Occupied，PUT Reserve 须拒绝");
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Occupied));
            Assert.That(after.MaterialId, Is.EqualTo("MAT-OCCUPIED-NOW"));
            Assert.That(after.Remark, Is.Null);
            Assert.That(after.BindSource, Is.EqualTo("RCS_QR"));
            Assert.That(ledger.ReserveCommitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R15B_外部校正先成功且仍满足Reserve前置_Reserve写入完整所有权不得混字段()
    {
        var ledger = new ConcurrentSlotLedger(OccupiedSlot(1, 1, 1, "MAT-OLD"));
        var sut = CreateSut(ledger);

        // 校正为 Empty → 满足 PUT Reserve 前置
        var correction = await sut.CorrectFromInventoryAsync(FrameId, 101, new[] { "" });
        Assert.That(correction.UpdatedCount, Is.EqualTo(1));
        var mid = ledger.Require(FrameId, 1);
        Assert.That(mid.SlotState, Is.EqualTo(SlotStates.Empty));

        var reserved = await sut.ReserveAsync(FrameId, "task-r15b", "MAT-PUT-NEW");
        var after = ledger.Require(FrameId, 1);

        Assert.Multiple(() =>
        {
            Assert.That(reserved, Is.Not.Null);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(after.Remark, Is.EqualTo("task-r15b"));
            Assert.That(after.BindSource, Is.EqualTo("RSV_PUT"));
            Assert.That(after.MaterialId, Is.EqualTo("MAT-PUT-NEW"));
            Assert.That(after.BindTime, Is.Not.Null);
            // 不得出现 STATE=Reserved 但 REMARK/物料仍为校正残留混字段
            Assert.That(after.MaterialId, Is.Not.EqualTo("MAT-OLD"));
            Assert.That(ledger.Trace.Contains("ReserveCommit"), Is.True);
        });
    }

    [Test]
    public async Task R16_人工校正条件写前Reserve先成功_返回ReservationConflict且快照不变()
    {
        var ledger = new ConcurrentSlotLedger(EmptySlot(2, 1, 2));
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ledger.PauseBeforeExternalWrite = pause;
        var sut = CreateSut(ledger);

        var setTask = Task.Run(() =>
            sut.SetSlotAsync(FrameId, 2, "MAT-MANUAL", SlotStates.Occupied, "tester"));

        await WaitUntilAsync(() => ledger.Trace.Contains("ExternalConditionalUpdateEnter"), TimeSpan.FromSeconds(5));

        var reserved = await sut.ReserveAsync(FrameId, "task-r16-correct", "MAT-RSV");
        Assert.That(reserved, Is.Not.Null);

        pause.TrySetResult();
        SlotMutationResult result;
        try
        {
            result = await setTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pause.TrySetResult();
        }

        var after = ledger.Require(FrameId, 2);
        TestContext.Out.WriteLine("R16 校正线性化: " + string.Join(" → ", ledger.Trace));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.ReservationConflict));
            Assert.That(result.Succeeded, Is.False);
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(after.Remark, Is.EqualTo("task-r16-correct"));
            Assert.That(after.BindSource, Is.EqualTo("RSV_PUT"));
            Assert.That(after.MaterialId, Is.EqualTo("MAT-RSV"));
            Assert.That(ledger.ExternalWriteCount, Is.EqualTo(1), "只调用一次原子 store，不得无条件重试");
            Assert.That(HasOrder(ledger.Trace,
                "ReserveCommit", "ExternalConditionalUpdate(affected=0)", "ClassifyReserved"), Is.True);
        });
    }

    [Test]
    public async Task R16_清槽条件写前Reserve先成功_返回ReservationConflict且不清REMARK()
    {
        var ledger = new ConcurrentSlotLedger(OccupiedSlot(3, 1, 3, "MAT-KEEP"));
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ledger.PauseBeforeExternalWrite = pause;
        var sut = CreateSut(ledger);

        // 先占住占用槽的 Reserve TAKE 路径所需状态；清槽目标 Empty
        var setTask = Task.Run(() =>
            sut.SetSlotAsync(FrameId, 3, null, SlotStates.Empty, "tester"));

        await WaitUntilAsync(() => ledger.Trace.Contains("ExternalConditionalUpdateEnter"), TimeSpan.FromSeconds(5));

        var reserved = await sut.ReserveTakeAsync(FrameId, "task-r16-clear");
        Assert.That(reserved, Is.Not.Null);

        pause.TrySetResult();
        SlotMutationResult result;
        try
        {
            result = await setTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pause.TrySetResult();
        }

        var after = ledger.Require(FrameId, 3);
        TestContext.Out.WriteLine("R16 清槽线性化: " + string.Join(" → ", ledger.Trace));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(SlotMutationStatus.ReservationConflict));
            Assert.That(after.SlotState, Is.EqualTo(SlotStates.Reserved));
            Assert.That(after.Remark, Is.EqualTo("task-r16-clear"), "清槽不得清空 REMARK");
            Assert.That(after.BindSource, Is.EqualTo("RSV_TAKE"));
            Assert.That(after.MaterialId, Is.EqualTo("MAT-KEEP"));
            Assert.That(ledger.ExternalWriteCount, Is.EqualTo(1));
        });
    }

    private static SlotAccountService CreateSut(ConcurrentSlotLedger ledger) =>
        new(new UnusedDbContextFactory(), ledger, NullLogger<SlotAccountService>.Instance);

    private static SlotRow EmptySlot(int slotNo, int layer, int pos) => new()
    {
        Id = slotNo,
        FrameId = FrameId,
        SlotNo = slotNo,
        LayerNo = layer,
        PosInLayer = pos,
        SlotState = SlotStates.Empty,
    };

    private static SlotRow OccupiedSlot(int slotNo, int layer, int pos, string material) => new()
    {
        Id = slotNo,
        FrameId = FrameId,
        SlotNo = slotNo,
        LayerNo = layer,
        PosInLayer = pos,
        SlotState = SlotStates.Occupied,
        MaterialId = material,
        BindSource = "CONFIRMED",
        BindTime = DateTime.Now.AddHours(-1),
    };

    private static bool HasOrder(IReadOnlyList<string> trace, params string[] expected)
    {
        var idx = 0;
        foreach (var step in trace)
        {
            if (step == expected[idx])
            {
                idx++;
                if (idx == expected.Length) return true;
            }
        }

        return false;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
                throw new TimeoutException("等待交错点超时");
            await Task.Yield();
        }
    }

    private sealed class UnusedDbContextFactory : IDbContextFactory<CncDbContext>
    {
        public CncDbContext CreateDbContext() =>
            throw new InvalidOperationException("并发测试 Reserve/外部写不应走 IDbContextFactory");

        public Task<CncDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("并发测试 Reserve/外部写不应走 IDbContextFactory");
    }
}
