using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.UI.Navigation;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 监控看板协调器：组合机台 Surface、KPI/告警/记录、对账状态条。
/// 位置变化 200ms 节流原地更新；告警/产量/加工记录 2s 轮询 + 事件即时刷新。
/// </summary>
public sealed partial class DashboardViewModel : PageViewModelBase, IDisposable
{
    private readonly ISignalStateStore _store;
    private readonly IAlarmEventService _alarms;
    private readonly IPositionScheduler _scheduler;
    private readonly IFrameService _frames;
    private readonly IWorkLineService _workLines;
    private readonly IEquipmentConfigService? _equipment;
    private readonly ICraftworkService? _crafts;
    private readonly IUserNotificationService _notify;
    private readonly IUiDispatcher _ui;
    private readonly IRcsTaskNavigator? _rcsNav;

    private readonly Dictionary<long, string> _equipmentNames = new();
    private readonly Dictionary<long, string> _equipmentNos = new();
    private readonly DispatcherTimer _throttle;
    private readonly DispatcherTimer _statsTimer;
    private volatile bool _positionsDirty;
    private bool _disposed;

    public DashboardMachineSurfaceViewModel Surface { get; }
    public DashboardFeedViewModel Feed { get; }
    public DashboardReconcileBarViewModel ReconcileBar { get; }

    public DashboardViewModel(ISignalStateStore store, IWorkRecordService workRecords, IAlarmEventService alarms,
        IPositionScheduler scheduler, IFrameService frames, IWorkLineService workLines, ICurrentUser user,
        IUserNotificationService notify, IUiDispatcher ui,
        IEquipmentConfigService? equipment = null, ICraftworkService? crafts = null,
        IUiExceptionMonitor? uiExceptions = null, IRcsTaskNavigator? rcsNav = null)
    {
        _rcsNav = rcsNav;
        _store = store;
        _alarms = alarms;
        _scheduler = scheduler;
        _frames = frames;
        _workLines = workLines;
        _equipment = equipment;
        _crafts = crafts;
        _notify = notify;
        _ui = ui;

        Surface = new DashboardMachineSurfaceViewModel(store, _equipmentNames, _equipmentNos);
        Feed = new DashboardFeedViewModel(workRecords, alarms, user, notify, ui, _equipmentNames);
        ReconcileBar = new DashboardReconcileBarViewModel(scheduler, MarshalToUi, uiExceptions);

        _store.PositionChanged += OnStoreChanged;
        _store.MachineChanged += OnStoreChanged;
        _alarms.AlarmRaised += OnAlarmRaised;
        _alarms.AlarmsChanged += OnAlarmsChanged;
        _workLines.WorkLinesChanged += OnWorkLinesChanged;

        _throttle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _throttle.Tick += (_, _) =>
        {
            if (_disposed || !_positionsDirty) return;
            _positionsDirty = false;
            Surface.UpdateMachinesUi();
        };

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) =>
        {
            if (_disposed) return;
            _ = RefreshStatsAndAlarmsAsync();
        };

