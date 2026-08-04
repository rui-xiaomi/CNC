using System.Collections.ObjectModel;
using System.Windows;
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
/// 监控看板：KPI + 启动对账状态条 + 产线流 + 左机台色块卡 | 右上最近加工记录 + 右下实时告警。
/// 位置变化 200ms 节流原地更新；告警/产量/加工记录 2s 轮询 + 事件即时刷新。
/// </summary>
public sealed partial class DashboardViewModel : PageViewModelBase, IDisposable
{
    private readonly ISignalStateStore _store;
    private readonly IWorkRecordService _workRecords;
    private readonly IAlarmEventService _alarms;
    private readonly IPositionScheduler _scheduler;
    private readonly IFrameService _frames;
    private readonly IWorkLineService _workLines;
    private readonly ICurrentUser _user;
    private readonly ReconciliationStatusBinder _reconcileBinder;

    private readonly Dictionary<long, MachineCardVm> _machines = new();
    private readonly Dictionary<(long Eq, long Pos), PositionCardVm> _positions = new();
    private readonly Dictionary<long, string> _equipmentNames = new();
    private readonly DispatcherTimer _throttle;
    private readonly DispatcherTimer _statsTimer;
    private volatile bool _positionsDirty;
    private bool _disposed;

    public DashboardViewModel(ISignalStateStore store, IWorkRecordService workRecords, IAlarmEventService alarms,
        IPositionScheduler scheduler, IFrameService frames, IWorkLineService workLines, ICurrentUser user)
    {
        _store = store;
        _workRecords = workRecords;
        _alarms = alarms;
        _scheduler = scheduler;
        _frames = frames;
        _workLines = workLines;
        _user = user;
        Machines = new ObservableCollection<MachineCardVm>();
        FlowNodes = new ObservableCollection<FlowNodeVm>();
        Alarms = new ObservableCollection<AlarmFeedItem>();
        RecentRecords = new ObservableCollection<WorkRecordFeedItem>();
        _store.PositionChanged += OnStoreChanged;
        _store.MachineChanged += OnStoreChanged;
        _alarms.AlarmRaised += OnAlarmRaised;
        _alarms.AlarmsChanged += (_, _) => _ = RefreshAlarmsAsync();
        _workLines.WorkLinesChanged += (_, _) => _ = RefreshFlowLineCodeAsync();

        // 启动对账状态：构造时读快照；后台事件经 Dispatcher 刷新绑定属性（只展示，不驱动重试）。
        _reconcileBinder = new ReconciliationStatusBinder(_scheduler, MarshalToUi);
        SyncReconcileUiFromBinder();
        _reconcileBinder.Changed += OnReconcileBinderChanged;

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
    public ObservableCollection<FlowNodeVm> FlowNodes { get; }
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
    [ObservableProperty] private string _alarmSubText = "—";
    [ObservableProperty] private bool _alarmsEmpty = true;
    [ObservableProperty] private bool _recordsEmpty = true;
    /// <summary>产线流选中的机台；null 表示未选或锚点节点。</summary>
    [ObservableProperty] private long? _selectedFlowEquipmentId;
    /// <summary>产线流头线体编码（如 LINE01）。</summary>
    [ObservableProperty] private string _flowLineCode = "—";

    /// <summary>启动对账主文案。</summary>
    [ObservableProperty] private string _reconcileTitle = "启动对账未开始";
    /// <summary>启动对账副文案（含锁定/原因；可截断）。</summary>
    [ObservableProperty] private string _reconcileSubText = "自动派工尚未开启";
    /// <summary>状态色资源键（Idle/Run/Warn/Ok），经 BrushConv 解析，不硬编码色值。</summary>
    [ObservableProperty] private string _reconcileBrushKey = "IdleBrush";
    /// <summary>软底色资源键。</summary>
    [ObservableProperty] private string _reconcileSoftBrushKey = "SoftIdleBrush";
    /// <summary>完整安全失败原因（ToolTip）；成功后为空。</summary>
    [ObservableProperty] private string? _reconcileDetailToolTip;
    /// <summary>对账是否已开闸（自动派工已开启）。</summary>
    [ObservableProperty] private bool _isReconcileGateOpen;

    private void OnReconcileBinderChanged(object? sender, EventArgs e) => SyncReconcileUiFromBinder();

    private void SyncReconcileUiFromBinder()
    {
        ReconcileTitle = _reconcileBinder.Title;
        ReconcileSubText = _reconcileBinder.SubText;
        ReconcileBrushKey = _reconcileBinder.BrushKey;
        ReconcileSoftBrushKey = _reconcileBinder.SoftBrushKey;
        ReconcileDetailToolTip = _reconcileBinder.DetailToolTip;
        IsReconcileGateOpen = _reconcileBinder.IsGateOpen;
    }

    private static void MarshalToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _throttle.Stop();
        _statsTimer.Stop();
        _reconcileBinder.Changed -= OnReconcileBinderChanged;
        _reconcileBinder.Dispose();
        _store.PositionChanged -= OnStoreChanged;
        _store.MachineChanged -= OnStoreChanged;
        _alarms.AlarmRaised -= OnAlarmRaised;
    }

