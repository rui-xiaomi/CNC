using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Common.Logging;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 日志/告警页：两 tab——①告警明细（MAS_AUTO_ALARM_EVENT，可筛未处理+标记已处理+全部确认）；
/// ②应用运行日志（读 logs/ 当天文件尾部，可按级别筛选）。
/// </summary>
public sealed partial class LogViewModel : PageViewModelBase
{
    private readonly IAlarmEventService _alarms;
    private readonly ILogFileReader _logReader;
    private readonly ICurrentUser _user;

    public LogViewModel(IAlarmEventService alarms, ILogFileReader logReader, ICurrentUser user)
    {
        _alarms = alarms;
        _logReader = logReader;
        _user = user;
        Alarms = new ObservableCollection<AlarmRow>();
        LogLines = new ObservableCollection<LogLine>();
        _alarms.AlarmRaised += OnAlarmRaised;
        _alarms.AlarmsChanged += (_, _) => _ = RefreshAlarmsAsync();
        _ = RefreshAlarmsAsync();
        _ = RefreshLogAsync();
    }

    public override string Key => "log";
    public override string Title => "日志/告警";

    public ObservableCollection<AlarmRow> Alarms { get; }
    public ObservableCollection<LogLine> LogLines { get; }

    [ObservableProperty] private bool _unhandledOnly;
    [ObservableProperty] private AlarmRow? _selectedAlarm;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _alarmsEmpty = true;
    [ObservableProperty] private bool _logsEmpty = true;

    public IReadOnlyList<int> AlarmCountOptions { get; } = new[] { 50, 100, 200, 500 };
    [ObservableProperty] private int _alarmCount = 50;

    public IReadOnlyList<string> LevelOptions { get; } = new[] { "全部", "INF", "WRN", "ERR" };
    [ObservableProperty] private string _selectedLevel = "全部";
    public IReadOnlyList<int> LineCountOptions { get; } = new[] { 50, 100, 200, 500, 1000, 2000 };
    [ObservableProperty] private int _lineCount = 200;

    partial void OnUnhandledOnlyChanged(bool value) => _ = RefreshAlarmsAsync();
    partial void OnAlarmCountChanged(int value) => _ = RefreshAlarmsAsync();
    partial void OnSelectedLevelChanged(string value) => _ = RefreshLogAsync();
    partial void OnLineCountChanged(int value) => _ = RefreshLogAsync();
    partial void OnSelectedTabIndexChanged(int value) => UpdateStatusBar();

    private void OnAlarmRaised(object? sender, AlarmRow e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (UnhandledOnly && e.State != "未处理") return;
            Alarms.Insert(0, e);
            while (Alarms.Count > AlarmCount) Alarms.RemoveAt(Alarms.Count - 1);
            AlarmsEmpty = false;
            if (SelectedTabIndex == 0) UpdateStatusBar();
        });
    }

    [RelayCommand]
    private async Task RefreshAlarmsAsync()
    {
        try
        {
            var rows = await _alarms.GetAlarmsAsync(UnhandledOnly, AlarmCount);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                Alarms.Clear();
                foreach (var r in rows) Alarms.Add(r);
                AlarmsEmpty = Alarms.Count == 0;
                if (SelectedTabIndex == 0) UpdateStatusBar();
            });
        }
        catch (Exception ex) { StatusMessage = $"告警加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task MarkHandledAsync()
    {
        if (SelectedAlarm is null) { StatusMessage = "请先选择一条告警"; return; }
        if (SelectedAlarm.State == "已处理") { StatusMessage = "该告警已处理"; return; }
        try
        {
            await _alarms.MarkHandledAsync(SelectedAlarm.Id, _user.Name);
            await RefreshAlarmsAsync();
            StatusMessage = "已标记为已处理";
        }
        catch (Exception ex) { StatusMessage = $"标记失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task MarkAllHandledAsync()
    {
        try
        {
            var pending = await _alarms.GetAlarmsAsync(unhandledOnly: true, limit: 500);
            if (pending.Count == 0) { StatusMessage = "没有未处理告警"; return; }
            foreach (var a in pending)
                await _alarms.MarkHandledAsync(a.Id, _user.Name);
            await RefreshAlarmsAsync();
            StatusMessage = $"已确认 {pending.Count} 条告警";
        }
        catch (Exception ex) { StatusMessage = $"全部确认失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteAllAlarmsAsync()
    {
        if (HandyControl.Controls.MessageBox.Show(
                "确定删除全部告警记录？此操作物理删除、不可恢复。",
                "清空告警二次确认",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;
        try
        {
            var n = await _alarms.DeleteAllAsync();
            await RefreshAlarmsAsync();
            StatusMessage = $"已删除全部告警（{n} 条）";
        }
        catch (Exception ex) { StatusMessage = $"删除失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshLogAsync()
    {
        try
        {
            var minLevel = SelectedLevel == "全部" ? null : SelectedLevel;
            var lines = await _logReader.TailAsync(LineCount, minLevel);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                LogLines.Clear();
                foreach (var l in lines) LogLines.Add(l);
                LogsEmpty = LogLines.Count == 0;
                if (SelectedTabIndex == 1) UpdateStatusBar();
            });
        }
        catch (Exception ex) { StatusMessage = $"日志加载失败：{ex.Message}"; }
    }

    private void UpdateStatusBar()
    {
        if (SelectedTabIndex == 1)
        {
            StatusMessage = LogsEmpty
                ? "暂无应用日志"
                : $"应用日志 {LogLines.Count} 行（级别：{SelectedLevel}）";
            return;
        }

        var unhandled = Alarms.Count(a => a.State == "未处理");
        StatusMessage = AlarmsEmpty
            ? (UnhandledOnly ? "暂无未处理告警" : "暂无告警记录")
            : $"告警 {Alarms.Count} 条 · 未处理 {unhandled}";
    }
}
