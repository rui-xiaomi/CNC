using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>
/// 重启接续（P1-4）：换架事务与盘点任务只登记在内存，重启即丢——之后的完成回调无人接，换架第二发不下发、扫码结果不回写。
/// 启动时从未完结任务重建二者。须注册在 <see cref="Rcs.RcsTaskTracker"/> 与 <see cref="PositionScheduler"/> 之前：
/// 二者首轮轮询 / 对账①b 会发出终态事件，编排器此时须已登记。
/// </summary>
public sealed class InFlightTransactionRecoveryService : IHostedService
{
    // 有界：库不可用时不无限拖住宿主启动，失败转告警人工核对。
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(15);

    private readonly IRcsTaskStore _store;
    private readonly IChangeFrameOrchestrator _changeFrame;
    private readonly IInventoryService _inventory;
    private readonly IAlarmEventService _alarms;
    private readonly ILogger<InFlightTransactionRecoveryService> _logger;

    public InFlightTransactionRecoveryService(IRcsTaskStore store, IChangeFrameOrchestrator changeFrame,
        IInventoryService inventory, IAlarmEventService alarms, ILogger<InFlightTransactionRecoveryService> logger)
    {
        _store = store;
        _changeFrame = changeFrame;
        _inventory = inventory;
        _alarms = alarms;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(RecoveryTimeout);
            var rows = new List<RcsTaskRow>();
            foreach (var id in await _store.GetUnfinishedTaskIdsAsync(cts.Token))
            {
                if (await _store.GetByTaskIdAsync(id, cts.Token) is { } row)
                    rows.Add(row);
            }
            var changeFrames = await _changeFrame.RecoverInFlightAsync(rows, cts.Token);
            var inventories = await _inventory.RecoverInFlightAsync(rows, cts.Token);
            if (changeFrames + inventories > 0)
                _logger.LogInformation("重启接续：换架事务 {Cf} 个、盘点任务 {Inv} 个", changeFrames, inventories);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重启接续换架/盘点事务失败，进行中的换架第二发不会自动下发、盘点结果不回写");
            try
            {
                await _alarms.RaiseRcsWarnAsync("SCHEDULER", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    "重启接续换架/盘点事务失败，进行中的换架与盘点需人工核对", null, CancellationToken.None);
            }
            catch (Exception alarmEx) { _logger.LogWarning(alarmEx, "重启接续失败告警落库失败"); }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
