using CncLoader.Communication.Rcs;
using CncLoader.Core.Rcs;
using static CncLoader.Core.Tests.Communication.RcsCallbackTestFakes;

namespace CncLoader.Core.Tests.Communication;

/// <summary>
/// P0-4：失败释放（第一组）+ 成功去重 / single-flight（第二组）+ 边界契约（第三组部分）。
/// 直接打真实 <see cref="RcsCallbackProcessor"/>。
/// </summary>
[TestFixture]
public sealed class RcsCallbackProcessorDeduplicationTests
{
    private const string PushTaskId = "LINE-MV-20260804120000-0001";
    private const string ScanTaskId = "LINE-ID-20260804120000-0002";
    private const string SharedTaskId = "LINE-XX-20260804120000-0099";

    private static readonly string PushCompletedBody =
        "{\"taskId\":\"" + PushTaskId + "\",\"data\":{\"system\":{\"error_code\":0,\"msg\":\"ok\"}}}";

    private static readonly string PushFailedBody =
        "{\"taskId\":\"" + PushTaskId + "\",\"data\":{\"system\":{\"error_code\":1,\"msg\":\"failed\"}}}";

    private static readonly string ScanCompletedBody =
        "{\"taskId\":\"" + ScanTaskId +
        "\",\"data\":{\"system\":{\"error_code\":0,\"msg\":\"ok\"},\"code\":\"F01\",\"products\":[\"EL-1\"]}}";

    private static readonly string WarnBody = """
        {"data":[{"robotCode":"AGV-01","beginTime":"2026-08-04 12:00:00","warnContent":"急停","taskCode":"T-9"}]}
        """;

    // ─── 第一组：失败释放 ───────────────────────────────────────────

    [Test]
    public async Task Push_UpdateStateAsync抛异常后_同key再次到达须再次持久化()
    {
        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => throw new InvalidOperationException("测试：落库失败"));
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var sut = CreateProcessor(store, new FakeAlarms());

