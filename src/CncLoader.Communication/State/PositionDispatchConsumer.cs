using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 唯一派工消费者：下料出队与上料分配必须在同一线程串行。
/// 对账未完成或暂停时不 dequeue、不上料。
/// </summary>
internal sealed class PositionDispatchConsumer
{
    private readonly IPositionDispatchHost _host;
    private readonly IDispatchQueue _queue;
    private readonly ILogger _logger;

    public PositionDispatchConsumer(IPositionDispatchHost host, IDispatchQueue queue, ILogger logger)
    {
        _host = host;
        _queue = queue;
        _logger = logger;
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_host.CanAutoDispatch)
                {
                    await Task.Delay(200, ct);
                    continue;
                }

                var item = _queue.Dequeue();
                if (item is not null)
                {
                    await _host.DispatchOneAsync(item, ct);
                    continue;
                }
                var dispatched = await _host.AllocateUploadsAsync(ct);
                if (!dispatched) await Task.Delay(200, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "派工循环异常"); await Task.Delay(500, ct); }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!_host.CanAutoDispatch) return;
        var item = _queue.Dequeue();
        if (item is not null)
        {
            await _host.DispatchOneAsync(item, ct);
            return;
        }
        await _host.AllocateUploadsAsync(ct);
    }
}

/// <summary>派工消费者对调度器的接缝：闸、上下文与预记/RCS 仍由宿主持有。</summary>
internal interface IPositionDispatchHost
{
    bool CanAutoDispatch { get; }
    Task DispatchOneAsync(DispatchItem item, CancellationToken ct);
    Task<bool> AllocateUploadsAsync(CancellationToken ct);
}
