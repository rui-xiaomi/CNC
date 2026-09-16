using CncLoader.Core.Rcs;

namespace CncLoader.Core.State;

/// <summary>
/// 人工「置空释放」对预记槽的放行条件。自动结算仍走 <see cref="SlotSettlement"/>（HasMat 未知 Hold）；
/// 本策略只给操作员在终态/无主预记上收口，不得放开在途任务。
/// </summary>
public static class ManualSlotClearPolicy
{
    /// <summary>
    /// 预记可否被置空释放覆盖：REMARK 空（异常预记），或绑定任务已 COMPLETED。
    /// 无任务行视为在途（预记先于落库，ADR-0002）；FAILED/CANCELED 走回滚/AutoRedo，不强制置空。
    /// </summary>
    public static bool AllowsForceClearReserved(string? taskId, string? taskState)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return true;
        return taskState == RcsTaskState.Completed;
    }
}
