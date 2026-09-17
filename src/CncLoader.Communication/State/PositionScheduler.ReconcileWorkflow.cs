using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

public sealed partial class PositionScheduler
{
    /// <summary>
    /// 单一启动对账工作流：attempt → 成功开闸结束，或失败 → interval → 下一 attempt。
    /// 同一时刻至多一个 workflow / 一个 attempt（D3/D7）。
    /// </summary>
    private async Task ReconcileWorkflowAsync(CancellationToken lifecycleToken)
    {
        while (!lifecycleToken.IsCancellationRequested)
        {
            if (_isReconciled || Volatile.Read(ref _gateOpened) != 0)
                return;

            ReconcileAttemptOutcome outcome;
            try
            {
                outcome = await TryReconcileOnceAsync(lifecycleToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifecycleToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 协调器已吞阶段异常；此处兜底防止 workflow 静默死亡，按失败重试（不开闸）。
                _logger.LogWarning(ex, "启动对账工作流未预期异常，将按失败间隔重试");
                _isReconciled = false;
                _reconciliationFailureReason = FormatFailureReason(
                    ReconcileRoundResult.Fail(ReconcilePhase.One, ex.Message ?? ex.GetType().Name, ex));
                _reconciliationState = (int)ReconciliationState.WaitingForRetry;
                PublishReconcileState();
                outcome = ReconcileAttemptOutcome.Failed;
            }

            if (outcome == ReconcileAttemptOutcome.Succeeded)
            {
                TryOpenGateAfterSuccess();
                return;
            }
            if (outcome == ReconcileAttemptOutcome.Cancelled)
                return;

            try
            {
                await DelayMsAsync(_options.ReconcileRetryIntervalMs, lifecycleToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifecycleToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<ReconcileAttemptOutcome> TryReconcileOnceAsync(CancellationToken ct)
    {
        // 进入每一轮 Reconciling：清空旧失败原因，属性与发布快照同一转换。
        _reconciliationState = (int)ReconciliationState.Reconciling;
        _reconciliationFailureReason = null;
        PublishReconcileState();
        Interlocked.Increment(ref _reconcileAttemptCount);

        ReconcileRoundResult round;
        try
        {
            round = await ReconcileAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 取消 ≠ 业务失败：不发布 WaitingForRetry / 不改写 FailureReason
            return ReconcileAttemptOutcome.Cancelled;
        }

        if (StartupReconcileCoordinator.DecideIsReconciled(round))
            return ReconcileAttemptOutcome.Succeeded;

        var reason = FormatFailureReason(round);
        _isReconciled = false;
        _reconciliationFailureReason = reason;
        _reconciliationState = (int)ReconciliationState.WaitingForRetry;
        PublishReconcileState();
        _logger.LogWarning(
            "启动对账失败，自动派工已锁定：阶段 {Phase}，原因 {Reason}；将在 {IntervalMs}ms 后自动重试（第 {Attempt} 次已失败）",
            round.FailedPhase, reason, _options.ReconcileRetryIntervalMs, ReconcileAttemptCount);
        return ReconcileAttemptOutcome.Failed;
    }

    private static string FormatFailureReason(ReconcileRoundResult round)
    {
        var phase = round.FailedPhase?.ToString() ?? "?";
        var detail = string.IsNullOrWhiteSpace(round.FailureReason)
            ? "对账失败"
            : round.FailureReason!;
        // FailureReason 已经过协调器 Sanitize；再包一层阶段前缀供看板/日志
        if (detail.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Pwd=", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Connection String", StringComparison.OrdinalIgnoreCase))
        {
            detail = "对账阶段执行异常（已隐藏敏感连接信息）";
        }
        return $"阶段 {phase}：{detail}";
    }

    private Task DelayMsAsync(int milliseconds, CancellationToken ct)
        => DelayOverride is not null
            ? DelayOverride(milliseconds, ct)
            : Task.Delay(milliseconds, ct);

    private enum ReconcileAttemptOutcome
    {
        Succeeded,
        Failed,
        Cancelled
    }
}
