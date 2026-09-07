using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class ReplayReservationTests
{
    [Test]
    public void 上料且起点是料架_须取料预记()
    {
        var plan = ReplayReservation.Decide("transit", "0", ManagedEndpointKind.Frame, ManagedEndpointKind.Position);
        Assert.That(plan, Is.EqualTo(ReplayReservationPlan.Take));
    }

    [Test]
    public void 下料且终点是料架_须入库预记()
    {
        var plan = ReplayReservation.Decide("transit", "1", ManagedEndpointKind.Position, ManagedEndpointKind.Frame);
        Assert.That(plan, Is.EqualTo(ReplayReservationPlan.Put));
    }

    [Test]
    public void 上料命名区_不建槽位账()
    {
        var plan = ReplayReservation.Decide("transit", "0", ManagedEndpointKind.Area, ManagedEndpointKind.Position);
        Assert.That(plan, Is.EqualTo(ReplayReservationPlan.Skip));
    }

    [Test]
    public void 抓取上料且起点是料架_须取料预记()
    {
        var plan = ReplayReservation.Decide("grab", "0", ManagedEndpointKind.Frame, ManagedEndpointKind.Position);
        Assert.That(plan, Is.EqualTo(ReplayReservationPlan.Take));
    }

    [Test]
    public void 抓取下料且终点是料架_须入库预记()
    {
        var plan = ReplayReservation.Decide("grab", "1", ManagedEndpointKind.Position, ManagedEndpointKind.Frame);
        Assert.That(plan, Is.EqualTo(ReplayReservationPlan.Put));
    }

    [TestCase("identify")]
    [TestCase("change_frame")]
    [TestCase("pallet_return")]
    public void 非搬运非抓取种类_不建槽位账(string kind)
    {
        var plan = ReplayReservation.Decide(kind, "0", ManagedEndpointKind.Frame, ManagedEndpointKind.Position);
        Assert.That(plan, Is.EqualTo(ReplayReservationPlan.Skip));
    }

    [Test]
    public async Task 已有预记_不再锁槽()
    {
        var slots = new MemorySlots { Existing = new ReservedSlot(2, 1, 1, 1, "M-1") };

        var hold = await ReplayReservation.EnsureAsync(
            slots, "T1", ReplayReservationPlan.Take, frameId: 2, materialId: "M-1");

        Assert.Multiple(() =>
        {
            Assert.That(hold.Ok, Is.True);
            Assert.That(hold.Created, Is.False);
            Assert.That(slots.ReserveTakeByMaterialCount, Is.EqualTo(0));
            Assert.That(slots.ReserveTakeCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task 预记已回滚_按物料补取料预记()
    {
        var slots = new MemorySlots();

        var hold = await ReplayReservation.EnsureAsync(
            slots, "T1", ReplayReservationPlan.Take, frameId: 2, materialId: "M-1");

        Assert.Multiple(() =>
        {
            Assert.That(hold.Ok, Is.True);
            Assert.That(hold.Created, Is.True);
            Assert.That(hold.IsTake, Is.True);
            Assert.That(slots.ReserveTakeByMaterialCount, Is.EqualTo(1));
            Assert.That(slots.LastMaterialId, Is.EqualTo("M-1"));
        });
    }

    [Test]
    public async Task 无物料码上料_走首个占用槽预记()
    {
        var slots = new MemorySlots();

        var hold = await ReplayReservation.EnsureAsync(
            slots, "T1", ReplayReservationPlan.Take, frameId: 2, materialId: null);

        Assert.Multiple(() =>
        {
            Assert.That(hold.Ok, Is.True);
            Assert.That(hold.Created, Is.True);
            Assert.That(slots.ReserveTakeCount, Is.EqualTo(1));
            Assert.That(slots.ReserveTakeByMaterialCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task 下料_补入库预记()
    {
        var slots = new MemorySlots();

        var hold = await ReplayReservation.EnsureAsync(
            slots, "T1", ReplayReservationPlan.Put, frameId: 2, materialId: "M-2");

        Assert.Multiple(() =>
        {
            Assert.That(hold.Ok, Is.True);
            Assert.That(hold.Created, Is.True);
            Assert.That(hold.IsTake, Is.False);
            Assert.That(slots.ReservePutCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Skip计划_不锁槽()
    {
        var slots = new MemorySlots();

        var hold = await ReplayReservation.EnsureAsync(
            slots, "T1", ReplayReservationPlan.Skip, frameId: 2, materialId: "M-1");

        Assert.Multiple(() =>
        {
            Assert.That(hold.Ok, Is.True);
            Assert.That(hold.Created, Is.False);
            Assert.That(slots.ReserveTakeCount, Is.EqualTo(0));
            Assert.That(slots.ReservePutCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task 补预记失败_拒绝()
    {
        var slots = new MemorySlots { NextReserveFails = true };

        var hold = await ReplayReservation.EnsureAsync(
            slots, "T1", ReplayReservationPlan.Take, frameId: 2, materialId: "M-1");

        Assert.Multiple(() =>
        {
            Assert.That(hold.Ok, Is.False);
            Assert.That(hold.Failure, Is.Not.Null);
            Assert.That(hold.Failure!.Success, Is.False);
        });
    }

    private sealed class MemorySlots : ISlotAccountService
    {
        public ReservedSlot? Existing { get; set; }
        public bool NextReserveFails { get; set; }
        public int ReserveTakeCount { get; private set; }
        public int ReserveTakeByMaterialCount { get; private set; }
        public int ReservePutCount { get; private set; }
        public string? LastMaterialId { get; private set; }

        public Task<ReservedSlot?> FindReservedAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult(Existing);

        public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
        {
            ReserveTakeCount++;
            return Task.FromResult(NextReserveFails ? null : new ReservedSlot(frameId, 1, 1, 1, "X"));
        }

        public Task<ReservedSlot?> ReserveTakeByMaterialAsync(long frameId, string taskId, string materialId, CancellationToken ct = default)
        {
            ReserveTakeByMaterialCount++;
            LastMaterialId = materialId;
            return Task.FromResult(NextReserveFails ? null : new ReservedSlot(frameId, 1, 1, 1, materialId));
        }

        public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
        {
            ReservePutCount++;
            return Task.FromResult(NextReserveFails ? null : new ReservedSlot(frameId, 1, 1, 1, materialId));
        }

        public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
        public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default) => Task.FromResult(new FrameOccupancy(0, 0, 0, 0));
        public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
            => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.Updated, frameId, slotNo, null));
        public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default) => Task.FromResult<SlotLocation?>(null);
        public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
            => Task.FromResult(InventoryCorrectionResult.Empty());
        public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
    }
}