    private async Task InitAsync()
    {
        try
        {
            var opts = await _frames.GetEquipmentOptionsAsync();
            foreach (var o in opts) _equipmentNames[o.Id] = o.DisplayName;
        }
        catch { /* 名称缺失时回退 EQ{id} */ }
        await RefreshFlowLineCodeAsync();
        await RefreshAsync();
    }

    private async Task RefreshFlowLineCodeAsync()
    {
        try
        {
            var lines = await _workLines.GetAllAsync();
            FlowLineCode = lines.FirstOrDefault()?.Code ?? "—";
        }
        catch { FlowLineCode = "—"; }
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

    [RelayCommand]
    private void SelectFlowNode(FlowNodeVm? node)
    {
        if (node is null || !node.IsEquipment) { SelectedFlowEquipmentId = null; ApplyMachineHighlight(); return; }
        SelectedFlowEquipmentId = SelectedFlowEquipmentId == node.EquipmentId ? null : node.EquipmentId;
        ApplyMachineHighlight();
    }

    private void ApplyMachineHighlight()
    {
        foreach (var m in Machines)
            m.IsHighlighted = SelectedFlowEquipmentId is long id && m.EquipmentId == id;
        foreach (var n in FlowNodes)
            n.IsSelected = n.IsEquipment && SelectedFlowEquipmentId is long id && n.EquipmentId == id;
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
            card.IsHighlighted = SelectedFlowEquipmentId == eqId;

            foreach (var p in group.OrderBy(x => x.PositionId))
            {
                var key = (p.EquipmentId, p.PositionId);
                seenPos.Add(key);
                if (_positions.TryGetValue(key, out var pos))
                {
                    pos.Update(p.State, p.MaterialId, p.StatusDetail,
                        ms?.PlcOnline ?? false, ms?.Safe, ms?.DoorOpen);
                }
                else
                {
                    var posVm = new PositionCardVm(p.EquipmentId, p.PositionId, p.State, p.MaterialId, p.StatusDetail,
                        ms?.PlcOnline ?? false, ms?.Safe, ms?.DoorOpen);
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
            card.IsHighlighted = SelectedFlowEquipmentId == ms.EquipmentId;
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

        // 保持 Machines 按 EquipmentId 升序（产线流与列表一致）
        var orderedMachines = Machines.OrderBy(m => m.EquipmentId).ToList();
        if (!Machines.SequenceEqual(orderedMachines))
        {
            Machines.Clear();
            foreach (var m in orderedMachines) Machines.Add(m);
        }

        TotalMachines = Machines.Count;
        OnlineMachines = Machines.Count(m => m.PlcOnline);
        OnlineSubText = TotalMachines == 0
            ? "无加工位"
            : $"{_positions.Count} 个加工位有信号";

        RebuildFlowNodes();
    }

    private void RebuildFlowNodes()
    {
        // 上料架 → EQ升序机台 → 终点分叉（下料 / NG）；锚点仅示意，不绑实时水位
        var desired = new List<FlowNodeVm>
        {
            new("upload", "上料架", null, isAnchor: true)
        };
        foreach (var m in Machines.OrderBy(x => x.EquipmentId))
            desired.Add(new FlowNodeVm($"eq-{m.EquipmentId}", m.Name, m.EquipmentId, isAnchor: false));
        desired.Add(new("ends", "终点", null, isAnchor: true) { IsEndFork = true });

        // 原地同步集合，避免整表 Clear 闪烁
        while (FlowNodes.Count > desired.Count) FlowNodes.RemoveAt(FlowNodes.Count - 1);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < FlowNodes.Count)
            {
                var cur = FlowNodes[i];
                var next = desired[i];
                if (cur.Key != next.Key)
                {
                    FlowNodes[i] = next;
                    cur = next;
                }
                else if (cur.Title != next.Title)
                    cur.Title = next.Title;
                cur.IsEndFork = next.IsEndFork;
            }
            else FlowNodes.Add(desired[i]);
        }

        for (var i = 0; i < FlowNodes.Count; i++)
        {
            var node = FlowNodes[i];
            node.ShowArrowAfter = i < FlowNodes.Count - 1;
            node.ArrowActive = false;
            node.UnloadArrowActive = false;
            node.NgArrowActive = false;

            if (node.IsEndFork)
            {
                node.AggregateDisplay = "下料出站";
                node.SummaryText = "合格出站";
                node.StateBadge = "ok";
                node.ForkSecondaryTitle = "NG出站";
                node.ForkSecondaryAggregate = "不良出站";
                node.ForkSecondarySummary = "不良出站";
                node.ForkSecondaryBadge = "ng";
                node.Seg1Text = "—";
                node.Seg1Badge = "idle";
                node.Seg2Text = "—";
                node.Seg2Badge = "idle";
                node.IsSelected = false;
                continue;
            }

            if (node.IsEquipment && node.EquipmentId is long eqId && _machines.TryGetValue(eqId, out var mach))
            {
                ApplyEquipmentAggregate(node, mach);
                var inboundBusy = mach.Positions.Any(p =>
                    p.State is PositionState.Dispatching or PositionState.Transporting);
                if (i > 0) FlowNodes[i - 1].ArrowActive = inboundBusy;
            }
            else
            {
                node.AggregateDisplay = "示意";
                node.SummaryText = "流向起点";
                node.StateBadge = "idle";
                node.Seg1Text = "起点";
                node.Seg1Badge = "idle";
                node.Seg2Text = "—";
                node.Seg2Badge = "idle";
            }

            node.IsSelected = node.IsEquipment && SelectedFlowEquipmentId == node.EquipmentId;
        }

        var fork = FlowNodes.LastOrDefault(n => n.IsEndFork);
        var lastEq = FlowNodes.LastOrDefault(n => n.IsEquipment);
        if (fork is not null && lastEq?.EquipmentId is long lastId
            && _machines.TryGetValue(lastId, out var lastMach))
        {
            var outbound = lastMach.Positions.Any(p =>
                p.State is PositionState.Dispatching or PositionState.Transporting);
            var toNg = lastMach.Positions.Any(p => p.State == PositionState.DoneNg);
            var toOk = lastMach.Positions.Any(p => p.State is PositionState.DoneOk or PositionState.Unloaded);
            fork.UnloadArrowActive = outbound || toOk;
            fork.NgArrowActive = outbound || toNg;
            lastEq.ArrowActive = fork.UnloadArrowActive || fork.NgArrowActive;
        }
    }

    private static void ApplyEquipmentAggregate(FlowNodeVm node, MachineCardVm mach)
    {
        var positions = mach.Positions.OrderBy(p => p.PositionId).ToList();
        if (positions.Count == 0)
        {
            node.AggregateDisplay = mach.PlcOnline ? "等待上料" : "离线";
            node.StateBadge = mach.PlcOnline ? "idle" : "offline";
            node.SummaryText = "无加工位";
            node.Seg1Text = "—";
            node.Seg1Badge = "idle";
            node.Seg2Text = "—";
            node.Seg2Badge = "idle";
            return;
        }

        var agg = AggregatePriority(positions.Select(p => p.State));
        node.AggregateDisplay = PositionStateNames.ToDisplay(agg);
        node.StateBadge = BadgeForAggregate(agg);

        var parts = new List<string>();
        void Add(string label, Func<PositionCardVm, bool> pred)
        {
            var n = positions.Count(pred);
            if (n > 0) parts.Add($"{n} {label}");
        }
        Add("报警", p => p.State == PositionState.Alarm);
        Add("检测中", p => p.State == PositionState.Processing);
        Add("搬运", p => p.State is PositionState.Dispatching or PositionState.Transporting);
        Add("已上料", p => p.State == PositionState.Loaded);
        Add("待料", p => p.State is PositionState.WaitLoad or PositionState.Unloaded);
        Add("OK", p => p.State == PositionState.DoneOk);
        Add("NG", p => p.State == PositionState.DoneNg);
        Add("离线", p => p.State == PositionState.Offline);
        node.SummaryText = parts.Count > 0 ? string.Join(" · ", parts.Take(2)) : "—";

        var (t1, b1) = SegFor(positions[0].State);
        node.Seg1Text = t1;
        node.Seg1Badge = b1;
        if (positions.Count > 1)
        {
            var (t2, b2) = SegFor(positions[1].State);
            node.Seg2Text = t2;
            node.Seg2Badge = b2;
        }
        else
        {
            node.Seg2Text = "—";
            node.Seg2Badge = "idle";
        }
    }

    private static (string Text, string Badge) SegFor(PositionState s) => s switch
    {
        PositionState.Alarm => ("报警", "alarm"),
        PositionState.Processing => ("检测", "run"),
        PositionState.Dispatching or PositionState.Transporting => ("搬运", "run"),
        PositionState.Loaded => ("已上料", "warn"),
        PositionState.DoneOk => ("OK", "ok"),
        PositionState.DoneNg => ("NG", "ng"),
        PositionState.Offline => ("离线", "offline"),
        PositionState.WaitLoad or PositionState.Unloaded => ("待料", "idle"),
        _ => ("—", "idle")
    };

    private static PositionState AggregatePriority(IEnumerable<PositionState> states)
    {
        var list = states.ToList();
        if (list.Count == 0) return PositionState.Offline;
        if (list.Any(s => s == PositionState.Alarm)) return PositionState.Alarm;
        if (list.Any(s => s is PositionState.Processing or PositionState.Dispatching or PositionState.Transporting))
            return list.Any(s => s == PositionState.Processing) ? PositionState.Processing : PositionState.Transporting;
        if (list.Any(s => s == PositionState.Loaded)) return PositionState.Loaded;
        if (list.Any(s => s is PositionState.DoneOk or PositionState.DoneNg))
            return list.Any(s => s == PositionState.DoneNg) ? PositionState.DoneNg : PositionState.DoneOk;
        if (list.Any(s => s is PositionState.WaitLoad or PositionState.Unloaded)) return PositionState.WaitLoad;
        if (list.All(s => s == PositionState.Offline)) return PositionState.Offline;
        return PositionState.WaitLoad;
    }

    private static string BadgeForAggregate(PositionState s) => s switch
    {
        PositionState.Offline => "offline",
        PositionState.Alarm => "alarm",
        PositionState.Processing or PositionState.Dispatching or PositionState.Transporting => "run",
        PositionState.DoneOk => "ok",
        PositionState.DoneNg => "ng",
        PositionState.Loaded => "warn",
        _ => "idle"
    };

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
                AlarmSubText = Alarms.Count > 0
                    ? $"最近 {Alarms[0].TimeText}"
                    : "暂无未处理";
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
                    RecentRecords.Add(WorkRecordFeedItem.From(r, ShortEquipmentName(eqName)));
                }
                RecordsEmpty = RecentRecords.Count == 0;
            });
        }
        catch { /* DB 未就绪时静默 */ }
    }

    /// <summary>下拉 DisplayName「内长宽 (EQ01)」→ 记录表只留短名，避免窄列溢出叠字。</summary>
    private static string ShortEquipmentName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "—";
        var i = displayName.LastIndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? displayName[..i] : displayName;
    }
}

