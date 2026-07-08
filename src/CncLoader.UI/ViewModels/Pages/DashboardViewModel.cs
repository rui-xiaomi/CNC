using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 监控看板（第四阶段⑦ 实时化）：加工位状态（订阅 ISignalStateStore）+ 当班 OK/NG 统计（IWorkRecordService）+
/// 未处理告警数（IAlarmEventService）+ 最近加工记录。
/// 性能：位置变化不再全量重建集合，而是 200ms 节流合并 + 卡片原地更新（DispatcherTimer 在 UI 线程执行，无跨线程同步 Invoke）。
/// </summary>
public sealed partial class DashboardViewModel : PageViewModelBase
{
    private readonly ISignalStateStore _store;
    private readonly IWorkRecordService _workRecords;
    private readonly IAlarmEventService _alarms;
    private readonly IPositionScheduler _scheduler;

    private readonly Dictionary<(long Eq, long Pos), PositionCardVm> _cards = new();
    private readonly DispatcherTimer _throttle;
    private readonly DispatcherTimer _statsTimer;
    private volatile bool _positionsDirty;

    public DashboardViewModel(ISignalStateStore store, IWorkRecordService workRecords, IAlarmEventService alarms, IPositionScheduler scheduler)
    {
        _store = store;
        _workRecords = workRecords;
        _alarms = alarms;
        _scheduler = scheduler;
        Positions = new ObservableCollection<PositionCardVm>();
        RecentRecords = new ObservableCollection<WorkRecordRow>();
        _store.PositionChanged += OnStoreChanged;
        _store.MachineChanged += OnStoreChanged;
        _alarms.AlarmRaised += OnAlarmRaised;
        _alarms.AlarmsChanged += (_, _) => _ = RefreshAlarmCountAsync();

        // 200ms 节流：转态风暴合并成一次 UI 更新，运行在 UI 线程（DispatcherTimer），避免逐事件同步 Invoke。
        _throttle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _throttle.Tick += (_, _) => { if (_positionsDirty) { _positionsDirty = false; UpdatePositionsUi(); } };
        _throttle.Start();

        // 2s 轮询：当班 OK/NG、最近加工记录等 DB 指标随生产实时刷新。
        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) => _ = RefreshStatsAndRecordsAsync();
        _statsTimer.Start();

        _positionsDirty = true;
        _ = RefreshAsync();
    }

    public override string Key => "dash";
    public override string Title => "监控看板";

    public ObservableCollection<PositionCardVm> Positions { get; }
    public ObservableCollection<WorkRecordRow> RecentRecords { get; }

    [ObservableProperty] private int _okCount;
    [ObservableProperty] private int _ngCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _alarmCount;
    [ObservableProperty] private int _onlineMachines;
    [ObservableProperty] private string _statusMessage = "";

    private void OnStoreChanged(object? sender, PositionStatus ps)
    {
        _positionsDirty = true;
        // 出结果时立即刷新产量统计与加工记录，不必等 2s 轮询。
        if (ps.State is PositionState.DoneOk or PositionState.DoneNg)
            _ = RefreshStatsAndRecordsAsync();
    }
    private void OnStoreChanged(object? sender, MachineStatus ms) => _positionsDirty = true;

    // 告警产生时即时刷新未处理数（AlarmRaised 可能在后台线程触发，标量属性 WPF 会自动 marshal）
    private void OnAlarmRaised(object? sender, Core.Plc.AlarmRow e) => _ = RefreshAlarmCountAsync();

    private async Task RefreshAlarmCountAsync()
    {
        try { AlarmCount = await _alarms.GetUnhandledCountAsync(); }
        catch { /* DB 未就绪：保持上次值 */ }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _positionsDirty = true; // 位置由节流器刷新
        await RefreshStatsAndRecordsAsync();
    }

    private async Task RefreshStatsAndRecordsAsync()
    {
        await RefreshStatsAsync();
        await RefreshRecordsAsync();
    }

    /// <summary>人工恢复告警：确认现场处理完毕后，把该加工位从 ALARM 重置回 WAIT_LOAD。</summary>
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

    /// <summary>增量更新加工位卡片（在 UI 线程调用）：存在则原地改状态，新增则追加，消失则移除。</summary>
    private void UpdatePositionsUi()
    {
        var positions = _store.GetAllPositions();
        var machines = _store.GetAllMachines().ToDictionary(m => m.EquipmentId);
        var seen = new HashSet<(long, long)>();

        foreach (var p in positions.OrderBy(x => x.EquipmentId).ThenBy(x => x.PositionId))
        {
            machines.TryGetValue(p.EquipmentId, out var m);
            var key = (p.EquipmentId, p.PositionId);
            seen.Add(key);
            if (_cards.TryGetValue(key, out var card))
            {
                card.Update(p.State, m?.PlcOnline ?? false, m?.Safe, m?.DoorOpen);
            }
            else
            {
                var c = new PositionCardVm(p.EquipmentId, p.PositionId, p.State, m?.PlcOnline ?? false, m?.Safe, m?.DoorOpen);
                _cards[key] = c;
                Positions.Add(c);
            }
        }

        foreach (var key in _cards.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Positions.Remove(_cards[key]);
            _cards.Remove(key);
        }

        OnlineMachines = machines.Values.Count(m => m.PlcOnline);
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var stats = await _workRecords.GetShiftStatsAsync();
            OkCount = stats.Ok;
            NgCount = stats.Ng;
            TotalCount = stats.Total;
            AlarmCount = await _alarms.GetUnhandledCountAsync();
        }
        catch (Exception ex) { StatusMessage = $"统计加载失败：{ex.Message}"; }
    }

    private async Task RefreshRecordsAsync()
    {
        try
        {
            var rows = await _workRecords.GetRecentAsync(20);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RecentRecords.Clear();
                foreach (var r in rows) RecentRecords.Add(r);
            });
        }
        catch { /* DB 未就绪时静默 */ }
    }
}

/// <summary>加工位卡片（监控看板）。可变字段为可观察属性，支持原地增量更新。</summary>
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

    /// <summary>是否处于告警态（供看板显示「恢复」按钮）。</summary>
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

    /// <summary>原地更新可变字段（避免全量重建集合）。</summary>
    public void Update(PositionState state, bool plcOnline, bool? safe, bool? doorOpen)
    {
        State = state;
        PlcOnline = plcOnline;
        Safe = safe;
        DoorOpen = doorOpen;
    }
}
