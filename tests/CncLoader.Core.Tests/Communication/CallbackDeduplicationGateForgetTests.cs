using CncLoader.Communication.Rcs;

namespace CncLoader.Core.Tests.Communication;

/// <summary>
/// P1-2：ForgetTask 与 final seen 顺序结构一致性（Dictionary + LinkedList）。
/// </summary>
[TestFixture]
public sealed class CallbackDeduplicationGateForgetTests
{
    private const int DefaultCapacity = 4000;

    [Test]
    public async Task 最小僵尸复现_容量2_旧顺序项不得删除重新提交的A()
    {
        var gate = new CallbackDeduplicationGate(finalSeenCapacity: 2);
        Assert.That((await Persist(gate, "push:A:0")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
        Assert.That((await Persist(gate, "push:B:0")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));

        gate.ForgetTask("A");

        Assert.That((await Persist(gate, "push:A:0")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted),
            "Forget 后同 key 应可重新 Persisted");

        Assert.That((await Persist(gate, "push:C:0")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));

        var aCalls = 0;
        var aAgain = await gate.ExecuteAsync("push:A:0", _ =>
        {
            aCalls++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        var bCalls = 0;
        var bAgain = await gate.ExecuteAsync("push:B:0", _ =>
        {
            bCalls++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(aAgain.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate),
                "容量压力下新 A 仍应在 final seen；当前僵尸 Queue A 会误删新 A → Persisted（RED）");
            Assert.That(aCalls, Is.EqualTo(0), "A 为 Duplicate 时不得再次持久化");
            Assert.That(bAgain.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted),
                "B 作为最老 live 被淘汰后应可重新 Persisted");
            Assert.That(bCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Push命名空间_Forget后重提_容量淘汰不提前删除新key()
    {
        var gate = new CallbackDeduplicationGate(2);
        await Persist(gate, "push:T1:0");
        await Persist(gate, "push:T2:0");
        gate.ForgetTask("T1");
        await Persist(gate, "push:T1:0");
        await Persist(gate, "push:T3:0");

        var r = await gate.ExecuteAsync("push:T1:0", _ => Task.FromResult(true), CancellationToken.None);
        Assert.That(r.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
    }

    [Test]
    public async Task Scan命名空间_Forget后重提_容量淘汰不提前删除新key()
    {
        var gate = new CallbackDeduplicationGate(2);
        await Persist(gate, "scan:S1:0");
        await Persist(gate, "scan:S2:0");
        gate.ForgetTask("S1");
        await Persist(gate, "scan:S1:0");
        await Persist(gate, "scan:S3:0");

        var calls = 0;
        var r = await gate.ExecuteAsync("scan:S1:0", _ =>
        {
            calls++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
            Assert.That(calls, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task 同taskId多errorCode_Forget删除全部push与scan且可全部重提()
    {
        var gate = new CallbackDeduplicationGate(10);
        await Persist(gate, "push:TX:0");
        await Persist(gate, "push:TX:1");
        await Persist(gate, "scan:TX:0");

        gate.ForgetTask("TX");

        Assert.That((await Persist(gate, "push:TX:0")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
        Assert.That((await Persist(gate, "push:TX:1")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
        Assert.That((await Persist(gate, "scan:TX:0")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));

        // 重提后均应在 final seen（Duplicate）；淘汰顺序由最小复现/循环用例覆盖
        Assert.That((await gate.ExecuteAsync("push:TX:0", _ => Task.FromResult(true), CancellationToken.None)).Outcome,
            Is.EqualTo(CallbackDedupOutcome.Duplicate));
        Assert.That((await gate.ExecuteAsync("push:TX:1", _ => Task.FromResult(true), CancellationToken.None)).Outcome,
            Is.EqualTo(CallbackDedupOutcome.Duplicate));
        Assert.That((await gate.ExecuteAsync("scan:TX:0", _ => Task.FromResult(true), CancellationToken.None)).Outcome,
            Is.EqualTo(CallbackDedupOutcome.Duplicate));
    }

    [Test]
    public async Task 不同taskId_ForgetA不影响B()
    {
        var gate = new CallbackDeduplicationGate(10);
        await Persist(gate, "push:A:0");
        await Persist(gate, "push:B:0");
        gate.ForgetTask("A");

        var bCalls = 0;
        var b = await gate.ExecuteAsync("push:B:0", _ =>
        {
            bCalls++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(b.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
            Assert.That(bCalls, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Warn_ForgetTask不删除warn_finalSeen()
    {
        var gate = new CallbackDeduplicationGate(10);
        var warnKey = "warn:R1|2026-08-05|content";
        await Persist(gate, warnKey);
        gate.ForgetTask("any-task");

        var calls = 0;
        var r = await gate.ExecuteAsync(warnKey, _ =>
        {
            calls++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
            Assert.That(calls, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task InFlight期间Forget_不取消leader_成功后进入finalSeen_无第二并发持久化()
    {
        var gate = new CallbackDeduplicationGate(10);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaderCalls = 0;
        var followerCalls = 0;

        var leaderTask = gate.ExecuteAsync("push:IF:0", async _ =>
        {
            Interlocked.Increment(ref leaderCalls);
            entered.TrySetResult();
            await hold.Task;
            return true;
        }, CancellationToken.None);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            gate.ForgetTask("IF");

            var followerTask = gate.ExecuteAsync("push:IF:0", _ =>
            {
                Interlocked.Increment(ref followerCalls);
                return Task.FromResult(true);
            }, CancellationToken.None);

            Assert.That(followerTask.IsCompleted, Is.False, "Forget 不得拆掉 in-flight；follower 仍须等待");
            Assert.That(Volatile.Read(ref leaderCalls), Is.EqualTo(1));
            Assert.That(Volatile.Read(ref followerCalls), Is.EqualTo(0));

            hold.TrySetResult();
            var leader = await leaderTask.WaitAsync(TimeSpan.FromSeconds(2));
            var follower = await followerTask.WaitAsync(TimeSpan.FromSeconds(2));

            var againCalls = 0;
            var again = await gate.ExecuteAsync("push:IF:0", _ =>
            {
                againCalls++;
                return Task.FromResult(true);
            }, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(leader.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
                Assert.That(follower.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
                Assert.That(Volatile.Read(ref leaderCalls), Is.EqualTo(1));
                Assert.That(Volatile.Read(ref followerCalls), Is.EqualTo(0));
                Assert.That(again.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
                Assert.That(againCalls, Is.EqualTo(0));
            });
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [Test]
    public async Task 重复Forget_幂等不抛且不影响其他key()
    {
        var gate = new CallbackDeduplicationGate(10);
        await Persist(gate, "push:A:0");
        await Persist(gate, "push:B:0");

        Assert.DoesNotThrow(() =>
        {
            gate.ForgetTask("A");
            gate.ForgetTask("A");
            gate.ForgetTask("");
            gate.ForgetTask("   ");
        });

        var b = await gate.ExecuteAsync("push:B:0", _ => Task.FromResult(true), CancellationToken.None);
        Assert.That(b.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
    }

    [Test]
    public async Task ForgetPersist循环_最新key不得被历史僵尸提前淘汰_顺序有界()
    {
        var gate = new CallbackDeduplicationGate(2);
        await Persist(gate, "push:A:0");
        await Persist(gate, "push:B:0");

        for (var i = 0; i < 20; i++)
        {
            gate.ForgetTask("A");
            Assert.That((await Persist(gate, "push:A:0")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
        }

        // 再写入 C，应淘汰最老 live B，而不是误删最新 A
        await Persist(gate, "push:C:0");

        var aCalls = 0;
        var a = await gate.ExecuteAsync("push:A:0", _ =>
        {
            aCalls++;
            return Task.FromResult(true);
        }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(a.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate),
                "多次 Forget→Persist 后最新 A 仍须存活；僵尸 Queue 会导致提前淘汰（RED）");
            Assert.That(aCalls, Is.EqualTo(0));
            Assert.That(gate.ProbeFinalSeenOrderCount, Is.EqualTo(gate.ProbeFinalSeenCount),
                "顺序结构条目数须与 live 集合一致（无僵尸无界增长）");
            Assert.That(gate.ProbeFinalSeenOrderCount, Is.LessThanOrEqualTo(2));
        });
    }

    [Test]
    public async Task 默认容量4000_第4001个成功项淘汰最老live()
    {
        var gate = new CallbackDeduplicationGate();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < DefaultCapacity; i++)
        {
            var r = await Persist(gate, $"cap-{i}");
            Assert.That(r.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
        }

        Assert.That((await Persist(gate, "cap-overflow")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));

        var replayCalls = 0;
        var replay = await gate.ExecuteAsync("cap-0", _ =>
        {
            replayCalls++;
            return Task.FromResult(true);
        }, CancellationToken.None);
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(replay.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
            Assert.That(replayCalls, Is.EqualTo(1));
            Assert.That(sw.ElapsedMilliseconds, Is.LessThan(5000), "4000 容量回归应保持可接受耗时");
            TestContext.Out.WriteLine($"容量4000回归耗时: {sw.ElapsedMilliseconds} ms");
        });
    }

    [Test]
    public async Task SingleFlight回归_成功Duplicate_失败释放_follower等待与取消()
    {
        var gate = new CallbackDeduplicationGate(10);

        // 成功后 Duplicate
        await Persist(gate, "push:SF:0");
        var dupCalls = 0;
        var dup = await gate.ExecuteAsync("push:SF:0", _ =>
        {
            dupCalls++;
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.That(dup.Outcome, Is.EqualTo(CallbackDedupOutcome.Duplicate));
        Assert.That(dupCalls, Is.EqualTo(0));

        // 失败释放后可再成为 leader
        var fail = await gate.ExecuteAsync("push:SF-fail:0", _ => Task.FromResult(false), CancellationToken.None);
        Assert.That(fail.Outcome, Is.EqualTo(CallbackDedupOutcome.Failed));
        Assert.That((await Persist(gate, "push:SF-fail:0")).Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));

        // follower 等待 + 取消
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaderCalls = 0;
        var followerCalls = 0;
        var leaderTask = gate.ExecuteAsync("push:SF-wait:0", async _ =>
        {
            Interlocked.Increment(ref leaderCalls);
            entered.TrySetResult();
            await hold.Task;
            return true;
        }, CancellationToken.None);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var cts = new CancellationTokenSource();
            var followerTask = gate.ExecuteAsync("push:SF-wait:0", _ =>
            {
                Interlocked.Increment(ref followerCalls);
                return Task.FromResult(true);
            }, cts.Token);

            cts.Cancel();
            var follower = await followerTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(follower.Outcome, Is.EqualTo(CallbackDedupOutcome.Cancelled));
            Assert.That(Volatile.Read(ref followerCalls), Is.EqualTo(0));

            hold.TrySetResult();
            var leader = await leaderTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Multiple(() =>
            {
                Assert.That(leader.Outcome, Is.EqualTo(CallbackDedupOutcome.Persisted));
                Assert.That(Volatile.Read(ref leaderCalls), Is.EqualTo(1));
            });
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    private static Task<CallbackDedupResult> Persist(CallbackDeduplicationGate gate, string key)
        => gate.ExecuteAsync(key, _ => Task.FromResult(true), CancellationToken.None);
}
