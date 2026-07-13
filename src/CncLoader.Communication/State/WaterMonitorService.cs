using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.State;

/// <summary>
/// 料架水位监视器实现（第四阶段⑥c，HostedService）：周期检查每机台的上/下料架占用，
/// 下料架接近满（剩余空槽 ≤ WaterFullThreshold）→ 触发换架(Unload)；上料架空（empty=total）→ 触发换架(Upload)。
/// 已有该角色换架事务在进行中则跳过（去重）；终态后清除 in-flight，允许再次自动换架。
/// </summary>
public sealed class WaterMonitorService : IHostedService, IWaterMonitorService, IAsyncDisposable
{
    private readonly IEquipmentConfigService _equipment;
    private readonly IFrameService _frames;
    private readonly ISlotAccountService _slots;
    private readonly IChangeFrameOrchestrator _changeFrame;
    private readonly RcsOptions _options;
    private readonly ILogger<WaterMonitorService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private readonly ConcurrentDictionary<(long Eq, FrameRole Role), byte> _inFlight = new();

    public WaterMonitorService(IEquipmentConfigService equipment, IFrameService frames, ISlotAccountService slots,
        IChangeFrameOrchestrator changeFrame, IOptions<AppOptions> options, ILogger<WaterMonitorService> logger)
    {
        _equipment = equipment;
        _frames = frames;
        _slots = slots;
        _changeFrame = changeFrame;
        _options = options.Value.Rcs;
        _logger = logger;
        _changeFrame.ProgressChanged += OnChangeFrameProgress;
    }

    public event EventHandler<WaterLevelEvent>? WaterLevelChanged;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.WaterMonitorEnabled)
        {
            _logger.LogInformation("水位监视器未启用（WaterMonitorEnabled=false）。");
            return Task.CompletedTask;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("水位监视器已启动（周期 {Ms}ms，满阈值 {N}）", _options.SchedulerIntervalMs * 4, _options.WaterFullThreshold);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loopTask is not null) { try { await _loopTask; } catch { /* ignore */ } }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "水位监视器检查异常"); }
            try { await Task.Delay(_options.SchedulerIntervalMs * 4, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<IReadOnlyList<WaterLevelEvent>> CheckAsync(CancellationToken ct = default)
    {
        var events = new List<WaterLevelEvent>();
        var allFrames = await _frames.GetAllAsync(ct);
        if (allFrames.Count == 0) return events;

        foreach (var frame in allFrames)
        {
            var occ = await _slots.GetOccupancyAsync(frame.Id, ct);
            var isFull = occ.Total > 0 && (occ.Total - occ.Occupied) <= _options.WaterFullThreshold && occ.Occupied > 0;
            var isEmpty = occ.Total > 0 && occ.Empty == occ.Total;
            if (!isFull && !isEmpty) continue;

            var binding = await FindBindingAsync(frame.Id, isEmpty, ct);
            if (binding is null) continue;

            var key = (binding.Value.EquipmentId, binding.Value.Role);
            if (_inFlight.ContainsKey(key)) continue;
            if (_changeFrame.GetActiveTransactions().Any(t => t.EquipmentId == binding.Value.EquipmentId && t.Role == binding.Value.Role))
                continue;

            var level = isFull ? "FULL" : "EMPTY";
            _logger.LogInformation("水位触发：料架 {Frame} {Level}（{Occ}/{Total}）→ 触发机台 {Eq} {Role} 换架",
                frame.Id, level, occ.Occupied, occ.Total, binding.Value.EquipmentId, binding.Value.Role);

            _inFlight[key] = 0;
            string? txnId = null;
            try
            {
                txnId = await _changeFrame.ChangeFrameAsync(binding.Value.EquipmentId, binding.Value.Role, "water-monitor", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "水位触发换架失败 料架 {Frame}", frame.Id);
                _inFlight.TryRemove(key, out _);
            }

            var evt = new WaterLevelEvent(binding.Value.EquipmentId, frame.Id, binding.Value.Role, level, occ.Total, occ.Occupied, occ.Empty, txnId);
            events.Add(evt);
            WaterLevelChanged?.Invoke(this, evt);
        }
        return events;
    }

    private void OnChangeFrameProgress(object? sender, ChangeFrameProgressEvent e)
    {
        if (e.State is "COMPLETED" or "FAILED" or "CANCELED" || e.Step == ChangeFrameStep.Alarm)
            _inFlight.TryRemove((e.EquipmentId, e.Role), out _);
    }

    /// <summary>找料架绑定的机台+角色（精确反查 FRAME_BIND）。满架优先取下料/中转/NG 角色，空架优先取上料角色；无绑定返回 null 跳过。</summary>
    private async Task<(long EquipmentId, FrameRole Role)?> FindBindingAsync(long frameId, bool preferUpload, CancellationToken ct)
    {
        var bindings = await _equipment.GetBindingByFrameAsync(frameId, ct);
        if (bindings.Count == 0)
        {
            _logger.LogDebug("料架 {Frame} 满/空但无有效 FRAME_BIND 绑定，跳过自动换架", frameId);
            return null;
        }
        var match = preferUpload
            ? bindings.FirstOrDefault(b => b.Role == FrameRole.Upload)
            : bindings.FirstOrDefault(b => b.Role != FrameRole.Upload);
        var picked = match ?? bindings[0];
        return (picked.EquipmentId, picked.Role);
    }

    public ValueTask DisposeAsync()
    {
        _changeFrame.ProgressChanged -= OnChangeFrameProgress;
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
