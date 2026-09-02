using CncLoader.Communication.Rcs;
using CncLoader.Core.Rcs;
using CncLoader.Core.Tests.Routing;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Rcs;

/// <summary>Redo：料架上料任务预记已回滚时须先补预记再下发。</summary>
[TestFixture]
public sealed class RedoReplayReservationTests
{
    private const string TaskId = "LINE01-TR-REDO-0001";

    [Test]
    public async Task Redo_预记已回滚_先按物料补预记再下发()
    {
        var h = Harness.Create();
        h.Slots.AllowReserve = true;
        h.SeedFailedUploadFromFrame(materialId: "M-9");

        var result = await h.Svc.RedoAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(h.Slots.ReserveTakeByMaterialCount, Is.EqualTo(1));
            Assert.That(h.Slots.LastMaterialId, Is.EqualTo("M-9"));
            Assert.That(h.Client.TransitCount, Is.EqualTo(1));
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Redo_预记仍在_不再锁槽且下发()
    {
        var h = Harness.Create();
        h.Slots.Existing = new ReservedSlot(FrameIdTransit, 1, 1, 1, "M-9");
        h.SeedFailedUploadFromFrame(materialId: "M-9");

        var result = await h.Svc.RedoAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(h.Slots.ReserveTakeByMaterialCount, Is.EqualTo(0));
            Assert.That(h.Client.TransitCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Redo_补预记失败_不得下发()
    {
        var h = Harness.Create();
        h.Slots.AllowReserve = false;
        h.SeedFailedUploadFromFrame(materialId: "M-9");

        var result = await h.Svc.RedoAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(h.Client.TransitCount, Is.EqualTo(0));
            Assert.That(h.Tasks.IncrementRedoCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Redo_下发失败_回滚本轮新预记()
    {
        var h = Harness.Create();
        h.Slots.AllowReserve = true;
        h.Client.FailNextTransit = true;
        h.SeedFailedUploadFromFrame(materialId: "M-9");

        var result = await h.Svc.RedoAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(h.Client.TransitCount, Is.EqualTo(1));
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AutoRedispatch_补预记失败_不Claim不下发()
    {
        var h = Harness.Create();
        h.Slots.AllowReserve = false;
        h.SeedFailedUploadFromFrame(materialId: "M-9");

        var result = await h.Svc.AutoRedispatchAsync(TaskId, maxRedoCount: 3);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(h.Tasks.TryClaimSuccessCount, Is.EqualTo(0));
            Assert.That(h.Client.TransitCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Redo_命名区上料_不补预记仍可下发()
    {
        var h = Harness.Create();
        h.SeedFailedUploadFromArea();

        var result = await h.Svc.RedoAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(h.Slots.ReserveTakeByMaterialCount, Is.EqualTo(0));
            Assert.That(h.Slots.ReserveTakeCount, Is.EqualTo(0));
            Assert.That(h.Client.TransitCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Redo_下发失败且预记本就在_不得回滚()
    {
        var h = Harness.Create();
        h.Slots.Existing = new ReservedSlot(FrameIdTransit, 1, 1, 1, "M-9");
        h.Client.FailNextTransit = true;
        h.SeedFailedUploadFromFrame(materialId: "M-9");

        var result = await h.Svc.RedoAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(h.Slots.ReserveTakeByMaterialCount, Is.EqualTo(0));
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(0));
            Assert.That(h.Client.TransitCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Redo_下料到料架_补入库预记再下发()
    {
        var h = Harness.Create();
        h.Slots.AllowReserve = true;
        h.SeedFailedDownloadToFrame(materialId: "M-2");

        var result = await h.Svc.RedoAsync(TaskId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(h.Slots.ReservePutCount, Is.EqualTo(1));
            Assert.That(h.Slots.LastMaterialId, Is.EqualTo("M-2"));
            Assert.That(h.Client.TransitCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AutoRedispatch_Claim失败_回滚本轮新预记()
    {
        var h = Harness.Create();
        h.Slots.AllowReserve = true;
        h.SeedFailedUploadFromFrame(materialId: "M-9", redoCount: 3);

        var result = await h.Svc.AutoRedispatchAsync(TaskId, maxRedoCount: 3);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(h.Tasks.TryClaimSuccessCount, Is.EqualTo(0));
            Assert.That(h.Client.TransitCount, Is.EqualTo(0));
            Assert.That(h.Slots.RollbackTakeCount, Is.EqualTo(1));
        });
    }

    private sealed class Harness
    {
        public required RcsTaskService Svc { get; init; }
        public required ReplaySlots Slots { get; init; }
        public required FakeRcsHttpClient Client { get; init; }
        public required MutableRcsTaskStore Tasks { get; init; }

        public static Harness Create()
        {
            var loc = new FakeLocationMapForRouting();
            SeedStandardAreas(loc);
            loc.Seed(PositionCellMap());
            loc.Seed(FrameShelf());
            loc.Seed(FrameCell());
            loc.Seed(FrameDownloadCell());

            var frames = new FakeFrameRoutingStore();
            frames.Seed(FrameIdTransit);
            frames.Seed(FrameIdDownload);

            var eq = new MutableEquipmentRoutingStore();
            SeedActiveEquipmentChain(eq);

            var resolver = new ManagedDispatchRouteResolver(
                loc, frames, NullLogger<ManagedDispatchRouteResolver>.Instance);
            var equipment = new TracingEquipmentConfigService(eq, new CallTrace());
            var validator = new RoutingAvailabilityValidator(
                eq, equipment, frames, NullLogger<RoutingAvailabilityValidator>.Instance);

            var client = new FakeRcsHttpClient();
            var tasks = new MutableRcsTaskStore();
            var slots = new ReplaySlots();
            var svc = new RcsTaskService(
                client, tasks, new NoopMsgLog(), new TrackingCallbackProcessor(),
                resolver, validator, NullLogger<RcsTaskService>.Instance, slots);
            return new Harness { Svc = svc, Slots = slots, Client = client, Tasks = tasks };
        }

        public void SeedFailedUploadFromFrame(string materialId, int redoCount = 0)
            => Tasks.Seed(new RcsTaskRow(
                1, TaskId, "transit", "0", RcsTaskState.Failed, "failed", 5,
                FrameCellCode, PositionCell, EqId, PositionId, materialId,
                null, null, redoCount, "0", DateTime.Now, null, null, "alarm-rollback"));

        public void SeedFailedUploadFromArea()
            => Tasks.Seed(new RcsTaskRow(
                1, TaskId, "transit", "0", RcsTaskState.Failed, "failed", 5,
                LoadAreaCode, PositionCell, EqId, PositionId, null,
                null, null, 0, "0", DateTime.Now, null, null, "fail"));

        public void SeedFailedDownloadToFrame(string materialId)
            => Tasks.Seed(new RcsTaskRow(
                1, TaskId, "transit", "1", RcsTaskState.Failed, "failed", 5,
                PositionCell, FrameCellCode, EqId, PositionId, materialId,
                null, null, 0, "0", DateTime.Now, null, null, "alarm-rollback"));
    }

    private sealed class NoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    }

    private sealed class ReplaySlots : ISlotAccountService
    {
        public ReservedSlot? Existing { get; set; }
        public bool AllowReserve { get; set; }
        public int ReserveTakeCount { get; private set; }
        public int ReserveTakeByMaterialCount { get; private set; }
        public int ReservePutCount { get; private set; }
        public int RollbackTakeCount { get; private set; }
        public string? LastMaterialId { get; private set; }

        public Task<ReservedSlot?> FindReservedAsync(string taskId, CancellationToken ct = default)
            => Task.FromResult(Existing);

        public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
        {
            ReserveTakeCount++;
            return Task.FromResult(AllowReserve ? new ReservedSlot(frameId, 1, 1, 1, "X") : null);
        }

        public Task<ReservedSlot?> ReserveTakeByMaterialAsync(long frameId, string taskId, string materialId, CancellationToken ct = default)
        {
            ReserveTakeByMaterialCount++;
            LastMaterialId = materialId;
            if (!AllowReserve) return Task.FromResult<ReservedSlot?>(null);
            Existing = new ReservedSlot(frameId, 1, 1, 1, materialId);
            return Task.FromResult<ReservedSlot?>(Existing);
        }

        public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default)
        {
            RollbackTakeCount++;
            Existing = null;
            return Task.FromResult(true);
        }

        public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
        {
            ReservePutCount++;
            LastMaterialId = materialId;
            if (!AllowReserve) return Task.FromResult<ReservedSlot?>(null);
            Existing = new ReservedSlot(frameId, 1, 1, 1, materialId);
            return Task.FromResult<ReservedSlot?>(Existing);
        }
        public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
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
