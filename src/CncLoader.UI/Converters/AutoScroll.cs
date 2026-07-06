using System.Windows;
using System.Windows.Controls;

namespace CncLoader.UI.Converters;

/// <summary>
/// 附加行为：ScrollViewer 内容增长时自动滚到底部（用于调用终端等日志追尾）。
/// 用法：<c>&lt;ScrollViewer conv:AutoScroll.ToEnd="True"&gt;</c>
/// </summary>
public static class AutoScroll
{
    public static readonly DependencyProperty ToEndProperty = DependencyProperty.RegisterAttached(
        "ToEnd", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnToEndChanged));

    public static void SetToEnd(DependencyObject o, bool value) => o.SetValue(ToEndProperty, value);
    public static bool GetToEnd(DependencyObject o) => (bool)o.GetValue(ToEndProperty);

    private static void OnToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue)
            sv.ScrollChanged += OnScrollChanged;
        else
            sv.ScrollChanged -= OnScrollChanged;
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 仅在内容变高（新增行）时追尾，用户手动上滚查看历史时不打断。
        if (e.ExtentHeightChange > 0 && sender is ScrollViewer sv)
            sv.ScrollToVerticalOffset(sv.ExtentHeight);
    }
}
