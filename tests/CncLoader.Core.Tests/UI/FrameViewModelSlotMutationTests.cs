using System.IO;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.UI;

/// <summary>
/// P0-6 R7：真实 <see cref="FrameViewModel"/> 校正/清槽命令按 <see cref="SlotMutationResult"/> 映射通知。
/// </summary>
[TestFixture]
public sealed class FrameViewModelSlotMutationTests
{
    [Test]
    public async Task R7_人工校正ReservationConflict_不得Success且须Warning预记文案()
    {
        var slots = new FakeSlots { NextResult = Conflict("task-secret-should-not-leak") };
        var notify = new FakeNotify();
        var frames = new FakeFrames();
        var vm = CreateVm(slots, notify, frames);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-NEW");

        var detailBefore = frames.GetDetailCount;
        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(0), "ReservationConflict 不得 Success");
            Assert.That(notify.WarningCount, Is.EqualTo(1));
            Assert.That(notify.Warnings, Has.Some.Contain("槽位已被任务预记，不能人工校正"));
            Assert.That(string.Join('\n', notify.AllMessages), Does.Not.Contain("task-secret-should-not-leak"));
            // 特征记录：当前忽略结果仍会 LoadDetail（本轮不改刷新策略）
            Assert.That(frames.GetDetailCount, Is.GreaterThan(detailBefore),
                "特征：当前冲突后仍刷新明细（记录用，非目标策略）");
        });
    }

    [Test]
    public async Task R7_清槽ReservationConflict_不得Success且须Warning()
    {
        var slots = new FakeSlots { NextResult = Conflict("task-clear-secret") };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "空(0)", material: "MAT-R");

        await vm.ClearSelectedSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(slots.LastMaterialId, Is.Null);
            Assert.That(slots.LastSlotState, Is.EqualTo(SlotStates.Empty));
            Assert.That(notify.SuccessCount, Is.EqualTo(0));
            Assert.That(notify.WarningCount, Is.EqualTo(1));
            Assert.That(notify.Warnings, Has.Some.Contain("槽位已被任务预记，不能清空"));
            Assert.That(string.Join('\n', notify.AllMessages), Does.Not.Contain("task-clear-secret"));
        });
    }

    [Test]
    public async Task R7_Updated_保持现有成功文案()
    {
        var slots = new FakeSlots { NextResult = Result(SlotMutationStatus.Updated) };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-OK");

        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(1));
            Assert.That(notify.WarningCount, Is.EqualTo(0));
            Assert.That(notify.ErrorCount, Is.EqualTo(0));
            Assert.That(notify.Successes, Has.Some.Contain("已校正"));
        });
    }

    [Test]
    public async Task R7_Unchanged_不得Success且须中性Info()
    {
        var slots = new FakeSlots { NextResult = Result(SlotMutationStatus.Unchanged) };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-SAME");

        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(0), "Unchanged 不得显示修改成功");
            Assert.That(notify.InfoCount, Is.EqualTo(1));
            Assert.That(notify.Infos, Has.Some.Contain("槽位状态无需修改"));
            Assert.That(notify.WarningCount, Is.EqualTo(0));
            Assert.That(notify.ErrorCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task R7_NotFound_不得Success且须Error槽位不存在()
    {
        var slots = new FakeSlots { NextResult = Result(SlotMutationStatus.NotFound, "料架 7 槽 2 不存在") };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-X");

        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(0));
            Assert.That(notify.ErrorCount, Is.EqualTo(1));
            Assert.That(notify.Errors, Has.Some.Contain("槽位不存在或已被删除"));
            Assert.That(string.Join('\n', notify.AllMessages), Does.Not.Contain("料架 7 槽 2 不存在"));
        });
    }

    [Test]
    public async Task R7_ConcurrencyConflict_不得Success且须Warning刷新重试()
    {
        var slots = new FakeSlots { NextResult = Result(SlotMutationStatus.ConcurrencyConflict, "并发") };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-Y");

        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(0));
            Assert.That(notify.WarningCount, Is.EqualTo(1));
            Assert.That(notify.Warnings, Has.Some.Contain("槽位状态已变化，请刷新后重试"));
        });
    }

    [Test]
    public async Task R7_DatabaseError_不得Success且不得泄露内部细节()
    {
        var slots = new FakeSlots
        {
            NextResult = Result(SlotMutationStatus.DatabaseError,
                "MySqlConnector.MySqlException: Access denied for user 'root'@'localhost' using password; ConnectionString=Server=127.0.0.1; SELECT * FROM MAS_AUTO_FRAME_SLOT")
        };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-Z");

        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(0));
            Assert.That(notify.ErrorCount, Is.EqualTo(1));
            Assert.That(notify.Errors, Has.Some.Contain("槽位操作失败，请查看日志"));
            foreach (var text in notify.AllMessages)
            {
                Assert.That(text, Does.Not.Contain("ConnectionString"));
                Assert.That(text, Does.Not.Contain("SELECT *"));
                Assert.That(text, Does.Not.Contain("password"));
                Assert.That(text, Does.Not.Contain("127.0.0.1"));
                Assert.That(text, Does.Not.Contain("Access denied"));
            }
        });
    }

    [Test]
    public async Task R7_Cancelled_不提示失败且命令不残留运行中()
    {
        var slots = new FakeSlots { NextResult = Result(SlotMutationStatus.Cancelled, "操作已取消") };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-C");

        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(0));
            Assert.That(notify.WarningCount, Is.EqualTo(0));
            Assert.That(notify.ErrorCount, Is.EqualTo(0));
            Assert.That(notify.InfoCount, Is.EqualTo(0));
            Assert.That(vm.CorrectSlotCommand.IsRunning, Is.False, "命令结束后不得残留运行中（等价 IsBusy 恢复）");
        });
    }

    [Test]
    public async Task R7_未知Status_不得Success且须安全Error不抛()
    {
        var slots = new FakeSlots { NextResult = Result((SlotMutationStatus)999, "internal-leak") };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-U");

        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(slots.CallCount, Is.EqualTo(1));
            Assert.That(notify.SuccessCount, Is.EqualTo(0));
            Assert.That(notify.ErrorCount, Is.EqualTo(1));
            Assert.That(notify.Errors, Has.Some.Contain("槽位操作未完成，请刷新后重试"));
            Assert.That(string.Join('\n', notify.AllMessages), Does.Not.Contain("internal-leak"));
            Assert.That(vm.CorrectSlotCommand.IsRunning, Is.False);
        });
    }

    [Test]
    public void R_人工状态选项不含预记_已有Reserved槽展示仍为预记()
    {
        var vm = CreateVm(new FakeSlots(), new FakeNotify());
        var reservedSlot = new SlotVm(2, "1层-2", "MAT-R", SlotStates.Reserved);
        vm.SelectedSlot = reservedSlot;

        Assert.Multiple(() =>
        {
            Assert.That(vm.SlotStateOptions, Does.Not.Contain("预记(3)"));
            Assert.That(vm.SlotStateOptions, Is.EquivalentTo(new[] { "空(0)", "占用(1)", "锁定(2)" }));
            Assert.That(vm.CorrectSlotState, Is.EqualTo("空(0)"), "选中预记槽时编辑默认落到允许项");
            Assert.That(reservedSlot.Reserved, Is.True);
            Assert.That(reservedSlot.StateBadge, Is.EqualTo("reserved"));
            Assert.That(reservedSlot.MaterialText, Is.EqualTo("MAT-R"));
        });
    }

    [Test]
    public async Task R_InvalidTargetState_不得Success且须固定Warning文案()
    {
        // 对应直接 SetSlot(target=Reserved) 被 Service 拒绝后的 VM 映射（绕过下拉的安全网）。
        var slots = new FakeSlots
        {
            NextResult = Result(SlotMutationStatus.InvalidTargetState,
                "预记状态只能由派工流程创建，不能人工设置")
        };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-X");

        await vm.CorrectSlotCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(notify.SuccessCount, Is.EqualTo(0));
            Assert.That(notify.WarningCount, Is.EqualTo(1));
            Assert.That(notify.Warnings, Has.Some.EqualTo("预记状态只能由派工流程创建，不能人工设置"));
            Assert.That(notify.ErrorCount, Is.EqualTo(0));
            Assert.That(string.Join('\n', notify.AllMessages), Does.Not.Contain("RSV_"));
            Assert.That(string.Join('\n', notify.AllMessages), Does.Not.Contain("task-"));
        });
    }

    [Test]
    public void R7_HandyControl通知服务不含SlotMutationStatus映射()
    {
        var src = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..", "..",
            "src", "CncLoader.UI", "Services", "HandyControlUserNotificationService.cs"));
        Assert.That(File.Exists(src), Is.True, $"找不到源文件：{src}");
        var text = File.ReadAllText(src);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Not.Contain("SlotMutationStatus"));
            Assert.That(text, Does.Not.Contain("SlotMutationResult"));
            Assert.That(text, Does.Contain("Growl.Success"));
            Assert.That(text, Does.Contain("Growl.Info"));
            Assert.That(text, Does.Contain("Growl.Warning"));
            Assert.That(text, Does.Contain("Growl.Error"));
        });
    }

    [Test]
    public async Task R7_Updated后刷新明细_清槽冲突当前也刷新_特征记录()
    {
        var frames = new FakeFrames();
        var slots = new FakeSlots { NextResult = Result(SlotMutationStatus.Updated) };
        var notify = new FakeNotify();
        var vm = CreateVm(slots, notify, frames);
        PrepareSelection(vm, correctState: "占用(1)", material: "MAT-OK");
        var beforeUpdated = frames.GetDetailCount;
        await vm.CorrectSlotCommand.ExecuteAsync(null);
        var afterUpdated = frames.GetDetailCount;

        slots.NextResult = Conflict("task-x");
        notify.Reset();
        PrepareSelection(vm, correctState: "空(0)", material: "MAT-R");
        var beforeConflict = frames.GetDetailCount;
        await vm.ClearSelectedSlotCommand.ExecuteAsync(null);
        var afterConflict = frames.GetDetailCount;

        Assert.Multiple(() =>
        {
            Assert.That(afterUpdated, Is.GreaterThan(beforeUpdated), "Updated 后应刷新明细");
            Assert.That(afterConflict, Is.GreaterThan(beforeConflict),
                "特征：当前 ReservationConflict 后仍刷新（GREEN 前不改策略）");
        });
    }

    private static FrameViewModel CreateVm(FakeSlots slots, FakeNotify notify, FakeFrames? frames = null)
        => new(
            frames ?? new FakeFrames(),
            slots,
            new FakeInventory(),
            new FakeScheduler(),
            new FakeUser(),
            notify);

    private static void PrepareSelection(FrameViewModel vm, string correctState, string? material)
    {
        vm.SelectedFrame = new FrameRowVm(new FrameListItem(7, "架A", "F-A", "1×2", 2, 1));
        vm.SelectedSlot = new SlotVm(2, "1层-2", material, SlotStates.Reserved);
        vm.CorrectMaterial = material ?? "";
        vm.CorrectSlotState = correctState;
    }

    private static SlotMutationResult Result(SlotMutationStatus status, string? message = null) =>
        new(status, 7, 2, 10, message, null);

    private static SlotMutationResult Conflict(string remarkInSnapshot) =>
        new(SlotMutationStatus.ReservationConflict, 7, 2, 10,
            "槽位已被任务预记，不能人工校正",
            new SlotMutationSnapshot(10, 7, 2, SlotStates.Reserved, "MAT-R", remarkInSnapshot, "RSV_PUT",
                DateTime.Now));

    private sealed class FakeNotify : IUserNotificationService
    {
        public List<string> Successes { get; } = [];
        public List<string> Infos { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public int SuccessCount => Successes.Count;
        public int InfoCount => Infos.Count;
        public int WarningCount => Warnings.Count;
        public int ErrorCount => Errors.Count;
        public IEnumerable<string> AllMessages => Successes.Concat(Infos).Concat(Warnings).Concat(Errors);

        public void Success(string message) => Successes.Add(message);
        public void Info(string message) => Infos.Add(message);
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) => Errors.Add(message);
        public void Reset()
        {
            Successes.Clear();
            Infos.Clear();
            Warnings.Clear();
            Errors.Clear();
        }
    }

    private sealed class FakeSlots : ISlotAccountService
    {
        public SlotMutationResult NextResult { get; set; } = Result(SlotMutationStatus.Updated);
        public int CallCount { get; private set; }
        public long LastFrameId { get; private set; }
        public int LastSlotNo { get; private set; }
        public string? LastMaterialId { get; private set; }
        public string? LastSlotState { get; private set; }
        public CancellationToken LastCt { get; private set; }

        public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState,
            string author, CancellationToken ct = default)
        {
            CallCount++;
            LastFrameId = frameId;
            LastSlotNo = slotNo;
            LastMaterialId = materialId;
            LastSlotState = slotState;
            LastCt = ct;
            return Task.FromResult(NextResult);
        }

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
        public int GetDetailCount { get; private set; }

        public Task<IReadOnlyList<FrameListItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameListItem>>(Array.Empty<FrameListItem>());

        public Task<FrameDetail?> GetDetailAsync(long frameId, CancellationToken ct = default)
        {
            GetDetailCount++;
            return Task.FromResult<FrameDetail?>(new FrameDetail(
                frameId, "架A", "F-A", 1, 2, 2, 1,
                Array.Empty<FrameBindRow>(),
                new[]
                {
                    new SlotItem(1, 1, 1, "1层-1", null, SlotStates.Empty, false),
                    new SlotItem(2, 1, 2, "1层-2", "MAT-R", SlotStates.Reserved, false),
                }));
        }

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

    private sealed class FakeInventory : IInventoryService
    {
        public event EventHandler<InventoryResultEvent>? InventoryCompleted
        {
            add { }
            remove { }
        }
        public Task<string> StartInventoryAsync(long frameId, int posStart, int count, string author, CancellationToken ct = default)
            => Task.FromResult("");
        public IReadOnlyList<InventoryTaskInfo> GetActiveInventories() => Array.Empty<InventoryTaskInfo>();
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
