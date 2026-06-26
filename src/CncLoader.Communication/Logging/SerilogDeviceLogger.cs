using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Logging;

/// <summary>
/// Phase 1 的设备流水实现：写入结构化日志。Phase 2 增加落库 MAS_AUTO_DEVICE_LOG 的实现。
/// </summary>
public sealed class SerilogDeviceLogger : IDeviceLogger
{
    private readonly ILogger<SerilogDeviceLogger> _logger;

    public SerilogDeviceLogger(ILogger<SerilogDeviceLogger> logger) => _logger = logger;

    public void Log(DeviceLogEntry e)
    {
        if (e.Success)
            _logger.LogInformation(
                "[{DeviceType}#{DeviceId}] {Action} {Addr} req={Req} resp={Resp} {Cost}ms",
                e.DeviceType, e.DeviceId, e.Action, e.RegisterAddress, e.Request, e.Response, e.CostMs);
        else
            _logger.LogWarning(
                "[{DeviceType}#{DeviceId}] {Action} {Addr} 失败：{Error} {Cost}ms",
                e.DeviceType, e.DeviceId, e.Action, e.RegisterAddress, e.Error, e.CostMs);
    }
}