        _positionsDirty = true;
        _ = InitAsync();
    }

    public override string Key => "dash";
    public override string Title => "监控看板";

    private void MarshalToUi(Action action) => _ui.Invoke(action);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _throttle.Stop();
        _statsTimer.Stop();
        ReconcileBar.Dispose();
        _store.PositionChanged -= OnStoreChanged;
        _store.MachineChanged -= OnStoreChanged;
        _alarms.AlarmRaised -= OnAlarmRaised;
        _alarms.AlarmsChanged -= OnAlarmsChanged;
        _workLines.WorkLinesChanged -= OnWorkLinesChanged;
    }

    public override void OnActivated()
    {
        if (_disposed) return;
        _throttle.Start();
        _statsTimer.Start();
        _positionsDirty = true;
        _ = RefreshStatsAndAlarmsAsync();
    }

    public override void OnDeactivated()
    {
        _throttle.Stop();
        _statsTimer.Stop();
    }

    private async Task InitAsync()
    {
        try
        {
            var opts = await _frames.GetEquipmentOptionsAsync();
            foreach (var o in opts) _equipmentNames[o.Id] = o.DisplayName;
        }
        catch { /* 名称缺失时回退 EQ{id} */ }
        await LoadProcessOrderAsync();
        if (!_disposed)
            _ui.Invoke(Surface.UpdateMachinesUi);
        await RefreshFlowLineCodeAsync();
        await RefreshAsync();
    }

    private async Task LoadProcessOrderAsync()
    {
        if (_equipment is null || _crafts is null) return;
        try
        {
            var crafts = await _crafts.GetByLineAsync(null);
            var order = new Dictionary<long, int>();
            var seq = 0;
            foreach (var c in crafts.OrderBy(x => x.Sort).ThenBy(x => x.Id))
            {
                var eqs = await _equipment.GetByCraftAsync(c.Id);
                foreach (var e in eqs.OrderBy(x => x.No, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id))
                {
                    order[e.Id] = seq++;
                    _equipmentNos[e.Id] = e.No;
                    if (!string.IsNullOrWhiteSpace(e.Name))
                        _equipmentNames[e.Id] = e.Name;
                }
            }
            Surface.SetProcessOrder(order);
        }
        catch { /* 排序缺失时产线流仍按机台主键 */ }
    }

    public static IReadOnlyList<T> OrderByProcess<T>(
        IEnumerable<T> items, Func<T, long> idOf, IReadOnlyDictionary<long, int> order)
        => DashboardSurfaceProjector.OrderByProcess(items, idOf, order);

    private async Task RefreshFlowLineCodeAsync()
    {
        try
        {
            var lines = await _workLines.GetAllAsync();
            Surface.FlowLineCode = lines.FirstOrDefault()?.Code ?? "—";
        }
        catch { Surface.FlowLineCode = "—"; }
    }

    private void OnStoreChanged(object? sender, PositionStatus ps)
    {
        if (_disposed) return;
        _positionsDirty = true;
        if (ps.State is PositionState.DoneOk or PositionState.DoneNg or PositionState.Processing)
            _ = RefreshStatsAndRecordsAsync();
    }
    private void OnStoreChanged(object? sender, MachineStatus ms)
    {
        if (_disposed) return;
        _positionsDirty = true;
    }
    private void OnAlarmRaised(object? sender, AlarmRow e)
    {
        if (_disposed) return;
        _ = Feed.RefreshAlarmsAsync();
    }
    private void OnAlarmsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ = Feed.RefreshAlarmsAsync();
    }

    private void OnWorkLinesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ = RefreshFlowLineCodeAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _positionsDirty = true;
        await RefreshStatsAndAlarmsAsync();
    }

    private async Task RefreshStatsAndAlarmsAsync()
    {
        if (_disposed) return;
        await Feed.RefreshStatsAsync();
        await Feed.RefreshAlarmsAsync();
        await Feed.RefreshRecordsAsync();
        if (_positionsDirty)
        {
            _positionsDirty = false;
            _ui.Invoke(Surface.UpdateMachinesUi);
        }
    }

    private async Task RefreshStatsAndRecordsAsync()
    {
        await Feed.RefreshStatsAsync();
        await Feed.RefreshRecordsAsync();
    }

    [RelayCommand]
    private void OpenCancelHold(PositionCardVm? card)
    {
        var id = CancelHoldDisplay.TryParseTaskId(card?.StatusDetail);
        if (id is null)
        {
            _notify.Warning("该工位没有待确认取消任务。");
            return;
        }
        if (_rcsNav is null)
        {
            _notify.Warning("无法打开 RCS 页。");
            return;
        }
        _rcsNav.OpenTask(id);
    }

    [RelayCommand]
    private async Task ResetAlarmAsync(PositionCardVm? card)
    {
        if (card is null) return;
        if (!_notify.Confirm(
                $"确认已现场处理 {card.EquipmentText} {card.PositionText} 的告警并恢复运行？", "恢复告警"))
            return;
        try
        {
            await _scheduler.ResetAlarmAsync(card.EquipmentId, card.PositionId);
            _notify.Success($"{card.EquipmentText} {card.PositionText} 已恢复，等待上料。");
        }
        catch (Exception ex) { _notify.Error($"恢复失败：{ex.Message}"); }
    }

    [RelayCommand]
    private Task MarkAlarmHandledAsync(AlarmFeedItem? item) => Feed.MarkAlarmHandledCommand.ExecuteAsync(item);

    [RelayCommand]
    private Task MarkAllAlarmsHandledAsync() => Feed.MarkAllAlarmsHandledCommand.ExecuteAsync(null);

    [RelayCommand]
    private void SelectFlowNode(FlowNodeVm? node) => Surface.SelectFlowNodeCommand.Execute(node);
}
