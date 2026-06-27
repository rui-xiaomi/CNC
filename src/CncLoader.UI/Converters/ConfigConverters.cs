using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

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
