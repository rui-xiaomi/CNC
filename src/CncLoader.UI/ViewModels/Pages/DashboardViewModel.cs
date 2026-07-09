using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 监控看板：KPI + 左机台（紧凑）| 右上最近加工记录 + 右下实时告警。
/// 位置变化 200ms 节流原地更新；告警/产量/加工记录 2s 轮询 + 事件即时刷新。
/// </summary>
public sealed partial class DashboardViewModel : PageViewModelBase
{
    private readonly ISignalStateStore _store;
    private readonly IWorkRecordService _workRecords;
    private readonly IAlarmEventService _alarms;
    private readonly IPositionScheduler _scheduler;
    private readonly IFrameService _frames;
    private readonly ICurrentUser _user;

    private readonly Dictionary<long, MachineCardVm> _machines = new();
    private readonly Dictionary<(long Eq, long Pos), PositionCardVm> _positions = new();
    private readonly Dictionary<long, string> _equipmentNames = new();
    private readonly DispatcherTimer _throttle;
    private readonly DispatcherTimer _statsTimer;
    private volatile bool _positionsDirty;

    public DashboardViewModel(ISignalStateStore store, IWorkRecordService workRecords, IAlarmEventService alarms,
        IPositionScheduler scheduler, IFrameService frames, ICurrentUser user)
    {
        _store = store;
        _workRecords = workRecords;
        _alarms = alarms;
        _scheduler = scheduler;
        _frames = frames;
        _user = user;
        Machines = new ObservableCollection<MachineCardVm>();
        Alarms = new ObservableCollection<AlarmFeedItem>();
        RecentRecords = new ObservableCollection<WorkRecordFeedItem>();
        _store.PositionChanged += OnStoreChanged;
        _store.MachineChanged += OnStoreChanged;
        _alarms.AlarmRaised += OnAlarmRaised;
        _alarms.AlarmsChanged += (_, _) => _ = RefreshAlarmsAsync();

        _throttle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _throttle.Tick += (_, _) => { if (_positionsDirty) { _positionsDirty = false; UpdateMachinesUi(); } };
        _throttle.Start();

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) => _ = RefreshStatsAndAlarmsAsync();
        _statsTimer.Start();

