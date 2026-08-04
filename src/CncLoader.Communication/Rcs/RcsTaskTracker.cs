using System.Collections.Concurrent;
using System.Text.Json;
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
/// 1) 兜底轮询——按 <see cref="RcsOptions.PollIntervalMs"/> 取未完结 taskId 批量 queryTask（IN），
///    按 §4.5 11→5 映射推进态；RCS 查无此任务 → 告警人工。
/// 2) 自动 redo——订阅 <see cref="IRcsCallbackNotifier.TaskStatusReceived"/>，FAILED 态经
///    <see cref="IRcsTaskStore.TryIncrementRedoIfUnderAsync"/> 原子递增后 <see cref="IRcsTaskService.RedispatchAsync"/>，
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
        var ids = await _store.GetUnfinishedTaskIdsAsync(ct);
        if (ids.Count == 0) return;

        // 批量 queryTask：condition IN taskId 列表。
        var req = new QueryTaskRequest
        {
            Condition = new QueryCondition
            {
                Relation = "AND",
                Conditions =
                {
                    new QueryConditionItem { Key = "taskId", Value = string.Join(",", ids), Operator = "IN", Order = "None" }
                }
            },
            PageIndex = 1,
            PageSize = Math.Max(10, ids.Count)
        };

        var result = await _taskSvc.QueryAsync(req, ct);
        if (!result.Success)
        {
            // 查询失败本身不告警（网络抖动常见）；下一轮再试。
            _logger.LogDebug("queryTask 未成功：{Msg}", result.Message ?? result.Error);
            return;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(result.RawResponse))
        {
            foreach (var (taskId, status) in ParseItems(result.RawResponse))
            {
                found.Add(taskId);
                await ApplyPollStateAsync(taskId, status, ct);
            }
        }

        // RCS 侧未返回的 taskId → 查无此任务告警 + 工位收口 Alarm（可点恢复）。
        foreach (var id in ids)
        {
            if (found.Contains(id) || !_notFoundAlarmed.TryAdd(id, true)) continue;
            await _alarms.RaiseRcsTaskNotFoundAsync(id, "轮询 queryTask 未返回该任务（RCS 侧查无），工位已收口可点恢复", ct);
            await NotifySchedulerAbandonedAsync(id, "RCS_NOT_FOUND", ct);
        }
    }

    private async Task ApplyPollStateAsync(string taskId, string rcsStatus, CancellationToken ct)
    {
        var state = RcsStatusMapper.ToTaskState(rcsStatus);
        if (state is null) return; // 未知态：等下一次

        var row = await _store.GetByTaskIdAsync(taskId, ct);
        if (row is null) return;
        if (row.TaskState == state) return; // 未变化

        // 与回调冲突时以 queryTask 为准：直接覆盖。
        if (!await _store.UpdateStateAsync(taskId, state, rcsStatus, row.ErrorMsg, ct))
        {
            _logger.LogWarning("跟踪器轮询更新任务态未生效（任务不存在）{TaskId} → {State}", taskId, state);
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
        if (await _store.TryIncrementRedoIfUnderAsync(taskId, max))
        {
            _logger.LogInformation("跟踪器自动 redo {TaskId}（来源 {Source}，REDO_COUNT+1）", taskId, source);
            await _taskSvc.RedispatchAsync(taskId);
        }
        else if (_redoLimitAlarmed.TryAdd(taskId, true))
        {
            _logger.LogWarning("任务 {TaskId} 自动重做已达上限 {Max}，告警人工并收口工位", taskId, max);
            await _alarms.RaiseRcsRedoLimitAsync(taskId, max, $"来源 {source}，工位已收口可点恢复");
            await NotifySchedulerAbandonedAsync(taskId, "REDO_LIMIT", CancellationToken.None);
        }
    }

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

    /// <summary>解析 queryTask 应答 items[]：返回 (taskId, status) 列表。</summary>
    private static IReadOnlyList<(string taskId, string status)> ParseItems(string? raw)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return list;
            foreach (var it in items.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Object) continue;
                var id = it.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                var st = it.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(st))
                    list.Add((id!, st!));
            }
        }
        catch { /* 解析失败：忽略本次，下一轮重试 */ }
        return list;
    }

    public async ValueTask DisposeAsync()
    {
        _notifier.TaskStatusReceived -= OnTaskStatusReceived;
        _cts.Cancel();
        _cts.Dispose();
        await Task.CompletedTask;
    }
}
