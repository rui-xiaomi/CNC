using CncLoader.Core.Signals;

namespace CncLoader.Core.Plc;

/// <summary>信号语义中文标签。</summary>
public static class SignalLabels
{
    public static string Get(SignalKey key) => key switch
    {
        SignalKey.Door => "门开关",
        SignalKey.MachineSafe => "机台安全",
        SignalKey.PosHasMat => "有料",
        SignalKey.PosAllowLoad => "允许上料",
        SignalKey.PosOk => "检测 OK",
        SignalKey.PosNg => "检测 NG",
        SignalKey.PosTestStart => "测试启动",
        _ => key.ToString()
    };

    public static string Get(string dbKey) =>
        SignalKeys.TryParse(dbKey, out var k) ? Get(k) : dbKey;

    /// <summary>将原始值翻译为语义文案。</summary>
    public static string Translate(SignalKey key, int raw, int onValue, int offValue)
    {
        if (raw == onValue) return key switch
        {
            SignalKey.Door => "开门 ON",
            SignalKey.MachineSafe => "安全 ON",
            SignalKey.PosHasMat => "有料 ON",
            SignalKey.PosAllowLoad => "允许 ON",
            SignalKey.PosOk => "OK ON",
            SignalKey.PosNg => "NG ON",
            SignalKey.PosTestStart => "启动 ON",
            _ => $"ON ({raw})"
        };
        if (raw == offValue) return key switch
        {
            SignalKey.Door => "闭合 OFF",
            SignalKey.MachineSafe => "不安全 OFF",
            SignalKey.PosHasMat => "无料 OFF",
            SignalKey.PosAllowLoad => "OFF",
            SignalKey.PosOk => "OFF",
            SignalKey.PosNg => "OFF",
            SignalKey.PosTestStart => "关闭 OFF",
            _ => $"OFF ({raw})"
        };
        return $"未知 ({raw})";
    }
}

/// <summary>测试机信号表模板（按机台类型 D 段基址）。</summary>
public static class SignalTableTemplates
{
    public sealed record TemplatePoint(
        SignalKey Signal,
        bool IsWrite,
        int RegisterOffset,
        string IoAddress,
        long? PositionSlot);

    /// <summary>按机台序号 1/2/3 返回信号表模板（内长宽/平面度/A基准）。</summary>
    public static IReadOnlyList<TemplatePoint> ForEquipmentIndex(int equipmentIndex) => equipmentIndex switch
    {
        1 => InnerLengthWidth,
        2 => Flatness,
        3 => ABase,
        _ => throw new ArgumentOutOfRangeException(nameof(equipmentIndex), "仅支持机台序号 1–3（对应信号表三台测试机）")
    };

    // 内长宽 D1000–D1102
    private static readonly TemplatePoint[] InnerLengthWidth =
    [
        new(SignalKey.Door, false, 1000, "I0.0", null),
        new(SignalKey.MachineSafe, false, 1002, "I0.1", null),
        new(SignalKey.PosHasMat, false, 1004, "I0.2", 1),
        new(SignalKey.PosAllowLoad, false, 1006, "I0.3", 1),
        new(SignalKey.PosOk, false, 1008, "I0.4", 1),
        new(SignalKey.PosNg, false, 1010, "I0.5", 1),
        new(SignalKey.PosHasMat, false, 1012, "I0.6", 2),
        new(SignalKey.PosAllowLoad, false, 1014, "I0.7", 2),
        new(SignalKey.PosOk, false, 1016, "I1.0", 2),
        new(SignalKey.PosNg, false, 1018, "I1.1", 2),
        new(SignalKey.PosTestStart, true, 1100, "Q0.0", 1),
        new(SignalKey.PosTestStart, true, 1102, "Q0.1", 2),
    ];

    // 平面度 D1200–D1302
    private static readonly TemplatePoint[] Flatness =
    [
        new(SignalKey.Door, false, 1200, "I2.4", null),
        new(SignalKey.MachineSafe, false, 1202, "I2.5", null),
        new(SignalKey.PosHasMat, false, 1204, "I2.6", 1),
        new(SignalKey.PosAllowLoad, false, 1206, "I2.7", 1),
        new(SignalKey.PosOk, false, 1208, "I3.0", 1),
        new(SignalKey.PosNg, false, 1210, "I3.1", 1),
        new(SignalKey.PosHasMat, false, 1212, "I3.2", 2),
        new(SignalKey.PosAllowLoad, false, 1214, "I3.3", 2),
        new(SignalKey.PosOk, false, 1216, "I3.4", 2),
        new(SignalKey.PosNg, false, 1218, "I3.5", 2),
        new(SignalKey.PosTestStart, true, 1300, "Q0.4", 1),
        new(SignalKey.PosTestStart, true, 1302, "Q0.5", 2),
    ];

    // A基准 D1400–D1502
    private static readonly TemplatePoint[] ABase =
    [
        new(SignalKey.Door, false, 1400, "I1.2", null),
        new(SignalKey.MachineSafe, false, 1402, "I1.3", null),
        new(SignalKey.PosHasMat, false, 1404, "I1.4", 1),
        new(SignalKey.PosAllowLoad, false, 1406, "I1.5", 1),
        new(SignalKey.PosOk, false, 1408, "I1.6", 1),
        new(SignalKey.PosNg, false, 1410, "I1.7", 1),
        new(SignalKey.PosHasMat, false, 1412, "I2.0", 2),
        new(SignalKey.PosAllowLoad, false, 1414, "I2.1", 2),
        new(SignalKey.PosOk, false, 1416, "I2.2", 2),
        new(SignalKey.PosNg, false, 1418, "I2.3", 2),
        new(SignalKey.PosTestStart, true, 1500, "Q0.2", 1),
        new(SignalKey.PosTestStart, true, 1502, "Q0.3", 2),
    ];
}
