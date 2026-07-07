using System.Collections.Concurrent;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 盘点后台任务实现（第四阶段⑥c）：发起 identifyQR → 订阅 <see cref="RcsCallbackNotifier.ScanResultReceived"/> →
/// products 按下发孔位顺序全量校正 FRAME_SLOT（<see cref="ISlotAccountService.CorrectFromInventoryAsync"/>）+ LAST_VERIFY_TIME。
/// 盘点 3~4 分钟、RCS 单任务串行——发起后异步，回调到达通知 UI。失败/取消经 <see cref="InventoryCompleted"/> 事件传出。
/// </summary>
public sealed class InventoryService : IInventoryService
{
    private readonly IRcsTaskService _taskSvc;
    private readonly ILocationMapService _locationMap;
    private readonly ISlotAccountService _slots;
    private readonly IAlarmEventService _alarms;
    private readonly RcsCallbackNotifier _notifier;
    private readonly ILogger<InventoryService> _logger;
    private readonly ConcurrentDictionary<string, InventoryTaskInfo> _active = new();

    public InventoryService(IRcsTaskService taskSvc, ILocationMapService locationMap, ISlotAccountService slots,
        IAlarmEventService alarms, RcsCallbackNotifier notifier, ILogger<InventoryService> logger)
    {
        _taskSvc = taskSvc;
        _locationMap = locationMap;
        _slots = slots;
        _alarms = alarms;
        _notifier = notifier;
        _logger = logger;
        _notifier.ScanResultReceived += OnScanResultReceived;
        _notifier.TaskStatusReceived += OnTaskStatusReceived;
    }

    public event EventHandler<InventoryResultEvent>? InventoryCompleted;

    public IReadOnlyList<InventoryTaskInfo> GetActiveInventories() => _active.Values.ToList();

    public async Task<string> StartInventoryAsync(long frameId, int posStart, int count, string author, CancellationToken ct = default)
    {
        var station = await _locationMap.ResolveFrameAsync(frameId, "station", ct);
        // LOCATION_MAP 未录入时用 fallback 编码（演示用；正式部署应录入 FRAME 站点）
        var stationCode = station?.RcsCode ?? $"FRAME-{frameId}";

        var r = await _taskSvc.DispatchIdentifyAsync(new IdentifyDispatchArgs
        {
            WorkLineId = 1, LineCode = "LINE", Priority = 8,
            Station = stationCode, PosStart = posStart, Count = count,
            FrameId = frameId, Author = author
        }, ct);

        if (!r.Success || string.IsNullOrEmpty(r.TaskId))
        {
            InventoryCompleted?.Invoke(this, new InventoryResultEvent(frameId, "", "FAILED", null, Array.Empty<string>(), 0, r.Error ?? r.Message));
            await _alarms.RaiseRcsTaskNotFoundAsync($"INVENTORY-FRAME-{frameId}", ct);
            return "";
        }

        var info = new InventoryTaskInfo(frameId, r.TaskId, posStart, count, DateTime.Now);
        _active[r.TaskId] = info;
        _logger.LogInformation("盘点已发起 料架 {Frame} 任务 {Task} 起始 {Start} 数 {N}", frameId, r.TaskId, posStart, count);
        return r.TaskId;
    }

    private void OnScanResultReceived(object? sender, RcsScanResultEvent e)
    {
        if (!_active.TryGetValue(e.TaskId, out var info)) return;
        _ = HandleScanAsync(info, e);
    }

    private void OnTaskStatusReceived(object? sender, RcsTaskStatusEvent e)
    {
        if (!_active.ContainsKey(e.TaskId)) return;
        if (e.TaskState is RcsTaskState.Failed or RcsTaskState.Canceled)
        {
            _active.TryRemove(e.TaskId, out _);
            InventoryCompleted?.Invoke(this, new InventoryResultEvent(0, e.TaskId, e.TaskState.ToString().ToUpperInvariant(), null, Array.Empty<string>(), 0, e.Message));
        }
    }

    private async Task HandleScanAsync(InventoryTaskInfo info, RcsScanResultEvent e)
    {
        try
        {
            if (e.ErrorCode != RcsErrorCode.Success)
            {
                _active.TryRemove(info.TaskId, out _);
                InventoryCompleted?.Invoke(this, new InventoryResultEvent(info.FrameId, info.TaskId, "FAILED", e.Code, e.Products, 0, e.Message));
                return;
            }

            var corrected = await _slots.CorrectFromInventoryAsync(info.FrameId, info.PosStart, e.Products);
            _active.TryRemove(info.TaskId, out _);
            _logger.LogInformation("盘点完成 料架 {Frame} 任务 {Task} 校正 {C} 个电极", info.FrameId, info.TaskId, corrected);
            InventoryCompleted?.Invoke(this, new InventoryResultEvent(info.FrameId, info.TaskId, "COMPLETED", e.Code, e.Products, corrected, null));
        }
        catch (Exception ex)
        {
            _active.TryRemove(info.TaskId, out _);
            _logger.LogWarning(ex, "盘点处理异常 料架 {Frame} 任务 {Task}", info.FrameId, info.TaskId);
            InventoryCompleted?.Invoke(this, new InventoryResultEvent(info.FrameId, info.TaskId, "FAILED", e.Code, e.Products, 0, ex.Message));
        }
    }
}
