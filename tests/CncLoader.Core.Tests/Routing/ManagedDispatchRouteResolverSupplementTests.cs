using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Communication.RcsCallbackTestFakes;

namespace CncLoader.Core.Tests.Routing;

/// <summary>P0-5 R16–R24 GREEN 补充：Resolver 歧义/禁用区分/异常与副作用顺序。</summary>
[TestFixture]
public sealed class ManagedDispatchRouteResolverSupplementTests
{
    [Test]
    public async Task Supplement_AmbiguousRcsCode_Rejects_DoesNotPickFirst()
    {
        var h = ManualReplayHarness.Create();
        h.LocationMap.Seed(new LocationMapItem
        {
            Id = 80, LocType = "POSITION", EquipmentId = ManualReplayRoutingCodes.SrcEq,
            PositionId = 2, RcsCode = ManualReplayRoutingCodes.AmbiguousCell, RcsType = "cell"
        });
        h.LocationMap.Seed(new LocationMapItem
        {
            Id = 81, LocType = "POSITION", EquipmentId = ManualReplayRoutingCodes.DstEq,
            PositionId = 2, RcsCode = ManualReplayRoutingCodes.AmbiguousCell, RcsType = "cell"
        });

        var resolved = await h.RouteResolver.ResolveAsync(
            ManualReplayRoutingCodes.AmbiguousCell, ManualReplayRoutingCodes.ToCell);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.Status, Is.EqualTo(ManagedDispatchRouteStatus.Ambiguous));
            Assert.That(resolved.IsResolved, Is.False);
        });

        h.ViewModel.SelectedKind = "搬运";
        h.ViewModel.FromCode = ManualReplayRoutingCodes.AmbiguousCell;
        h.ViewModel.ToCode = ManualReplayRoutingCodes.ToCell;
        await h.ViewModel.DispatchCommand.ExecuteAsync(null);
        Assert.That(h.Client.SendCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Supplement_DisabledMap_DistinctFromNotFound()
    {
        var loc = new FakeLocationMapForRouting();
        loc.Seed(new LocationMapItem
        {
            Id = 1, LocType = "POSITION", EquipmentId = 30, PositionId = 1,
            RcsCode = "CELL-X", RcsType = "cell"
        }, state: "1");
        loc.Seed(new LocationMapItem
        {
            Id = 2, LocType = "POSITION", EquipmentId = 40, PositionId = 1,
            RcsCode = "CELL-Y", RcsType = "cell"
        }, state: "0");

        var resolver = new ManagedDispatchRouteResolver(
            loc, new FakeFrameRoutingStore(), NullLogger<ManagedDispatchRouteResolver>.Instance);

        var disabled = await resolver.ResolveAsync("CELL-X", "CELL-Y");
        var missing = await resolver.ResolveAsync("NO-SUCH", "CELL-Y");

        Assert.Multiple(() =>
        {
            Assert.That(disabled.Status, Is.EqualTo(ManagedDispatchRouteStatus.Disabled));
            Assert.That(missing.Status, Is.EqualTo(ManagedDispatchRouteStatus.NotFound));
            Assert.That(disabled.Status, Is.Not.EqualTo(missing.Status));
        });
    }

    [Test]
    public async Task Supplement_ResolverException_ConfigurationUnavailable_RcsZero()
    {
        var boom = new ThrowingLocationMapStore();
        var resolver = new ManagedDispatchRouteResolver(
            boom, new FakeFrameRoutingStore(), NullLogger<ManagedDispatchRouteResolver>.Instance);
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(10, "L", 20, 1, 30);
        var equipment = new TracingEquipmentConfigService(store, new CallTrace());
        var validator = new CountingRoutingValidator(
            new RoutingAvailabilityValidator(
                store, equipment, new FakeFrameRoutingStore(),
                NullLogger<RoutingAvailabilityValidator>.Instance));
        var client = new FakeRcsHttpClient();
        var taskStore = new MutableRcsTaskStore();
        var svc = new global::CncLoader.Communication.Rcs.RcsTaskService(
            client, taskStore, new NoopMsg(), new TrackingCallbackProcessor(),
            resolver, validator, NullLogger<global::CncLoader.Communication.Rcs.RcsTaskService>.Instance, new TrackingSlotsForClosure());

        var result = await svc.DispatchTransitAsync(new TransitDispatchArgs
        {
            FromCode = "A", ToCode = "B", WorkLineId = 10, LineCode = "L"
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FailureKind, Is.EqualTo(RcsFailureKind.ConfigurationUnavailable)
                .Or.EqualTo(RcsFailureKind.RouteUnavailable));
            Assert.That(client.SendCount, Is.EqualTo(0));
            Assert.That(taskStore.CreateCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Supplement_FinalFail_RedoCountUnchanged_CallbackNotForgotten()
    {
        // Pre 通过、Final 失败：第二次 Validate 拒绝 → 不得 Increment / Forget / RCS
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(
            ManualReplayRoutingCodes.LineId, ManualReplayRoutingCodes.LineCode,
            ManualReplayRoutingCodes.CraftId, 1, ManualReplayRoutingCodes.SrcEq);
        store.SeedNextEquipment(
            ManualReplayRoutingCodes.DstEq, ManualReplayRoutingCodes.DstCraftId,
            2, ManualReplayRoutingCodes.LineId);
        var loc = new FakeLocationMapForRouting();
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
        var equipment = new TracingEquipmentConfigService(store, new CallTrace());
        var frames = new FakeFrameRoutingStore();
        var inner = new RoutingAvailabilityValidator(
            store, equipment, frames, NullLogger<RoutingAvailabilityValidator>.Instance);
        var flip = new FinalFailValidator(inner);
        var resolver = new ManagedDispatchRouteResolver(
            loc, frames, NullLogger<ManagedDispatchRouteResolver>.Instance);
        var client = new FakeRcsHttpClient();
        var taskStore = new MutableRcsTaskStore();
        var callbacks = new TrackingCallbackProcessor();
        var svc = new global::CncLoader.Communication.Rcs.RcsTaskService(
            client, taskStore, new NoopMsg(), callbacks,
            resolver, flip, NullLogger<global::CncLoader.Communication.Rcs.RcsTaskService>.Instance, new TrackingSlotsForClosure());
        taskStore.Seed(new RcsTaskRow(
            1, "LINE-A-MV-FINAL-FAIL", "transit", "2", RcsTaskState.Failed, "failed", 5,
            ManualReplayRoutingCodes.FromCell, ManualReplayRoutingCodes.ToCell,
            ManualReplayRoutingCodes.SrcEq, 1, null, null, null,
            3, "0", DateTime.Now, null, null, "prev"));

        var result = await svc.RedoAsync("LINE-A-MV-FINAL-FAIL");
        var after = taskStore.Snapshot("LINE-A-MV-FINAL-FAIL")!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(flip.CallCount, Is.EqualTo(2), "Pre + Final");
            Assert.That(taskStore.IncrementRedoCount, Is.EqualTo(0));
            Assert.That(after.RedoCount, Is.EqualTo(3));
            Assert.That(callbacks.ForgetCount, Is.EqualTo(0));
            Assert.That(client.SendCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Supplement_RedispatchFinalFail_OriginalSnapshotIntact()
    {
        var h = ManualReplayHarness.Create();
        var row = h.SeedHistoricalTask(
            "LINE-A-MV-REDIS-SNAP", state: RcsTaskState.Failed, error: "keep-me", redoCount: 4);
        h.Store.SetCraftState(ManualReplayRoutingCodes.CraftId, "1");
        var before = h.TaskStore.Snapshot(row.RcsTaskId!)!;

        var result = await h.TaskService.RedispatchAsync(row.RcsTaskId!);
        var after = h.TaskStore.Snapshot(row.RcsTaskId!)!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(after.TaskState, Is.EqualTo(before.TaskState));
            Assert.That(after.ErrorMsg, Is.EqualTo(before.ErrorMsg));
            Assert.That(after.RedoCount, Is.EqualTo(before.RedoCount));
            Assert.That(after.FromCode, Is.EqualTo(before.FromCode));
            Assert.That(after.ToCode, Is.EqualTo(before.ToCode));
            Assert.That(h.Callbacks.ForgetCount, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Supplement_ManualAllActive_PreAndFinalEachOnce()
    {
        var h = ManualReplayHarness.Create();
        h.ViewModel.SelectedKind = "搬运";
        h.ViewModel.FromCode = ManualReplayRoutingCodes.FromCell;
        h.ViewModel.ToCode = ManualReplayRoutingCodes.ToCell;
        await h.ViewModel.DispatchCommand.ExecuteAsync(null);

        Assert.Multiple(() =>
        {
            Assert.That(h.Client.SendCount, Is.EqualTo(1));
            Assert.That(h.Validator.CallCount, Is.EqualTo(3),
                "ViewModel Pre + Service Pre + Service Final 各一次");
            Assert.That(h.Validator.Results.Count(r => r.IsAvailable), Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Supplement_RedoRedispatch_AllActive_SendsOnceEach()
    {
        var h = ManualReplayHarness.Create();
        var redoRow = h.SeedHistoricalTask("LINE-A-MV-OK-REDO", redoCount: 0);
        var r1 = await h.TaskService.RedoAsync(redoRow.RcsTaskId!);
        Assert.That(r1.Success, Is.True);
        Assert.That(h.Client.SendCount, Is.EqualTo(1));

        var h2 = ManualReplayHarness.Create();
        var redisRow = h2.SeedHistoricalTask("LINE-A-MV-OK-REDIS", redoCount: 1);
        var r2 = await h2.TaskService.RedispatchAsync(redisRow.RcsTaskId!);
        Assert.That(r2.Success, Is.True);
        Assert.That(h2.Client.SendCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Supplement_SameInstance_ActiveThenDisable_ImmediateReject()
    {
        var h = ManualReplayHarness.Create();
        h.ViewModel.SelectedKind = "搬运";
        h.ViewModel.FromCode = ManualReplayRoutingCodes.FromCell;
        h.ViewModel.ToCode = ManualReplayRoutingCodes.ToCell;
        await h.ViewModel.DispatchCommand.ExecuteAsync(null);
        Assert.That(h.Client.SendCount, Is.EqualTo(1));

        h.Store.SetWorkLineState(ManualReplayRoutingCodes.LineId, "1");
        await h.ViewModel.DispatchCommand.ExecuteAsync(null);
        Assert.That(h.Client.SendCount, Is.EqualTo(1), "禁用后同实例立即拒发");
    }

    [Test]
    public async Task Supplement_HistoricalCallback_ValidatorCallsRemainZero()
    {
        var h = ManualReplayHarness.Create();
        h.Store.SetEquipmentState(ManualReplayRoutingCodes.SrcEq, "1");
        var v0 = h.Validator.CallCount;

        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var real = CreateProcessor(store, new FakeAlarms());
        var body = "{\"taskId\":\"LINE-A-MV-HIST-SUP\",\"data\":{\"system\":{\"error_code\":0,\"msg\":\"ok\"}}}";
        await real.HandlePushTaskStatusAsync(body);

        Assert.Multiple(() =>
        {
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            Assert.That(h.Validator.CallCount, Is.EqualTo(v0), "历史 callback 不得调用 Validator");
            Assert.That(h.Client.SendCount, Is.EqualTo(0));
        });
    }

    private sealed class FinalFailValidator : IRoutingAvailabilityValidator
    {
        private readonly IRoutingAvailabilityValidator _inner;
        private int _calls;
        public FinalFailValidator(IRoutingAvailabilityValidator inner) => _inner = inner;
        public int CallCount => _calls;

        public async Task<RoutingAvailabilityResult> ValidateAsync(
            DispatchRouteContext context, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 1) return await _inner.ValidateAsync(context, ct);
            return RoutingAvailabilityResult.Unavailable(
                RoutingUnavailableReason.EquipmentDisabled, "Equipment",
                context.SourceEquipmentId, "Final 模拟禁用");
        }
    }

    private sealed class ThrowingLocationMapStore : ILocationMapRoutingStore
    {
        public Task<IReadOnlyList<LocationMapRoutingRow>> FindByPositionAsync(
            long equipmentId, long? positionId, string rcsType, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<LocationMapRoutingRow>> FindByFrameAsync(
            long frameId, string rcsType, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<LocationMapRoutingRow>> FindByAreaAsync(
            string locName, CancellationToken ct = default)
            => throw new InvalidOperationException("boom");
        public Task<IReadOnlyList<LocationMapRoutingRow>> FindByRcsCodeAsync(
            string rcsCode, CancellationToken ct = default)
            => throw new InvalidOperationException("boom-rcs");
    }

    private sealed class NoopMsg : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    }
}