        var ack1 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);
        var ack2 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(PushTaskId));
            Assert.That(ack2, Does.Contain(PushTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                "持久化失败后同 key 应再次调用 UpdateStateAsync；当前 MarkSeen 先提交会吞掉第二次");
            Assert.That(store.UpdateTaskIds, Is.EqualTo(new[] { PushTaskId, PushTaskId }));
            Assert.That(store.UpdateStates[0], Is.EqualTo(RcsTaskState.Completed));
        });
    }

    /// <summary>
    /// FAILED 终态下 UpdateStateAsync 抛异常后同 key 可重试（异常路径；显式 false 另见 UpdateStateAsync返回false 测试）。
    /// </summary>
    [Test]
    public async Task Push_Failed终态_UpdateStateAsync抛异常后_同key须再次持久化_代替显式失败()
    {
        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => throw new InvalidOperationException("测试：FAILED 态落库失败"));
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var sut = CreateProcessor(store, new FakeAlarms());

        await sut.HandlePushTaskStatusAsync(PushFailedBody);
        await sut.HandlePushTaskStatusAsync(PushFailedBody);

        Assert.Multiple(() =>
        {
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                "FAILED 终态持久化失败后同 key 应可重试；当前会被 seen 吞掉");
            Assert.That(store.UpdateStates, Is.EqualTo(new[] { RcsTaskState.Failed, RcsTaskState.Failed }));
        });
    }

    [Test]
    public async Task Scan_UpdateStateAsync抛异常后_同key再次到达须再次持久化()
    {
        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => throw new InvalidOperationException("测试：scan 落库失败"));
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var sut = CreateProcessor(store, new FakeAlarms());

        var ack1 = await sut.HandleScanTaskStatusAsync(ScanCompletedBody);
        var ack2 = await sut.HandleScanTaskStatusAsync(ScanCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(ScanTaskId));
            Assert.That(ack2, Does.Contain(ScanTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                "scan 持久化失败后同 key 应再次调用 UpdateStateAsync；须走 HandleScanTaskStatusAsync");
            Assert.That(store.UpdateTaskIds, Is.EqualTo(new[] { ScanTaskId, ScanTaskId }));
        });
    }

    [Test]
    public async Task Warn_RaiseRcsWarnAsync抛异常后_同key再次到达须再次落告警()
    {
        var alarms = new FakeAlarms();
        alarms.EnqueueRaise(_ => throw new InvalidOperationException("测试：告警落库失败"));
        alarms.EnqueueRaise(_ => Task.FromResult(101L));
        var sut = CreateProcessor(new FakeTaskStore(), alarms);

        var ack1 = await sut.HandleWarnCallbackAsync(WarnBody);
        var ack2 = await sut.HandleWarnCallbackAsync(WarnBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain("\"taskId\":\"\""));
            Assert.That(ack2, Does.Contain("\"taskId\":\"\""));
            Assert.That(alarms.RaiseRcsWarnCalls, Is.EqualTo(2),
                "warn 落库失败后同 key 应再次调用 RaiseRcsWarnAsync；当前 MarkSeen 先提交会吞掉第二次");
            Assert.That(alarms.RobotCodes, Is.EqualTo(new[] { "AGV-01", "AGV-01" }));
            Assert.That(alarms.WarnContents, Is.EqualTo(new[] { "急停", "急停" }));
        });
    }

    [Test]
    public async Task Push_持久化期间取消后_释放key_第二次未取消token须再次持久化()
    {
        var store = new FakeTaskStore();
        using var cts = new CancellationTokenSource();
        store.EnqueueUpdate(call =>
        {
            cts.Cancel();
            call.Ct.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        });
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var sut = CreateProcessor(store, new FakeAlarms());

        var ack1 = await sut.HandlePushTaskStatusAsync(PushCompletedBody, cts.Token);
        var ack2 = await sut.HandlePushTaskStatusAsync(PushCompletedBody, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(PushTaskId));
            Assert.That(ack2, Does.Contain(PushTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                "取消后不得留下 final seen；第二次未取消 token 应再次进入 UpdateStateAsync");
            Assert.That(store.Tokens[0].IsCancellationRequested, Is.True);
            Assert.That(store.Tokens[1].IsCancellationRequested, Is.False);
        });
    }

    // ─── 第二组：成功去重与并发 single-flight ───────────────────────

    [Test]
    public async Task Push_成功后同key只持久化一次()
    {
        var store = new FakeTaskStore();
        var sut = CreateProcessor(store, new FakeAlarms());

        var ack1 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);
        var ack2 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(PushTaskId));
            Assert.That(ack2, Does.Contain(PushTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1),
                "成功后同 key 重复推送不得再次持久化（既有成功去重特征）");
        });
    }

    [Test]
    public async Task Push_并发同key只有leader执行持久化()
    {
        var store = new FakeTaskStore();
        var gate = store.EnqueueBlockingUpdate();
        var sut = CreateProcessor(store, new FakeAlarms());

        var leaderTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
        try
        {
            await WaitEnteredAsync(gate);
            var followerTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
            var callsWhileBlocked = store.UpdateStateCalls;

            gate.ReleaseSuccess();
            await leaderTask;
            await followerTask;

            Assert.Multiple(() =>
            {
                Assert.That(callsWhileBlocked, Is.EqualTo(1),
                    "follower 到达时不得开启第二次 UpdateStateAsync");
                Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            gate.ReleaseSuccess();
        }
    }

    [Test]
    public async Task Push_follower必须等待leader完成不得提前ACK()
    {
        var store = new FakeTaskStore();
        var gate = store.EnqueueBlockingUpdate();
        var sut = CreateProcessor(store, new FakeAlarms());

        var leaderTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
        try
        {
            await WaitEnteredAsync(gate);
            var followerTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
            var followerCompletedEarly = followerTask.IsCompleted;

            gate.ReleaseSuccess();
            var ackLeader = await leaderTask;
            var ackFollower = await followerTask;

            Assert.Multiple(() =>
            {
                Assert.That(followerCompletedEarly, Is.False,
                    "leader 持久化未完成时 follower 不得提前返回 ACK；当前 MarkSeen 会令 follower 立即完成");
                Assert.That(ackLeader, Does.Contain(PushTaskId));
                Assert.That(ackFollower, Does.Contain(PushTaskId));
                Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            gate.ReleaseSuccess();
        }
    }

    [Test]
    public async Task Push_leader成功后_follower作为重复成功完成且不重复持久化()
    {
        var store = new FakeTaskStore();
        var gate = store.EnqueueBlockingUpdate();
        var sut = CreateProcessor(store, new FakeAlarms());

        var leaderTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
        try
        {
            await WaitEnteredAsync(gate);
            var followerTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
            var followerCompletedEarly = followerTask.IsCompleted;

            gate.ReleaseSuccess();
            var ackLeader = await leaderTask;
            var ackFollower = await followerTask;

            Assert.Multiple(() =>
            {
                Assert.That(followerCompletedEarly, Is.False,
                    "follower 须等待 leader；完成后才可作为重复成功返回");
                Assert.That(ackLeader, Does.Contain(PushTaskId));
                Assert.That(ackFollower, Does.Contain(PushTaskId));
                Assert.That(store.UpdateStateCalls, Is.EqualTo(1),
                    "leader 成功后 follower 不得再触发持久化");
            });
        }
        finally
        {
            gate.ReleaseSuccess();
        }
    }

    [Test]
    public async Task Push_leader失败后_follower不串行重试_第三次新回调可接管()
    {
        var store = new FakeTaskStore();
        var gate = store.EnqueueBlockingUpdate();
        var sut = CreateProcessor(store, new FakeAlarms());

        var leaderTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
        try
        {
            await WaitEnteredAsync(gate);
            var followerTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
            var followerCompletedEarly = followerTask.IsCompleted;
            var callsWhileBlocked = store.UpdateStateCalls;

            gate.ReleaseException(new InvalidOperationException("测试：leader 落库失败"));
            var ackLeader = await leaderTask;
            var ackFollower = await followerTask;
            var callsAfterFailWave = store.UpdateStateCalls;

            var ackThird = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

            Assert.Multiple(() =>
            {
                Assert.That(followerCompletedEarly, Is.False,
                    "leader 失败前 follower 不得提前完成");
                Assert.That(callsWhileBlocked, Is.EqualTo(1),
                    "同波 follower 不得自动成为第二个 leader（避免串行重试风暴）");
                Assert.That(ackLeader, Does.Contain(PushTaskId));
                Assert.That(ackFollower, Does.Contain(PushTaskId));
                Assert.That(callsAfterFailWave, Is.EqualTo(1),
                    "失败波次内持久化仍只应 1 次");
                Assert.That(ackThird, Does.Contain(PushTaskId));
                Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                    "leader 失败并释放 in-flight 后，第三次新同 key 回调必须再次进入持久化；当前 seen 会吞掉");
            });
        }
        finally
        {
            gate.ReleaseSuccess();
        }
    }

    [Test]
    public async Task Warn_follower必须等待leader_成功后只落告警一次()
    {
        var alarms = new FakeAlarms();
        var gate = alarms.EnqueueBlockingRaise();
        var sut = CreateProcessor(new FakeTaskStore(), alarms);

        var leaderTask = sut.HandleWarnCallbackAsync(WarnBody);
        try
        {
            await WaitEnteredAsync(gate);
            var followerTask = sut.HandleWarnCallbackAsync(WarnBody);
            var followerCompletedEarly = followerTask.IsCompleted;
            var callsWhileBlocked = alarms.RaiseRcsWarnCalls;

            gate.ReleaseSuccess();
            var ackLeader = await leaderTask;
            var ackFollower = await followerTask;

            Assert.Multiple(() =>
            {
                Assert.That(followerCompletedEarly, Is.False,
                    "warn follower 须等待 leader；当前 MarkSeen 会提前 ACK");
                Assert.That(callsWhileBlocked, Is.EqualTo(1));
                Assert.That(ackLeader, Does.Contain("\"taskId\":\"\""));
                Assert.That(ackFollower, Does.Contain("\"taskId\":\"\""));
                Assert.That(alarms.RaiseRcsWarnCalls, Is.EqualTo(1),
                    "warn single-flight 成功后 RaiseRcsWarnAsync 只调用一次");
            });
        }
        finally
        {
            gate.ReleaseSuccess();
        }
    }

    // ─── 第三组：key 隔离 / 事件 / follower Cancellation ─────────────

    [Test]
    public async Task Push与Scan_相同taskId与errorCode_namespace隔离各自持久化一次()
    {
        var store = new FakeTaskStore();
        var sut = CreateProcessor(store, new FakeAlarms());
        var pushBody =
            "{\"taskId\":\"" + SharedTaskId + "\",\"data\":{\"system\":{\"error_code\":0,\"msg\":\"ok\"}}}";
        var scanBody =
            "{\"taskId\":\"" + SharedTaskId +
            "\",\"data\":{\"system\":{\"error_code\":0,\"msg\":\"ok\"},\"code\":\"F01\",\"products\":[\"EL-1\"]}}";

        await sut.HandlePushTaskStatusAsync(pushBody);
        await sut.HandleScanTaskStatusAsync(scanBody);
        await sut.HandlePushTaskStatusAsync(pushBody);
        await sut.HandleScanTaskStatusAsync(scanBody);

        Assert.Multiple(() =>
        {
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                "push:{id}:0 与 scan:{id}:0 不得互相视为 Duplicate");
            Assert.That(store.UpdateTaskIds, Is.EqualTo(new[] { SharedTaskId, SharedTaskId }));
        });
    }

    [Test]
    public async Task TaskCallback与Warn_namespace独立互不吞()
    {
        var store = new FakeTaskStore();
        var alarms = new FakeAlarms();
        var sut = CreateProcessor(store, alarms);

        await sut.HandlePushTaskStatusAsync(PushCompletedBody);
        await sut.HandleWarnCallbackAsync(WarnBody);
        await sut.HandlePushTaskStatusAsync(PushCompletedBody);
        await sut.HandleWarnCallbackAsync(WarnBody);

        Assert.Multiple(() =>
        {
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            Assert.That(alarms.RaiseRcsWarnCalls, Is.EqualTo(1),
                "push 成功不得吞掉 warn；各自只持久化一次");
        });
    }

    [Test]
    public async Task 两个Warn_key字段不同_分别持久化()
    {
        var alarms = new FakeAlarms();
        var sut = CreateProcessor(new FakeTaskStore(), alarms);
        var warnA = """
            {"data":[{"robotCode":"AGV-01","beginTime":"2026-08-04 12:00:00","warnContent":"急停","taskCode":"T-1"}]}
            """;
        var warnB = """
            {"data":[{"robotCode":"AGV-01","beginTime":"2026-08-04 12:00:00","warnContent":"碰撞","taskCode":"T-1"}]}
            """;

        await sut.HandleWarnCallbackAsync(warnA);
        await sut.HandleWarnCallbackAsync(warnB);
        await sut.HandleWarnCallbackAsync(warnA);

        Assert.Multiple(() =>
        {
            Assert.That(alarms.RaiseRcsWarnCalls, Is.EqualTo(2),
                "warnContent 不同 → 不同 key，应各落一次；重复 A 不再落");
            Assert.That(alarms.WarnContents, Is.EqualTo(new[] { "急停", "碰撞" }));
        });
    }

    [Test]
    public async Task 订阅者异常后_finalSeen不回滚_同key不重复持久化_ACK兼容()
    {
        var store = new FakeTaskStore();
        var notifier = new RcsCallbackNotifier();
        notifier.TaskStatusReceived += (_, _) => throw new InvalidOperationException("测试：坏订阅者");
        var sut = CreateProcessor(store, new FakeAlarms(), notifier);

        var ack1 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);
        var ack2 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(PushTaskId));
            Assert.That(ack2, Does.Contain(PushTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1),
                "持久化成功后事件异常不得回滚 final seen，第二次不得再持久化");
        });
    }

    [Test]
    public async Task 坏订阅者不得阻断后续正常订阅者()
    {
        var store = new FakeTaskStore();
        var notifier = new RcsCallbackNotifier();
        var goodCalls = 0;
        notifier.TaskStatusReceived += (_, _) => throw new InvalidOperationException("测试：坏订阅者先抛");
        notifier.TaskStatusReceived += (_, _) => Interlocked.Increment(ref goodCalls);
        var sut = CreateProcessor(store, new FakeAlarms(), notifier);

        var ack = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack, Does.Contain(PushTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            Assert.That(goodCalls, Is.EqualTo(1),
                "逐订阅者隔离后，坏订阅者不得阻断后续正常订阅者");
        });
    }

    [Test]
    public async Task Push_坏订阅者后正常订阅者仍调用_重复不持久化不发事件()
    {
        var store = new FakeTaskStore();
        var notifier = new RcsCallbackNotifier();
        var badCalls = 0;
        var goodCalls = 0;
        notifier.TaskStatusReceived += (_, _) =>
        {
            Interlocked.Increment(ref badCalls);
            throw new InvalidOperationException("测试：push 坏订阅者");
        };
        notifier.TaskStatusReceived += (_, _) => Interlocked.Increment(ref goodCalls);
        var sut = CreateProcessor(store, new FakeAlarms(), notifier);

        var ack1 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);
        var ack2 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(PushTaskId));
            Assert.That(ack2, Does.Contain(PushTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            Assert.That(badCalls, Is.EqualTo(1));
            Assert.That(goodCalls, Is.EqualTo(1), "push 正常订阅者须被调用一次");
        });
    }

    [Test]
    public async Task Scan_坏订阅者后正常订阅者仍调用_重复不持久化不发事件()
    {
        var store = new FakeTaskStore();
        var notifier = new RcsCallbackNotifier();
        var badCalls = 0;
        var goodCalls = 0;
        notifier.ScanResultReceived += (_, _) =>
        {
            Interlocked.Increment(ref badCalls);
            throw new InvalidOperationException("测试：scan 坏订阅者");
        };
        notifier.ScanResultReceived += (_, _) => Interlocked.Increment(ref goodCalls);
        var sut = CreateProcessor(store, new FakeAlarms(), notifier);

        var ack1 = await sut.HandleScanTaskStatusAsync(ScanCompletedBody);
        var ack2 = await sut.HandleScanTaskStatusAsync(ScanCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(ScanTaskId));
            Assert.That(ack2, Does.Contain(ScanTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            Assert.That(badCalls, Is.EqualTo(1));
            Assert.That(goodCalls, Is.EqualTo(1), "scan 正常订阅者须被调用一次");
        });
    }

    [Test]
    public async Task Warn_坏订阅者后正常订阅者仍调用_重复不落告警不发事件()
    {
        var alarms = new FakeAlarms();
        var notifier = new RcsCallbackNotifier();
        var badCalls = 0;
        var goodCalls = 0;
        notifier.WarnReceived += (_, _) =>
        {
            Interlocked.Increment(ref badCalls);
            throw new InvalidOperationException("测试：warn 坏订阅者");
        };
        notifier.WarnReceived += (_, _) => Interlocked.Increment(ref goodCalls);
        var sut = CreateProcessor(new FakeTaskStore(), alarms, notifier);

        var ack1 = await sut.HandleWarnCallbackAsync(WarnBody);
        var ack2 = await sut.HandleWarnCallbackAsync(WarnBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain("\"taskId\":\"\""));
            Assert.That(ack2, Does.Contain("\"taskId\":\"\""));
            Assert.That(alarms.RaiseRcsWarnCalls, Is.EqualTo(1));
            Assert.That(badCalls, Is.EqualTo(1));
            Assert.That(goodCalls, Is.EqualTo(1), "warn 正常订阅者须被调用一次");
        });
    }

    [Test]
    public async Task Duplicate与follower不重复触发事件()
    {
        var store = new FakeTaskStore();
        var notifier = new RcsCallbackNotifier();
        var eventCount = 0;
        notifier.TaskStatusReceived += (_, _) => Interlocked.Increment(ref eventCount);
        var sut = CreateProcessor(store, new FakeAlarms(), notifier);

        var gate = store.EnqueueBlockingUpdate();
        var leaderTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
        try
        {
            await WaitEnteredAsync(gate);
            var followerTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
            gate.ReleaseSuccess();
            await leaderTask;
            await followerTask;
            await sut.HandlePushTaskStatusAsync(PushCompletedBody);

            Assert.Multiple(() =>
            {
                Assert.That(eventCount, Is.EqualTo(1),
                    "仅 Persisted leader 发事件；follower/Duplicate 不得再触发");
                Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            gate.ReleaseSuccess();
        }
    }

    [Test]
    public async Task Push_UpdateStateAsync返回false后_同key须再次持久化且事件只在成功时触发()
    {
        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => Task.FromResult(false));
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var notifier = new RcsCallbackNotifier();
        var eventCount = 0;
        notifier.TaskStatusReceived += (_, _) => Interlocked.Increment(ref eventCount);
        var sut = CreateProcessor(store, new FakeAlarms(), notifier);

        var ack1 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);
        var ack2 = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(PushTaskId));
            Assert.That(ack2, Does.Contain(PushTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                "UpdateStateAsync=false（任务行不存在）不得提交 final seen；同 key 须再次持久化");
            Assert.That(eventCount, Is.EqualTo(1), "成功事件只在第二次 Persisted 时触发一次");
        });
    }

    [Test]
    public async Task Scan_UpdateStateAsync返回false后_同key须再次持久化且事件只在成功时触发()
    {
        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => Task.FromResult(false));
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var notifier = new RcsCallbackNotifier();
        var eventCount = 0;
        notifier.ScanResultReceived += (_, _) => Interlocked.Increment(ref eventCount);
        var sut = CreateProcessor(store, new FakeAlarms(), notifier);

        var ack1 = await sut.HandleScanTaskStatusAsync(ScanCompletedBody);
        var ack2 = await sut.HandleScanTaskStatusAsync(ScanCompletedBody);

        Assert.Multiple(() =>
        {
            Assert.That(ack1, Does.Contain(ScanTaskId));
            Assert.That(ack2, Does.Contain(ScanTaskId));
            Assert.That(store.UpdateStateCalls, Is.EqualTo(2));
            Assert.That(eventCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Push_UpdateStateAsync返回false_follower不串行重试_第三次可接管()
    {
        var store = new FakeTaskStore();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.EnqueueUpdate(async _ =>
        {
            entered.TrySetResult();
            await hold.Task;
            return false;
        });
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var sut = CreateProcessor(store, new FakeAlarms());

        var leaderTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
        try
        {
            var winner = await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (winner != entered.Task)
                Assert.Fail("leader 未进入 UpdateStateAsync");
            await entered.Task;

            var followerTask = sut.HandlePushTaskStatusAsync(PushCompletedBody);
            Assert.That(followerTask.IsCompleted, Is.False);
            Assert.That(store.UpdateStateCalls, Is.EqualTo(1));

            hold.TrySetResult();
            var ackLeader = await leaderTask;
            var ackFollower = await followerTask;
            var callsAfterFailWave = store.UpdateStateCalls;

            var ackThird = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

            Assert.Multiple(() =>
            {
                Assert.That(ackLeader, Does.Contain(PushTaskId));
                Assert.That(ackFollower, Does.Contain(PushTaskId));
                Assert.That(callsAfterFailWave, Is.EqualTo(1),
                    "false 波次内 follower 不得再开第二次持久化");
                Assert.That(ackThird, Does.Contain(PushTaskId));
                Assert.That(store.UpdateStateCalls, Is.EqualTo(2),
                    "false 释放 in-flight 后第三次新回调可接管");
            });
        }
        finally
        {
            hold.TrySetResult();
        }
    }

    [Test]
    public async Task Push_UpdateStateAsync返回true后_同key仍只持久化一次()
    {
        var store = new FakeTaskStore();
        store.EnqueueUpdate(_ => Task.FromResult(true));
        var sut = CreateProcessor(store, new FakeAlarms());

        await sut.HandlePushTaskStatusAsync(PushCompletedBody);
        await sut.HandlePushTaskStatusAsync(PushCompletedBody);

        Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task Follower取消_不影响leader_释放后第三次为Duplicate()
    {
        var store = new FakeTaskStore();
        var gate = store.EnqueueBlockingUpdate();
        var sut = CreateProcessor(store, new FakeAlarms());

        var leaderTask = sut.HandlePushTaskStatusAsync(PushCompletedBody, CancellationToken.None);
        try
        {
            await WaitEnteredAsync(gate);

            using var followerCts = new CancellationTokenSource();
            var followerTask = sut.HandlePushTaskStatusAsync(PushCompletedBody, followerCts.Token);
            Assert.That(followerTask.IsCompleted, Is.False, "follower 须进入等待后再取消");

            followerCts.Cancel();
            var ackFollower = await followerTask;

            Assert.Multiple(() =>
            {
                Assert.That(ackFollower, Does.Contain(PushTaskId),
                    "follower 取消后仍返回兼容 ACK");
                Assert.That(leaderTask.IsCompleted, Is.False, "leader 不得被 follower 取消连带结束");
                Assert.That(store.UpdateStateCalls, Is.EqualTo(1));
                Assert.That(store.Tokens[0].IsCancellationRequested, Is.False,
                    "leader 的持久化 CT 不得被 follower 取消");
            });

            gate.ReleaseSuccess();
            var ackLeader = await leaderTask;
            var ackThird = await sut.HandlePushTaskStatusAsync(PushCompletedBody);

            Assert.Multiple(() =>
            {
                Assert.That(ackLeader, Does.Contain(PushTaskId));
                Assert.That(ackThird, Does.Contain(PushTaskId));
                Assert.That(store.UpdateStateCalls, Is.EqualTo(1),
                    "leader 成功后 in-flight 已提交 final seen；第三次应为 Duplicate");
            });
        }
        finally
        {
            gate.ReleaseSuccess();
        }
    }
}
