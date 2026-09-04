namespace CncLoader.Core.State;

/// <summary>HasMat 完成复核的三态读取转换。</summary>
public static class HasMatReading
{
    /// <summary>
    /// 与 <see cref="CncLoader.Core.Signals.SignalConventions.Interpret"/> 对齐：
    /// 有通信错误 → 未知；On → 有料；Off → 无料；其余寄存器值 → 未知（不得当无料）。
    /// </summary>
    public static bool? From(int rawValue, int onValue, int offValue, string? error)
    {
        if (error is not null) return null;
        if (rawValue == onValue) return true;
        if (rawValue == offValue) return false;
        return null;
    }
}

public enum HasMatRecheckDecision
{
    Hold,
    Confirmed,
    Alarm
}

/// <summary>单次 HasMat 完成复核的状态机决策。</summary>
public readonly record struct HasMatRecheckResult(
    HasMatRecheckDecision Decision,
    PositionState NextState,
    int FailureCount,
    string? StatusDetail,
    bool AlarmRaisedNow)
{
    public bool PermitsTestStart => Decision == HasMatRecheckDecision.Confirmed;
    public bool PermitsSlotSettlement => Decision == HasMatRecheckDecision.Confirmed;
}

/// <summary>
/// 按当前任务和运输阶段跟踪 HasMat 连续未知次数；一个实例只供一个加工位使用。
/// </summary>
public sealed class HasMatRecheckTracker
{
    private string? _taskId;
    private PositionPhase? _phase;
    private int _failureCount;
    private bool _alarmed;

    public HasMatRecheckResult Evaluate(
        string? taskId,
        PositionPhase phase,
        bool? freshHasMat,
        int threshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);

        if (!string.Equals(_taskId, taskId, StringComparison.Ordinal) || _phase != phase)
        {
            Reset();
            _taskId = taskId;
            _phase = phase;
        }

        if (_alarmed)
            return Alarm(alarmRaisedNow: false);

        if (freshHasMat is null)
        {
            _failureCount++;
            if (_failureCount >= threshold)
            {
                _alarmed = true;
                return Alarm(alarmRaisedNow: true);
            }

            return new HasMatRecheckResult(
                HasMatRecheckDecision.Hold,
                PositionState.Transporting,
                _failureCount,
                $"复核中（{_failureCount}/{threshold}）",
                AlarmRaisedNow: false);
        }

        _failureCount = 0;
        var matches = phase == PositionPhase.Upload ? freshHasMat.Value : !freshHasMat.Value;
        if (!matches)
        {
            _alarmed = true;
            return Alarm(alarmRaisedNow: true);
        }

        return new HasMatRecheckResult(
            HasMatRecheckDecision.Confirmed,
            phase == PositionPhase.Upload ? PositionState.Loaded : PositionState.Unloaded,
            FailureCount: 0,
            StatusDetail: null,
            AlarmRaisedNow: false);
    }

    public void Reset()
    {
        _taskId = null;
        _phase = null;
        _failureCount = 0;
        _alarmed = false;
    }

    private HasMatRecheckResult Alarm(bool alarmRaisedNow) => new(
        HasMatRecheckDecision.Alarm,
        PositionState.Alarm,
        _failureCount,
        StatusDetail: null,
        alarmRaisedNow);
}
