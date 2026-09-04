using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.UI;

/// <summary>
/// P0-6 R10：用户触发盘点最终回写按 <see cref="InventoryCorrectionResult"/> 映射通知。
/// </summary>
[TestFixture]
public sealed class FrameViewModelInventoryCorrectionTests
{
    [Test]
    public void R10_混合盘点完成_不得Success且须Warning含更新与跳过预记数()
    {
        var notify = new FakeNotify();
        var inventory = new FakeInventory();
        var frames = new FakeFrames();
        var vm = new FrameViewModel(
            frames,
            new FakeSlots(),
            inventory,
            new FakeScheduler(),
            new FakeUser(),
            notify,
            new ImmediateUiDispatcher(),
            new StubDialogService());
        vm.SelectedFrame = new FrameRowVm(new FrameListItem(7, "架A", "F-A", "1×2", 2, 1));

        var correction = new InventoryCorrectionResult(
            InventoryCorrectionStatus.CompletedWithWarnings,
            RequestedCount: 2,
            UpdatedCount: 1,
            UnchangedCount: 0,
            ReservationConflictCount: 1,
            NotFoundCount: 0,
            ConflictSlots:
            [
                new SlotMutationSnapshot(2, 7, 2, SlotStates.Reserved, "MAT-R",
                    null, "RSV_PUT", DateTime.Now)
            ]);

        inventory.RaiseCompleted(new InventoryResultEvent(
            7, "task-ui-1", "COMPLETED", "F-A", new[] { "MAT-A", "MAT-B" },
            correction.UpdatedCount, null, correction));

        Assert.Multiple(() =>
        {
            Assert.That(notify.SuccessCount, Is.EqualTo(0), "有预记冲突时不得纯 Success");
            Assert.That(notify.WarningCount, Is.EqualTo(1), "每个最终结果只发一个通知");
            Assert.That(notify.ErrorCount, Is.EqualTo(0));
            Assert.That(notify.Warnings.Any(t =>
                    t.Contains("1", StringComparison.Ordinal) &&
                    (t.Contains("更新", StringComparison.Ordinal) || t.Contains("校正", StringComparison.Ordinal))),
                Is.True, "须包含更新数");
            Assert.That(notify.Warnings.Any(t =>
                    t.Contains("1", StringComparison.Ordinal) &&
                    (t.Contains("预记", StringComparison.Ordinal) || t.Contains("跳过", StringComparison.Ordinal))),
                Is.True, "须包含跳过预记数");
            Assert.That(string.Join('\n', notify.AllMessages), Does.Not.Contain("task-inv-secret-should-not-leak"));
        });
    }

    [Test]
    public async Task R10_发起盘点成功不得当作回写全部成功()
    {
        var notify = new FakeNotify();
        var inventory = new FakeInventory { NextTaskId = "task-started-only" };
        var vm = new FrameViewModel(
            new FakeFrames(),
            new FakeSlots(),
            inventory,
            new FakeScheduler(),
            new FakeUser(),
            notify,
            new ImmediateUiDispatcher(),
            new StubDialogService());
        vm.SelectedFrame = new FrameRowVm(new FrameListItem(7, "架A", "F-A", "1×2", 2, 1));

        // StartInventory 仍走 Info 通知；本断言锁定：发起返回不得产生 Success「校正完成」。
        await vm.StartInventoryCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(inventory.StartCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(0), "任务已发起 ≠ 盘点回写全部成功");
        });
    }

    private sealed class FakeNotify : IUserNotificationService
    {
        public List<string> Successes { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Infos { get; } = [];
        public List<string> Errors { get; } = [];
        public int SuccessCount => Successes.Count;
        public int WarningCount => Warnings.Count;
        public int ErrorCount => Errors.Count;
        public IEnumerable<string> AllMessages => Successes.Concat(Infos).Concat(Warnings).Concat(Errors);
        public void Success(string message) => Successes.Add(message);
        public void Info(string message) => Infos.Add(message);
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) => Errors.Add(message);

        public bool ConfirmAnswer { get; set; } = true;
        public bool Confirm(string message, string title) => ConfirmAnswer;
        public void Alert(string message, string title) => Warnings.Add(message);
    }

    private sealed class FakeInventory : IInventoryService
    {
        public string NextTaskId { get; set; } = "task-x";
        public int StartCount { get; private set; }
        public event EventHandler<InventoryResultEvent>? InventoryCompleted;
        public void RaiseCompleted(InventoryResultEvent e) => InventoryCompleted?.Invoke(this, e);
        public Task<string> StartInventoryAsync(long frameId, int posStart, int count, string author, CancellationToken ct = default)
        {
            StartCount++;
            return Task.FromResult(NextTaskId);
        }
        public IReadOnlyList<InventoryTaskInfo> GetActiveInventories() => Array.Empty<InventoryTaskInfo>();
    }

    private sealed class FakeSlots : ISlotAccountService
    {
        public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState,
            string author, CancellationToken ct = default)
            => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.Updated, frameId, slotNo, null));
        public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
            => Task.FromResult<ReservedSlot?>(null);
        public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
            => Task.FromResult<ReservedSlot?>(null);
        public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
            IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
        public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult(new FrameOccupancy(0, 0, 0, 0));
        public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default)
            => Task.FromResult<SlotLocation?>(null);
        public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
            => Task.FromResult(InventoryCorrectionResult.Empty());
        public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
    }

    private sealed class FakeFrames : IFrameService
    {
        public Task<IReadOnlyList<FrameListItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameListItem>>(Array.Empty<FrameListItem>());
        public Task<FrameDetail?> GetDetailAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<FrameDetail?>(new FrameDetail(
                frameId, "架A", "F-A", 1, 2, 2, 1,
                Array.Empty<FrameBindRow>(),
                Array.Empty<SlotItem>()));
        public Task<long> CreateFrameAsync(FrameCreateModel model, string author, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<NamedOption>> GetEquipmentOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task BindEquipmentAsync(long frameId, long equipmentId, string roleCode, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnbindAsync(long bindId, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<FrameEditModel?> GetFrameForEditAsync(long id, CancellationToken ct = default) => Task.FromResult<FrameEditModel?>(null);
        public Task UpdateFrameAsync(FrameEditModel model, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeleteCheckResult> CheckDeleteFrameAsync(long id, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteFrameAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeScheduler : IPositionScheduler
    {
        public bool IsReconciled => true;
        public ReconciliationState ReconciliationState => ReconciliationState.Succeeded;
        public string? ReconciliationFailureReason => null;
        public bool IsAutoDispatchPaused => false;
        public event EventHandler? Reconciled { add { } remove { } }
        public event EventHandler<ReconciliationSnapshot>? ReconciliationStateChanged { add { } remove { } }
        public void SetAutoDispatchPaused(bool paused) { }
        public Task ResetAlarmAsync(long equipmentId, long positionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTaskAbandonedAsync(string taskId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public void InvalidateFrameBindingCache(long? equipmentId = null) { }
    }

    private sealed class FakeUser : ICurrentUser
    {
        public string Name => "tester";
    }
}