        _positionsDirty = true;
        _ = InitAsync();
    }

    public override string Key => "dash";
    public override string Title => "监控看板";

    public ObservableCollection<MachineCardVm> Machines { get; }
    public ObservableCollection<AlarmFeedItem> Alarms { get; }
    public ObservableCollection<WorkRecordFeedItem> RecentRecords { get; }

    [ObservableProperty] private int _okCount;
    [ObservableProperty] private int _ngCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _alarmCount;
    [ObservableProperty] private int _onlineMachines;
    [ObservableProperty] private int _totalMachines;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _onlineSubText = "";
    /// <summary>OK 卡副文案：标明「工位判定」口径，避免理解成整件过线。</summary>
    [ObservableProperty] private string _okSubText = "今日工位判定 · 非整件";
    [ObservableProperty] private string _ngSubText = "今日工位判定 · 非整件";
    [ObservableProperty] private bool _alarmsEmpty = true;
    [ObservableProperty] private bool _recordsEmpty = true;

    private async Task InitAsync()
    {
        try
        {
            var opts = await _frames.GetEquipmentOptionsAsync();
            foreach (var o in opts) _equipmentNames[o.Id] = o.DisplayName;
        }
        catch { /* 名称缺失时回退 EQ{id} */ }
        await RefreshAsync();
    }

    private void OnStoreChanged(object? sender, PositionStatus ps)
    {
        _positionsDirty = true;
        if (ps.State is PositionState.DoneOk or PositionState.DoneNg or PositionState.Processing)
            _ = RefreshStatsAndRecordsAsync();
    }
    private void OnStoreChanged(object? sender, MachineStatus ms) => _positionsDirty = true;
    private void OnAlarmRaised(object? sender, AlarmRow e) => _ = RefreshAlarmsAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _positionsDirty = true;
        await RefreshStatsAndAlarmsAsync();
    }

    private async Task RefreshStatsAndAlarmsAsync()
    {
        await RefreshStatsAsync();
        await RefreshAlarmsAsync();
        await RefreshRecordsAsync();
    }

    private async Task RefreshStatsAndRecordsAsync()
    {
        await RefreshStatsAsync();
        await RefreshRecordsAsync();
    }

    [RelayCommand]
    private async Task ResetAlarmAsync(PositionCardVm? card)
    {
        if (card is null) return;
        var confirm = System.Windows.MessageBox.Show(
            $"确认已现场处理 {card.EquipmentText} {card.PositionText} 的告警并恢复运行？",
            "恢复告警", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.OK) return;
        try
        {
            await _scheduler.ResetAlarmAsync(card.EquipmentId, card.PositionId);
            HandyControl.Controls.Growl.Success($"{card.EquipmentText} {card.PositionText} 已恢复，等待上料。");
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"恢复失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task MarkAlarmHandledAsync(AlarmFeedItem? item)
    {
        if (item is null || item.IsHandled) return;
        try
        {
            await _alarms.MarkHandledAsync(item.Id, _user.Name);
            HandyControl.Controls.Growl.Success("告警已确认。");
            await RefreshAlarmsAsync();
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"确认失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task MarkAllAlarmsHandledAsync()
    {
        try
        {
            // 按库内未处理全量确认（不限当前列表条数）
            var pending = await _alarms.GetAlarmsAsync(unhandledOnly: true, limit: 500);
            if (pending.Count == 0) { HandyControl.Controls.Growl.Info("没有未处理告警。"); return; }
            foreach (var a in pending)
                await _alarms.MarkHandledAsync(a.Id, _user.Name);
            HandyControl.Controls.Growl.Success($"已确认 {pending.Count} 条告警。");
            await RefreshAlarmsAsync();
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"全部确认失败：{ex.Message}"); }
    }

    private void UpdateMachinesUi()
    {
        var positions = _store.GetAllPositions();
        var machineSnaps = _store.GetAllMachines().ToDictionary(m => m.EquipmentId);
        var seenEq = new HashSet<long>();
        var seenPos = new HashSet<(long, long)>();

        foreach (var group in positions.GroupBy(p => p.EquipmentId).OrderBy(g => g.Key))
        {
            var eqId = group.Key;
            seenEq.Add(eqId);
            machineSnaps.TryGetValue(eqId, out var ms);
            var name = _equipmentNames.TryGetValue(eqId, out var n) ? n : $"EQ{eqId}";

            if (!_machines.TryGetValue(eqId, out var card))
            {
                card = new MachineCardVm(eqId, name);
                _machines[eqId] = card;
                Machines.Add(card);
            }
            card.Update(ms?.PlcOnline ?? false, ms?.Safe, ms?.DoorOpen);

            foreach (var p in group.OrderBy(x => x.PositionId))
            {
                var key = (p.EquipmentId, p.PositionId);
                seenPos.Add(key);
                if (_positions.TryGetValue(key, out var pos))
                {
                    pos.Update(p.State, ms?.PlcOnline ?? false, ms?.Safe, ms?.DoorOpen);
                }
                else
                {
                    var posVm = new PositionCardVm(p.EquipmentId, p.PositionId, p.State, ms?.PlcOnline ?? false, ms?.Safe, ms?.DoorOpen);
                    _positions[key] = posVm;
                    card.Positions.Add(posVm);
                }
            }
        }

        // 仅有机台信号、尚无加工位快照的机台也展示
        foreach (var ms in machineSnaps.Values.OrderBy(m => m.EquipmentId))
        {
            if (seenEq.Contains(ms.EquipmentId)) continue;
            seenEq.Add(ms.EquipmentId);
            var name = _equipmentNames.TryGetValue(ms.EquipmentId, out var n) ? n : $"EQ{ms.EquipmentId}";
            if (!_machines.TryGetValue(ms.EquipmentId, out var card))
            {
                card = new MachineCardVm(ms.EquipmentId, name);
                _machines[ms.EquipmentId] = card;
                Machines.Add(card);
            }
            card.Update(ms.PlcOnline, ms.Safe, ms.DoorOpen);
        }

        foreach (var key in _positions.Keys.Where(k => !seenPos.Contains(k)).ToList())
        {
            var pos = _positions[key];
            if (_machines.TryGetValue(key.Eq, out var m)) m.Positions.Remove(pos);
            _positions.Remove(key);
        }
        foreach (var eqId in _machines.Keys.Where(k => !seenEq.Contains(k)).ToList())
        {
            Machines.Remove(_machines[eqId]);
            _machines.Remove(eqId);
        }

        TotalMachines = Machines.Count;
        OnlineMachines = Machines.Count(m => m.PlcOnline);
        // 主数字已是「在线/总数」，副文案只报加工位数，避免再写一遍 3/3
        OnlineSubText = TotalMachines == 0
            ? "无加工位"
            : $"{_positions.Count} 个加工位有信号";
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var stats = await _workRecords.GetShiftStatsAsync();
            OkCount = stats.Ok;
            NgCount = stats.Ng;
            TotalCount = stats.Total;
            // 总数含进行中/异常，不等于 OK+NG；副文案说明口径，避免当成整件产量
            var openOrOther = Math.Max(0, stats.Total - stats.Ok - stats.Ng);
            OkSubText = openOrOther > 0
                ? $"今日工位判定 · 另有 {openOrOther} 条未结/异常"
                : "今日工位判定 · 非整件";
            NgSubText = "今日工位判定 · 非整件";
            AlarmCount = await _alarms.GetUnhandledCountAsync();
        }
        catch (Exception ex) { StatusMessage = $"统计加载失败：{ex.Message}"; }
    }

    private async Task RefreshAlarmsAsync()
    {
        try
        {
            AlarmCount = await _alarms.GetUnhandledCountAsync();
            // 看板只盯未处理，避免「角标 30 / 列表全是已处理」错觉
            var rows = await _alarms.GetAlarmsAsync(unhandledOnly: true, limit: 30);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Alarms.Clear();
                foreach (var r in rows) Alarms.Add(AlarmFeedItem.From(r));
                AlarmsEmpty = Alarms.Count == 0;
            });
        }
        catch { /* DB 未就绪时静默 */ }
    }

    private async Task RefreshRecordsAsync()
    {
        try
        {
            var rows = await _workRecords.GetRecentAsync(30);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RecentRecords.Clear();
                foreach (var r in rows)
                {
                    var eqName = _equipmentNames.TryGetValue(r.EquipmentId, out var n)
                        ? n
                        : $"EQ{r.EquipmentId:D2}";
                    RecentRecords.Add(WorkRecordFeedItem.From(r, eqName));
                }
                RecordsEmpty = RecentRecords.Count == 0;
            });
        }
        catch { /* DB 未就绪时静默 */ }
    }
}

