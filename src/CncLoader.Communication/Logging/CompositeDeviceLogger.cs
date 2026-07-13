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

        _ = _store.AppendAsync(e);
    }
}
