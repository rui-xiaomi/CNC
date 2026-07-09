using System.Collections.Concurrent;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 盘点后台任务实现（第四阶段⑥c）：发起 identifyQR → 订阅 <see cref="RcsCallbackNotifier.ScanResultReceived"/> →
/// products 按下发孔位顺序全量校正 FRAME_SLOT（<see cref="ISlotAccountService.CorrectFromInventoryAsync"/>）+ LAST_VERIFY_TIME。
/// 盘点 3~4 分钟、RCS 单任务串行——发起后异步，回调到达通知 UI。失败/取消经 <see cref="InventoryCompleted"/> 事件传出。
/// 发起前互斥：派工队列非空或有在途换架则拒发（与定期盘点 IsRcsIdle 对齐）。
/// </summary>
public sealed class InventoryService : IInventoryService
{
    private readonly IRcsTaskService _taskSvc;
    private readonly ILocationMapService _locationMap;
    private readonly ISlotAccountService _slots;
    private readonly IEquipmentConfigService _equipment;
    private readonly IAlarmEventService _alarms;
    private readonly IDispatchQueue _queue;
    private readonly IChangeFrameOrchestrator _changeFrame;
    private readonly RcsCallbackNotifier _notifier;
    private readonly ILogger<InventoryService> _logger;
    private readonly ConcurrentDictionary<string, InventoryTaskInfo> _active = new();

    public InventoryService(IRcsTaskService taskSvc, ILocationMapService locationMap, ISlotAccountService slots,
        IEquipmentConfigService equipment, IAlarmEventService alarms, IDispatchQueue queue,
        IChangeFrameOrchestrator changeFrame, RcsCallbackNotifier notifier, ILogger<InventoryService> logger)
    {
        _taskSvc = taskSvc;
        _locationMap = locationMap;
        _slots = slots;
        _equipment = equipment;
        _alarms = alarms;
        _queue = queue;
        _changeFrame = changeFrame;
        _notifier = notifier;
        _logger = logger;
        _notifier.ScanResultReceived += OnScanResultReceived;
        _notifier.TaskStatusReceived += OnTaskStatusReceived;
    }

    public event EventHandler<InventoryResultEvent>? InventoryCompleted;

    public IReadOnlyList<InventoryTaskInfo> GetActiveInventories() => _active.Values.ToList();

    public async Task<string> StartInventoryAsync(long frameId, int posStart, int count, string author, CancellationToken ct = default)
    {
        // 与定期盘点一致：有搬运排队或换架进行中则拒发，避免抢 RCS。
        if (_queue.Count > 0 || _changeFrame.GetActiveTransactions().Count > 0)
        {
            const string busy = "有搬运/换架进行中，请稍后再盘点";
            _logger.LogWarning("盘点拒发 料架 {Frame}：{Msg}", frameId, busy);
            InventoryCompleted?.Invoke(this, new InventoryResultEvent(frameId, "", "FAILED", null, Array.Empty<string>(), 0, busy));
            return "";
        }

        // station → shelf（种子多为 shelf）；禁止 FRAME-{id} 假码。
        var station = await _locationMap.ResolveFrameAsync(frameId, "station", ct)
                      ?? await _locationMap.ResolveFrameAsync(frameId, "shelf", ct);
        if (station is null || string.IsNullOrWhiteSpace(station.RcsCode))
        {
            var msg = $"料架 {frameId} 未录入 LOCATION_MAP（station/shelf），请先配置位置映射";
            _logger.LogWarning("盘点拒发：{Msg}", msg);
            InventoryCompleted?.Invoke(this, new InventoryResultEvent(frameId, "", "FAILED", null, Array.Empty<string>(), 0, msg));
            await _alarms.RaiseRcsTaskNotFoundAsync($"INVENTORY-FRAME-{frameId}", ct);
            return "";
        }

        // 料架 → 绑定机台 → 线体（与调度器一致）；无绑定则回退 LINE/1
        var line = await ResolveLineForFrameAsync(frameId, ct);

        var r = await _taskSvc.DispatchIdentifyAsync(new IdentifyDispatchArgs
        {
            WorkLineId = line.WorkLineId, LineCode = line.LineCode, Priority = 8,
            Station = station.RcsCode, PosStart = posStart, Count = count,
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

    /// <summary>按料架绑定机台反查线体；多绑定取首条；无绑定回退 WorkLineId=1 / LINE。</summary>
    private async Task<WorkLineRef> ResolveLineForFrameAsync(long frameId, CancellationToken ct)
    {
        var binds = await _equipment.GetBindingByFrameAsync(frameId, ct);
        foreach (var b in binds)
        {
            var line = await _equipment.GetWorkLineByEquipmentAsync(b.EquipmentId, ct);
            if (line is not null) return line;
        }
        _logger.LogWarning("盘点料架 {Frame} 无绑定机台或线体，回退 LINE/1", frameId);
        return new WorkLineRef(1, "LINE");
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
