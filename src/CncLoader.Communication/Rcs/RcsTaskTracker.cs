using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 任务跟踪器（第四阶段④，<see cref="IHostedService"/>）。
/// 三职责：
/// 1) 兜底轮询——按 <see cref="RcsOptions.PollIntervalMs"/> 取未完结本地 taskId 批量 queryTask（key=Id IN），
///    按 §4.5 11→5 映射推进态；RCS 查无此任务 → 告警人工。
/// 2) 自动 redo——订阅 <see cref="IRcsCallbackNotifier.TaskStatusReceived"/>，FAILED 态调用
///    <see cref="IRcsTaskService.AutoRedispatchAsync"/>（门禁后原子 Claim 再发送），
///    超过 <see cref="RcsOptions.MaxAutoRedo"/> → 告警人工。
/// 3) 取消工单——CANCELED 态 → <see cref="IAlarmEventService.RaiseRcsTaskCanceledAsync"/>（步骤⑤状态机在确认前锁点位）。
/// 与回调冲突时以 queryTask 为准（轮询覆盖回调已写的态）。
/// </summary>
public sealed class RcsTaskTracker : IHostedService, IAsyncDisposable
{
    private readonly IRcsTaskService _taskSvc;
    private readonly IRcsTaskStore _store;
    private readonly IAlarmEventService _alarms;
    private readonly RcsCallbackNotifier _notifier;
    private readonly RcsOptions _options;
    private readonly IRcsRuntimeConfig _runtime;
    private readonly IServiceProvider _services;
    private readonly ILogger<RcsTaskTracker> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    // 去重集合：同一 taskId 的取消/查无/重做上限告警只发一次，终态后清理。
    private readonly ConcurrentDictionary<string, bool> _canceledAlarmed = new();
    private readonly ConcurrentDictionary<string, bool> _notFoundAlarmed = new();
    private readonly ConcurrentDictionary<string, bool> _redoLimitAlarmed = new();

    public RcsTaskTracker(
        IRcsTaskService taskSvc,
        IRcsTaskStore store,
        IAlarmEventService alarms,
        RcsCallbackNotifier notifier,
        IOptions<AppOptions> options,
        IRcsRuntimeConfig runtime,
        IServiceProvider services,
        ILogger<RcsTaskTracker> logger)
    {
        _taskSvc = taskSvc;
        _store = store;
        _alarms = alarms;
        _notifier = notifier;
        _options = options.Value.Rcs;
        _runtime = runtime;
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.TrackerEnabled)
        {
            _logger.LogInformation("RCS 任务跟踪器未启用（TrackerEnabled=false）。");
            return Task.CompletedTask;
        }

