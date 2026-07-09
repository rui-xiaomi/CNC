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

/// <summary>加工位状态徽标 → 状态色 Brush（监控看板 StateBadge 字符串转 Brush）。
/// offline→FgMuted / alarm→Alarm / run→Run / ok→Ok / ng→Alarm / idle→Idle。</summary>
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
            "ng" => "AlarmBrush",
            _ => "IdleBrush"
        };
        return System.Windows.Application.Current?.TryFindResource(name) ?? System.Windows.Media.Brushes.Gray;
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
