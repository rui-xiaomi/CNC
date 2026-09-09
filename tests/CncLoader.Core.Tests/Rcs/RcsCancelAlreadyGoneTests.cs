using CncLoader.Communication.Rcs;
using CncLoader.Core.Rcs;
using CncLoader.Core.Tests.Routing;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using static CncLoader.Core.Tests.Routing.TypedEndpointSeedShapes;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class RcsCancelAlreadyGoneTests
{
    private FakeRcsHttpClient _client = null!;
    private MutableRcsTaskStore _tasks = null!;
    private RcsCallbackNotifier _notifier = null!;
    private RcsTaskService _svc = null!;
    private RcsTaskStatusEvent? _lastEvent;

    [SetUp]
    public void SetUp()
    {
        var loc = new FakeLocationMapForRouting();
        SeedStandardAreas(loc);
        loc.Seed(PositionCellMap());
        loc.Seed(FrameShelf());
        loc.Seed(FrameCell());

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

        _client = new FakeRcsHttpClient();
        _tasks = new MutableRcsTaskStore();
        _notifier = new RcsCallbackNotifier();
        _lastEvent = null;
        _notifier.TaskStatusReceived += (_, e) => _lastEvent = e;
        _svc = new RcsTaskService(
            _client, _tasks, new NoopMsgLog(), new TrackingCallbackProcessor(),
            resolver, validator, NullLogger<RcsTaskService>.Instance,
            new TrackingSlotsForClosure(), _notifier);
    }

    [Test]
    public async Task Cancel_Rcs已无此任务_本地未完结应收口为CANCELED()
    {
        SeedDispatched("T-GONE");
        _client.CancelFailMessage = "任务编号T-GONE不存在";

        var result = await _svc.CancelAsync("T-GONE");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.Snapshot("T-GONE")!.TaskState, Is.EqualTo(RcsTaskState.Canceled));
            Assert.That(_lastEvent?.TaskState, Is.EqualTo(RcsTaskState.Canceled));
            Assert.That(_lastEvent?.Source, Is.EqualTo("cancel"));
            Assert.That(_client.CancelCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Cancel_本地已是CANCELED_再次取消视为成功()
    {
        _tasks.Seed(Row("T-DUP", RcsTaskState.Canceled));
        _client.CancelFailMessage = "任务编号T-DUP不存在";

        var result = await _svc.CancelAsync("T-DUP");

        Assert.That(result.Success, Is.True);
        Assert.That(_tasks.Snapshot("T-DUP")!.TaskState, Is.EqualTo(RcsTaskState.Canceled));
    }

    [Test]
    public async Task Cancel_已完成任务_Rcs不存在不得改写COMPLETED()
    {
        _tasks.Seed(Row("T-DONE", RcsTaskState.Completed));
        _client.CancelFailMessage = "任务编号T-DONE不存在";

        var result = await _svc.CancelAsync("T-DONE");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(_tasks.Snapshot("T-DONE")!.TaskState, Is.EqualTo(RcsTaskState.Completed));
            Assert.That(_lastEvent, Is.Null);
        });
    }

    [Test]
    public async Task Cancel_无本地任务且Rcs不存在_保持失败()
    {
        _client.CancelFailMessage = "任务编号T-MISS不存在";

        var result = await _svc.CancelAsync("T-MISS");

        Assert.That(result.Success, Is.False);
        Assert.That(_lastEvent, Is.Null);
    }

    [Test]
    public async Task Cancel_Rcs成功但本地已COMPLETED_不改写()
    {
        _tasks.Seed(Row("T-DONE-OK", RcsTaskState.Completed));

        var result = await _svc.CancelAsync("T-DONE-OK");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.Snapshot("T-DONE-OK")!.TaskState, Is.EqualTo(RcsTaskState.Completed));
            Assert.That(_lastEvent, Is.Null);
        });
    }

    [Test]
    public async Task Cancel_Rcs成功_本地标CANCELED并通知()
    {
        SeedDispatched("T-OK");

        var result = await _svc.CancelAsync("T-OK");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(_tasks.Snapshot("T-OK")!.TaskState, Is.EqualTo(RcsTaskState.Canceled));
            Assert.That(_lastEvent?.TaskState, Is.EqualTo(RcsTaskState.Canceled));
        });
    }

    [TestCase("canceled", RcsTaskState.Canceled)]
    [TestCase("CANCELLED", RcsTaskState.Canceled)]
    [TestCase("Killed", RcsTaskState.Canceled)]
    [TestCase("underway", RcsTaskState.Executing)]
    public void ToTaskState_大小写与cancelled拼写(string raw, string expected)
        => Assert.That(RcsStatusMapper.ToTaskState(raw), Is.EqualTo(expected));

    [Test]
    public void IsAlreadyCanceledOrGone_文档事例()
    {
        Assert.That(RcsCancelSemantics.IsAlreadyCanceledOrGone("任务编号T1不存在", null), Is.True);
        Assert.That(RcsCancelSemantics.IsAlreadyCanceledOrGone(null, "task not found"), Is.True);
        Assert.That(RcsCancelSemantics.IsAlreadyCanceledOrGone("网络超时", null), Is.False);
    }

    private void SeedDispatched(string id)
        => _tasks.Seed(Row(id, RcsTaskState.Dispatched));

    private static RcsTaskRow Row(string id, string state)
        => new(1, id, "transit", "0", state, "underway", 5,
            LoadAreaCode, PositionCell, EqId, PositionId, null, null, null,
            0, "0", DateTime.Now, DateTime.Now, null, null);

    private sealed class NoopMsgLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