        _notifier.TaskStatusReceived += OnTaskStatusReceived;
        _loopTask = Task.Run(() => PollLoopAsync(_cts.Token));
        _logger.LogInformation("RCS 任务跟踪器已启动（轮询初值 {Ms}ms，自动 redo 上限 {Max}）",
            _runtime.PollIntervalMs, _options.MaxAutoRedo);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _notifier.TaskStatusReceived -= OnTaskStatusReceived;
        _cts.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(cancellationToken); }
            catch { /* 取消/超时均接受 */ }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "RCS 跟踪器轮询异常"); }

            try { await Task.Delay(_runtime.PollIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var ids = (await _store.GetUnfinishedTaskIdsAsync(ct))
            .Where(id => !RcsTaskId.IsAssignedRemoteId(id))
            .ToList();
        if (ids.Count == 0) return;

        // 批量 queryTask：key=Id IN 本地 taskId（L1-GB-…），不是回包号。
        var req = QueryTaskRequest.ForLocalIds(ids);

        var result = await _taskSvc.QueryAsync(req, ct);
        if (!result.Success)
        {
            // 查询失败本身不告警（网络抖动常见）；下一轮再试。
            _logger.LogDebug("queryTask 未成功：{Msg}", result.Message ?? result.Error);
            return;
        }

        var queryItems = string.IsNullOrWhiteSpace(result.RawResponse)
            ? Array.Empty<(string TaskId, string Status)>()
            : RcsAckParser.ParseQueryItems(result.RawResponse).ToArray();

        var lookupIds = new List<string>(ids.Count + queryItems.Length);
        lookupIds.AddRange(ids);
        foreach (var (taskId, _) in queryItems)
            lookupIds.Add(taskId);
        var rows = await _store.GetByTaskIdsAsync(lookupIds, ct);

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (taskId, status) in queryItems)
        {
            rows.TryGetValue(taskId, out var row);
            var local = row?.RcsTaskId ?? taskId;
            found.Add(local);
            await ApplyPollStateAsync(local, status, ct, row);
        }

        // RCS 侧未返回的 taskId → 查无此任务告警 + 工位收口 Alarm（可点恢复）。
        // 先落库后下发，下发最长持续「超时×次数+退避」：未满宽限的任务可能尚未送达 RCS，不判查无（P0-3）。
        foreach (var id in ids)
        {
            if (found.Contains(id) || _notFoundAlarmed.ContainsKey(id)) continue;
            rows.TryGetValue(id, out var pending);
            if (pending is not null && !IsNotFoundDue(pending)) continue;
            if (!_notFoundAlarmed.TryAdd(id, true)) continue;
            await _alarms.RaiseRcsTaskNotFoundAsync(id, "轮询 queryTask 未返回该任务（RCS 侧查无），工位已收口可点恢复", ct);
            // P0-3：RCS 查无任务 → 落 FAILED 终态，移出未完结列表。否则该 taskId 每 3s 都进 queryTask IN 列表
            // 且只增不减，月级积累导致请求体膨胀。直接落库不走 notifier，故不触发 auto-redo（任务已判定不存在，重发无意义）。
            if (!await _store.UpdateStateAsync(id, RcsTaskState.Failed, null, "RCS 侧查无此任务，已落 FAILED 收口", ct))
                _logger.LogWarning("RCS 查无任务 {TaskId} 落 FAILED 未生效（任务不存在）", id);
            await NotifySchedulerAbandonedAsync(id, "RCS_NOT_FOUND", ct);
            await NotifyOperationsAbandonedAsync(id, "RCS_NOT_FOUND", ct);
        }
    }

    /// <summary>
    /// 查无收口通知换架编排与盘点：二者只靠状态事件推进，查无直接落库不发事件，不通知会一直挂在进行中
    /// （结果未知保留的事务尤甚）。redo 达上限走 FAILED 事件，已由二者自行收口，不经此处。
    /// </summary>
    private async Task NotifyOperationsAbandonedAsync(string taskId, string reason, CancellationToken ct)
    {
        try
        {
            if (_services.GetService<IChangeFrameOrchestrator>() is { } changeFrame)
                await changeFrame.NotifyTaskAbandonedAsync(taskId, reason, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "通知换架编排任务放弃失败 {TaskId} {Reason}", taskId, reason); }

        try
        {
            if (_services.GetService<IInventoryService>() is { } inventory)
                await inventory.NotifyTaskAbandonedAsync(taskId, reason, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "通知盘点任务放弃失败 {TaskId} {Reason}", taskId, reason); }
    }

    /// <summary>RCS 现场确认：新建任务约 3s 后 queryTask 可查到。</summary>
    private static readonly TimeSpan RcsQueryVisibilityDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 是否已可判「查无」（P0-3）。该次下发已回写 DISPATCH_TIME（≥ SEND_TIME）：从该时刻起等可见延迟 + 一个轮询周期；
    /// 下发中或刚 Claim/重发（SEND_TIME 已刷新、DISPATCH_TIME 仍是旧值）：按整段下发耗时宽限从 SEND_TIME 算。
    /// </summary>
    private bool IsNotFoundDue(RcsTaskRow row)
        => StaleReservationPolicy.IsQueryNotFoundDue(
            DateTime.Now,
            row.SendTime,
            row.DispatchTime,
            RcsQueryVisibilityDelay + TimeSpan.FromMilliseconds(_runtime.PollIntervalMs),
            StaleReservationPolicy.ComputeGrace(_runtime.RequestTimeoutMs, _runtime.MaxRetries));

    private async Task ApplyPollStateAsync(
        string taskId, string rcsStatus, CancellationToken ct, RcsTaskRow? row = null)
    {
        var state = RcsStatusMapper.ToTaskState(rcsStatus);
        if (state is null) return; // 未知态：等下一次

        row ??= await _store.GetByTaskIdAsync(taskId, ct);
        if (row is null) return;
        if (row.TaskState == state) return; // 未变化

        // 与回调冲突时以 queryTask 为准：直接覆盖。
        if (!await _store.UpdateStateAsync(taskId, state, rcsStatus, row.ErrorMsg, ct))
        {
            _logger.LogWarning("跟踪器轮询更新任务态未生效（任务不存在或终态不可回退）{TaskId} → {State}", taskId, state);
            return;
        }

        _notifier.RaiseTaskStatus(new RcsTaskStatusEvent(taskId, ErrorCodeFrom(state), null, state) { Source = "poll" });
        _logger.LogInformation("跟踪器轮询 {TaskId} RCS={Rcs} → {State}", taskId, rcsStatus, state);

        // 落到终态后清理该 taskId 的去重标记。
        if (RcsStatusMapper.IsTerminal(state)) ClearDedup(taskId);
    }

    /// <summary>回调或轮询发现 FAILED/CANCELED 时统一处理（订阅 notifier）。</summary>
    private void OnTaskStatusReceived(object? sender, RcsTaskStatusEvent e)
    {
        // 不在事件回调里直接 await（可能命中 Kestrel 线程池线程）；丢到后台任务。
        _ = HandleStatusEventAsync(e.TaskId, e.TaskState, e.Source);
    }

    private async Task HandleStatusEventAsync(string taskId, string state, string source)
    {
        try
        {
            if (state == RcsTaskState.Failed)
                await AutoRedoAsync(taskId, source);
            else if (state == RcsTaskState.Canceled && _canceledAlarmed.TryAdd(taskId, true))
                await _alarms.RaiseRcsTaskCanceledAsync(taskId, $"RCS 回报取消（来源 {source}），需人工处理小车/容器并确认");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "跟踪器处理事件 {TaskId} {State} 异常", taskId, state); }
    }

    private async Task AutoRedoAsync(string taskId, string source)
    {
        var max = Math.Max(1, _options.MaxAutoRedo);
        var result = await _taskSvc.AutoRedispatchAsync(taskId, max);
        if (result.Success)
        {
            _logger.LogInformation("跟踪器自动 redo {TaskId}（来源 {Source}）", taskId, source);
            return;
        }

        if (result.FailureKind == RcsFailureKind.RedoLimitReached
            && _redoLimitAlarmed.TryAdd(taskId, true))
        {
            _logger.LogWarning("任务 {TaskId} 自动重做已达上限 {Max}，告警人工并收口工位", taskId, max);
            await _alarms.RaiseRcsRedoLimitAsync(taskId, max, $"来源 {source}，工位已收口可点恢复");
            await NotifySchedulerAbandonedAsync(taskId, "REDO_LIMIT", CancellationToken.None);
            return;
        }

        if (result.FailureKind is RcsFailureKind.RouteUnavailable
            or RcsFailureKind.ConfigurationUnavailable)
        {
            _logger.LogWarning("跟踪器自动 redo 路由拒发 {TaskId}（来源 {Source}），收口工位：{Msg}",
                taskId, source, result.Message ?? result.Error);
            await NotifySchedulerAbandonedAsync(taskId, "AUTO_REDO_ROUTE_UNAVAILABLE", CancellationToken.None);
            await NotifyOperationsAbandonedAsync(taskId, "AUTO_REDO_ROUTE_UNAVAILABLE", CancellationToken.None);
            return;
        }

        if (result.FailureKind == RcsFailureKind.AutoRedoNotClaimable)
        {
            _logger.LogWarning("跟踪器自动 redo Claim 未抢占 {TaskId}（来源 {Source}）：{Msg}",
                taskId, source, result.Message ?? result.Error);
        }
    }

    /// <summary>
    /// 测试探测：单次触发真实 <see cref="AutoRedoAsync"/>，不启动轮询循环、不改生产逻辑。
    /// </summary>
    internal Task ProbeAutoRedoOnceAsync(string taskId, string source = "probe")
        => AutoRedoAsync(taskId, source);

    /// <summary>测试探测：跑一轮真实兜底轮询（含查无任务落 FAILED 收口），不启动轮询循环。</summary>
    internal Task ProbePollOnceAsync(CancellationToken ct = default)
        => PollOnceAsync(ct);

    private async Task NotifySchedulerAbandonedAsync(string taskId, string reason, CancellationToken ct)
    {
        try
        {
            var scheduler = _services.GetService<IPositionScheduler>();
            if (scheduler is null) return;
            await scheduler.NotifyTaskAbandonedAsync(taskId, reason, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通知调度器任务放弃失败 {TaskId} {Reason}", taskId, reason);
        }
    }

    // 轮询事件本身无 error_code（queryTask 只回状态字），此处按终态语义合成用于展示：
    // 仅真失败→1、取消→9；进行中/已完成等无错误含义一律 0，避免 EXECUTING 误显示 error_code=1。
    private static int ErrorCodeFrom(string state) => state switch
    {
        RcsTaskState.Failed => RcsErrorCode.Error,
        RcsTaskState.Canceled => RcsErrorCode.Cancel,
        _ => RcsErrorCode.Success
    };

    private void ClearDedup(string taskId)
    {
        _canceledAlarmed.TryRemove(taskId, out _);
        _notFoundAlarmed.TryRemove(taskId, out _);
        _redoLimitAlarmed.TryRemove(taskId, out _);
    }

    public async ValueTask DisposeAsync()
    {
        _notifier.TaskStatusReceived -= OnTaskStatusReceived;
        _cts.Cancel();
        _cts.Dispose();
        await Task.CompletedTask;
    }
}
