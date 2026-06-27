using CncLoader.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Logging;

/// <summary>设备流水：Serilog 结构化日志 + 落库 MAS_AUTO_DEVICE_LOG（读/连接类自动记录）。</summary>
public sealed class CompositeDeviceLogger : IDeviceLogger
{
    private readonly ILogger<CompositeDeviceLogger> _logger;
    private readonly IDeviceLogStore _store;
    private readonly Func<string> _author;

    public CompositeDeviceLogger(ILogger<CompositeDeviceLogger> logger, IDeviceLogStore store,
        CncLoader.Common.Identity.ICurrentUser currentUser)
    {
        _logger = logger;
        _store = store;
        _author = () => currentUser.Name;
    }

    public void Log(DeviceLogEntry entry)
    {
        var e = entry.Author is null ? entry with { Author = _author() } : entry;

        if (e.Success)
            _logger.LogInformation("[{DeviceType}#{DeviceId}] {Action} {Addr} req={Req} resp={Resp} {Cost}ms",
                e.DeviceType, e.DeviceId, e.Action, e.RegisterAddress, e.Request, e.Response, e.CostMs);
        else
            _logger.LogWarning("[{DeviceType}#{DeviceId}] {Action} {Addr} 失败：{Error} {Cost}ms",
                e.DeviceType, e.DeviceId, e.Action, e.RegisterAddress, e.Error, e.CostMs);

        // 写操作由 PlcOperationService 先落库，避免重复
        if (e.Action == DeviceAction.Write) return;

        _ = _store.AppendAsync(e);
    }
}
