namespace CncLoader.Core.Rcs;

/// <summary>
/// 料架水位监视器（第四阶段⑥c，§6.2 v2.2）：周期检查料架占用，满/空阈值触发自动换架。
/// 触发条件由槽位账计数产生：下料/中转/NG 架接近满（剩余空槽 ≤ WaterFullThreshold）→ 呼叫 AGV 拉走满架；
/// 上料架取空（空槽 = total）→ 呼叫 AGV 拉走空架。换架/回收 priority ≥ 紧急下料（架满会堵死下料）。
/// 自动触发与槽位账接入调度器相关——⑥c 提供服务+周期检查+自动调 IChangeFrameOrchestrator，演示可手动改槽位触发。
/// </summary>
public interface IWaterMonitorService
{
    /// <summary>水位事件（接近满/已空/触发换架）。</summary>
    event EventHandler<WaterLevelEvent>? WaterLevelChanged;

    /// <summary>立即执行一次检查（不等周期）。返回本次触发的事件列表。</summary>
    Task<IReadOnlyList<WaterLevelEvent>> CheckAsync(CancellationToken ct = default);
}

/// <summary>水位事件。</summary>
public sealed record WaterLevelEvent(
    long EquipmentId,
    long FrameId,
    FrameRole Role,
    string Level,    // FULL / EMPTY
    int Total,
    int Occupied,
    int Empty,
    string? TriggeredTxnId);