/// <summary>产线流工序节点（看板顶部横向总览）。末尾可用 IsEndFork 表示下料/NG 分叉。</summary>
public sealed partial class FlowNodeVm : ObservableObject
{
    public FlowNodeVm(string key, string title, long? equipmentId, bool isAnchor)
    {
        Key = key;
        Title = title;
        EquipmentId = equipmentId;
        IsAnchor = isAnchor;
    }

    public string Key { get; }
    public long? EquipmentId { get; }
    public bool IsAnchor { get; }
    public bool IsEquipment => !IsAnchor && EquipmentId is not null && !IsEndFork;
    public string EquipmentCode => EquipmentId is long id ? $"EQ{id:D2}" : "";

    /// <summary>终点分叉：主卡=下料，副卡=NG。</summary>
    public bool IsEndFork { get; set; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _aggregateDisplay = "—";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _stateBadge = "idle";
    [ObservableProperty] private bool _showArrowAfter;
    [ObservableProperty] private bool _arrowActive;
    [ObservableProperty] private bool _isSelected;

    // 原型 st-slots 双槽
    [ObservableProperty] private string _seg1Text = "—";
    [ObservableProperty] private string _seg1Badge = "idle";
    [ObservableProperty] private string _seg2Text = "—";
    [ObservableProperty] private string _seg2Badge = "idle";

    // 分叉副支（NG）
    [ObservableProperty] private string _forkSecondaryTitle = "NG 出站";
    [ObservableProperty] private string _forkSecondaryAggregate = "不良出站";
    [ObservableProperty] private string _forkSecondarySummary = "不良出站";
    [ObservableProperty] private string _forkSecondaryBadge = "ng";
    [ObservableProperty] private bool _unloadArrowActive;
    [ObservableProperty] private bool _ngArrowActive;
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
    [ObservableProperty] private bool _isHighlighted;

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

/// <summary>加工位色块卡（挂在机台卡下）。</summary>
public sealed partial class PositionCardVm : ObservableObject
{
    public PositionCardVm(long equipmentId, long positionId, PositionState state, string? materialId,
        string? statusDetail,
        bool plcOnline, bool? safe, bool? doorOpen)
    {
        EquipmentId = equipmentId;
        PositionId = positionId;
        _state = state;
        _materialId = materialId;
        _statusDetail = statusDetail;
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
    [NotifyPropertyChangedFor(nameof(IsVerdict))]
    [NotifyPropertyChangedFor(nameof(VerdictText))]
    private PositionState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaterialText))]
    private string? _materialId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateDisplay))]
    private string? _statusDetail;

    public bool IsAlarm => State == PositionState.Alarm;
    /// <summary>判定态（OK/NG/报警）用大号等宽展示——看板签名元素。</summary>
    public bool IsVerdict => State is PositionState.DoneOk or PositionState.DoneNg or PositionState.Alarm;
    public string VerdictText => State switch
    {
        PositionState.DoneOk => "OK",
        PositionState.DoneNg => "NG",
        PositionState.Alarm => "ALM",
        _ => ""
    };
    public string MaterialText => string.IsNullOrWhiteSpace(MaterialId) ? "—" : MaterialId!;

    [ObservableProperty] private bool _plcOnline;
    [ObservableProperty] private bool? _safe;
    [ObservableProperty] private bool? _doorOpen;

    public string EquipmentText => $"EQ{EquipmentId}";
    public string PositionText => $"工位{PositionId}";
    public string StateDisplay => StatusDetail ?? PositionStateNames.ToDisplay(State);
    public string StateBadge => State switch
    {
        PositionState.Offline => "offline",
        PositionState.Alarm => "alarm",
        PositionState.Processing => "run",
        PositionState.DoneOk => "ok",
        PositionState.DoneNg => "ng",
        PositionState.Dispatching or PositionState.Transporting => "run",
        PositionState.Loaded => "warn",
        _ => "idle"
    };

    public void Update(PositionState state, string? materialId, string? statusDetail,
        bool plcOnline, bool? safe, bool? doorOpen)
    {
        State = state;
        MaterialId = materialId;
        StatusDetail = statusDetail;
        PlcOnline = plcOnline;
        Safe = safe;
        DoorOpen = doorOpen;
    }
}

