using CncLoader.Core.Config;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data.Repositories;

// Phase 3 配置管理共享辅助：软删语义与事务纪律。公开 Service 各占一文件。
// 全部经 IDbContextFactory 短连接，State=="0" 视为启用/有效。

internal static class ConfigFlags
{
    // 软删语义的唯一定义在 Core 的 ConfigActivity；此处只做转发，避免"0"/"1"在两处各写一遍。
    public const string Active = ConfigActivity.Active;
    public const string Disabled = ConfigActivity.Disabled;
    public static bool IsEnabled(string state) => ConfigActivity.IsActive(state);
    public static string ToState(bool enabled) => enabled ? Active : Disabled;
    public static bool IsTrue(string? flag) => flag == "1";
    public static string ToFlag(bool value) => value ? "1" : "0";

    /// <summary>FRAME_ROLE "0"/"1"/"2"/"3" → 中文角色名（列表展示用）。</summary>
    public static string FrameRoleText(string roleCode) => roleCode switch
    {
        "0" => "上料架",
        "1" => "下料架",
        "2" => "中转架",
        "3" => "NG架",
        _ => "未知"
    };
}

/// <summary>
/// 配置软删的事务纪律：引用校验、软删、级联软删必须在同一 context + 同一事务内完成。
/// 拆成两次连接时，校验通过后引用可能刚被新增（TOCTOU），级联也可能只删一半。
/// 新增配置实体的删除一律走这里，别再各写一遍 BeginTransaction/Commit。
/// </summary>
internal static class ConfigSoftDelete
{
    public static async Task RunAsync(
        IDbContextFactory<CncDbContext> factory,
        Func<CncDbContext, CancellationToken, Task> checkThenMarkDeleted,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await checkThenMarkDeleted(db, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }
}
