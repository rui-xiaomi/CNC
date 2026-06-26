namespace CncLoader.Core.Signals;

/// <summary>
/// 信号数值约定（来自《测试机信号表》）：读 ON=1 / OFF=2；写 启动=1 / 关闭=2。
/// </summary>
public static class SignalConventions
{
    public const int DefaultOnValue = 1;
    public const int DefaultOffValue = 2;

    public const int StartValue = 1;
    public const int StopValue = 2;

    /// <summary>按点位约定将原始寄存器值翻译为布尔（ON=true）。非 On/Off 值返回 null。</summary>
    public static bool? Interpret(int rawValue, PlcPointDefinition point)
    {
        if (rawValue == point.OnValue) return true;
        if (rawValue == point.OffValue) return false;
        return null;
    }
}

/// <summary>解析寄存器地址，如 "D1006" → 区号 'D' + 偏移 1006。</summary>
public static class RegisterAddress
{
    public static (char Area, int Offset) Parse(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("寄存器地址为空", nameof(address));

        var trimmed = address.Trim().ToUpperInvariant();
        var area = char.IsLetter(trimmed[0]) ? trimmed[0] : 'D';
        var digits = char.IsLetter(trimmed[0]) ? trimmed[1..] : trimmed;
        if (!int.TryParse(digits, out var offset))
            throw new FormatException($"无法解析寄存器地址：{address}");
        return (area, offset);
    }

    /// <summary>取数值偏移（保持寄存器号），用于 Modbus 读写。</summary>
    public static int ToRegisterIndex(string address) => Parse(address).Offset;
}
