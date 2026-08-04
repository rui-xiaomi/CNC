namespace CncLoader.Core.State;

/// <summary>启动对账阶段（§6.3 ① / ①b / ② / ③）。</summary>
public enum ReconcilePhase
{
    One = 1,
    OneB = 2,
    Two = 3,
    Three = 4
}

/// <summary>单阶段结果。</summary>
public sealed record ReconcilePhaseResult(bool Succeeded, ReconcilePhase Phase, string? FailureReason = null)
{
    public static ReconcilePhaseResult Ok(ReconcilePhase phase) => new(true, phase);

    public static ReconcilePhaseResult Fail(ReconcilePhase phase, string reason)
        => new(false, phase, reason);
}

/// <summary>一轮启动对账（四阶段）的聚合结果。</summary>
public sealed record ReconcileRoundResult(
    bool Succeeded,
    ReconcilePhase? FailedPhase = null,
    string? FailureReason = null,
    Exception? Exception = null)
{
    public static ReconcileRoundResult Ok() => new(true);

    public static ReconcileRoundResult Fail(ReconcilePhase phase, string reason, Exception? exception = null)
        => new(false, phase, reason, exception);
}

/// <summary>
/// 启动对账阶段编排：fail-closed，任一阶段失败立即终止本轮后续阶段。
/// </summary>
public sealed class StartupReconcileCoordinator
{
    /// <summary>将 ①b <c>QueryAsync</c> 应答映射为阶段结果；<c>Success=false</c> 视为失败。</summary>
    public static ReconcilePhaseResult MapQueryResult(bool querySuccess, string? failureReason = null)
    {
        if (querySuccess)
            return ReconcilePhaseResult.Ok(ReconcilePhase.OneB);

        var reason = string.IsNullOrWhiteSpace(failureReason)
            ? "queryTask 失败（Success=false）"
            : failureReason;
        return ReconcilePhaseResult.Fail(ReconcilePhase.OneB, reason);
    }

    /// <summary>仅对账成功时才允许置 <c>IsReconciled=true</c>。</summary>
    public static bool DecideIsReconciled(ReconcileRoundResult round)
        => round.Succeeded;

    public async Task<ReconcileRoundResult> RunAsync(
        Func<CancellationToken, Task<ReconcilePhaseResult>> phaseOne,
        Func<CancellationToken, Task<ReconcilePhaseResult>> phaseOneB,
        Func<CancellationToken, Task<ReconcilePhaseResult>> phaseTwo,
        Func<CancellationToken, Task<ReconcilePhaseResult>> phaseThree,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(phaseOne);
        ArgumentNullException.ThrowIfNull(phaseOneB);
        ArgumentNullException.ThrowIfNull(phaseTwo);
        ArgumentNullException.ThrowIfNull(phaseThree);

        var failed = await RunPhaseAsync(ReconcilePhase.One, phaseOne, ct).ConfigureAwait(false);
        if (failed is not null) return failed;

        failed = await RunPhaseAsync(ReconcilePhase.OneB, phaseOneB, ct).ConfigureAwait(false);
        if (failed is not null) return failed;

        failed = await RunPhaseAsync(ReconcilePhase.Two, phaseTwo, ct).ConfigureAwait(false);
        if (failed is not null) return failed;

        failed = await RunPhaseAsync(ReconcilePhase.Three, phaseThree, ct).ConfigureAwait(false);
        if (failed is not null) return failed;

        return ReconcileRoundResult.Ok();
    }

    private static async Task<ReconcileRoundResult?> RunPhaseAsync(
        ReconcilePhase phase,
        Func<CancellationToken, Task<ReconcilePhaseResult>> phaseFunc,
        CancellationToken ct)
    {
        try
        {
            var result = await phaseFunc(ct).ConfigureAwait(false);
            if (result.Succeeded) return null;

            var reason = string.IsNullOrWhiteSpace(result.FailureReason)
                ? $"对账阶段 {phase} 失败"
                : result.FailureReason;
            return ReconcileRoundResult.Fail(result.Phase, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ReconcileRoundResult.Fail(phase, SanitizeFailureReason(ex), ex);
        }
    }

    /// <summary>供日志/UI 的失败原因：保留异常消息主体，去掉疑似连接串片段。</summary>
    internal static string SanitizeFailureReason(Exception ex)
    {
        var msg = ex.Message ?? ex.GetType().Name;
        if (msg.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Pwd=", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Connection String", StringComparison.OrdinalIgnoreCase))
        {
            return $"{ex.GetType().Name}：对账阶段执行异常（已隐藏敏感连接信息）";
        }
        return msg;
    }
}
