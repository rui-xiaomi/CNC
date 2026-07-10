using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.State;
using CncLoader.UI.Navigation;

namespace CncLoader.UI.ViewModels;

/// <summary>
/// 主窗口外壳 ViewModel：扁平导航、当前页路由、全局状态（线体/PLC在线/告警/时钟/心跳）。
/// 标题栏线体为可切换下拉，数据来自 <see cref="IWorkLineService"/>；DB 未就绪时回退为占位项。
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly ISignalStateStore _store;
    private readonly IWorkLineService _workLineService;
    private readonly IPlcCatalogService _plcCatalog;
    private readonly IAlarmEventService _alarms;
    private readonly DispatcherTimer _timer;
    private long _heartbeat;

    public ShellViewModel(INavigationService navigation, ISignalStateStore store,
        IWorkLineService workLineService, IPlcCatalogService plcCatalog, IAlarmEventService alarms)
    {
        _navigation = navigation;
        _store = store;
        _workLineService = workLineService;
        _plcCatalog = plcCatalog;
        _alarms = alarms;
        _navigation.Navigated += OnNavigated;
        _store.MachineChanged += OnMachineChanged;
        _alarms.AlarmRaised += OnAlarmRaised;
        _alarms.AlarmsChanged += (_, _) => _ = RefreshAlarmCountAsync();
        _workLineService.WorkLinesChanged += OnWorkLinesChanged;

        NavItems = new ObservableCollection<NavItem>(BuildNavItems());
        WorkLines = new ObservableCollection<WorkLineListItem>();
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        UpdateClock();
        _ = RefreshAlarmCountAsync();
    }

    public ObservableCollection<NavItem> NavItems { get; }
    public ObservableCollection<WorkLineListItem> WorkLines { get; }

    [ObservableProperty] private NavItem? _selectedNavItem;
    [ObservableProperty] private PageViewModelBase? _currentPage;
    [ObservableProperty] private string _currentTitle = "监控看板";
    [ObservableProperty] private WorkLineListItem? _selectedWorkLine;
    [ObservableProperty] private string _workLineName = "未选择线体";
    [ObservableProperty] private string _plcStatusText = "PLC --/--";
    [ObservableProperty] private string _plcProtocolText = "PLC --";
    [ObservableProperty] private bool _plcAllOnline;
    [ObservableProperty] private int _unhandledAlarms;
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _heartbeatText = "000000";

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is not null) _navigation.NavigateTo(value.Key);
    }

    partial void OnSelectedWorkLineChanged(WorkLineListItem? value)
    {
        WorkLineName = value is null ? "未选择线体" : $"{value.Name} ({value.Code})";
    }

    /// <summary>初始化导航到默认页（监控看板），并异步加载线体下拉。</summary>
    public void Start()
    {
        SelectedNavItem = NavItems.FirstOrDefault(i => i.Key == "dash");
        RefreshPlcStatus();
        _ = LoadWorkLinesAsync();
        _ = LoadPlcProtocolsAsync();
    }

    /// <summary>读取已配置 PLC 的协议，标题栏按实际协议显示（多协议并存时用 "/" 连接）。</summary>
    private async Task LoadPlcProtocolsAsync()
    {
        try
        {
            var plcs = await _plcCatalog.GetAllAsync();
            var protocols = plcs
                .Select(p => string.IsNullOrWhiteSpace(p.Protocol) ? "ModbusTCP" : p.Protocol)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            PlcProtocolText = protocols.Count == 0 ? "PLC --" : $"PLC {string.Join("/", protocols)}";
        }
        catch
        {
            // DB 未就绪等：保持占位，不阻塞 UI。
            PlcProtocolText = "PLC --";
        }
    }

    private void OnWorkLinesChanged(object? sender, EventArgs e)
        => Application.Current?.Dispatcher.BeginInvoke(() => _ = LoadWorkLinesAsync());

    private async Task LoadWorkLinesAsync()
    {
        try
        {
            var keepId = SelectedWorkLine?.Id;
            var lines = await _workLineService.GetAllAsync();
            WorkLines.Clear();
            foreach (var l in lines) WorkLines.Add(l);
            // 优先保持当前选中线体；改名后用同 Id 新对象刷新标题栏文案
            SelectedWorkLine = (keepId is long id ? WorkLines.FirstOrDefault(l => l.Id == id) : null)
                               ?? WorkLines.FirstOrDefault();
            // 同引用未变时也强制刷一次显示名（防御）
            if (SelectedWorkLine is not null)
                WorkLineName = $"{SelectedWorkLine.Name} ({SelectedWorkLine.Code})";
        }
        catch
        {
            // DB 未就绪等：保持空下拉，标题栏显示"未选择线体"，不阻塞 UI。
            WorkLines.Clear();
            SelectedWorkLine = null;
        }
    }

    [RelayCommand]
    private void Navigate(string key)
    {
        var item = NavItems.FirstOrDefault(i => i.Key == key);
        if (item is not null) SelectedNavItem = item;
    }

    private void OnNavigated(object? sender, PageViewModelBase page)
    {
        CurrentPage = page;
        CurrentTitle = page.Title;
    }

    private void OnMachineChanged(object? sender, MachineStatus e) => RefreshPlcStatus();

    private void RefreshPlcStatus()
    {
        var machines = _store.GetAllMachines();
        var total = machines.Count;
        var online = machines.Count(m => m.PlcOnline);
        PlcAllOnline = total > 0 && online == total;
        PlcStatusText = total == 0 ? "PLC --/--" : $"PLC {online}/{total}";
    }

    private void OnTick()
    {
        _heartbeat++;
        HeartbeatText = _heartbeat.ToString("D6");
        UpdateClock();
        // 每 5s 刷新未处理告警角标（覆盖告警产生/标记已处理/清空等各来源，统一在 UI 线程）
        if (_heartbeat % 5 == 0) _ = RefreshAlarmCountAsync();
    }

    private void OnAlarmRaised(object? sender, AlarmRow e)
    {
        _ = RefreshAlarmCountAsync();
        Application.Current?.Dispatcher.BeginInvoke(() => ShowAlarmNotification(e));
    }

    /// <summary>
    /// 需人工介入的 RCS 任务告警弹模态框；其余严重告警 Growl，普通告警 Growl.Warning。
    /// </summary>
    private static void ShowAlarmNotification(AlarmRow e)
    {
        var title = e.AlarmType switch
        {
            "RCS_CANCELED" => "RCS 任务已取消",
            "RCS_REDO_LIMIT" => "RCS 自动重做达上限",
            "RCS_NOT_FOUND" => "RCS 任务查无此单",
            "RCS_WARN" => "RCS 严重告警",
            "PLC_COMM" => "PLC 通信告警",
            _ => string.IsNullOrWhiteSpace(e.AlarmType) ? "系统告警" : $"告警 · {e.AlarmType}"
        };

        if (e.AlarmType is "RCS_CANCELED" or "RCS_REDO_LIMIT" or "RCS_NOT_FOUND")
        {
            HandyControl.Controls.MessageBox.Show(
                e.Message + "\n\n请到「RCS 任务」或「日志/告警」页跟进处理。",
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (e.Level == "严重")
            HandyControl.Controls.Growl.Error($"{title}：{e.Message}");
        else
            HandyControl.Controls.Growl.Warning($"{title}：{e.Message}");
    }

    private async Task RefreshAlarmCountAsync()
    {
        try { UnhandledAlarms = await _alarms.GetUnhandledCountAsync(); }
        catch { /* DB 未就绪等：保持上次值，不阻塞 UI */ }
    }

    private void UpdateClock() => Clock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static IEnumerable<NavItem> BuildNavItems() =>
    [
        new("dash",  "监控看板",   "运行", Icons.Dashboard),
        new("line",  "线体管理",   "配置", Icons.Line),
        new("craft", "工序管理",   "配置", Icons.Craft),
        new("eq",    "机台管理",   "配置", Icons.Equipment),
        new("plc",   "PLC 管理",   "设备", Icons.Plc),
        new("rcs",   "RCS 任务",   "物流", Icons.Agv),
        new("agv",   "AGV 管理",   "设备", Icons.Agv),
        new("scan",  "扫码枪管理", "设备", Icons.Scan),
        new("frame", "料架管理",   "物流", Icons.Frame),
        new("log",   "日志/告警",  "运行", Icons.Dashboard),
    ];

    public void Dispose()
    {
        _timer.Stop();
        _navigation.Navigated -= OnNavigated;
        _store.MachineChanged -= OnMachineChanged;
        _alarms.AlarmRaised -= OnAlarmRaised;
        _workLineService.WorkLinesChanged -= OnWorkLinesChanged;
    }
}

/// <summary>侧栏线性图标的 Path Geometry 数据（24×24，1.5px 描边风格）。</summary>
internal static class Icons
{
    public const string Dashboard = "M3,3 H10 V12 H3 Z M14,3 H21 V8 H14 Z M14,12 H21 V21 H14 Z M3,16 H10 V21 H3 Z";
    public const string Line = "M4,7 H20 M4,12 H20 M4,17 H20";
    public const string Craft = "M4,6 H20 V10 H4 Z M4,14 H14 V18 H4 Z";
    public const string Equipment = "M4,4 H20 V20 H4 Z M9,9 H15 V15 H9 Z";
    public const string Plc = "M3,6 H21 V18 H3 Z M7,10 V14 M11,10 V14 M15,10 V14";
    public const string Point = "M4,5 H20 M4,9 H14 M4,13 H18 M4,17 H10 M17,15 L20,18 L17,21";
    public const string Agv = "M3,9 H17 V16 H3 Z M17,11 H20 L21,14 M6,16 a1.6,1.6 0 1 0 0.01,0 M14,16 a1.6,1.6 0 1 0 0.01,0";
    public const string Scan = "M4,7 V5 H7 M20,7 V5 H17 M4,17 V19 H7 M20,17 V19 H17 M7,12 H17";
    public const string Frame = "M3,3 H21 V21 H3 Z M3,9 H21 M3,15 H21 M9,3 V21";
}
