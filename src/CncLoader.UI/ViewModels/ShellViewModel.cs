using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Core.State;
using CncLoader.UI.Navigation;

namespace CncLoader.UI.ViewModels;

/// <summary>
/// 主窗口外壳 ViewModel：扁平导航、当前页路由、全局状态（线体/PLC在线/告警/时钟/心跳）。
/// </summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly INavigationService _navigation;
    private readonly ISignalStateStore _store;
    private readonly DispatcherTimer _timer;
    private long _heartbeat;

    public ShellViewModel(INavigationService navigation, ISignalStateStore store)
    {
        _navigation = navigation;
        _store = store;
        _navigation.Navigated += OnNavigated;
        _store.MachineChanged += OnMachineChanged;

        NavItems = new ObservableCollection<NavItem>(BuildNavItems());
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        UpdateClock();
    }

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem? _selectedNavItem;
    [ObservableProperty] private PageViewModelBase? _currentPage;
    [ObservableProperty] private string _currentTitle = "监控看板";
    [ObservableProperty] private string _workLineName = "未选择线体";
    [ObservableProperty] private string _plcStatusText = "PLC --/--";
    [ObservableProperty] private bool _plcAllOnline;
    [ObservableProperty] private int _unhandledAlarms;
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private string _heartbeatText = "000000";

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is not null) _navigation.NavigateTo(value.Key);
    }

    /// <summary>外部（App）注入初始线体名等上下文。</summary>
    public void SetContext(string workLineName) => WorkLineName = workLineName;

    /// <summary>初始化导航到默认页（监控看板）。</summary>
    public void Start()
    {
        SelectedNavItem = NavItems.FirstOrDefault(i => i.Key == "dash");
        RefreshPlcStatus();
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
    }

    private void UpdateClock() => Clock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static IEnumerable<NavItem> BuildNavItems() =>
    [
        new("dash",  "监控看板",   "运行", Icons.Dashboard),
        new("line",  "线体管理",   "配置", Icons.Line),
        new("craft", "工序管理",   "配置", Icons.Craft),
        new("eq",    "机台管理",   "配置", Icons.Equipment),
        new("plc",   "PLC 管理",   "设备", Icons.Plc),
        new("agv",   "AGV 管理",   "设备", Icons.Agv),
        new("scan",  "扫码枪管理", "设备", Icons.Scan),
        new("frame", "料架管理",   "物流", Icons.Frame),
    ];

    public void Dispose()
    {
        _timer.Stop();
        _navigation.Navigated -= OnNavigated;
        _store.MachineChanged -= OnMachineChanged;
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
    public const string Agv = "M3,9 H17 V16 H3 Z M17,11 H20 L21,14 M6,16 a1.6,1.6 0 1 0 0.01,0 M14,16 a1.6,1.6 0 1 0 0.01,0";
    public const string Scan = "M4,7 V5 H7 M20,7 V5 H17 M4,17 V19 H7 M20,17 V19 H17 M7,12 H17";
    public const string Frame = "M3,3 H21 V21 H3 Z M3,9 H21 M3,15 H21 M9,3 V21";
}
