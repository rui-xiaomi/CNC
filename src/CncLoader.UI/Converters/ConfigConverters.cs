using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CncLoader.Core.Config;

namespace CncLoader.UI.Converters;

/// <summary>bool → 「是」/「否」（扫码枪、自动发送等列）。</summary>
public sealed class BoolToYesNoConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? "是" : "否";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>bool → 「启用」/「禁用」（状态徽标）。</summary>
public sealed class BoolToEnabledTextConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? "启用" : "禁用";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>启用状态 → 状态灯颜色（启用 OkBrush / 禁用 IdleBrush）。</summary>
public sealed class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value is true ? "OkBrush" : "IdleBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>料架角色 → 颜色（上料架 AccentBrush / 下料架 WarnBrush）。</summary>
public sealed class UploadRoleBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value is true ? "AccentBrush" : "WarnBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>bool 取反（用于 IsEnabled 绑定 IsTesting 等）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public static readonly InverseBoolConverter Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>WorkLineListItem → 「名称 (编码)」显示文本。</summary>
public sealed class WorkLineDisplayConverter : IValueConverter
{
    public static readonly WorkLineDisplayConverter Instance = new();
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is WorkLineListItem item)
            return $"{item.Name} ({item.Code})";
        return "未选择线体";
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
