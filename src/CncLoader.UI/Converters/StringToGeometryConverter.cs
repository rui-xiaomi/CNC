using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CncLoader.UI.Converters;

/// <summary>将 Path 几何字符串转为 Geometry（导航图标用）。</summary>
public sealed class StringToGeometryConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Geometry.Parse(s) : Geometry.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
