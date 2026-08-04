namespace CncLoader.Core.Rcs;

public enum ReservationFirstDispatchStatus
{
    ReservationFailed,
    /// <summary>预记成功后权威路由校验失败；已尝试回滚，未调用 RCS。</summary>
    RouteUnavailable,
    DispatchFailed,
    Dispatched
}

/// <summary>一次“先预记、后下发”的补偿式派工结果。</summary>
public sealed record ReservationFirstDispatchResult<TReservation>
    where TReservation : class
{
    public required ReservationFirstDispatchStatus Status { get; init; }
    public TReservation? Reservation { get; init; }
    public RcsResult? DispatchResult { get; init; }
    public Exception? Exception { get; init; }
    public bool RollbackSucceeded { get; init; }
    public RoutingAvailabilityResult? RouteResult { get; init; }
}

/// <summary>
/// 统一执行「原子预记 → 最终路由门禁 → RCS 下发」；
/// 预记失败不触碰 RCS；最终门禁失败走既有补偿回滚且不下发；下发失败同样回滚。
/// </summary>
public sealed class ReservationFirstDispatcher
{
    public async Task<ReservationFirstDispatchResult<TReservation>> ExecuteAsync<TReservation>(
        string taskId,
        Func<string, CancellationToken, Task<TReservation?>> reserveAsync,
        Func<string, CancellationToken, Task<RoutingAvailabilityResult>> validateFinalAsync,
        Func<string, CancellationToken, Task<RcsResult>> dispatchAsync,
        Func<string, CancellationToken, Task<bool>> rollbackAsync,
        CancellationToken ct = default)
        where TReservation : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(validateFinalAsync);

        TReservation? reservation;
        try { reservation = await reserveAsync(taskId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new ReservationFirstDispatchResult<TReservation>
            {
                Status = ReservationFirstDispatchStatus.ReservationFailed,
                Exception = ex
            };
        }
        if (reservation is null)
        {
            return new ReservationFirstDispatchResult<TReservation>
            {
                Status = ReservationFirstDispatchStatus.ReservationFailed
            };
        }

        try
        {
            RoutingAvailabilityResult route;
            try { route = await validateFinalAsync(taskId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await TryRollbackAsync(taskId, rollbackAsync, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                return new ReservationFirstDispatchResult<TReservation>
                {
                    Status = ReservationFirstDispatchStatus.RouteUnavailable,
                    Reservation = reservation,
                    Exception = ex,
                    RouteResult = RoutingAvailabilityResult.Unavailable(
                        RoutingUnavailableReason.ConfigurationUnavailable,
                        "Configuration", null, "最终路由校验异常，拒绝下发"),
                    RollbackSucceeded = await TryRollbackAsync(taskId, rollbackAsync, ct)
                };
            }

            if (!route.IsAvailable)
            {
                return new ReservationFirstDispatchResult<TReservation>
                {
                    Status = ReservationFirstDispatchStatus.RouteUnavailable,
                    Reservation = reservation,
                    RouteResult = route,
                    RollbackSucceeded = await TryRollbackAsync(taskId, rollbackAsync, ct)
                };
            }

            var dispatch = await dispatchAsync(taskId, ct);
            if (dispatch.Success && string.Equals(dispatch.TaskId, taskId, StringComparison.Ordinal))
            {
                return new ReservationFirstDispatchResult<TReservation>
                {
                    Status = ReservationFirstDispatchStatus.Dispatched,
                    Reservation = reservation,
                    DispatchResult = dispatch,
                    RouteResult = route
                };
            }

            return new ReservationFirstDispatchResult<TReservation>
            {
                Status = ReservationFirstDispatchStatus.DispatchFailed,
                Reservation = reservation,
                DispatchResult = dispatch,
                RouteResult = route,
                RollbackSucceeded = await TryRollbackAsync(taskId, rollbackAsync, ct)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryRollbackAsync(taskId, rollbackAsync, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            return new ReservationFirstDispatchResult<TReservation>
            {
                Status = ReservationFirstDispatchStatus.DispatchFailed,
                Reservation = reservation,
                Exception = ex,
                RollbackSucceeded = await TryRollbackAsync(taskId, rollbackAsync, ct)
            };
        }
    }

    private static async Task<bool> TryRollbackAsync(
        string taskId,
        Func<string, CancellationToken, Task<bool>> rollbackAsync,
        CancellationToken ct)
    {
        try { return await rollbackAsync(taskId, ct); }
        catch { return false; }
    }
}
