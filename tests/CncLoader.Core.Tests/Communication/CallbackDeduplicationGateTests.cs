using CncLoader.Communication.Rcs;

namespace CncLoader.Core.Tests.Communication;

/// <summary>
/// P0-4 第三组：直接测真实 <see cref="CallbackDeduplicationGate"/> 容量与 in-flight 边界。
/// </summary>
[TestFixture]
public sealed class CallbackDeduplicationGateTests
{
    private const int FinalSeenCapacity = 4000;

    [Test]
    public async Task FinalSeen容量4000_最早成功key淘汰后可再处理()
    {
        var gate = new CallbackDeduplicationGate();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < FinalSeenCapacity; i++)
        {
            var key = $"cap-{i}";
            var r = await gate.ExecuteAsync(key, _ => Task.FromResult(true), CancellationToken.None);
            Assert.That(r.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
        }

        // 第 4001 个成功 key 应淘汰最早的 cap-0
        var overflow = await gate.ExecuteAsync("cap-overflow", _ => Task.FromResult(true), CancellationToken.None);
        Assert.That(overflow.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));

        var replayCalls = 0;
        var replay = await gate.ExecuteAsync("cap-0", _ =>
        {
            replayCalls++;
            return Task.FromResult(true);
        }, CancellationToken.None);
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(replay.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted),
                "最早 final seen 被 FIFO 淘汰后应可重新成为 leader 并持久化");
            Assert.That(replayCalls, Is.EqualTo(1));
            TestContext.Out.WriteLine($"容量淘汰测试耗时: {sw.ElapsedMilliseconds} ms");
        });
    }

    [Test]
    public async Task InFlight不参与容量淘汰_follower仍等待原leader()
    {
        var gate = new CallbackDeduplicationGate();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaderPersistCalls = 0;
        var followerPersistCalls = 0;

        var leaderTask = gate.ExecuteAsync("key-A", async _ =>
        {
            Interlocked.Increment(ref leaderPersistCalls);
            entered.TrySetResult();
            await hold.Task;
            return true;
        }, CancellationToken.None);

        try
        {
            var enteredWinner = await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (enteredWinner != entered.Task)
                Assert.Fail("leader 未进入持久化");
            await entered.Task;

            for (var i = 0; i < FinalSeenCapacity; i++)
            {
                var r = await gate.ExecuteAsync($"fill-{i}", _ => Task.FromResult(true), CancellationToken.None);
                Assert.That(r.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
            }

            var followerTask = gate.ExecuteAsync("key-A", _ =>
            {
                Interlocked.Increment(ref followerPersistCalls);
                return Task.FromResult(true);
            }, CancellationToken.None);

            Assert.That(followerTask.IsCompleted, Is.False,
                "填满 final seen 后 key-A follower 仍须等待原 leader，不得另开持久化");
            Assert.That(VolatileRead(ref leaderPersistCalls), Is.EqualTo(1));
            Assert.That(VolatileRead(ref followerPersistCalls), Is.EqualTo(0));

            hold.TrySetResult();
            var leaderResult = await leaderTask;
            var followerResult = await followerTask;

            Assert.Multiple(() =>
            {
                Assert.That(leaderResult.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
                Assert.That(followerResult.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
                Assert.That(VolatileRead(ref leaderPersistCalls), Is.EqualTo(1));
                Assert.That(VolatileRead(ref followerPersistCalls), Is.EqualTo(0),
                    "follower 不得执行第二次 key-A 持久化");
            });
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [Test]
    public async Task Persist返回false_不进finalSeen_可再次成为leader()
    {
        var gate = new CallbackDeduplicationGate();
        var first = await gate.ExecuteAsync("miss-1", _ => Task.FromResult(false), CancellationToken.None);
        var secondCalls = 0;
        var second = await gate.ExecuteAsync("miss-1", _ =>
        {
            secondCalls++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.Outcome, Is.EqualTo(CallbackDedupOutcome.Failed));
            Assert.That(first.ErrorMessage, Does.Contain("不存在").Or.Contain("未更新"));
            Assert.That(second.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
            Assert.That(secondCalls, Is.EqualTo(1));
        });
    }

    private static int VolatileRead(ref int location) => Volatile.Read(ref location);
}
