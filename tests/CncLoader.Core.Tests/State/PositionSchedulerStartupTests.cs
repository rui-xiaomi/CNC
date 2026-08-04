using System.Diagnostics;
using CncLoader.Common.Configuration;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CncLoader.Core.Tests.State;

/// <summary>P0-3：真实 PositionScheduler 启动路径 fail-closed / 自动重试接线。</summary>
[TestFixture]
public sealed class PositionSchedulerReconciliationStartupTests
{
    [Test]
    public async Task StartAsync_对账失败时_IsReconciled为false且双循环不启动()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 100;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 查询未完结任务失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);
        BlockRetryDelay(scheduler);
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.False);
                Assert.That(scheduler.StateLoopStartCount, Is.Zero);
                Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
                Assert.That(scheduler.ReconciledRaiseCount, Is.Zero);
                Assert.That(reconciled, Is.Zero);
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_对账成功时_开闸并双循环各启动一次且事件一次()
    {
        var fakes = SchedulerFakes.Create();
        var scheduler = fakes.CreateScheduler();
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.True);
                Assert.That(scheduler.StateLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.DispatchLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.ReconciledRaiseCount, Is.EqualTo(1));
                Assert.That(reconciled, Is.EqualTo(1));
                Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.Succeeded));
                Assert.That(scheduler.ReconciliationFailureReason, Is.Null);
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task ProbeDispatchOnce_未对账时_不得调用派工依赖()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 100;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("对账失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);
        BlockRetryDelay(scheduler);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(scheduler.IsReconciled, Is.False);
            fakes.Queue.Enqueue(new DispatchItem
            {
                EquipmentId = 1,
                PositionId = 1,
                Phase = PositionPhase.Unload,
                Priority = 8,
                FromCode = "A",
                ToCode = "B",
                WorkLineId = 1,
                LineCode = "LINE"
            });

            await scheduler.ProbeDispatchOnceAsync(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(fakes.TaskService.DispatchTransitCalls, Is.Zero);
                Assert.That(fakes.Queue.Count, Is.EqualTo(1), "未对账时不得消费派工队列");
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_第一次失败第二次成功_自动重试并仅开闸一次()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 1;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 首次查询失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        var delayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            using var reg = ct.Register(() => delayRelease.TrySetCanceled(ct));
            await delayRelease.Task.WaitAsync(ct);
        };
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(scheduler.IsReconciled, Is.False);
            Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.WaitingForRetry));
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            delayRelease.TrySetResult();
            await WaitUntilAsync(() => scheduler.IsReconciled, TimeSpan.FromSeconds(3));

            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.True);
                Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.Succeeded));
                Assert.That(scheduler.ReconciliationFailureReason, Is.Null);
                Assert.That(scheduler.StateLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.DispatchLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.ReconciledRaiseCount, Is.EqualTo(1));
                Assert.That(reconciled, Is.EqualTo(1));
                Assert.That(scheduler.ReconcileAttemptCount, Is.GreaterThanOrEqualTo(2));
                Assert.That(scheduler.ReconcileRetryLoopStartCount, Is.EqualTo(1));
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_连续多次失败_保持未开闸且不重复启循环()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 100;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 持续失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        var delayHits = 0;
        var secondDelayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DelayOverride = async (_, ct) =>
        {
            var n = Interlocked.Increment(ref delayHits);
            if (n == 1)
            {
                releaseDelay.TrySetResult();
                // 立即放行第一次等待，进入第二次对账
                return;
            }
            if (n == 2)
                secondDelayEntered.TrySetResult();
            using var reg = ct.Register(() => { });
            await Task.Delay(Timeout.Infinite, ct);
        };
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await releaseDelay.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => scheduler.ReconcileAttemptCount >= 2, TimeSpan.FromSeconds(3));
            await secondDelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.False);
                Assert.That(scheduler.StateLoopStartCount, Is.Zero);
                Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
                Assert.That(scheduler.ReconciledRaiseCount, Is.Zero);
                Assert.That(reconciled, Is.Zero);
                Assert.That(scheduler.ReconcileRetryLoopStartCount, Is.EqualTo(1), "不得为每次失败新建重试后台任务");
                Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.WaitingForRetry));
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_失败后_状态与失败原因可读且不泄露敏感信息()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 100;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException(
            "DB fail Server=x;Password=SuperSecret;Pwd=AlsoSecret;Connection String leaked");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);
        BlockRetryDelay(scheduler);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.WaitingForRetry));
                Assert.That(scheduler.ReconciliationFailureReason, Is.Not.Null.And.Not.Empty);
                Assert.That(scheduler.ReconciliationFailureReason, Does.Contain("①").Or.Contain("One").Or.Contain("阶段"));
                Assert.That(scheduler.ReconciliationFailureReason, Does.Not.Contain("SuperSecret"));
                Assert.That(scheduler.ReconciliationFailureReason, Does.Not.Contain("AlsoSecret"));
                Assert.That(scheduler.ReconciliationFailureReason, Does.Not.Contain("Password=").IgnoreCase);
                Assert.That(scheduler.ReconciliationFailureReason, Does.Not.Contain("Pwd=").IgnoreCase);
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_重试成功后_状态成功且失败原因清空()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 1;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 临时失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        var delayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            using var reg = ct.Register(() => delayRelease.TrySetCanceled(ct));
            await delayRelease.Task.WaitAsync(ct);
        };

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(scheduler.ReconciliationFailureReason, Is.Not.Null.And.Not.Empty);
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            delayRelease.TrySetResult();
            await WaitUntilAsync(() => scheduler.ReconciliationState == ReconciliationState.Succeeded, TimeSpan.FromSeconds(3));

            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.True);
                Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.Succeeded));
                Assert.That(scheduler.ReconciliationFailureReason, Is.Null);
                Assert.That(scheduler.StateLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.DispatchLoopStartCount, Is.EqualTo(1));
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_取消首次对账_不记失败且不开闸()
    {
        var fakes = SchedulerFakes.Create();
        using var cts = new CancellationTokenSource();
        fakes.TaskStore.BeforeUnfinished = () => cts.Cancel();
        var scheduler = fakes.CreateScheduler();
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(cts.Token);
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.False);
                Assert.That(scheduler.StateLoopStartCount, Is.Zero);
                Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
                Assert.That(reconciled, Is.Zero);
                Assert.That(scheduler.ReconciliationState, Is.Not.EqualTo(ReconciliationState.WaitingForRetry));
                Assert.That(scheduler.ReconciliationFailureReason, Is.Null);
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_取消等待重试_不记新失败且不开闸()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 100;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(CancellationToken.None);
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var reasonBefore = scheduler.ReconciliationFailureReason;

        await scheduler.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(scheduler.IsReconciled, Is.False);
            Assert.That(scheduler.StateLoopStartCount, Is.Zero);
            Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
            Assert.That(reconciled, Is.Zero);
            Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.WaitingForRetry),
                "取消应保持最后可信状态，不得伪装成成功");
            Assert.That(scheduler.ReconciliationFailureReason, Is.EqualTo(reasonBefore),
                "取消不得改写/追加业务失败原因");
        });
    }

    [Test]
    public async Task StartAsync_失败与重试成功_应发布状态变化通知()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 1;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 首次失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        var snapshots = new System.Collections.Concurrent.ConcurrentQueue<ReconciliationSnapshot>();
        scheduler.ReconciliationStateChanged += (_, s) => snapshots.Enqueue(s);
        var delayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            using var reg = ct.Register(() => delayRelease.TrySetCanceled(ct));
            await delayRelease.Task.WaitAsync(ct);
        };

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(snapshots.Any(s => s.State == ReconciliationState.WaitingForRetry), Is.True);
            Assert.That(snapshots.Any(s => s.State == ReconciliationState.WaitingForRetry && !string.IsNullOrEmpty(s.FailureReason)), Is.True);
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            delayRelease.TrySetResult();
            await WaitUntilAsync(() => scheduler.ReconciliationState == ReconciliationState.Succeeded, TimeSpan.FromSeconds(3));

            var snapList = snapshots.ToArray();
            var success = snapList.Last(s => s.State == ReconciliationState.Succeeded);
            Assert.Multiple(() =>
            {
                Assert.That(success.FailureReason, Is.Null);
                Assert.That(success.IsReconciled, Is.True);
                Assert.That(snapList.Count(s => s.State == ReconciliationState.Succeeded), Is.EqualTo(1));
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StopAsync先于开闸_不得开闸且双循环不启动()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 1;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 首次失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        var delayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openClaimReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openClaimFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            using var reg = ct.Register(() => delayRelease.TrySetCanceled(ct));
            await delayRelease.Task.WaitAsync(ct);
        };
        // 开闸声明前同步进入 stopping（确定性；禁止在回调内 await StopAsync）
        scheduler.BeforeOpenGateClaim = () =>
        {
            openClaimReached.TrySetResult();
            scheduler.EnterStopping();
            openClaimFinished.TrySetResult();
        };
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(CancellationToken.None);
        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        delayRelease.TrySetResult();
        await openClaimReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await openClaimFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        // 等待 TryOpenGateAfterSuccess 在回调返回后跑完（同线程续体，Yield 足够）
        await Task.Yield();
        await Task.Yield();

        Assert.Multiple(() =>
        {
            Assert.That(scheduler.IsReconciled, Is.False, "停止先取得生命周期后禁止 IsReconciled=true");
            Assert.That(scheduler.StateLoopStartCount, Is.Zero);
            Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
            Assert.That(scheduler.ReconciledRaiseCount, Is.Zero);
            Assert.That(reconciled, Is.Zero);
            Assert.That(scheduler.ReconciliationState, Is.Not.EqualTo(ReconciliationState.Succeeded));
        });

        await scheduler.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task 开闸完整完成后Stop_允许已开闸且双循环完整()
    {
        var fakes = SchedulerFakes.Create();
        var scheduler = fakes.CreateScheduler();
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(scheduler.IsReconciled, Is.True);
            Assert.That(scheduler.StateLoopStartCount, Is.EqualTo(1));
            Assert.That(scheduler.DispatchLoopStartCount, Is.EqualTo(1));
            Assert.That(reconciled, Is.EqualTo(1));
        });

        await scheduler.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            // 开闸已完整完成：允许 IsReconciled 保持 true；循环随后被取消，但不得出现「只启一半」
            Assert.That(scheduler.IsReconciled, Is.True);
            Assert.That(scheduler.StateLoopStartCount, Is.EqualTo(1));
            Assert.That(scheduler.DispatchLoopStartCount, Is.EqualTo(1));
            Assert.That(scheduler.ReconciledRaiseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task WaitingForRetry进入Reconciling_必须清空失败原因()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 1;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 旧失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        var reconcilingSnaps = new System.Collections.Concurrent.ConcurrentQueue<ReconciliationSnapshot>();
        scheduler.ReconciliationStateChanged += (_, s) =>
        {
            if (s.State == ReconciliationState.Reconciling)
                reconcilingSnaps.Enqueue(s);
        };
        var delayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReconcileEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            using var reg = ct.Register(() => delayRelease.TrySetCanceled(ct));
            await delayRelease.Task.WaitAsync(ct);
        };
        fakes.TaskStore.BeforeUnfinished = () =>
        {
            if (fakes.TaskStore.UnfinishedFailRemaining == 0)
                secondReconcileEntered.TrySetResult();
        };

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(scheduler.ReconciliationFailureReason, Is.Not.Null.And.Not.Empty);
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            delayRelease.TrySetResult();
            await secondReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(
                () => reconcilingSnaps.Count >= 2
                      || scheduler.ReconciliationState == ReconciliationState.Succeeded,
                TimeSpan.FromSeconds(3));

            var retryReconciling = reconcilingSnaps.LastOrDefault();
            Assert.Multiple(() =>
            {
                Assert.That(retryReconciling, Is.Not.Null, "重试轮应发布 Reconciling 快照");
                Assert.That(retryReconciling!.FailureReason, Is.Null,
                    "WaitingForRetry→Reconciling 快照不得携带旧失败原因");
                Assert.That(scheduler.ReconciliationFailureReason, Is.Null,
                    "属性与快照同一转换，不得残留旧失败原因");
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task TryOpenGateAfterSuccess_重复调用_只开闸一次()
    {
        var fakes = SchedulerFakes.Create();
        var scheduler = fakes.CreateScheduler();
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.That(scheduler.TryOpenGateAfterSuccess(), Is.False);
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.StateLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.DispatchLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.ReconciledRaiseCount, Is.EqualTo(1));
                Assert.That(reconciled, Is.EqualTo(1));
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    private static void BlockRetryDelay(PositionScheduler scheduler)
    {
        scheduler.DelayOverride = (_, ct) => Task.Delay(Timeout.Infinite, ct);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate()) return;
            await Task.Delay(15);
        }
        Assert.Fail($"等待条件超时（{timeout.TotalSeconds:0.#}s）");
    }

    private sealed class SchedulerFakes
    {
        public FakeTaskStore TaskStore { get; } = new();
        public FakeTaskService TaskService { get; } = new();
        public PriorityDispatchQueue Queue { get; } = new();
        public FakePoints Points { get; } = new();
        public FakeSlots Slots { get; } = new();

        public static SchedulerFakes Create() => new();

        public PositionScheduler CreateScheduler(int retryIntervalMs = 5000)
        {
            var options = Options.Create(new AppOptions
            {
                Rcs = new RcsOptions
                {
                    SchedulerEnabled = true,
                    SchedulerIntervalMs = 10_000,
                    ReconcileRetryIntervalMs = retryIntervalMs
                }
            });
            var equipment = new FakeEquipment();
            return new PositionScheduler(
                new SignalStateStore(),
                TaskService,
                TaskStore,
                Points,
                new FakePlcOps(),
                new FakeRoutes(),
                Queue,
                new FakeAlarms(),
                options,
                NullLogger<PositionScheduler>.Instance,
                new FakeWorkRecords(),
                equipment,
                Slots,
                new CncLoader.Core.Tests.Routing.WorkLineOnlyRoutingValidator(equipment));
        }
    }

    private sealed class FakeTaskStore : IRcsTaskStore
    {
        public Exception? UnfinishedException { get; set; }
        public int UnfinishedFailRemaining { get; set; }
        public Action? BeforeUnfinished { get; set; }

        public Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
        {
            BeforeUnfinished?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (UnfinishedFailRemaining > 0)
            {
                UnfinishedFailRemaining--;
                throw UnfinishedException ?? new InvalidOperationException("对账失败");
            }
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        public Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult<RcsTaskRow?>(null);

        public Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default) => Task.FromResult(1L);
        public Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null, string? error = null, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryIncrementRedoIfUnderAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default) => Task.FromResult(false);
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
    }

    private sealed class FakeTaskService : IRcsTaskService
    {
        public int DispatchTransitCalls { get; private set; }

        public Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
        {
            DispatchTransitCalls++;
            return Task.FromResult(RcsResult.Fail("", "不应调用"));
        }

        public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default)
            => Task.FromResult(new RcsResult(true, 200, true, null, "", "[]", null, 0));

        public Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default) => Fail();
        public Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default) => Fail();
        public Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> DispatchPalletReturnAsync(long equipmentId, long? positionId, string fromCode, string toCode,
            long workLineId, string lineCode, string author, CancellationToken ct = default) => Fail();
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentTasksAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentMessagesAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryMessagesAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;

        private static Task<RcsResult> Fail() => Task.FromResult(RcsResult.Fail("", "stub"));
    }

    private sealed class FakePoints : IPlcPointSource
    {
        public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcPointDefinition>>(Array.Empty<PlcPointDefinition>());
        public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcPointDefinition>>(Array.Empty<PlcPointDefinition>());
        public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcPointDefinition>>(Array.Empty<PlcPointDefinition>());
    }

    private sealed class FakeSlots : ISlotAccountService
    {
        public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
            IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
        public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default) => Task.FromResult<ReservedSlot?>(null);
        public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default) => Task.FromResult<ReservedSlot?>(null);
        public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default) => Task.FromResult(new FrameOccupancy(0, 0, 0, 0));
        public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
            => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.Updated, frameId, slotNo, null));
        public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default) => Task.FromResult<SlotLocation?>(null);
        public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
            => Task.FromResult(InventoryCorrectionResult.Empty());
        public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
    }

    private sealed class FakePlcOps : IPlcOperationService
    {
        public Task<IReadOnlyList<PlcReadResult>> ReadPointsAsync(long plcId, bool readOnlySignals = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcReadResult>>(Array.Empty<PlcReadResult>());
        public Task<PlcReadResult> ReadRegisterAsync(long plcId, string registerAddress, int length, CancellationToken ct = default)
            => Task.FromResult(new PlcReadResult(null, "", null, registerAddress, 0, "", false, null, null));
        public Task<PlcWriteResult> WriteWithConfirmAsync(long plcId, string registerAddress, int value, string author, CancellationToken ct = default)
            => Task.FromResult(new PlcWriteResult(registerAddress, value, null, false, 0, "stub"));
        public Task<PlcWriteResult> VerifyWriteAsync(long plcId, string registerAddress, int expectedValue, CancellationToken ct = default)
            => Task.FromResult(new PlcWriteResult(registerAddress, expectedValue, null, false, 0, "stub"));
    }

    private sealed class FakeRoutes : IRouteResolver
    {
        public Task<(string from, string to)?> ResolveUploadAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<(string, string)?>(null);
        public Task<(string from, string to)?> ResolveUnloadAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<(string, string)?>(null);
        public Task<string?> ResolvePositionCellAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> ResolveFrameCellAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeAlarms : IAlarmEventService
    {
#pragma warning disable CS0067
        public event EventHandler<AlarmRow>? AlarmRaised;
        public event EventHandler? AlarmsChanged;
#pragma warning restore CS0067
        public Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent, string? taskCode, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task MarkHandledAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> DeleteAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> GetUnhandledCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeWorkRecords : IWorkRecordService
    {
        public Task<long> RecordStartAsync(WorkRecordStartArgs args, CancellationToken ct = default) => Task.FromResult(0L);
        public Task RecordResultAsync(long recordId, string result, string? remark, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkRecordRow?> FindOpenByPositionAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<WorkRecordRow?>(null);
        public Task<IReadOnlyList<WorkRecordRow>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkRecordRow>>(Array.Empty<WorkRecordRow>());
        public Task<WorkShiftStats> GetShiftStatsAsync(CancellationToken ct = default) => Task.FromResult(new WorkShiftStats(0, 0, 0));
    }

    private sealed class FakeEquipment : IEquipmentConfigService
    {
        public Task<WorkLineRef?> GetWorkLineByEquipmentAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<WorkLineRef?>(new WorkLineRef(1, "LINE"));
        public Task<EquipmentFrameBindingIds> GetFrameBindingIdsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult(new EquipmentFrameBindingIds(null, null));
        public Task<long?> GetFrameBindingByRoleAsync(long equipmentId, FrameRole role, CancellationToken ct = default)
            => Task.FromResult<long?>(null);
        public Task<IReadOnlyList<long>> GetNextProcessEquipmentsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        public Task<bool> HasSubsequentProcessAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<IReadOnlyList<NamedOption>> GetCraftworkOptionsAsync(CancellationToken ct = default) => EmptyNamed();
        public Task<IReadOnlyList<EquipmentListItem>> GetByCraftAsync(long? craftworkId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentListItem>>(Array.Empty<EquipmentListItem>());
        public Task<IReadOnlyList<PositionItem>> GetPositionsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PositionItem>>(Array.Empty<PositionItem>());
        public Task<IReadOnlyList<EquipmentFrameBinding>> GetFrameBindingsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentFrameBinding>>(Array.Empty<EquipmentFrameBinding>());
        public Task<IReadOnlyList<NamedOption>> GetPlcOptionsAsync(CancellationToken ct = default) => EmptyNamed();
        public Task<IReadOnlyList<NamedOption>> GetFrameOptionsAsync(CancellationToken ct = default) => EmptyNamed();
        public Task<string> SuggestNextNoAsync(CancellationToken ct = default) => Task.FromResult("EQ01");
        public Task<long> CreateEquipmentAsync(EquipmentCreateModel model, string author, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<EquipmentEditModel?> GetByIdAsync(long equipmentId, CancellationToken ct = default) => Task.FromResult<EquipmentEditModel?>(null);
        public Task UpdateAsync(EquipmentEditModel model, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameBindingInfo>>(Array.Empty<FrameBindingInfo>());
        public Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default) => Task.CompletedTask;

        private static Task<IReadOnlyList<NamedOption>> EmptyNamed()
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
    }
}