/// <summary>机台治具卡（看板签名元素）。</summary>
public sealed partial class MachineCardVm : ObservableObject
{
    public MachineCardVm(long equipmentId, string name)
    {
        EquipmentId = equipmentId;
        Name = name;
        Positions = new ObservableCollection<PositionCardVm>();
    }

    public long EquipmentId { get; }
    public string Name { get; }
    public string EquipmentCode => $"EQ{EquipmentId:D2}";
    public ObservableCollection<PositionCardVm> Positions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoorText))]
    [NotifyPropertyChangedFor(nameof(DoorBadge))]
    private bool? _doorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SafeText))]
    [NotifyPropertyChangedFor(nameof(SafeBadge))]
    private bool? _safe;

    [ObservableProperty] private bool _plcOnline;

    public string DoorText => DoorOpen switch { true => "门 开", false => "门 闭", _ => "门 未知" };
    public string SafeText => Safe switch { true => "安全", false => "不安全", _ => "安全 未知" };
    public string DoorBadge => DoorOpen == true ? "alarm" : DoorOpen == false ? "ok" : "idle";
    public string SafeBadge => Safe == true ? "ok" : Safe == false ? "alarm" : "idle";

    public void Update(bool plcOnline, bool? safe, bool? doorOpen)
    {
        PlcOnline = plcOnline;
        Safe = safe;
        DoorOpen = doorOpen;
    }
}

