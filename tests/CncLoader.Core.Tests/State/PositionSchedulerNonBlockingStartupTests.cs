using System.Diagnostics;
using CncLoader.Common.Configuration;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P1-1：首次对账不得阻塞 Host / PositionScheduler.StartAsync（单一后台 workflow）。
/// </summary>
[TestFixture]
public sealed class PositionSchedulerNonBlockingStartupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    [Test]
    public async Task StartAsync_首次对账挂起时_必须在Reconcile完成前返回()
    {
        var fakes = SchedulerFakes.Create();
        var hang = NewTcs();
        var entered = NewTcs();
        fakes.TaskStore.HangUnfinished = hang;
        fakes.TaskStore.OnUnfinishedEntered = () => entered.TrySetResult();
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);

        var startTask = scheduler.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Bound);
            await startTask.WaitAsync(Bound);

            Assert.Multiple(() =>
            {
                Assert.That(hang.Task.IsCompleted, Is.False, "StartAsync 返回时首次对账仍应挂起");
                Assert.That(scheduler.IsReconciled, Is.False);
                Assert.That(scheduler.StateLoopStartCount, Is.Zero);
                Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
                Assert.That(scheduler.ReconciledRaiseCount, Is.Zero);
            });
        }
        finally
        {
            await ReleaseHangAndStopAsync(scheduler, startTask, hang);
        }
    }

    [Test]
    public async Task StartAsync_返回时_门闩关闭且双循环与事件均为零()
    {
        var fakes = SchedulerFakes.Create();
        var hang = NewTcs();
        var entered = NewTcs();
        fakes.TaskStore.HangUnfinished = hang;
        fakes.TaskStore.OnUnfinishedEntered = () => entered.TrySetResult();
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        var startTask = scheduler.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Bound);
            await startTask.WaitAsync(Bound);

            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.False);
                Assert.That(scheduler.StateLoopStartCount, Is.Zero);
                Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
                Assert.That(scheduler.ReconciledRaiseCount, Is.Zero);
                Assert.That(reconciled, Is.Zero);
                Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.Reconciling));
            });
        }
        finally
        {
            await ReleaseHangAndStopAsync(scheduler, startTask, hang);
        }
    }

    [Test]
    public async Task StartAsync_返回后_后续HostedService可继续启动()
    {
        var fakes = SchedulerFakes.Create();
        var hang = NewTcs();
        var entered = NewTcs();
        fakes.TaskStore.HangUnfinished = hang;
        fakes.TaskStore.OnUnfinishedEntered = () => entered.TrySetResult();
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);
        var probe = new ProbeHostedService();

        var startTask = scheduler.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Bound);
            // 契约：Host 串行启动中，调度器 StartAsync 返回后才能启动后续服务
            await startTask.WaitAsync(Bound);
            await probe.StartAsync(CancellationToken.None).WaitAsync(Bound);

            Assert.Multiple(() =>
            {
                Assert.That(probe.StartCount, Is.EqualTo(1));
                Assert.That(hang.Task.IsCompleted, Is.False, "后续服务启动时对账仍可挂起");
                Assert.That(scheduler.IsReconciled, Is.False);
            });
        }
        finally
        {
            await probe.StopAsync(CancellationToken.None);
            await ReleaseHangAndStopAsync(scheduler, startTask, hang);
        }
    }

    [Test]
    public async Task 挂起attempt后成功_只开闸一次且双循环与事件各一次()
    {
        var fakes = SchedulerFakes.Create();
        var hang = NewTcs();
        var entered = NewTcs();
        fakes.TaskStore.HangUnfinished = hang;
        fakes.TaskStore.OnUnfinishedEntered = () => entered.TrySetResult();
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        var startTask = scheduler.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Bound);
            await startTask.WaitAsync(Bound);

            Assert.That(scheduler.IsReconciled, Is.False);
            hang.TrySetResult();

            await WaitUntilAsync(() => scheduler.IsReconciled, Bound);

            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.True);
                Assert.That(scheduler.StateLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.DispatchLoopStartCount, Is.EqualTo(1));
                Assert.That(scheduler.ReconciledRaiseCount, Is.EqualTo(1));
                Assert.That(reconciled, Is.EqualTo(1));
                Assert.That(scheduler.ReconciliationState, Is.EqualTo(ReconciliationState.Succeeded));
            });
        }
        finally
        {
            await ReleaseHangAndStopAsync(scheduler, startTask, hang);
        }
    }

    [Test]
    public async Task 挂起期间Stop再释放成功_不开闸且循环与事件为零()
    {
        var fakes = SchedulerFakes.Create();
        var hang = NewTcs();
        var entered = NewTcs();
        // 模拟下游忽略 CT：Stop 取消后 attempt 仍可“成功返回”
        fakes.TaskStore.HangUnfinished = hang;
        fakes.TaskStore.HangIgnoresCancellation = true;
        fakes.TaskStore.OnUnfinishedEntered = () => entered.TrySetResult();
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        var startTask = scheduler.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(Bound);
            await startTask.WaitAsync(Bound);

            await scheduler.StopAsync(CancellationToken.None);
            hang.TrySetResult();

            await WaitUntilAsync(() => fakes.TaskStore.ConcurrentAttempts == 0, Bound);

            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.False);
                Assert.That(scheduler.StateLoopStartCount, Is.Zero);
                Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
                Assert.That(scheduler.ReconciledRaiseCount, Is.Zero);
                Assert.That(reconciled, Is.Zero);
                Assert.That(scheduler.ReconciliationState, Is.Not.EqualTo(ReconciliationState.Succeeded));
            });
        }
        finally
        {
            await ReleaseHangAndStopAsync(scheduler, startTask, hang);
        }
    }

    [Test]
    public async Task 首次失败后_StartAsync早已返回且后台串行重试()
    {
        var fakes = SchedulerFakes.Create();
        var firstHang = NewTcs();
        var firstEntered = NewTcs();
        var secondEntered = NewTcs();
        var delayEntered = NewTcs();
        var delayRelease = NewTcs();
        var attempt = 0;
        fakes.TaskStore.OnUnfinishedEntered = () =>
        {
            var n = Interlocked.Increment(ref attempt);
            if (n == 1) firstEntered.TrySetResult();
            if (n == 2) secondEntered.TrySetResult();
        };
        fakes.TaskStore.HangUnfinishedFactory = () =>
        {
            // 第一次挂起后失败；第二次进入即可观测（持续失败，验证串行重试不开闸）
            if (Volatile.Read(ref attempt) <= 1) return firstHang;
            return null;
        };
        fakes.TaskStore.UnfinishedFailRemaining = 100;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 首次失败");
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            using var reg = ct.Register(() => delayRelease.TrySetCanceled(ct));
            await delayRelease.Task.WaitAsync(ct);
        };

        var startTask = scheduler.StartAsync(CancellationToken.None);
        try
        {
            await firstEntered.Task.WaitAsync(Bound);
            await startTask.WaitAsync(Bound);
            Assert.That(scheduler.IsReconciled, Is.False);
            Assert.That(fakes.TaskStore.MaxConcurrentAttempts, Is.LessThanOrEqualTo(1));

            firstHang.TrySetResult();
            await delayEntered.Task.WaitAsync(Bound);
            delayRelease.TrySetResult();
            await secondEntered.Task.WaitAsync(Bound);

            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.False);
                Assert.That(fakes.TaskStore.MaxConcurrentAttempts, Is.LessThanOrEqualTo(1));
                Assert.That(attempt, Is.GreaterThanOrEqualTo(2));
                Assert.That(scheduler.StateLoopStartCount, Is.Zero);
                Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
            });
        }
        finally
        {
            scheduler.EnterStopping();
            firstHang.TrySetResult();
            delayRelease.TrySetResult();
            await SafeStopAsync(scheduler, startTask);
        }
    }

    [Test]
    public async Task 第一次失败第二次挂起_不出现第三个并发attempt()
    {
        var fakes = SchedulerFakes.Create();
        var secondHang = NewTcs();
        var secondEntered = NewTcs();
        var delayRelease = NewTcs();
        var delayEntered = NewTcs();
        var attempt = 0;
        fakes.TaskStore.UnfinishedFailRemaining = 1;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 首次失败");
        fakes.TaskStore.OnUnfinishedEntered = () =>
        {
            var n = Interlocked.Increment(ref attempt);
            if (n == 2) secondEntered.TrySetResult();
        };
        fakes.TaskStore.HangUnfinishedFactory = () => Volatile.Read(ref attempt) >= 2 ? secondHang : null;
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            using var reg = ct.Register(() => delayRelease.TrySetCanceled(ct));
            await delayRelease.Task.WaitAsync(ct);
        };

        var startTask = scheduler.StartAsync(CancellationToken.None);
        try
        {
            await startTask.WaitAsync(Bound);
            await delayEntered.Task.WaitAsync(Bound);
            delayRelease.TrySetResult();
            await secondEntered.Task.WaitAsync(Bound);

            // 第二次仍挂起期间，不得再起第三个 attempt
            await WaitUntilAsync(() => fakes.TaskStore.ConcurrentAttempts == 1, Bound);
            Assert.Multiple(() =>
            {
                Assert.That(attempt, Is.EqualTo(2));
                Assert.That(fakes.TaskStore.MaxConcurrentAttempts, Is.LessThanOrEqualTo(1));
                Assert.That(fakes.TaskStore.ConcurrentAttempts, Is.EqualTo(1));
            });
        }
        finally
        {
            scheduler.EnterStopping();
            secondHang.TrySetResult();
            delayRelease.TrySetResult();
            await SafeStopAsync(scheduler, startTask);
        }
    }

    [Test]
    public async Task 多次StartAsync_后台对账workflow至多一个()
    {
        var fakes = SchedulerFakes.Create();
        var hang = NewTcs();
        var entered = NewTcs();
        fakes.TaskStore.HangUnfinished = hang;
        fakes.TaskStore.HangIgnoresCancellation = true;
        fakes.TaskStore.OnUnfinishedEntered = () => entered.TrySetResult();
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);

        var start1 = scheduler.StartAsync(CancellationToken.None);
        Task? start2 = null;
        try
        {
            await entered.Task.WaitAsync(Bound);
            start2 = scheduler.StartAsync(CancellationToken.None);

            await start1.WaitAsync(Bound);
            await start2.WaitAsync(Bound);

            Assert.Multiple(() =>
            {
                Assert.That(fakes.TaskStore.MaxConcurrentAttempts, Is.LessThanOrEqualTo(1));
                Assert.That(fakes.TaskStore.TotalAttemptEntries, Is.EqualTo(1),
                    "非法/重复 StartAsync 也不得并行打出多个 reconciliation attempt");
                Assert.That(scheduler.ReconcileRetryLoopStartCount, Is.LessThanOrEqualTo(1));
                Assert.That(scheduler.IsReconciled, Is.False);
            });
        }
        finally
        {
            await ReleaseHangAndStopAsync(scheduler, start1, hang);
            if (start2 is not null)
                await ObserveAsync(start2);
        }
    }

    [Test]
    public async Task StartAsync_入口token已取消_不留对账任务且不开闸()
    {
        var fakes = SchedulerFakes.Create();
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 60_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reconciled = 0;
        scheduler.Reconciled += (_, _) => reconciled++;

        Assert.ThrowsAsync<OperationCanceledException>(
            async () => await scheduler.StartAsync(cts.Token).WaitAsync(Bound));
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(scheduler.IsReconciled, Is.False);
                Assert.That(scheduler.StateLoopStartCount, Is.Zero);
                Assert.That(scheduler.DispatchLoopStartCount, Is.Zero);
                Assert.That(reconciled, Is.Zero);
                Assert.That(scheduler.ReconcileRetryLoopStartCount, Is.Zero);
                Assert.That(fakes.TaskStore.TotalAttemptEntries, Is.Zero,
                    "入口已取消时不得启动 reconciliation attempt / 遗留后台任务");
                Assert.That(fakes.TaskStore.ConcurrentAttempts, Is.Zero);
            });
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task WaitingForRetry进入Reconciling_必须清空失败原因_且StartAsync不阻塞()
    {
        var fakes = SchedulerFakes.Create();
        fakes.TaskStore.UnfinishedFailRemaining = 1;
        fakes.TaskStore.UnfinishedException = new InvalidOperationException("① 旧失败");
        var reconcilingSnaps = new System.Collections.Concurrent.ConcurrentQueue<ReconciliationSnapshot>();
        var secondEntered = NewTcs();
        var delayEntered = NewTcs();
        var delayRelease = NewTcs();
        var attempt = 0;
        fakes.TaskStore.OnUnfinishedEntered = () =>
        {
            if (Interlocked.Increment(ref attempt) == 2)
                secondEntered.TrySetResult();
        };
        var scheduler = fakes.CreateScheduler(retryIntervalMs: 50);
        scheduler.ReconciliationStateChanged += (_, s) =>
        {
            if (s.State == ReconciliationState.Reconciling)
                reconcilingSnaps.Enqueue(s);
        };
        scheduler.DelayOverride = async (_, ct) =>
        {
            delayEntered.TrySetResult();
            using var reg = ct.Register(() => delayRelease.TrySetCanceled(ct));
            await delayRelease.Task.WaitAsync(ct);
        };

        var startTask = scheduler.StartAsync(CancellationToken.None);
        try
        {
            await startTask.WaitAsync(Bound);
            await WaitUntilAsync(
                () => !string.IsNullOrEmpty(scheduler.ReconciliationFailureReason),
                Bound);

            await delayEntered.Task.WaitAsync(Bound);
            delayRelease.TrySetResult();
            await secondEntered.Task.WaitAsync(Bound);

            await WaitUntilAsync(
                () => reconcilingSnaps.Count >= 2
                      || scheduler.ReconciliationState == ReconciliationState.Succeeded,
                Bound);

            var retryReconciling = reconcilingSnaps.LastOrDefault();
            Assert.Multiple(() =>
            {
                Assert.That(retryReconciling, Is.Not.Null);
                Assert.That(retryReconciling!.FailureReason, Is.Null);
                Assert.That(scheduler.ReconciliationFailureReason, Is.Null);
            });
        }
        finally
        {
            scheduler.EnterStopping();
            delayRelease.TrySetResult();
            await SafeStopAsync(scheduler, startTask);
        }
    }

    private static TaskCompletionSource NewTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>先进入 stopping 再释放挂起，避免 RED 收尾误开闸。</summary>
    private static async Task ReleaseHangAndStopAsync(
        PositionScheduler scheduler, Task startTask, params TaskCompletionSource[] hangs)
    {
        scheduler.EnterStopping();
        foreach (var hang in hangs)
            hang.TrySetResult();
        await SafeStopAsync(scheduler, startTask);
    }

    private static async Task SafeStopAsync(PositionScheduler scheduler, Task startTask)
    {
        try { await startTask.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch { /* RED/取消路径可能仍挂起；Stop 负责收尾 */ }
        await scheduler.StopAsync(CancellationToken.None);
        await ObserveAsync(startTask);
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (Exception) { /* 测试收尾：避免未观察异常 */ }
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

    private sealed class ProbeHostedService : IHostedService
    {
        public int StartCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SchedulerFakes
    {
        public FakeTaskStore TaskStore { get; } = new();
        public FakeTaskService TaskService { get; } = new();
        public PriorityDispatchQueue Queue { get; } = new();

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
                new FakePoints(),
                new FakePlcOps(),
                new FakeRoutes(),
                Queue,
                new FakeAlarms(),
                options,
                NullLogger<PositionScheduler>.Instance,
                new FakeWorkRecords(),
                equipment,
                new FakeSlots(),
                new CncLoader.Core.Tests.Routing.WorkLineOnlyRoutingValidator(equipment));
        }
    }

    private sealed class FakeTaskStore : IRcsTaskStore
    {
        private int _concurrent;
        private int _maxConcurrent;
        private int _totalEntries;

        public Exception? UnfinishedException { get; set; }
        public int UnfinishedFailRemaining { get; set; }
        public Action? OnUnfinishedEntered { get; set; }
        public TaskCompletionSource? HangUnfinished { get; set; }
        public Func<TaskCompletionSource?>? HangUnfinishedFactory { get; set; }
        public bool HangIgnoresCancellation { get; set; }

        public int ConcurrentAttempts => Volatile.Read(ref _concurrent);
        public int MaxConcurrentAttempts => Volatile.Read(ref _maxConcurrent);
        public int TotalAttemptEntries => Volatile.Read(ref _totalEntries);

        public async Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
        {
            OnUnfinishedEntered?.Invoke();
            var n = Interlocked.Increment(ref _concurrent);
            Interlocked.Increment(ref _totalEntries);
            UpdateMax(n);
            try
            {
                var hang = HangUnfinishedFactory?.Invoke() ?? HangUnfinished;
                if (hang is not null)
                {
                    if (HangIgnoresCancellation)
                        await hang.Task;
                    else
                    {
                        using var reg = ct.Register(() => hang.TrySetCanceled(ct));
                        await hang.Task.WaitAsync(ct);
                    }
                }

                if (!HangIgnoresCancellation)
                    ct.ThrowIfCancellationRequested();

                if (UnfinishedFailRemaining > 0)
                {
                    UnfinishedFailRemaining--;
                    throw UnfinishedException ?? new InvalidOperationException("对账失败");
                }

                return Array.Empty<string>();
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        private void UpdateMax(int n)
        {
            while (true)
            {
                var cur = Volatile.Read(ref _maxConcurrent);
                if (n <= cur) return;
                if (Interlocked.CompareExchange(ref _maxConcurrent, n, cur) == cur) return;
            }
        }

        public Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult<RcsTaskRow?>(null);
        public Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default) => Task.FromResult(1L);
        public Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null, string? error = null, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AutoRedoClaimResult> TryClaimAutoRedoAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
            => Task.FromResult(AutoRedoClaimResult.NotClaimable);
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
    }

    private sealed class FakeTaskService : IRcsTaskService
    {
        public Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "不应调用"));
        public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default)
            => Task.FromResult(new RcsResult(true, 200, true, null, "", "[]", null, 0));
        public Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default) => Fail();
        public Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default) => Fail();
        public Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default) => Fail();
        public Task<RcsResult> AutoRedispatchAsync(string rcsTaskId, int maxRedoCount, CancellationToken ct = default) => Fail();
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
        public void Invalidate() { }
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
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
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