/// <summary>看板底部最近加工记录行。</summary>
public sealed class WorkRecordFeedItem
{
    public long Id { get; init; }
    public DateTime? SortTime { get; init; }
    public string TimeText { get; init; } = "";
    public string EquipmentText { get; init; } = "";
    public string PositionText { get; init; } = "";
    public string MaterialText { get; init; } = "";
    public string ResultText { get; init; } = "";
    public string ResultBadge { get; init; } = "idle";
    public int SortElapsed { get; init; }
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
        var sortTime = r.WorkEndTime ?? r.WorkStartTime;
        return new WorkRecordFeedItem
        {
            Id = r.Id,
            SortTime = sortTime,
            TimeText = sortTime?.ToString("HH:mm:ss") ?? "—",
            EquipmentText = string.IsNullOrWhiteSpace(equipmentName) ? $"EQ{r.EquipmentId:D2}" : equipmentName,
            PositionText = FormatPosition(r.PositionCode),
            MaterialText = string.IsNullOrWhiteSpace(r.MaterialId) ? "—" : r.MaterialId!,
            ResultText = resultText,
            ResultBadge = badge,
            SortElapsed = r.ElapsedSeconds ?? -1,
            ElapsedText = r.ElapsedSeconds is int s ? $"{s}s" : "—"
        };
    }

    /// <summary>POS-1 / POS1 → 工位1，避免窄列显示成 POS…</summary>
    private static string FormatPosition(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";
        var c = code.Trim();
        if (c.StartsWith("POS-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(c.AsSpan("POS-".Length), out var idDash))
            return $"工位{idDash}";
        if (c.StartsWith("POS", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(c.AsSpan(3), out var id))
            return $"工位{id}";
        var p = c.LastIndexOf('P');
        if (p >= 0 && p + 1 < c.Length && int.TryParse(c.AsSpan(p + 1), out var idP))
            return $"工位{idP}";
        return c;
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

    public bool IsHandled => State is "1" or "已处理";
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
