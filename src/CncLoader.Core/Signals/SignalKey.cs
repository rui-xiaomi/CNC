namespace CncLoader.Core.Signals;

/// <summary>
/// 标准信号语义枚举，对应 MAS_AUTO_PLC_POINT.SIGNAL_KEY。
/// 机台级信号（DOOR / MACHINE_SAFE）不带加工位；其余为工位级。
/// </summary>
public enum SignalKey
{
    /// <summary>门开关（机台级，读）。</summary>
    Door,
    /// <summary>机台安全（机台级，读）。</summary>
    MachineSafe,
    /// <summary>工位有料（读）。</summary>
    PosHasMat,
    /// <summary>工位允许上料（读）。</summary>
    PosAllowLoad,
    /// <summary>工位检测 OK（读）。</summary>
    PosOk,
    /// <summary>工位检测 NG（读）。</summary>
    PosNg,
    /// <summary>工位测试启动（写）。</summary>
    PosTestStart
}

/// <summary>SignalKey 与数据库字符串键的双向映射。</summary>
public static class SignalKeys
{
    public const string Door = "DOOR";
    public const string MachineSafe = "MACHINE_SAFE";
    public const string PosHasMat = "POS_HAS_MAT";
    public const string PosAllowLoad = "POS_ALLOW_LOAD";
    public const string PosOk = "POS_OK";
    public const string PosNg = "POS_NG";
    public const string PosTestStart = "POS_TEST_START";

    public static string ToDbKey(SignalKey key) => key switch
    {
        SignalKey.Door => Door,
        SignalKey.MachineSafe => MachineSafe,
        SignalKey.PosHasMat => PosHasMat,
        SignalKey.PosAllowLoad => PosAllowLoad,
        SignalKey.PosOk => PosOk,
        SignalKey.PosNg => PosNg,
        SignalKey.PosTestStart => PosTestStart,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    public static bool TryParse(string dbKey, out SignalKey key)
    {
        switch (dbKey?.Trim().ToUpperInvariant())
        {
            case Door: key = SignalKey.Door; return true;
            case MachineSafe: key = SignalKey.MachineSafe; return true;
            case PosHasMat: key = SignalKey.PosHasMat; return true;
            case PosAllowLoad: key = SignalKey.PosAllowLoad; return true;
            case PosOk: key = SignalKey.PosOk; return true;
            case PosNg: key = SignalKey.PosNg; return true;
            case PosTestStart: key = SignalKey.PosTestStart; return true;
            default: key = default; return false;
        }
    }

    /// <summary>是否为机台级信号（不绑定加工位）。</summary>
    public static bool IsMachineLevel(SignalKey key) => key is SignalKey.Door or SignalKey.MachineSafe;
}
