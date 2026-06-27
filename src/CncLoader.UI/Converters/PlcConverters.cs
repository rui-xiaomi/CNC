using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CncLoader.UI.Converters;

/// <summary>资源键名 → SolidColorBrush（用于 PLC 状态灯）。</summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Application.Current is null) return Brushes.Gray;
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToOnOffConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "轮询中" : "单次读";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SemanticColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool isOn || Application.Current is null) return Brushes.Black;
        var key = isOn ? "OkBrush" : "FgMutedBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Black;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
