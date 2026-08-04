using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class ReservationFirstDispatcherTests
{
    private static Task<RoutingAvailabilityResult> AlwaysAvailable(string _, CancellationToken __)
        => Task.FromResult(RoutingAvailabilityResult.Available(new WorkLineRef(1, "LINE")));

    [Test]
    public async Task 预记失败时不得调用Rcs()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var dispatchCalls = 0;

        var result = await dispatcher.ExecuteAsync<object>(
            "task-1",
            (_, _) => Task.FromResult<object?>(null),
            AlwaysAvailable,
            (_, _) => { dispatchCalls++; return Task.FromResult(Success("task-1")); },
            (_, _) => Task.FromResult(true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ReservationFirstDispatchStatus.ReservationFailed));
            Assert.That(dispatchCalls, Is.Zero);
        });
    }

    [Test]
    public async Task 预记抛异常时也不得调用Rcs()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var dispatchCalls = 0;

        var result = await dispatcher.ExecuteAsync<object>(
            "task-1",
            (_, _) => throw new InvalidOperationException("数据库异常"),
            AlwaysAvailable,
            (_, _) => { dispatchCalls++; return Task.FromResult(Success("task-1")); },
            (_, _) => Task.FromResult(true));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ReservationFirstDispatchStatus.ReservationFailed));
            Assert.That(result.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(dispatchCalls, Is.Zero);
        });
    }

    [Test]
    public async Task 应先预记再用同一TaskId下发()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var events = new List<string>();

        var result = await dispatcher.ExecuteAsync(
            "task-1",
            (taskId, _) => { events.Add($"reserve:{taskId}"); return Task.FromResult<object?>(new object()); },
            async (taskId, _) => { events.Add($"final:{taskId}"); return await AlwaysAvailable(taskId, default); },
            (taskId, _) => { events.Add($"dispatch:{taskId}"); return Task.FromResult(Success(taskId)); },
            (taskId, _) => { events.Add($"rollback:{taskId}"); return Task.FromResult(true); });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ReservationFirstDispatchStatus.Dispatched));
            Assert.That(events, Is.EqualTo(new[] { "reserve:task-1", "final:task-1", "dispatch:task-1" }));
        });
    }

    [Test]
    public async Task 最终门禁失败时应回滚且不得调用Rcs()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var events = new List<string>();

        var result = await dispatcher.ExecuteAsync(
            "task-1",
            (taskId, _) => { events.Add($"reserve:{taskId}"); return Task.FromResult<object?>(new object()); },
            (_, _) =>
            {
                events.Add("final");
                return Task.FromResult(RoutingAvailabilityResult.Unavailable(
                    RoutingUnavailableReason.WorkLineDisabled, "WorkLine", 10, "线体禁用"));
            },
            (taskId, _) => { events.Add($"dispatch:{taskId}"); return Task.FromResult(Success(taskId)); },
            (taskId, _) => { events.Add($"rollback:{taskId}"); return Task.FromResult(true); });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ReservationFirstDispatchStatus.RouteUnavailable));
            Assert.That(result.RollbackSucceeded, Is.True);
            Assert.That(events, Is.EqualTo(new[] { "reserve:task-1", "final", "rollback:task-1" }));
        });
    }

    [Test]
    public async Task Rcs明确失败时应回滚预记()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var events = new List<string>();

        var result = await dispatcher.ExecuteAsync(
            "task-1",
            (taskId, _) => { events.Add($"reserve:{taskId}"); return Task.FromResult<object?>(new object()); },
            AlwaysAvailable,
            (taskId, _) => { events.Add($"dispatch:{taskId}"); return Task.FromResult(RcsResult.Fail("", "拒绝")); },
            (taskId, _) => { events.Add($"rollback:{taskId}"); return Task.FromResult(true); });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ReservationFirstDispatchStatus.DispatchFailed));
            Assert.That(result.RollbackSucceeded, Is.True);
            Assert.That(events, Is.EqualTo(new[] { "reserve:task-1", "dispatch:task-1", "rollback:task-1" }));
        });
    }

    [Test]
    public async Task Rcs抛异常时应回滚预记()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var rolledBack = false;

        var result = await dispatcher.ExecuteAsync(
            "task-1",
            (_, _) => Task.FromResult<object?>(new object()),
            AlwaysAvailable,
            (_, _) => throw new InvalidOperationException("网络异常"),
            (_, _) => { rolledBack = true; return Task.FromResult(true); });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ReservationFirstDispatchStatus.DispatchFailed));
            Assert.That(result.Exception, Is.TypeOf<InvalidOperationException>());
            Assert.That(rolledBack, Is.True);
        });
    }

    [Test]
    public async Task 并发争抢同一资源时只有预记成功者可以下发()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var reservationOwner = 0;
        var dispatchCalls = 0;

        Task<object?> Reserve(string _, CancellationToken __) =>
            Task.FromResult(Interlocked.CompareExchange(ref reservationOwner, 1, 0) == 0 ? new object() : null);
        Task<RcsResult> Dispatch(string taskId, CancellationToken _) =>
            Task.FromResult(Interlocked.Increment(ref dispatchCalls) == 1
                ? Success(taskId)
                : RcsResult.Fail("", "不应出现第二次下发"));

        var results = await Task.WhenAll(
            dispatcher.ExecuteAsync("task-1", Reserve, AlwaysAvailable, Dispatch, (_, _) => Task.FromResult(true)),
            dispatcher.ExecuteAsync("task-2", Reserve, AlwaysAvailable, Dispatch, (_, _) => Task.FromResult(true)));

        Assert.Multiple(() =>
        {
            Assert.That(dispatchCalls, Is.EqualTo(1));
            Assert.That(results.Count(r => r.Status == ReservationFirstDispatchStatus.Dispatched), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Status == ReservationFirstDispatchStatus.ReservationFailed), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Rcs返回不同TaskId时应按失败回滚()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var rolledBack = false;

        var result = await dispatcher.ExecuteAsync(
            "task-expected",
            (_, _) => Task.FromResult<object?>(new object()),
            AlwaysAvailable,
            (_, _) => Task.FromResult(Success("task-other")),
            (_, _) => { rolledBack = true; return Task.FromResult(true); });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ReservationFirstDispatchStatus.DispatchFailed));
            Assert.That(rolledBack, Is.True);
        });
    }

    [Test]
    public void 下发期间取消时也应回滚预记并继续传播取消()
    {
        var dispatcher = new ReservationFirstDispatcher();
        var rolledBack = false;
        using var cts = new CancellationTokenSource();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await dispatcher.ExecuteAsync(
                "task-1",
                (_, _) => Task.FromResult<object?>(new object()),
                AlwaysAvailable,
                (_, _) => { cts.Cancel(); throw new OperationCanceledException(cts.Token); },
                (_, _) => { rolledBack = true; return Task.FromResult(true); },
                cts.Token));

        Assert.That(rolledBack, Is.True);
    }

    private static RcsResult Success(string taskId) =>
        new(true, 200, true, "ok", "", "{}", null, 1) { TaskId = taskId };
}