/// <summary>加工位行（挂在机台卡下）。</summary>
public sealed partial class PositionCardVm : ObservableObject
{
    public PositionCardVm(long equipmentId, long positionId, PositionState state, bool plcOnline, bool? safe, bool? doorOpen)
    {
        EquipmentId = equipmentId;
        PositionId = positionId;
        _state = state;
        _plcOnline = plcOnline;
        _safe = safe;
        _doorOpen = doorOpen;
    }

    public long EquipmentId { get; }
    public long PositionId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateDisplay))]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    [NotifyPropertyChangedFor(nameof(IsAlarm))]
    private PositionState _state;

    public bool IsAlarm => State == PositionState.Alarm;

    [ObservableProperty] private bool _plcOnline;
    [ObservableProperty] private bool? _safe;
    [ObservableProperty] private bool? _doorOpen;

    public string EquipmentText => $"EQ{EquipmentId}";
    public string PositionText => $"工位{PositionId}";
    public string StateDisplay => PositionStateNames.ToDisplay(State);
    public string StateBadge => State switch
    {
        PositionState.Offline => "offline",
        PositionState.Alarm => "alarm",
        PositionState.Processing => "run",
        PositionState.DoneOk => "ok",
        PositionState.DoneNg => "ng",
        PositionState.Dispatching or PositionState.Transporting => "run",
        _ => "idle"
    };

    public void Update(PositionState state, bool plcOnline, bool? safe, bool? doorOpen)
    {
        State = state;
        PlcOnline = plcOnline;
        Safe = safe;
        DoorOpen = doorOpen;
    }
}

/// <summary>看板底部最近加工记录行。</summary>
public sealed class WorkRecordFeedItem
{
    public long Id { get; init; }
    public string TimeText { get; init; } = "";
    public string EquipmentText { get; init; } = "";
    public string PositionText { get; init; } = "";
    public string ElectrodeText { get; init; } = "";
    public string ResultText { get; init; } = "";
    public string ResultBadge { get; init; } = "idle";
    public string ElapsedText { get; init; } = "";

    public static WorkRecordFeedItem From(WorkRecordRow r, string equipmentName)
    {
        var (resultText, badge) = r.WorkResult switch
        {
            "0" => ("OK", "ok"),
            "1" => ("NG", "ng"),
            "2" => ("异常", "alarm"),
            _ => ("进行中", "run")
        };
        return new WorkRecordFeedItem
        {
            Id = r.Id,
            TimeText = (r.WorkEndTime ?? r.WorkStartTime)?.ToString("HH:mm:ss") ?? "—",
            EquipmentText = equipmentName,
            PositionText = string.IsNullOrWhiteSpace(r.PositionCode) ? "—" : r.PositionCode,
            ElectrodeText = string.IsNullOrWhiteSpace(r.ElectrodeId) ? "—" : r.ElectrodeId!,
            ResultText = resultText,
            ResultBadge = badge,
            ElapsedText = r.ElapsedSeconds is int s ? $"{s}s" : "—"
        };
    }
}

/// <summary>看板右侧告警流条目。</summary>
public sealed partial class AlarmFeedItem : ObservableObject
{
    public long Id { get; init; }
    public DateTime Time { get; init; }
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
    public string AlarmType { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHandled))]
    [NotifyPropertyChangedFor(nameof(LevelBadge))]
    [NotifyPropertyChangedFor(nameof(StateTag))]
    private string _state = "0";

    public bool IsHandled => State != "0";
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelBadge => Level switch
    {
        "严重" or "1" => "alarm",
        "警告" or "2" => "warn",
        _ => IsHandled ? "idle" : "warn"
    };
    public string StateTag => IsHandled ? "已处理" : "未处理";

    public static AlarmFeedItem From(AlarmRow r) => new()
    {
        Id = r.Id,
        Time = r.Time,
        Level = r.Level,
        Message = r.Message,
        AlarmType = r.AlarmType,
        State = r.State
    };
}
