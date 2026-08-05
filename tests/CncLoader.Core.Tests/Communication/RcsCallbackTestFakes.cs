using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Communication.Rcs;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Communication;

/// <summary>P0-4 回调去重测试共用手写 fake / 闸门（实例隔离，无 DB/RCS/PLC）。</summary>
internal static class RcsCallbackTestFakes
{
    internal static RcsCallbackProcessor CreateProcessor(
        FakeTaskStore store,
        FakeAlarms alarms,
        RcsCallbackNotifier? notifier = null)
        => new(
            new FakeMessageLog(),
            store,
            alarms,
            notifier ?? new RcsCallbackNotifier(),
            NullLogger<RcsCallbackProcessor>.Instance);

    internal static async Task WaitEnteredAsync(PersistGate gate)
    {
        var winner = await Task.WhenAny(gate.Entered, Task.Delay(TimeSpan.FromSeconds(5)));
        if (winner != gate.Entered)
            Assert.Fail("leader 未在时限内进入持久化闸门（Entered）；检查 fake/TCS 编排");
        await gate.Entered;
    }

    /// <summary>TCS 可控持久化闸门：Entered → 阻塞 → ReleaseSuccess / ReleaseException。</summary>
    internal sealed class PersistGate
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task HoldAsync()
        {
            _entered.TrySetResult();
            await _release.Task;
        }

        public void ReleaseSuccess() => _release.TrySetResult();

        public void ReleaseException(Exception ex) => _release.TrySetException(ex);
    }

    internal sealed class FakeTaskStore : IRcsTaskStore
    {
        private readonly Queue<Func<UpdateCall, Task<bool>>> _scripts = new();
        private readonly List<string> _taskIds = new();
        private readonly List<string> _states = new();
        private readonly List<CancellationToken> _tokens = new();

        public int UpdateStateCalls { get; private set; }
        public IReadOnlyList<string> UpdateTaskIds => _taskIds;
        public IReadOnlyList<string> UpdateStates => _states;
        public IReadOnlyList<CancellationToken> Tokens => _tokens;

        public void EnqueueUpdate(Func<UpdateCall, Task<bool>> script) => _scripts.Enqueue(script);

        public PersistGate EnqueueBlockingUpdate()
        {
            var gate = new PersistGate();
            _scripts.Enqueue(async _ =>
            {
                await gate.HoldAsync();
                return true;
            });
            return gate;
        }

        public Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null,
            string? error = null, CancellationToken ct = default)
        {
            UpdateStateCalls++;
            _taskIds.Add(rcsTaskId);
            _states.Add(taskState);
            _tokens.Add(ct);
            if (_scripts.Count == 0)
                return Task.FromResult(true);
            return _scripts.Dequeue()(new UpdateCall(rcsTaskId, taskState, ct));
        }

        public Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default) => Task.FromResult(1L);
        public Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AutoRedoClaimResult> TryClaimAutoRedoAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
            => Task.FromResult(AutoRedoClaimResult.NotClaimable);
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult<RcsTaskRow?>(null);
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
        public Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    internal sealed record UpdateCall(string TaskId, string TaskState, CancellationToken Ct);

    internal sealed class FakeAlarms : IAlarmEventService
    {
        private readonly Queue<Func<WarnCall, Task<long>>> _scripts = new();
        private readonly List<string> _robots = new();
        private readonly List<string> _contents = new();

        public int RaiseRcsWarnCalls { get; private set; }
        public IReadOnlyList<string> RobotCodes => _robots;
        public IReadOnlyList<string> WarnContents => _contents;

#pragma warning disable CS0067
        public event EventHandler<AlarmRow>? AlarmRaised;
        public event EventHandler? AlarmsChanged;
#pragma warning restore CS0067

        public void EnqueueRaise(Func<WarnCall, Task<long>> script) => _scripts.Enqueue(script);

        public PersistGate EnqueueBlockingRaise()
        {
            var gate = new PersistGate();
            _scripts.Enqueue(async _ =>
            {
                await gate.HoldAsync();
                return 1L;
            });
            return gate;
        }

        public Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent,
            string? taskCode, CancellationToken ct = default)
        {
            RaiseRcsWarnCalls++;
            _robots.Add(robotCode);
            _contents.Add(warnContent);
            if (_scripts.Count == 0)
                return Task.FromResult(1L);
            return _scripts.Dequeue()(new WarnCall(robotCode, beginTime, warnContent, taskCode, ct));
        }

        public Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task MarkHandledAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> DeleteAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> GetUnhandledCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    internal sealed record WarnCall(
        string RobotCode, string BeginTime, string WarnContent, string? TaskCode, CancellationToken Ct);

    internal sealed class FakeMessageLog : IRcsMessageLog
    {
        public Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
    }
}
