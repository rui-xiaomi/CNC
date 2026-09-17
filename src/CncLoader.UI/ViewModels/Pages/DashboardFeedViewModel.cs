using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>KPI / 告警 / 加工记录。集合写入经 UI 调度器。</summary>
public sealed partial class DashboardFeedViewModel : ObservableObject
{
    private readonly IWorkRecordService _workRecords;
    private readonly IAlarmEventService _alarms;
    private readonly ICurrentUser _user;
    private readonly IUserNotificationService _notify;
    private readonly IUiDispatcher _ui;
    private readonly Dictionary<long, string> _equipmentNames;

    internal DashboardFeedViewModel(
        IWorkRecordService workRecords,
        IAlarmEventService alarms,
        ICurrentUser user,
        IUserNotificationService notify,
        IUiDispatcher ui,
        Dictionary<long, string> equipmentNames)
    {
        _workRecords = workRecords;
        _alarms = alarms;
        _user = user;
        _notify = notify;
        _ui = ui;
        _equipmentNames = equipmentNames;
        Alarms = new ObservableCollection<AlarmFeedItem>();
        RecentRecords = new ObservableCollection<WorkRecordFeedItem>();
    }

    public ObservableCollection<AlarmFeedItem> Alarms { get; }
    public ObservableCollection<WorkRecordFeedItem> RecentRecords { get; }

    [ObservableProperty] private int _okCount;
    [ObservableProperty] private int _ngCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _alarmCount;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _okSubText = "今日工位判定 · 非整件";
    [ObservableProperty] private string _ngSubText = "今日工位判定 · 非整件";
    [ObservableProperty] private string _alarmSubText = "—";
    [ObservableProperty] private bool _alarmsEmpty = true;
    [ObservableProperty] private bool _recordsEmpty = true;

    [RelayCommand]
    private async Task MarkAlarmHandledAsync(AlarmFeedItem? item)
    {
        if (item is null || item.IsHandled) return;
        try
        {
            await _alarms.MarkHandledAsync(item.Id, _user.Name);
            _notify.Success("告警已确认。");
            await RefreshAlarmsAsync();
        }
        catch (Exception ex) { _notify.Error($"确认失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task MarkAllAlarmsHandledAsync()
    {
        try
        {
            var pending = await _alarms.GetAlarmsAsync(unhandledOnly: true, limit: 500);
            if (pending.Count == 0) { _notify.Info("没有未处理告警。"); return; }
            foreach (var a in pending)
                await _alarms.MarkHandledAsync(a.Id, _user.Name);
            _notify.Success($"已确认 {pending.Count} 条告警。");
            await RefreshAlarmsAsync();
        }
        catch (Exception ex) { _notify.Error($"全部确认失败：{ex.Message}"); }
    }

    internal async Task RefreshStatsAsync()
    {
        try
        {
            var stats = await _workRecords.GetShiftStatsAsync();
            OkCount = stats.Ok;
            NgCount = stats.Ng;
            TotalCount = stats.Total;
            var openOrOther = Math.Max(0, stats.Total - stats.Ok - stats.Ng);
            OkSubText = openOrOther > 0
                ? $"今日工位判定 · 另有 {openOrOther} 条未结/异常"
                : "今日工位判定 · 非整件";
            NgSubText = "今日工位判定 · 非整件";
            AlarmCount = await _alarms.GetUnhandledCountAsync();
        }
        catch (Exception ex) { StatusMessage = $"统计加载失败：{ex.Message}"; }
    }

    internal async Task RefreshAlarmsAsync()
    {
        try
        {
            AlarmCount = await _alarms.GetUnhandledCountAsync();
            var rows = await _alarms.GetAlarmsAsync(unhandledOnly: true, limit: 30);
            _ui.Post(() =>
            {
                Alarms.Clear();
                foreach (var r in rows) Alarms.Add(AlarmFeedItem.From(r));
                AlarmsEmpty = Alarms.Count == 0;
                AlarmSubText = Alarms.Count > 0
                    ? $"最近 {Alarms[0].TimeText}"
                    : "暂无未处理";
            });
        }
        catch { /* DB 未就绪时静默 */ }
    }

    internal async Task RefreshRecordsAsync()
    {
        try
        {
            var rows = await _workRecords.GetRecentAsync(30);
            _ui.Post(() =>
            {
                RecentRecords.Clear();
                foreach (var r in rows)
                {
                    var eqName = _equipmentNames.TryGetValue(r.EquipmentId, out var n)
                        ? n
                        : $"EQ{r.EquipmentId:D2}";
                    RecentRecords.Add(WorkRecordFeedItem.From(r, ShortEquipmentName(eqName)));
                }
                RecordsEmpty = RecentRecords.Count == 0;
            });
        }
        catch { /* DB 未就绪时静默 */ }
    }

    private static string ShortEquipmentName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "—";
        var i = displayName.LastIndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? displayName[..i] : displayName;
    }
}
