using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;

namespace CncLoader.UI.Converters;

/// <summary>bool → 「是」/「否」（扫码枪、自动发送等列）。</summary>
public sealed class BoolToYesNoConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is true ? "是" : "否";
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>RCS 任务种类英文码 → 中文（transit→搬运 等）。</summary>
public sealed class RcsKindToZhConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        RcsDisplayLabels.KindToZh(value as string);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>RCS 任务态英文码 → 中文（COMPLETED→已完成 等）。</summary>
public sealed class RcsStateToZhConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        RcsDisplayLabels.StateToZh(value as string);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>OUT/IN → 出站/入站。</summary>
public sealed class RcsDirectionToZhConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        RcsDisplayLabels.DirectionToZh(value as string);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>transitTask → 搬运下发 等。</summary>
public sealed class RcsInterfaceToZhConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        RcsDisplayLabels.InterfaceToZh(value as string);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>bool Success → 成功/失败。</summary>
public sealed class RcsResultToZhConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        RcsDisplayLabels.ResultToZh(value is true);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>耗时 ms →「12 ms」/「—」。</summary>
public sealed class RcsCostToZhConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        RcsDisplayLabels.CostToZh(value as int? ?? (value is int i ? i : null));
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>JSON 字符串缩进格式化（详情区用）。</summary>
public sealed class PrettyJsonConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        RcsDisplayLabels.FormatJson(value as string);
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>FrameRole → 上料架/下料架…</summary>
public sealed class FrameRoleToZhConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is FrameRole r ? RcsDisplayLabels.FrameRoleToZh(r) : (value?.ToString() ?? "");
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>ChangeFrameStep → 拉旧架/送新架…</summary>
public sealed class ChangeFrameStepToZhConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is ChangeFrameStep s ? RcsDisplayLabels.ChangeFrameStepToZh(s) : (value?.ToString() ?? "");
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>取消任务的人工处理标记：仅 CANCELED 任务有意义。
/// values[0]=TaskState，values[1]=CancelManualFlag（0=待处理，1=已处理）；非取消任务显示空。</summary>
public sealed class CancelFlagConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c)
    {
        var state = values.Length > 0 ? values[0] as string : null;
        var flag = values.Length > 1 ? values[1] as string : null;
        if (state != "CANCELED") return "";
        return flag is "0" or "0 " ? "待处理" : (flag is "1" ? "已处理" : "");
    }
    public object[] ConvertBack(object? value, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>加工位状态徽标 → 状态色 Brush（对齐原型 token）。
/// offline→FgMuted / alarm→Alarm / run→Run / ok→Ok / ng→Ng(#F97316) / warn→Warn / idle→Idle。</summary>
public sealed class StateBadgeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value as string ?? "idle";
        var name = key switch
        {
            "offline" => "FgMutedBrush",
            "alarm" => "AlarmBrush",
            "warn" => "WarnBrush",
            "run" => "RunBrush",
            "ok" => "OkBrush",
            "ng" => "NgBrush",
            _ => "IdleBrush"
        };
        return System.Windows.Application.Current?.TryFindResource(name) ?? System.Windows.Media.Brushes.Gray;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>状态徽标 → soft tint（对齐原型 color-mix：status 18%/14%/22% + Surface）。</summary>
public sealed class StateBadgeToSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value as string ?? "idle";
        var name = key switch
        {
            "offline" => "SoftOfflineBrush",
            "alarm" => "SoftAlarmBrush",
            "warn" => "SoftWarnBrush",
            "run" => "SoftRunBrush",
            "ok" => "SoftOkBrush",
            "ng" => "SoftNgBrush",
            _ => "SoftIdleBrush"
        };
        return System.Windows.Application.Current?.TryFindResource(name)
               ?? System.Windows.Application.Current?.TryFindResource("Surface2Brush")
               ?? System.Windows.Media.Brushes.DimGray;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>状态徽标 → 14×14 矢量 Geometry（监控看板状态图标；非 emoji）。</summary>
public sealed class StateBadgeToIconConverter : IValueConverter
{
    // EvenOdd 空心圆：外圆 − 内圆
    private static readonly Geometry IdleRing = Geometry.Parse(
        "M7,1.2 A5.8,5.8 0 1 1 6.99,1.2 M7,3.4 A3.6,3.6 0 1 1 6.99,3.4");
    private static readonly Geometry OfflineRing = Geometry.Parse(
        "M7,1.5 A5.5,5.5 0 1 1 6.99,1.5 M7,3.8 A3.2,3.2 0 1 1 6.99,3.8");
    private static readonly Geometry OkCheck = Geometry.Parse(
        "M2.2,7.2 L5.5,10.5 L11.8,3.5 L10.4,2.2 L5.5,7.8 L3.5,5.9 Z");
    private static readonly Geometry RunTriangle = Geometry.Parse("M2.5,2 L12,7 L2.5,12 Z");
    private static readonly Geometry WarnTriangle = Geometry.Parse(
        "M7,1.5 L13,12.5 H1 Z M6.3,5.2 H7.7 V8.2 H6.3 Z M6.3,9.2 H7.7 V10.6 H6.3 Z");
    private static readonly Geometry AlarmBang = Geometry.Parse(
        "M7,1.2 L13.2,12.8 H0.8 Z M6.2,5 H7.8 V8.2 H6.2 Z M6.2,9.2 H7.8 V10.8 H6.2 Z");
    private static readonly Geometry NgSquare = Geometry.Parse("M3,3 H11 V11 H3 Z");

    static StateBadgeToIconConverter()
    {
        IdleRing.Freeze();
        OfflineRing.Freeze();
        OkCheck.Freeze();
        RunTriangle.Freeze();
        WarnTriangle.Freeze();
        AlarmBang.Freeze();
        NgSquare.Freeze();
    }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value as string ?? "idle";
        return key switch
        {
            "ok" => OkCheck,
            "run" => RunTriangle,
            "warn" => WarnTriangle,
            "alarm" => AlarmBang,
            "ng" => NgSquare,
            "offline" => OfflineRing,
            _ => IdleRing
        };
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>看板状态色：idle 用更亮的 DashIdle，避免灰蒙。</summary>
public sealed class DashStateBadgeToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value as string ?? "idle";
        var name = key switch
        {
            "offline" => "FgMutedBrush",
            "alarm" => "DashAlarmBrush",
            "warn" => "WarnBrush",
            "run" => "RunBrush",
            "ok" => "OkBrush",
            "ng" => "NgBrush",
            _ => "DashIdleBrush"
        };
        return System.Windows.Application.Current?.TryFindResource(name) ?? System.Windows.Media.Brushes.Gray;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>看板 soft tint：idle/offline 用冷灰蓝，拉开与卡片面对比。</summary>
public sealed class DashStateBadgeToSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value as string ?? "idle";
        var name = key switch
        {
            "offline" => "DashSoftOfflineBrush",
            "alarm" => "SoftAlarmBrush",
            "warn" => "SoftWarnBrush",
            "run" => "SoftRunBrush",
            "ok" => "SoftOkBrush",
            "ng" => "SoftNgBrush",
            // 空闲用干净 Surface2，避免 soft 灰洗导致整页发灰
            _ => "DashSurface2Brush"
        };
        return System.Windows.Application.Current?.TryFindResource(name)
               ?? System.Windows.Application.Current?.TryFindResource("DashSurface2Brush")
               ?? System.Windows.Media.Brushes.DimGray;
    }
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

/// <summary>bool → Visible / Collapsed。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is Visibility.Visible;
}

/// <summary>bool → Collapsed / Visible（与 BoolToVisibility 相反，用于空态占位）。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is not Visibility.Visible;
}

/// <summary>告警级别文案 → 状态色（严重/警告/信息）。</summary>
public sealed class AlarmLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = (value as string ?? "").Trim() switch
        {
            "严重" or "1" or "ERR" or "Error" => "AlarmBrush",
            "警告" or "2" or "WRN" or "Warning" => "WarnBrush",
            "信息" or "INF" or "Information" => "RunBrush",
            _ => "FgMutedBrush"
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>告警状态「未处理/已处理」→ 徽标色。</summary>
public sealed class AlarmStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var key = value is "未处理" or "0" ? "WarnBrush" : "IdleBrush";
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
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
