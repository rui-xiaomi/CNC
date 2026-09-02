namespace CncLoader.Core.Rcs;

/// <summary>重发前是否需要（再）锁槽。</summary>
public enum ReplayReservationPlan
{
    /// <summary>命名区 / 换架 / 抓取 / 盘点：无槽位账。</summary>
    Skip,
    Take,
    Put
}

/// <summary>重发预记结果：Created 表示本轮新锁，下发失败须回滚；已有预记不回滚。</summary>
public sealed record ReplayReservationHold(bool Ok, bool Created, bool IsTake, RcsResult? Failure)
{
    public static ReplayReservationHold Skipped() => new(true, false, false, null);
    public static ReplayReservationHold AlreadyHeld(bool isTake) => new(true, false, isTake, null);
    public static ReplayReservationHold NewlyCreated(bool isTake) => new(true, true, isTake, null);
    public static ReplayReservationHold Reject(string message) =>
        new(false, false, false, RcsResult.Fail("", message));
}

/// <summary>
/// Redo / Redispatch / AutoRedo：预记已回滚则先再预记再下发；已有预记幂等跳过。
/// 上料优先按物料码锁回原件，找不到该物料不得改抢其它槽。
/// </summary>
public static class ReplayReservation
{
    public static ReplayReservationPlan Decide(
        string? kind,
        string? taskType,
        ManagedEndpointKind? fromKind,
        ManagedEndpointKind? toKind)
    {
        if (!IsTransitKind(kind))
            return ReplayReservationPlan.Skip;

        if (taskType == "0" && fromKind == ManagedEndpointKind.Frame)
            return ReplayReservationPlan.Take;
        if (taskType == "1" && toKind == ManagedEndpointKind.Frame)
            return ReplayReservationPlan.Put;
        return ReplayReservationPlan.Skip;
    }

    public static async Task<ReplayReservationHold> EnsureAsync(
        ISlotAccountService slots,
        string taskId,
        ReplayReservationPlan plan,
        long frameId,
        string? materialId,
        CancellationToken ct = default)
    {
        if (plan == ReplayReservationPlan.Skip)
            return ReplayReservationHold.Skipped();
        if (string.IsNullOrWhiteSpace(taskId) || frameId <= 0)
            return ReplayReservationHold.Reject("槽位预记失败，拒绝重发");

        var existing = await slots.FindReservedAsync(taskId, ct);
        if (existing is not null)
            return ReplayReservationHold.AlreadyHeld(plan == ReplayReservationPlan.Take);

        ReservedSlot? reserved = plan switch
        {
            ReplayReservationPlan.Take when !string.IsNullOrWhiteSpace(materialId) =>
                await slots.ReserveTakeByMaterialAsync(frameId, taskId, materialId, ct),
            ReplayReservationPlan.Take =>
                await slots.ReserveTakeAsync(frameId, taskId, ct),
            ReplayReservationPlan.Put =>
                await slots.ReserveAsync(frameId, taskId, materialId, ct),
            _ => null
        };

        if (reserved is null)
            return ReplayReservationHold.Reject("槽位预记失败，拒绝重发");
        return ReplayReservationHold.NewlyCreated(plan == ReplayReservationPlan.Take);
    }

    private static bool IsTransitKind(string? kind)
        => kind is null or "" or "transit" or "TR";
}
