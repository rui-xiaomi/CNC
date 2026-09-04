namespace CncLoader.Core.State;

/// <summary>启动对账状态快照（供事件通知与看板展示）。</summary>
public sealed record ReconciliationSnapshot(
    ReconciliationState State,
    string? FailureReason,
    bool IsReconciled);

/// <summary>
/// 对账状态变化发布：仅在状态或失败原因变化时通知；相同快照不重复触发。
/// </summary>
public sealed class ReconciliationStatePublisher
{
    private readonly object _gate = new();
    private ReconciliationSnapshot _current = new(ReconciliationState.NotStarted, null, false);

    public ReconciliationSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<ReconciliationSnapshot>? Changed;

    /// <summary>尝试发布；未变化返回 false 且不触发事件。</summary>
    public bool TryPublish(ReconciliationState state, string? failureReason, bool isReconciled)
    {
        var next = new ReconciliationSnapshot(state, failureReason, isReconciled);
        lock (_gate)
        {
            if (_current == next) return false;
            _current = next;
        }
        Raise(next);
        return true;
    }

    private void Raise(ReconciliationSnapshot snap)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var d in handlers.GetInvocationList())
        {
            try { ((EventHandler<ReconciliationSnapshot>)d).Invoke(this, snap); }
            catch
            {
                // 订阅方异常不得破坏调度主流程；具体日志由宿主记录。
            }
        }
    }
}

/// <summary>看板文案/语义映射（纯逻辑，不进 XAML Converter）。</summary>
public static class ReconciliationStatusPresentation
{
    public static (string Title, string SubText, string BrushKey, string SoftBrushKey, string? DetailToolTip, bool IsGateOpen)
        Map(ReconciliationSnapshot snap)
    {
        var reason = SanitizeDisplayReason(snap.FailureReason);
        return snap.State switch
        {
            ReconciliationState.NotStarted => (
                "启动对账未开始",
                "自动派工尚未开启",
                "IdleBrush",
                "SoftIdleBrush",
                null,
                false),
            ReconciliationState.Reconciling => (
                "正在执行启动对账",
                "自动派工已锁定",
                "RunBrush",
                "SoftRunBrush",
                null,
                false),
            ReconciliationState.WaitingForRetry => (
                "启动对账失败，正在重试",
                string.IsNullOrEmpty(reason)
                    ? "自动派工已锁定；系统将自动重试"
                    : $"自动派工已锁定：{reason}；系统将自动重试",
                "WarnBrush",
                "SoftWarnBrush",
                string.IsNullOrEmpty(reason) ? "自动派工已锁定；系统将自动重试" : reason,
                false),
            ReconciliationState.Succeeded => (
                "启动对账完成",
                "自动派工已开启",
                "OkBrush",
                "SoftOkBrush",
                null,
                true),
            ReconciliationState.Disabled => (
                "调度器未启用",
                "自动派工未开启（SchedulerEnabled=false）",
                "IdleBrush",
                "SoftIdleBrush",
                "调度器关闭时不对账、不开闸；手工 RCS / PLC 不受影响",
                false),
            _ => (
                "启动对账未开始",
                "自动派工尚未开启",
                "IdleBrush",
                "SoftIdleBrush",
                null,
                snap.IsReconciled)
        };
    }

    /// <summary>去掉异常类型名前缀与疑似连接串，避免看板泄露内部细节。</summary>
    public static string SanitizeDisplayReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "";
        var s = reason.Trim();
        if (s.Contains("Password=", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Pwd=", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Connection String", StringComparison.OrdinalIgnoreCase))
        {
            return "对账阶段执行异常（已隐藏敏感信息）";
        }

        // 去掉 "InvalidOperationException：" / "System.XxxException：" 前缀
        var idx = s.IndexOf("Exception", StringComparison.Ordinal);
        if (idx >= 0)
        {
            var colon = s.IndexOfAny([':', '：'], idx);
            if (colon > idx && colon + 1 < s.Length)
                s = s[(colon + 1)..].Trim();
        }
        return s;
    }
}
