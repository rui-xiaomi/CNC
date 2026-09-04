using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.State;

/// <summary>
/// 定期盘点后台调度（"盘点管家"）：每 <see cref="RcsOptions.InventoryIntervalMinutes"/> 分钟，在 RCS 空闲时
/// 逐料架发起 identifyQR 扫码核账（<see cref="IInventoryService.StartInventoryAsync"/>）。RCS 单任务串行——
/// 一架盘点完成（约 3~4 分钟）再发下一架；盘点期间不与上下料/换架抢占（发起前检查队列与在途事务）。
/// 默认关（<see cref="RcsOptions.InventoryAutoEnabled"/>=false），需运维显式开启。
/// </summary>
public sealed class InventorySchedulerService : IHostedService, IAsyncDisposable
{
    private readonly IFrameService _frames;
    private readonly IInventoryService _inventory;
    private readonly IDispatchQueue _queue;
    private readonly IChangeFrameOrchestrator _changeFrame;
    private readonly RcsOptions _options;
    private readonly ILogger<InventorySchedulerService> _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<InventoryResultEvent>> _waits = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private DateTime _lastRun = DateTime.MinValue;

    // 单架盘点等待上限（盘点约 3~4 分钟，留足冗余）。
    private static readonly TimeSpan PerFrameTimeout = TimeSpan.FromMinutes(8);
    // 检查节拍。
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    public InventorySchedulerService(IFrameService frames, IInventoryService inventory, IDispatchQueue queue,
        IChangeFrameOrchestrator changeFrame, IOptions<AppOptions> options, ILogger<InventorySchedulerService> logger)
    {
        _frames = frames;
        _inventory = inventory;
        _queue = queue;
        _changeFrame = changeFrame;
        _options = options.Value.Rcs;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.InventoryAutoEnabled)
        {
            _logger.LogInformation("定期盘点未启用（InventoryAutoEnabled=false）。");
            return Task.CompletedTask;
        }
        _inventory.InventoryCompleted += OnInventoryCompleted;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("定期盘点已启动（间隔 {N} 分钟）", _options.InventoryIntervalMinutes);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _inventory.InventoryCompleted -= OnInventoryCompleted;
        _cts?.Cancel();
        if (_loopTask is not null) { try { await _loopTask; } catch { /* ignore */ } }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var due = (DateTime.Now - _lastRun) >= TimeSpan.FromMinutes(_options.InventoryIntervalMinutes);
                if (due && IsRcsIdle())
                {
                    await RunPassAsync(ct);
                    _lastRun = DateTime.Now;
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "定期盘点检查异常"); }
            try { await Task.Delay(CheckInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>RCS 空闲：派工队列空 + 无在盘点 + 无换架事务。</summary>
    private bool IsRcsIdle()
        => _queue.Count == 0
           && _inventory.GetActiveInventories().Count == 0
           && _changeFrame.GetActiveTransactions().Count == 0;

    private async Task RunPassAsync(CancellationToken ct)
    {
        var frames = await _frames.GetAllAsync(ct);
        _logger.LogInformation("定期盘点开始一轮：{N} 个料架", frames.Count);
        foreach (var frame in frames)
        {
            if (ct.IsCancellationRequested) break;
            // 发起前再确认 RCS 空闲（生产任务优先，避免抢占）
            if (!IsRcsIdle())
            {
                _logger.LogInformation("定期盘点：检测到搬运/换架任务，本轮剩余料架推迟到下一轮");
                break;
            }
            await InventoryOneAsync(frame.Id, frame.SlotTotal, ct);
        }
    }

    private async Task InventoryOneAsync(long frameId, int slotTotal, CancellationToken ct)
    {
        var count = slotTotal > 0 ? slotTotal : 1;
        // AUTHOR 列 VARCHAR(15)，不可用超长系统名（曾用 inventory-scheduler 落库失败）。
        // 起始孔位 101 = 1 层 1 位（identifyQR 三位数、百位为面）
        var taskId = await _inventory.StartInventoryAsync(frameId, 101, count, "inv-auto", ct);
        if (string.IsNullOrEmpty(taskId))
        {
            _logger.LogWarning("定期盘点：料架 {Frame} 发起失败，跳过", frameId);
            return;
        }

        var tcs = new TaskCompletionSource<InventoryResultEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waits[taskId] = tcs;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerFrameTimeout);
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token));
            if (completed == tcs.Task)
            {
                var r = await tcs.Task; // WhenAny 已确认完成；await 解包，异常走原异常而非 AggregateException
                _logger.LogInformation("定期盘点：料架 {Frame} 任务 {Task} {State}，校正 {C}", frameId, taskId, r.State, r.CorrectedCount);
            }
            else
            {
                _logger.LogWarning("定期盘点：料架 {Frame} 任务 {Task} 等待超时，继续下一架", frameId, taskId);
            }
        }
        catch (OperationCanceledException) { /* 停机/超时 */ }
        finally { _waits.TryRemove(taskId, out _); }
    }

    private void OnInventoryCompleted(object? sender, InventoryResultEvent e)
    {
        if (_waits.TryGetValue(e.TaskId, out var tcs))
            tcs.TrySetResult(e);
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
