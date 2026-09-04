using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Logging;

/// <summary>
/// 设备流水：Serilog + 条件落库 MAS_AUTO_DEVICE_LOG。
/// 写操作由 <c>PlcOperationService</c> 先落库，此处跳过以免重复；
/// 成功读/连默认不落库（见 <see cref="LoggingOptions.PersistSuccessfulReads"/>），失败始终落库。
/// </summary>
public sealed class CompositeDeviceLogger : IDeviceLogger
{
    private readonly ILogger<CompositeDeviceLogger> _logger;
    private readonly IDeviceLogStore _store;
    private readonly LoggingOptions _logging;
    private readonly Func<string> _author;
    /// <summary>失败记录同源节流窗口：PLC 离线时每点位每 500ms 一次失败，节流避免洪水写库打满连接池（P1-3）。</summary>
    private static readonly TimeSpan FailureThrottle = TimeSpan.FromSeconds(30);
    private readonly object _throttleGate = new();
    private readonly Dictionary<string, DateTime> _lastFailureAt = new();

    public CompositeDeviceLogger(ILogger<CompositeDeviceLogger> logger, IDeviceLogStore store,
        IOptions<AppOptions> options, CncLoader.Common.Identity.ICurrentUser currentUser)
    {
        _logger = logger;
        _store = store;
        _logging = options.Value.Logging;
        _author = () => currentUser.Name;
    }

    public void Log(DeviceLogEntry entry)
    {
        var e = entry.Author is null ? entry with { Author = _author() } : entry;

        if (e.Success)
        {
            // 轮询读量极大：默认打 Debug，避免 Information 文件日志与 500ms 轮询同速膨胀。
            if (e.Action is DeviceAction.Read or DeviceAction.Heartbeat)
                _logger.LogDebug("[{DeviceType}#{DeviceId}] {Action} {Addr} req={Req} resp={Resp} {Cost}ms",
                    e.DeviceType, e.DeviceId, e.Action, e.RegisterAddress, e.Request, e.Response, e.CostMs);
            else
                _logger.LogInformation("[{DeviceType}#{DeviceId}] {Action} {Addr} req={Req} resp={Resp} {Cost}ms",
                    e.DeviceType, e.DeviceId, e.Action, e.RegisterAddress, e.Request, e.Response, e.CostMs);
        }
        else
        {
            _logger.LogWarning("[{DeviceType}#{DeviceId}] {Action} {Addr} 失败：{Error} {Cost}ms",
                e.DeviceType, e.DeviceId, e.Action, e.RegisterAddress, e.Error, e.CostMs);
        }

        // 写操作由 PlcOperationService 先落库，避免重复
        if (e.Action == DeviceAction.Write) return;

        // 成功读/连/断/心跳：默认不落库；失败始终落库。
        if (e.Success && !_logging.PersistSuccessfulReads
            && e.Action is DeviceAction.Read or DeviceAction.Connect or DeviceAction.Disconnect or DeviceAction.Heartbeat)
            return;

        // 失败落库同源 30s 节流：避免 PLC 离线时洪水式并发写库打满连接池；节流期内的失败仍写文件日志（上方已记）。
        if (!e.Success && !TryAcquireFailureThrottle(e))
            return;

        _ = PersistObservedAsync(e);
    }

    /// <summary>观察落库异常（不抛未观察 Task 异常），DB 抖动时只记日志不拖垮调用方。</summary>
    private async Task PersistObservedAsync(DeviceLogEntry e)
    {
        try { await _store.AppendAsync(e); }
        catch (Exception ex) { _logger.LogWarning(ex, "设备流水落库失败"); }
    }

    private bool TryAcquireFailureThrottle(DeviceLogEntry e)
    {
        var key = $"{e.DeviceType}|{e.DeviceId}|{e.Action}|{e.RegisterAddress}";
        var now = DateTime.UtcNow;
        lock (_throttleGate)
        {
            if (_lastFailureAt.TryGetValue(key, out var last) && now - last < FailureThrottle)
                return false;
            _lastFailureAt[key] = now;
            return true;
        }
    }
}
