using System.Collections.Concurrent;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 机台线体/料架绑定缓存、软删权威重读、派工 Hold 与路由拒发限频。
/// 线体缓存仅提示：命中后仍读权威配置，不可用则淘汰。
/// </summary>
internal sealed class PositionRouteCache
{
    private static readonly TimeSpan RouteWarnThrottle = TimeSpan.FromSeconds(30);
    private const int UnloadRouteRetryLimit = 5;

    private readonly ConcurrentDictionary<long, WorkLineRef> _lineCache = new();
    private readonly ConcurrentDictionary<long, EquipmentFrameBindingIds> _bindingCache = new();
    private readonly ConcurrentDictionary<long, DateTime> _routeWarnStamp = new();
    private readonly ConcurrentDictionary<long, string> _equipmentDispatchHolds = new();

    private readonly IEquipmentConfigService _equipment;
    private readonly IAlarmEventService _alarms;
    private readonly IDispatchQueue _queue;
    private readonly ILogger _logger;
    private readonly Func<(long Eq, long Pos), SemaphoreSlim> _gateFor;
    private readonly Action<PositionContext, PositionState> _setState;

    public PositionRouteCache(
        IEquipmentConfigService equipment,
        IAlarmEventService alarms,
        IDispatchQueue queue,
        ILogger logger,
        Func<(long Eq, long Pos), SemaphoreSlim> gateFor,
        Action<PositionContext, PositionState> setState)
    {
        _equipment = equipment;
        _alarms = alarms;
        _queue = queue;
        _logger = logger;
        _gateFor = gateFor;
        _setState = setState;
    }

    public bool HasLine(long equipmentId) => _lineCache.ContainsKey(equipmentId);

    public void CacheLine(long equipmentId, WorkLineRef line) => _lineCache[equipmentId] = line;

    public void InvalidateLine(long equipmentId) => _lineCache.TryRemove(equipmentId, out _);

    public bool IsHeld(long equipmentId) => _equipmentDispatchHolds.ContainsKey(equipmentId);

    public void SetHold(long equipmentId, bool held, string? reason = null)
    {
        if (held)
        {
            _equipmentDispatchHolds[equipmentId] = reason ?? "";
            _logger.LogWarning("机台 {Eq} 自动派工已锁定：{Reason}", equipmentId, reason ?? "—");
        }
        else if (_equipmentDispatchHolds.TryRemove(equipmentId, out _))
        {
            _logger.LogInformation("机台 {Eq} 自动派工锁定已解除", equipmentId);
        }
    }

    public void InvalidateBindings(long? equipmentId = null)
    {
        if (equipmentId is long eq)
        {
            _bindingCache.TryRemove(eq, out _);
            _lineCache.TryRemove(eq, out _);
            _logger.LogInformation("已失效机台 {Eq} 的料架/线体缓存", eq);
        }
        else
        {
            _bindingCache.Clear();
            _lineCache.Clear();
            _logger.LogInformation("已清空全部料架/线体缓存");
        }
    }

    /// <summary>
    /// 反查机台所属线体。缓存仅提示/快照，命中后仍须读权威配置；
    /// 不可用则淘汰缓存并返回 null；禁止默认 LINE / 任意首条线体。
    /// </summary>
    public async Task<WorkLineRef?> ResolveLineAsync(long equipmentId, CancellationToken ct)
    {
        try
        {
            var line = await _equipment.GetWorkLineByEquipmentAsync(equipmentId, ct);
            if (line is null)
            {
                _lineCache.TryRemove(equipmentId, out _);
                LogRouteUnavailableThrottled(equipmentId,
                    $"机台 {equipmentId} 线体路由不可用（缺失或已禁用），拒绝派工");
                return null;
            }
            _lineCache[equipmentId] = line;
            return line;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _lineCache.TryRemove(equipmentId, out _);
            _logger.LogWarning(ex, "机台 {Eq} 线体路由权威查询异常，fail-closed", equipmentId);
            return null;
        }
    }

    public void LogRouteUnavailableThrottled(long equipmentId, string message)
    {
        var now = DateTime.UtcNow;
        if (_routeWarnStamp.TryGetValue(equipmentId, out var last)
            && now - last < RouteWarnThrottle)
            return;
        _routeWarnStamp[equipmentId] = now;
        _logger.LogWarning("{Msg}", message);
    }

    /// <summary>取机台上/下料架绑定 ID（带缓存；配置变更须先失效）。</summary>
    public async Task<EquipmentFrameBindingIds> ResolveBindingsAsync(long equipmentId, CancellationToken ct)
    {
        if (_bindingCache.TryGetValue(equipmentId, out var cached)) return cached;
        var ids = await _equipment.GetFrameBindingIdsAsync(equipmentId, ct);
        _bindingCache[equipmentId] = ids;
        return ids;
    }

    /// <summary>下料路由暂不可用：回队；超过次数转 Alarm，避免空转丢件。</summary>
    public async Task DeferUnloadAsync(DispatchItem item, PositionContext ctx, string reason, CancellationToken ct)
    {
        if (item.RetryCount >= UnloadRouteRetryLimit)
        {
            var g = _gateFor((item.EquipmentId, item.PositionId));
            await g.WaitAsync(ct);
            try
            {
                ctx.AlarmRaised = true;
                _setState(ctx, PositionState.Alarm);
                await _alarms.RaiseRcsTaskNotFoundAsync(
                    $"UNLOAD-EQ{item.EquipmentId}-POS{item.PositionId}",
                    $"EQ{item.EquipmentId} POS{item.PositionId} 下料路由持续不可用（已回队 {item.RetryCount} 次）：{reason}", ct);
                _logger.LogWarning("EQ{Eq} POS{Pos} 下料路由持续不可用 → ALARM：{Reason}",
                    item.EquipmentId, item.PositionId, reason);
            }
            finally { g.Release(); }
            return;
        }

        _queue.Enqueue(item with { RetryCount = item.RetryCount + 1 });
        LogRouteUnavailableThrottled(item.EquipmentId,
            $"EQ{item.EquipmentId} POS{item.PositionId} {reason}，已回队（第 {item.RetryCount + 1} 次）");
    }
}
