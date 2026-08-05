namespace CncLoader.Core.Rcs;

/// <summary>自动重派原子 Claim 结果（门禁通过后抢占发送权）。</summary>
public enum AutoRedoClaimResult
{
    /// <summary>已原子占用：RedoCount+1、STATE→DISPATCHED、Error 已清。</summary>
    Claimed = 0,
    /// <summary>已达 MaxAutoRedo，不得发送。</summary>
    LimitReached,
    /// <summary>状态已变化 / 已被其他 AutoRedo claim / 非候选态。</summary>
    NotClaimable,
    /// <summary>任务行不存在。</summary>
    NotFound
}

/// <summary>
/// AutoRedo Claim 规则：候选态与 Tracker FAILED 触发对齐；未知态 fail-closed。
/// </summary>
public static class AutoRedoClaimRules
{
    /// <summary>允许 Claim 的任务态（当前仅 FAILED）。</summary>
    public static readonly string[] ClaimableStates = { RcsTaskState.Failed };

    public static bool IsClaimableState(string? taskState)
        => taskState == RcsTaskState.Failed;

    /// <summary>在条件更新未命中后，根据当前行区分失败原因（仅诊断，不再次修改）。</summary>
    public static AutoRedoClaimResult ClassifyMiss(bool exists, string? taskState, int redoCount, int maxRedo)
    {
        if (!exists) return AutoRedoClaimResult.NotFound;
        if (redoCount >= maxRedo) return AutoRedoClaimResult.LimitReached;
        if (!IsClaimableState(taskState)) return AutoRedoClaimResult.NotClaimable;
        // 行看似可 claim 但条件更新未命中：视为并发竞争 fail-closed。
        return AutoRedoClaimResult.NotClaimable;
    }
}
