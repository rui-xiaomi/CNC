using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using CncLoader.Common.Configuration;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Options;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// RCS 任务管理页（第四阶段①骨架）：连接配置展示 + 手动下发测试（搬运/抓取/识别）+
/// 取消/redo/查询 + 任务列表 + 报文流水 + 位置映射(LOCATION_MAP)录入。
/// 后续步骤②③④在此基础上接入回调/模拟器/跟踪器。
/// </summary>
public sealed partial class RcsViewModel : PageViewModelBase
{
    private const string RouteUnavailableUiMessage = "路由配置已禁用或不可用";

    private readonly IRcsTaskService _rcs;
    private readonly ILocationMapService _locationMap;
    private readonly IWorkLineService _workLineService;
    private readonly IEquipmentConfigService _equipment;
    private readonly IFrameService _frames;
    private readonly IRcsCallbackNotifier _callbacks;
    private readonly IChangeFrameOrchestrator _changeFrame;
    private readonly IRcsConnectionConfigService _connConfig;
    private readonly IRcsRuntimeConfig _runtime;
    private readonly IRcsCallbackListener _callbackListener;
    private readonly IPositionScheduler _scheduler;
    private readonly ICurrentUser _user;
    private readonly IManagedDispatchRouteResolver _routeResolver;
    private readonly IRoutingAvailabilityValidator _routingValidator;
    private readonly IUserNotificationService _notify;
    private readonly IUiDispatcher _ui;
    private readonly RcsOptions _options;

    private long _workLineId;
    private long _agvId;
    private long _connectionConfigId;
    private string _lineCode = "LINE";
    private bool _suppressPositionReload;
    private bool _suppressLocTypeSideEffects;
    /// <summary>真实 RCS 模式下「测试连接」成功时锁定的 BaseUrl+ClientCode；配置变更即失效。</summary>
    private string? _verifiedConnectionKey;
    private bool _suppressConnectionVerifyInvalidation;
    private bool _suppressPauseSideEffects;

    public RcsViewModel(IRcsTaskService rcs, ILocationMapService locationMap,
        IWorkLineService workLineService, IEquipmentConfigService equipment, IFrameService frames,
        IRcsCallbackNotifier callbacks, IChangeFrameOrchestrator changeFrame,
        IRcsConnectionConfigService connConfig, IRcsRuntimeConfig runtime,
        IRcsCallbackListener callbackListener, IPositionScheduler scheduler, ICurrentUser user,
        IManagedDispatchRouteResolver routeResolver, IRoutingAvailabilityValidator routingValidator,
        IUserNotificationService notify, IUiDispatcher ui,
        IOptions<AppOptions> options)
    {
        _rcs = rcs;
        _locationMap = locationMap;
        _workLineService = workLineService;
        _equipment = equipment;
        _frames = frames;
        _callbacks = callbacks;
        _changeFrame = changeFrame;
        _connConfig = connConfig;
        _runtime = runtime;
        _callbackListener = callbackListener;
        _scheduler = scheduler;
        _user = user;
        _routeResolver = routeResolver;
        _routingValidator = routingValidator;
        _notify = notify;
        _ui = ui;
        _options = options.Value.Rcs;

        KindOptions = new[] { "搬运", "抓取", "识别" };
        LocTypeOptions = new[] { "区域", "机台", "加工位", "料架" };
        LocFilterTypeOptions = new[] { "全部", "区域", "机台", "加工位", "料架" };
        RcsTypeOptions = new[] { "站点", "仓位", "料架站" };
        AreaNameOptions = new[] { "上料区", "下料区", "满架缓存区", "空架缓存区", "托盘回收区" };
        FilteredLocations = new ObservableCollection<LocationMapItem>();
        MsgDirectionOptions = new[] { "全部", "出站", "入站" };
        MsgInterfaceOptions = new[] { "全部", "搬运下发", "定制任务", "取消任务", "查询任务",
            "状态回调", "扫码回调", "告警回调" };
        MsgLimitOptions = new[] { 100, 500, 1000, 2000 };
        TerminalLines = new ObservableCollection<string>();
        Tasks = new ObservableCollection<RcsTaskRow>();
        Messages = new ObservableCollection<RcsMsgRow>();
        Locations = new ObservableCollection<LocationMapItem>();
        LocEquipmentOptions = new ObservableCollection<NamedOption>();
        LocPositionOptions = new ObservableCollection<NamedOption>();
        LocFrameOptions = new ObservableCollection<NamedOption>();

        LoadConnectionFromRuntime();
        RefreshModeBanner();
        _suppressPauseSideEffects = true;
        try { PauseAutoDispatch = _scheduler.IsAutoDispatchPaused; }
        finally { _suppressPauseSideEffects = false; }
        RefreshAutoDispatchStatusText();

        _callbacks.TaskStatusReceived += OnTaskStatusReceived;
        _callbacks.ScanResultReceived += OnScanResultReceived;
        _callbacks.WarnReceived += OnWarnReceived;

        _changeFrame.ProgressChanged += OnChangeFrameProgress;

        _ = InitializeAsync();
    }

    private void LoadConnectionFromRuntime()
    {
        var snap = _runtime.Snapshot();
        _suppressConnectionVerifyInvalidation = true;
        try
        {
            BaseUrl = snap.BaseUrl;
            ClientCode = snap.ClientCode;
            CallbackHost = snap.CallbackHost;
            CallbackPort = snap.CallbackPort;
            RequestTimeoutMs = snap.RequestTimeoutMs;
            MaxRetries = snap.MaxRetries;
            PollIntervalMs = snap.PollIntervalMs;
        }
        finally { _suppressConnectionVerifyInvalidation = false; }
        EffectiveBaseUrl = snap.BaseUrl;
        RefreshModeBanner();
    }

    private void RefreshModeBanner()
    {
        if (_options.UseSimulator)
        {
            RcsModeText = "模拟器";
            RcsModeBrushKey = "WarnBrush";
            RcsModeHint = "当前为 RCS 模拟器模式（Rcs.UseSimulator=true）";
        }
        else
        {
            RcsModeText = "真实 RCS";
            RcsModeBrushKey = "AccentBrush";
            RcsModeHint = "当前对接真实 RCS（Rcs.UseSimulator=false）";
        }
    }

    private void RefreshAutoDispatchStatusText()
    {
        AutoDispatchStatusText = PauseAutoDispatch
            ? "已暂停自动派工：调度器不再产生/下发新的自动上料、下料任务；手工下发与任务跟踪仍可用"
            : "自动派工运行中（默认）";
        AutoDispatchStatusBrushKey = PauseAutoDispatch ? "WarnBrush" : "OkBrush";
    }

    private static string ConnectionKey(string? baseUrl, string? clientCode)
        => $"{(baseUrl ?? "").Trim()}|{ (clientCode ?? "").Trim()}";

    private void InvalidateConnectionVerification(string reason)
    {
        if (_verifiedConnectionKey is null)
        {
            RefreshDispatchGateHint();
            return;
        }
        _verifiedConnectionKey = null;
        if (ConnectionHealthBrushKey == "OkBrush")
        {
            ConnectionHealthText = "需重新测试";
            ConnectionHealthBrushKey = "WarnBrush";
        }
        Append($"> 连通验证已失效：{reason}");
        RefreshDispatchGateHint();
    }

    private void RefreshDispatchGateHint()
    {
        if (_options.UseSimulator)
        {
            DispatchGateHint = "";
            return;
        }
        if (_verifiedConnectionKey is null
            || !string.Equals(_verifiedConnectionKey, ConnectionKey(BaseUrl, ClientCode), StringComparison.Ordinal))
        {
            DispatchGateHint = "请先测试真实 RCS 连接（修改地址/clientCode 后需重新测试）";
            return;
        }
        DispatchGateHint = "";
    }

    partial void OnBaseUrlChanged(string value)
    {
        if (_suppressConnectionVerifyInvalidation) return;
        InvalidateConnectionVerification("BaseUrl 已修改");
    }

    partial void OnClientCodeChanged(string value)
    {
        if (_suppressConnectionVerifyInvalidation) return;
        InvalidateConnectionVerification("ClientCode 已修改");
    }

    partial void OnPauseAutoDispatchChanged(bool value)
    {
        if (_suppressPauseSideEffects) return;
        _scheduler.SetAutoDispatchPaused(value);
        RefreshAutoDispatchStatusText();
        Append(value
            ? "> 已开启「暂停自动派工 / 仅手动测试」"
            : "> 已关闭「暂停自动派工」，恢复自动上下料派工");
        if (value)
            _notify.Warning("已暂停自动派工：仅允许本页手工下发测试。");
        else
            _notify.Success("已恢复自动派工。");
    }

    private void OnTaskStatusReceived(object? sender, RcsTaskStatusEvent e)
    {
        Append($"↩ {e.Source} {e.TaskId} error_code={e.ErrorCode} → {e.TaskState}");
        _ = RefreshOnCallbackAsync();
    }

    private void OnScanResultReceived(object? sender, RcsScanResultEvent e)
    {
        Append($"↩ scanTaskStatus {e.TaskId} 料架 {e.Code} 扫得 {e.Products.Count} 码 → {RcsErrorCode.ToTaskState(e.ErrorCode)}");
        _ = RefreshOnCallbackAsync();
    }

    private void OnWarnReceived(object? sender, RcsWarnEvent e)
    {
        Append($"⚠ warnCallback 车{e.RobotCode} {e.WarnContent}");
        if (AutoRefreshMessages) _ = RefreshMessagesAsync();
    }

    /// <summary>回调到达刷新：任务列表始终刷新；报文流水仅在"自动刷新"开启时刷新（避免筛选/排查时被刷走）。</summary>
    private async Task RefreshOnCallbackAsync()
    {
        await RefreshTasksAsync();
        if (AutoRefreshMessages) await RefreshMessagesAsync();
    }

    public override string Key => "rcs";
    public override string Title => "RCS 任务管理";
    public override string Description => "RCS 对接：任务下发、跟踪、报文流水与位置映射。";

    public string[] KindOptions { get; }
    public string[] LocTypeOptions { get; }
    public string[] LocFilterTypeOptions { get; }
    public string[] RcsTypeOptions { get; }
    public string[] AreaNameOptions { get; }
    public string[] MsgDirectionOptions { get; }
    public string[] MsgInterfaceOptions { get; }
    public int[] MsgLimitOptions { get; }
    public ObservableCollection<string> TerminalLines { get; }
    public ObservableCollection<RcsTaskRow> Tasks { get; }
    public ObservableCollection<RcsMsgRow> Messages { get; }
    public ObservableCollection<LocationMapItem> Locations { get; }
    /// <summary>位置映射列表（按类型筛选后）。</summary>
    public ObservableCollection<LocationMapItem> FilteredLocations { get; }
    public ObservableCollection<NamedOption> LocEquipmentOptions { get; }
    public ObservableCollection<NamedOption> LocPositionOptions { get; }
    public ObservableCollection<NamedOption> LocFrameOptions { get; }

    // 连接配置（可编辑，落库 MAS_AUTO_WORKLINE_AGV）
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _clientCode = "";
    [ObservableProperty] private string _callbackHost = "0.0.0.0";
    [ObservableProperty] private int _callbackPort = 9080;
    [ObservableProperty] private int _requestTimeoutMs = 10000;
    [ObservableProperty] private int _maxRetries = 3;
    [ObservableProperty] private int _pollIntervalMs = 3000;
    [ObservableProperty] private bool _isSavingConnection;
    [ObservableProperty] private bool _isTestingConnection;
    [ObservableProperty] private string _connectionHealthText = "未测试";
    [ObservableProperty] private string _connectionHealthBrushKey = "IdleBrush";
    [ObservableProperty] private bool _isTestingCallback;
    [ObservableProperty] private string _callbackHealthText = "未测试";
    [ObservableProperty] private string _callbackHealthBrushKey = "IdleBrush";

    /// <summary>Rcs.UseSimulator 模式文案：模拟器 / 真实 RCS。</summary>
    [ObservableProperty] private string _rcsModeText = "";
    [ObservableProperty] private string _rcsModeBrushKey = "IdleBrush";
    [ObservableProperty] private string _rcsModeHint = "";
    /// <summary>运行时实际生效的出站 BaseUrl（非猜测）。</summary>
    [ObservableProperty] private string _effectiveBaseUrl = "";
    /// <summary>暂停自动派工 / 仅手动测试（进程内）。</summary>
    [ObservableProperty] private bool _pauseAutoDispatch;
    [ObservableProperty] private string _autoDispatchStatusText = "";
    [ObservableProperty] private string _autoDispatchStatusBrushKey = "OkBrush";
    /// <summary>真实 RCS 下发门禁提示。</summary>
    [ObservableProperty] private string _dispatchGateHint = "";

    // 手动下发表单
    [ObservableProperty] private string _selectedKind = "搬运";
    [ObservableProperty] private string _fromCode = "201101";
    [ObservableProperty] private string _toCode = "201102";
    [ObservableProperty] private int _priority = 5;

    /// <summary>搬运：起终点都要。</summary>
    public bool ShowTransitParams => SelectedKind is "搬运" || SelectedKind.StartsWith("搬运");
    /// <summary>抓取：源/目标站 + 抓取孔位参数。</summary>
    public bool ShowGrabParams => SelectedKind is "抓取" || SelectedKind.StartsWith("抓取");
    /// <summary>识别：料架站 + 起始孔/数量。</summary>
    public bool ShowIdentifyParams => SelectedKind is "识别" || SelectedKind.StartsWith("识别");
    /// <summary>识别无终点；抓取终点=目标站。</summary>
    public bool ShowToCode => !ShowIdentifyParams;
    public string FromLabelText => ShowIdentifyParams ? "料架站" : (ShowGrabParams ? "源站" : "起点");
    public string ToLabelText => ShowGrabParams ? "目标站" : "终点";

    partial void OnSelectedKindChanged(string value)
    {
        OnPropertyChanged(nameof(ShowTransitParams));
        OnPropertyChanged(nameof(ShowGrabParams));
        OnPropertyChanged(nameof(ShowIdentifyParams));
        OnPropertyChanged(nameof(ShowToCode));
        OnPropertyChanged(nameof(FromLabelText));
        OnPropertyChanged(nameof(ToLabelText));
    }
    // 抓取参数（简化：单条 GrabItem）
    [ObservableProperty] private int _srcNo = 101;
    [ObservableProperty] private int _srcPos = 101;
    [ObservableProperty] private int _dstNo = 201;
    [ObservableProperty] private int _dstPos = 101;
    [ObservableProperty] private string _grabData = "100";
    // 识别参数
    [ObservableProperty] private int _posStart = 101;
    [ObservableProperty] private int _identifyCount = 3;
    // 取消/redo（任务列表点选回填 OperateTaskId）
    [ObservableProperty] private RcsTaskRow? _selectedTask;
    [ObservableProperty] private string _operateTaskId = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    partial void OnSelectedTaskChanged(RcsTaskRow? value)
    {
        if (!string.IsNullOrWhiteSpace(value?.RcsTaskId))
            OperateTaskId = value.RcsTaskId!;
    }

    // 报文流水筛选（服务端查询）+ 自动刷新开关
    [ObservableProperty] private string _msgFilterDirection = "全部";
    [ObservableProperty] private string _msgFilterInterface = "全部";
    [ObservableProperty] private string _msgFilterTaskId = "";
    [ObservableProperty] private int _msgLimit = 100;
    [ObservableProperty] private bool _autoRefreshMessages = true;
    [ObservableProperty] private RcsMsgRow? _selectedMessage;

    // 换架/空托盘回收（第四阶段⑥b）
    [ObservableProperty] private string _changeFrameEquipmentId = "1";
    [ObservableProperty] private string _changeFrameRole = "上料架";
    public string[] ChangeFrameRoleOptions { get; } = new[] { "上料架", "下料架" };
    [ObservableProperty] private string _palletReturnFromCode = "P100";
    public ObservableCollection<ChangeFrameProgressEvent> ChangeFrameTransactions { get; } = new();

    // 位置映射编辑（下拉存中文，保存时转英文码；机台/工位/料架用 NamedOption）
    [ObservableProperty] private LocationMapItem? _selectedLocation;
    [ObservableProperty] private string _locType = "区域";
    [ObservableProperty] private string _locRcsCode = "";
    [ObservableProperty] private string _locRcsType = "站点";
    [ObservableProperty] private string _locName = "";
    [ObservableProperty] private NamedOption? _selectedLocEquipment;
    [ObservableProperty] private NamedOption? _selectedLocPosition;
    [ObservableProperty] private NamedOption? _selectedLocFrame;
    [ObservableProperty] private long _editingLocId;
    [ObservableProperty] private string _locFilterType = "全部";
    [ObservableProperty] private string _locationCountText = "共 0 条";

    /// <summary>区域：名称用预设；其它类型名称作备注。</summary>
    public bool ShowLocAreaName => LocType is "区域";
    public bool ShowLocRemarkName => LocType is not "区域";
    /// <summary>机台 / 加工位：需选机台。</summary>
    public bool ShowLocEquipment => LocType is "机台" or "加工位";
    /// <summary>仅加工位：需选工位。</summary>
    public bool ShowLocPosition => LocType is "加工位";
    /// <summary>仅料架：需选料架。</summary>
    public bool ShowLocFrame => LocType is "料架";

    partial void OnLocTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowLocAreaName));
        OnPropertyChanged(nameof(ShowLocRemarkName));
        OnPropertyChanged(nameof(ShowLocEquipment));
        OnPropertyChanged(nameof(ShowLocPosition));
        OnPropertyChanged(nameof(ShowLocFrame));
        if (_suppressLocTypeSideEffects) return;

        // 切类型时清掉无关关联，避免误保存脏引用。
        if (value is "区域")
        {
            _suppressPositionReload = true;
            try
            {
                SelectedLocEquipment = NoneOption;
                SelectedLocPosition = NoneOption;
                SelectedLocFrame = NoneOption;
            }
            finally { _suppressPositionReload = false; }
            ReplaceOnUi(LocPositionOptions, new[] { NoneOption });
            if (string.IsNullOrWhiteSpace(LocName) || !AreaNameOptions.Contains(LocName))
                LocName = AreaNameOptions[0];
            LocRcsType = "站点";
        }
        else if (value is "料架")
        {
            _suppressPositionReload = true;
            try
            {
                SelectedLocEquipment = NoneOption;
                SelectedLocPosition = NoneOption;
            }
            finally { _suppressPositionReload = false; }
            ReplaceOnUi(LocPositionOptions, new[] { NoneOption });
            if (LocRcsType is "站点") LocRcsType = "料架站";
        }
        else if (value is "机台")
        {
            _suppressPositionReload = true;
            try
            {
                SelectedLocPosition = NoneOption;
                SelectedLocFrame = NoneOption;
            }
            finally { _suppressPositionReload = false; }
        }
        else if (value is "加工位")
        {
            _suppressPositionReload = true;
            try { SelectedLocFrame = NoneOption; }
            finally { _suppressPositionReload = false; }
            if (LocRcsType is "料架站") LocRcsType = "仓位";
        }
    }

    partial void OnLocFilterTypeChanged(string value) => ApplyLocationFilter();

    /// <summary>从线体列表刷新 RCS 下发用的线体 Id/编码/AGV（改名后编码变更也同步）。</summary>
    private async Task RefreshWorkLineContextAsync()
    {
        try
        {
            var lines = await _workLineService.GetAllAsync();
            var first = lines.FirstOrDefault(l => l.Id == _workLineId) ?? lines.FirstOrDefault();
            if (first is not null)
            {
                _workLineId = first.Id;
                _lineCode = string.IsNullOrWhiteSpace(first.Code) ? "LINE" : first.Code;
                _agvId = first.AgvId;
            }
        }
        catch { /* DB 未就绪：保持原值 */ }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await RefreshWorkLineContextAsync();
            _workLineService.WorkLinesChanged += (_, _) => _ = RefreshWorkLineContextAsync();
        }
        catch { /* DB 未就绪：用默认 LINE */ }

        try
        {
            var cfg = _agvId > 0
                ? await _connConfig.GetByWorkLineAgvIdAsync(_agvId)
                : await _connConfig.GetAsync();
            if (cfg is not null)
            {
                _connectionConfigId = cfg.Id;
                if (cfg.AgvId > 0) _agvId = cfg.AgvId;
                BaseUrl = cfg.BaseUrl;
                ClientCode = cfg.ClientCode;
                CallbackHost = cfg.CallbackHost;
                CallbackPort = cfg.CallbackPort;
                RequestTimeoutMs = cfg.RequestTimeoutMs;
                MaxRetries = cfg.MaxRetries;
                PollIntervalMs = cfg.PollIntervalMs;
            }
            else
                LoadConnectionFromRuntime();
        }
        catch { LoadConnectionFromRuntime(); }

        RefreshCallbackListenHint();
        EffectiveBaseUrl = _runtime.BaseUrl;
        RefreshModeBanner();
        RefreshDispatchGateHint();

        await LoadLocationRefOptionsAsync();
        await RefreshTasksAsync();
        await RefreshMessagesAsync();
        await RefreshLocationsAsync();
    }

    /// <summary>根据宿主启动结果刷新回调状态灯（未点「测试本机监听」时也能看到是否在听）。</summary>
    private void RefreshCallbackListenHint()
    {
        if (_callbackListener.IsListening)
        {
            CallbackHealthText = $"本机监听 {_callbackListener.BoundHost}:{_callbackListener.BoundPort}";
            CallbackHealthBrushKey = "OkBrush";
        }
        else
        {
            var err = _callbackListener.ListenError;
            CallbackHealthText = string.IsNullOrWhiteSpace(err) ? "未监听" : "启动失败";
            CallbackHealthBrushKey = "AlarmBrush";
        }
    }

    /// <summary>任意网卡 / 非法 IP / 环回 → 用 127.0.0.1 做本机探针。</summary>
    private static string ResolveLoopbackProbeHost(string? boundHost)
    {
        if (string.IsNullOrWhiteSpace(boundHost)) return "127.0.0.1";
        if (!System.Net.IPAddress.TryParse(boundHost, out var ip)) return "127.0.0.1";
        if (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)
            || System.Net.IPAddress.IsLoopback(ip))
            return "127.0.0.1";
        return ip.ToString();
    }

    [RelayCommand]
    private async Task SaveConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            _notify.Warning("请填写 RCS 地址。");
            return;
        }
        if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _notify.Warning("RCS 地址须为 http(s)://… 形式。");
            return;
        }
        if (string.IsNullOrWhiteSpace(ClientCode))
        {
            _notify.Warning("请填写 clientCode。");
            return;
        }
        if (CallbackPort is < 1 or > 65535)
        {
            _notify.Warning("回调端口无效。");
            return;
        }
        var callbackHost = string.IsNullOrWhiteSpace(CallbackHost) ? "0.0.0.0" : CallbackHost.Trim();
        if (!System.Net.IPAddress.TryParse(callbackHost, out var callbackIp))
        {
            _notify.Warning("回调 Host 须为合法 IP（如 0.0.0.0 或 127.0.0.1）。");
            return;
        }
        // 规范化书写（00.0.0.0 → 0.0.0.0），避免脏值落库导致测试回调连错地址。
        callbackHost = callbackIp.Equals(System.Net.IPAddress.Any) || callbackIp.Equals(System.Net.IPAddress.IPv6Any)
            ? "0.0.0.0"
            : callbackIp.ToString();

        IsSavingConnection = true;
        try
        {
            var draft = new RcsConnectionConfig
            {
                Id = _connectionConfigId,
                AgvId = _agvId > 0 ? _agvId : 1,
                BaseUrl = BaseUrl.Trim(),
                ClientCode = ClientCode.Trim(),
                CallbackHost = callbackHost,
                CallbackPort = CallbackPort,
                RequestTimeoutMs = RequestTimeoutMs,
                MaxRetries = MaxRetries,
                PollIntervalMs = PollIntervalMs
            };
            var saved = await _connConfig.SaveAsync(draft, _user.Name);
            _connectionConfigId = saved.Id;
            _agvId = saved.AgvId;
            _runtime.Apply(saved);
            LoadConnectionFromRuntime();
            // 保存可能改写出站目标；与已验证键不一致则失效（Load 用 suppress，此处显式比对）。
            if (!string.Equals(_verifiedConnectionKey, ConnectionKey(saved.BaseUrl, saved.ClientCode), StringComparison.Ordinal))
                InvalidateConnectionVerification("连接配置已保存且出站目标变更");
            else
                RefreshDispatchGateHint();

            var callbackChanged = !string.Equals(saved.CallbackHost, _runtime.BootCallbackHost, StringComparison.OrdinalIgnoreCase)
                                  || saved.CallbackPort != _runtime.BootCallbackPort;
            if (callbackChanged)
                _notify.Warning("已保存。回调 Host/Port 已变更，需重启客户端后生效。");
            else
                _notify.Success("RCS 连接配置已保存（出站立即生效）。");
            Append($"> 已保存连接配置 {saved.BaseUrl} client={saved.ClientCode}");
        }
        catch (Exception ex)
        {
            _notify.Error($"保存失败：{ex.Message}");
        }
        finally { IsSavingConnection = false; }
    }

    private async Task LoadLocationRefOptionsAsync()
    {
        try
        {
            var eqs = await _frames.GetEquipmentOptionsAsync();
            var frames = await _equipment.GetFrameOptionsAsync();
            ReplaceOnUi(LocEquipmentOptions, PrependNone(eqs));
            ReplaceOnUi(LocFrameOptions, PrependNone(frames));
            ReplaceOnUi(LocPositionOptions, new[] { NoneOption });
        }
        catch (Exception ex) { StatusMessage = $"位置映射下拉加载失败：{ex.Message}"; }
    }

    private static readonly NamedOption NoneOption = new(0, "（无）");

    private static IReadOnlyList<NamedOption> PrependNone(IReadOnlyList<NamedOption> items)
    {
        var list = new List<NamedOption>(items.Count + 1) { NoneOption };
        list.AddRange(items);
        return list;
    }

    partial void OnSelectedLocEquipmentChanged(NamedOption? value)
    {
        if (_suppressPositionReload) return;
        _ = ReloadPositionsForEquipmentAsync(value?.Id ?? 0, preferPositionId: null);
    }

    private async Task ReloadPositionsForEquipmentAsync(long equipmentId, long? preferPositionId)
    {
        try
        {
            IReadOnlyList<NamedOption> opts = new[] { NoneOption };
            if (equipmentId > 0)
            {
                var positions = await _equipment.GetPositionsAsync(equipmentId);
                opts = PrependNone(positions.Select(p => new NamedOption(p.Id, $"{p.Name}({p.Code})")).ToList());
            }
            ReplaceOnUi(LocPositionOptions, opts);
            var pick = preferPositionId is > 0
                ? LocPositionOptions.FirstOrDefault(x => x.Id == preferPositionId.Value) ?? NoneOption
                : NoneOption;
            _suppressPositionReload = true;
            try { SelectedLocPosition = pick; }
            finally { _suppressPositionReload = false; }
        }
        catch (Exception ex) { StatusMessage = $"工位下拉加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task DispatchAsync()
    {
        if (!TryValidateManualDispatch(out var from, out var to, out var error))
        {
            _notify.Warning(error);
            StatusMessage = error;
            return;
        }

        if (!_options.UseSimulator)
        {
            var key = ConnectionKey(BaseUrl, ClientCode);
            if (_verifiedConnectionKey is null
                || !string.Equals(_verifiedConnectionKey, key, StringComparison.Ordinal))
            {
                const string gate = "请先测试真实 RCS 连接";
                _notify.Warning(gate);
                StatusMessage = gate;
                RefreshDispatchGateHint();
                return;
            }

            var confirmMsg =
                $"当前 RCS BaseUrl：{_runtime.BaseUrl}\n" +
                $"任务类型：{SelectedKind}\n" +
                $"起点：{from}\n" +
                $"终点：{(ShowIdentifyParams ? "（识别无终点）" : to)}\n" +
                $"优先级：{Priority}\n\n" +
                "请确认现场人员、设备和路径已经清场";
            if (!_notify.Confirm(confirmMsg, "真实 RCS 下发确认"))
            {
                Append("> 用户取消真实 RCS 下发（未落库、未发 HTTP）");
                return;
            }
        }

        IsBusy = true;
        try
        {
            RcsResult r;
            if (ShowGrabParams)
            {
                Append($"> 抓取 {from} → {to} 孔位 {SrcNo}/{SrcPos}→{DstNo}/{DstPos}");
                r = await _rcs.DispatchGrabAsync(new GrabDispatchArgs
                {
                    WorkLineId = _workLineId,
                    LineCode = _lineCode,
                    Priority = Priority,
                    SrcStation = from,
                    DstStation = to,
                    Items = new[] { new GrabItem { SrcNo = SrcNo, SrcPos = SrcPos, DstNo = DstNo, DstPos = DstPos, Data = GrabData } }
                });
            }
            else if (ShowIdentifyParams)
            {
                Append($"> 识别 {from} 起始 {PosStart} 数量 {IdentifyCount}");
                r = await _rcs.DispatchIdentifyAsync(new IdentifyDispatchArgs
                {
                    WorkLineId = _workLineId,
                    LineCode = _lineCode,
                    Priority = Priority,
                    Station = from,
                    PosStart = PosStart,
                    Count = IdentifyCount
                });
            }
            else
            {
                // 手动搬运：Resolve → Validate（早期反馈）；发送边界再权威读一次
                if (!await TryValidateManagedTransitRouteAsync(from, to))
                    return;
                Append($"> 搬运 {from} → {to}");
                r = await _rcs.DispatchTransitAsync(new TransitDispatchArgs
                {
                    WorkLineId = _workLineId,
                    LineCode = _lineCode,
                    TaskType = "2",
                    Priority = Priority,
                    FromCode = from,
                    ToCode = to
                });
            }
            ReportResult(r);
        }
        catch (Exception ex)
        {
            _notify.Error($"下发异常：{ex.Message}");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshTasksAsync();
            await RefreshMessagesAsync();
            // 自动回填最新 taskId，方便直接 redo/取消，无需手动复制。
            if (Tasks.FirstOrDefault()?.RcsTaskId is { } newestId)
            {
                OperateTaskId = newestId;
                SelectedTask = Tasks.FirstOrDefault(t => t.RcsTaskId == newestId);
            }
        }
    }

    /// <summary>手工下发校验：优先级 1～10；搬运点到点起终点非空且不同。不生成假点位。</summary>
    private bool TryValidateManualDispatch(out string from, out string to, out string error)
    {
        from = (FromCode ?? "").Trim();
        to = (ToCode ?? "").Trim();
        error = "";

        if (Priority is < 1 or > 10)
        {
            error = "优先级须为协议允许范围 1～10。";
            return false;
        }

        if (ShowIdentifyParams)
        {
            if (string.IsNullOrWhiteSpace(from))
            {
                error = "请填写料架站编码。";
                return false;
            }
            return true;
        }

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            error = ShowGrabParams ? "请填写源站与目标站。" : "起点和终点不能为空。";
            return false;
        }

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            error = ShowGrabParams ? "源站与目标站不能相同。" : "起点和终点不能相同。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 手动搬运早期门禁：每次重新 Resolve + Validate；不缓存上次结论。
    /// 失败仅 Warning，不调用 Service。发送边界在 <see cref="IRcsTaskService"/> 再权威读一次。
    /// </summary>
    private async Task<bool> TryValidateManagedTransitRouteAsync(string from, string to)
    {
        try
        {
            var resolved = await _routeResolver.ResolveAsync(from, to);
            var ctx = resolved.IsResolved
                ? resolved.Context!
                : new DispatchRouteContext
                {
                    SourceEquipmentId = 0,
                    DestEquipmentId = RouteDependency.RequiredMissing,
                    FromCode = from,
                    ToCode = to,
                    RequiresResolvedCells = true
                };

            var pre = await _routingValidator.ValidateAsync(ctx);
            if (resolved.IsResolved && pre.IsAvailable)
                return true;

            _notify.Warning(RouteUnavailableUiMessage);
            StatusMessage = RouteUnavailableUiMessage;
            Append($"> 拒绝下发：{RouteUnavailableUiMessage}");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _notify.Warning(RouteUnavailableUiMessage);
            StatusMessage = RouteUnavailableUiMessage;
            return false;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { _notify.Warning("请填任务号。"); return; }
        IsBusy = true;
        try
        {
            Append($"> cancelTask {OperateTaskId}");
            ReportResult(await _rcs.CancelAsync(OperateTaskId.Trim()));
        }
        finally { IsBusy = false; await RefreshTasksAsync(); await RefreshMessagesAsync(); }
    }

    [RelayCommand]
    private async Task RedoAsync()
    {
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { _notify.Warning("请填任务号。"); return; }
        IsBusy = true;
        try
        {
            Append($"> 重试 {OperateTaskId}");
            ReportResult(await _rcs.RedoAsync(OperateTaskId.Trim()));
        }
        finally { IsBusy = false; await RefreshTasksAsync(); await RefreshMessagesAsync(); }
    }

    [RelayCommand]
    private async Task ConfirmCancelHandledAsync()
    {
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { _notify.Warning("请填任务号。"); return; }
        try
        {
            await _rcs.ConfirmCancelHandledAsync(OperateTaskId.Trim());
            _notify.Success("已标记取消任务人工处理确认。");
            await RefreshTasksAsync();
        }
        catch (Exception ex) { _notify.Error($"确认失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task ChangeFrameAsync()
    {
        if (!long.TryParse(ChangeFrameEquipmentId?.Trim(), out var eqId) || eqId <= 0)
        {
            _notify.Warning("请填机台 ID（数字）。");
            return;
        }
        var role = ChangeFrameRole == "下料架" ? FrameRole.Unload : FrameRole.Upload;
        try
        {
            var txnId = await _changeFrame.ChangeFrameAsync(eqId, role, "operator");
            Append($"> 换架 {txnId}（机台{eqId} {ChangeFrameRole}）");
            _notify.Info($"换架已发起 {txnId}，进度见终端");
        }
        catch (Exception ex) { _notify.Error($"换架失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task PalletReturnAsync()
    {
        if (string.IsNullOrWhiteSpace(PalletReturnFromCode)) { _notify.Warning("请填回收点位编码。"); return; }
        try
        {
            var dest = (await _locationMap.ResolveAreaAsync(_options.PalletReturnArea))?.RcsCode;
            if (string.IsNullOrWhiteSpace(dest)) { _notify.Warning($"托盘回收区 {_options.PalletReturnArea} 未在 LOCATION_MAP 录入"); return; }
            Append($"> 空托盘回收 {PalletReturnFromCode} → {dest}");
            var r = await _rcs.DispatchPalletReturnAsync(0, null, PalletReturnFromCode.Trim(), dest, _workLineId, _lineCode, "operator");
            ReportResult(r);
        }
        catch (Exception ex) { _notify.Error($"回收失败：{ex.Message}"); }
    }

    private void OnChangeFrameProgress(object? sender, ChangeFrameProgressEvent e)
    {
        _ui.Invoke(() =>
        {
            Append($"↻ 换架 {e.TxnId} {e.Step} {e.State}{(string.IsNullOrEmpty(e.Message) ? "" : " " + e.Message)}");
            if (e.State == "COMPLETED") _notify.Success($"换架 {e.TxnId} 完成");
            else if (e.State == "FAILED" || e.Step == ChangeFrameStep.Alarm) _notify.Warning($"换架 {e.TxnId} 异常：{e.Message}");
        });
        _ = RefreshChangeFrameTransactionsAsync();
    }

    private async Task RefreshChangeFrameTransactionsAsync()
    {
        var rows = _changeFrame.GetActiveTransactions();
        _ui.Invoke(() =>
        {
            ChangeFrameTransactions.Clear();
            foreach (var r in rows) ChangeFrameTransactions.Add(r);
        });
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl)
            || !Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ConnectionHealthText = "地址无效";
            ConnectionHealthBrushKey = "AlarmBrush";
            _notify.Warning("请先填写有效的 RCS 地址。");
            return;
        }

        IsTestingConnection = true;
        ConnectionHealthText = "测试中…";
        ConnectionHealthBrushKey = "WarnBrush";
        try
        {
            // 用表单当前值测出站（未保存也可测）；不改回调启动快照。
            _runtime.Apply(new RcsConnectionConfig
            {
                Id = _connectionConfigId,
                AgvId = _agvId,
                BaseUrl = BaseUrl.Trim(),
                ClientCode = string.IsNullOrWhiteSpace(ClientCode) ? "CNC" : ClientCode.Trim(),
                CallbackHost = string.IsNullOrWhiteSpace(CallbackHost) ? "0.0.0.0" : CallbackHost.Trim(),
                CallbackPort = CallbackPort,
                RequestTimeoutMs = RequestTimeoutMs,
                MaxRetries = MaxRetries,
                PollIntervalMs = PollIntervalMs
            });

            EffectiveBaseUrl = _runtime.BaseUrl;
            Append($"> 测试连接 queryTask → {_runtime.BaseUrl}");
            var r = await _rcs.QueryAsync(new QueryTaskRequest { PageIndex = 1, PageSize = 1 });
            if (RcsAckParser.IsHttpReachable(r))
            {
                ConnectionHealthText = $"连通 {r.ElapsedMs}ms";
                ConnectionHealthBrushKey = "OkBrush";
                _verifiedConnectionKey = ConnectionKey(BaseUrl, ClientCode);
                if (r.Success)
                    Append($"< 连通 OK {r.ElapsedMs}ms");
                else
                    Append($"< 连通 OK HTTP{r.HttpStatus} {r.ElapsedMs}ms（业务ACK：{r.Message ?? "无 Success"}）");
                _notify.Success($"RCS 连通成功 {r.ElapsedMs}ms");
                RefreshDispatchGateHint();
            }
            else
            {
                var detail = r.Error ?? r.Message ?? "失败";
                ConnectionHealthText = "不通";
                ConnectionHealthBrushKey = "AlarmBrush";
                _verifiedConnectionKey = null;
                Append($"< 连通失败 HTTP{r.HttpStatus} {detail}");
                _notify.Warning($"RCS 连通失败：{detail}");
                RefreshDispatchGateHint();
            }
        }
        catch (Exception ex)
        {
            ConnectionHealthText = "不通";
            ConnectionHealthBrushKey = "AlarmBrush";
            _verifiedConnectionKey = null;
            Append($"< 连通异常 {ex.Message}");
            _notify.Error($"测试异常：{ex.Message}");
            RefreshDispatchGateHint();
        }
        finally
        {
            IsTestingConnection = false;
            await RefreshMessagesAsync();
        }
    }

    [RelayCommand]
    private async Task TestCallbackAsync()
    {
        IsTestingCallback = true;
        CallbackHealthText = "测试中…";
        CallbackHealthBrushKey = "WarnBrush";
        try
        {
            if (!_callbackListener.IsListening)
            {
                var err = _callbackListener.ListenError ?? "回调宿主未启动";
                CallbackHealthText = "未监听";
                CallbackHealthBrushKey = "AlarmBrush";
                Append($"< 回调未监听：{err}");
                _notify.Warning($"回调未监听：{err}");
                return;
            }

            // 测实际已绑定端口；任意网卡/非法 Host 用环回探测（Kestrel 可能 ListenAnyIP 但 BoundHost 曾是脏值）。
            var host = ResolveLoopbackProbeHost(_callbackListener.BoundHost);
            var port = _callbackListener.BoundPort;
            var url = $"http://{host}:{port}{RcsCallbackInterfaces.PushTaskStatusPath}";

            // 空 taskId 探针：处理器会应答但不改任务态、不派发事件。
            var body = """{"taskId":"","data":{"system":{"error_code":0,"msg":"callback-probe"}}}""";
            Append($"> 测试本机监听 POST {url}");

            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var resp = await http.PostAsync(url, content);
            sw.Stop();
            var ack = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                CallbackHealthText = $"本机可达 {sw.ElapsedMilliseconds}ms";
                CallbackHealthBrushKey = "OkBrush";
                Append($"< 本机监听 OK HTTP{(int)resp.StatusCode} {sw.ElapsedMilliseconds}ms（仅证明本机 Kestrel；不代表 RCS→工控机网络已通） {ack}");
                _notify.Success(
                    $"本机监听可达 {sw.ElapsedMilliseconds}ms（不代表 RCS 服务器回调网络已打通）");
            }
            else
            {
                CallbackHealthText = "本机不通";
                CallbackHealthBrushKey = "AlarmBrush";
                Append($"< 本机监听失败 HTTP{(int)resp.StatusCode} {ack}");
                _notify.Warning($"本机监听不通：HTTP{(int)resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            CallbackHealthText = "本机不通";
            CallbackHealthBrushKey = "AlarmBrush";
            Append($"< 本机监听异常 {ex.Message}");
            _notify.Error($"本机监听测试异常：{ex.Message}");
        }
        finally
        {
            IsTestingCallback = false;
            await RefreshMessagesAsync();
        }
    }

    [RelayCommand]
    private async Task QueryAsync()
    {
        IsBusy = true;
        try
        {
            Append("> queryTask (最近未完结)");
            var r = await _rcs.QueryAsync(new QueryTaskRequest { PageIndex = 1, PageSize = 50 });
            ReportResult(r);
        }
        finally { IsBusy = false; await RefreshMessagesAsync(); }
    }

    [RelayCommand]
    private async Task RefreshTasksAsync()
    {
        try
        {
            var keepId = SelectedTask?.RcsTaskId ?? OperateTaskId;
            var rows = await _rcs.GetRecentTasksAsync(100);
            ReplaceOnUi(Tasks, rows);
            if (!string.IsNullOrWhiteSpace(keepId))
                SelectedTask = Tasks.FirstOrDefault(t => t.RcsTaskId == keepId);
        }
        catch (Exception ex) { StatusMessage = $"任务加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshMessagesAsync()
    {
        try
        {
            var keepId = SelectedMessage?.Id;
            var rows = await _rcs.QueryMessagesAsync(new RcsMsgQuery
            {
                Direction = RcsDisplayLabels.DirectionFromZh(MsgFilterDirection),
                Interface = RcsDisplayLabels.InterfaceFromZh(MsgFilterInterface),
                TaskId = string.IsNullOrWhiteSpace(MsgFilterTaskId) ? null : MsgFilterTaskId.Trim(),
                Limit = MsgLimit
            });
            ReplaceOnUi(Messages, rows);
            if (keepId is long id)
                SelectedMessage = Messages.FirstOrDefault(m => m.Id == id);
        }
        catch (Exception ex) { StatusMessage = $"报文加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshLocationsAsync()
    {
        try
        {
            var keepId = SelectedLocation?.Id ?? EditingLocId;
            var rows = await _locationMap.GetAllAsync();
            ReplaceOnUi(Locations, rows);
            ApplyLocationFilter();
            if (keepId > 0)
                SelectedLocation = FilteredLocations.FirstOrDefault(x => x.Id == keepId)
                    ?? Locations.FirstOrDefault(x => x.Id == keepId);
        }
        catch (Exception ex) { StatusMessage = $"位置映射加载失败：{ex.Message}"; }
    }

    private void ApplyLocationFilter()
    {
        IEnumerable<LocationMapItem> q = Locations;
        if (LocFilterType is not "全部" and not null and not "")
        {
            var code = LocationDisplayLabels.LocTypeFromZh(LocFilterType);
            q = q.Where(x => x.LocType == code);
        }
        var list = q.ToList();
        ReplaceOnUi(FilteredLocations, list);
        LocationCountText = LocFilterType is "全部" or null or ""
            ? $"共 {Locations.Count} 条"
            : $"共 {list.Count} / {Locations.Count} 条";
    }

    /// <summary>把集合的整体替换 marshal 到 UI 线程（回调事件在后台线程触发，直接改 ObservableCollection 会抛跨线程异常）。</summary>
    private void ReplaceOnUi<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
        => _ui.Invoke(() =>
        {
            target.Clear();
            foreach (var i in items) target.Add(i);
        });

    partial void OnSelectedLocationChanged(LocationMapItem? value)
    {
        if (value is null) return;
        EditingLocId = value.Id;
        _suppressLocTypeSideEffects = true;
        try
        {
            LocType = LocationDisplayLabels.LocTypeToZh(value.LocType);
            LocRcsCode = value.RcsCode;
            LocRcsType = LocationDisplayLabels.RcsTypeToZh(value.RcsType);
            LocName = value.LocType == "AREA"
                ? LocationDisplayLabels.AreaNameToZh(value.LocName)
                : (value.LocName ?? "");
        }
        finally { _suppressLocTypeSideEffects = false; }

        _suppressPositionReload = true;
        try
        {
            SelectedLocEquipment = LocEquipmentOptions.FirstOrDefault(x => x.Id == (value.EquipmentId ?? 0)) ?? NoneOption;
            SelectedLocFrame = LocFrameOptions.FirstOrDefault(x => x.Id == (value.FrameId ?? 0)) ?? NoneOption;
        }
        finally { _suppressPositionReload = false; }
        _ = ReloadPositionsForEquipmentAsync(value.EquipmentId ?? 0, value.PositionId);
    }

    [RelayCommand]
    private void NewLocation()
    {
        EditingLocId = 0;
        SelectedLocation = null;
        _suppressLocTypeSideEffects = true;
        try
        {
            LocType = "区域";
            LocRcsCode = "";
            LocRcsType = "站点";
            LocName = AreaNameOptions[0];
        }
        finally { _suppressLocTypeSideEffects = false; }
        OnPropertyChanged(nameof(ShowLocAreaName));
        OnPropertyChanged(nameof(ShowLocRemarkName));
        OnPropertyChanged(nameof(ShowLocEquipment));
        OnPropertyChanged(nameof(ShowLocPosition));
        OnPropertyChanged(nameof(ShowLocFrame));
        _suppressPositionReload = true;
        try
        {
            SelectedLocEquipment = NoneOption;
            SelectedLocFrame = NoneOption;
            SelectedLocPosition = NoneOption;
        }
        finally { _suppressPositionReload = false; }
        ReplaceOnUi(LocPositionOptions, new[] { NoneOption });
    }

    [RelayCommand]
    private async Task SaveLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(LocRcsCode)) { _notify.Warning("请填 RCS 编码。"); return; }
        try
        {
            var locTypeCode = LocationDisplayLabels.LocTypeFromZh(LocType);
            var locNameRaw = string.IsNullOrWhiteSpace(LocName) ? null : LocName.Trim();
            // 按类型只保留相关关联，避免隐藏字段脏值落库。
            long? eqId = ShowLocEquipment && SelectedLocEquipment is { Id: > 0 } e ? e.Id : null;
            long? posId = ShowLocPosition && SelectedLocPosition is { Id: > 0 } p ? p.Id : null;
            long? frameId = ShowLocFrame && SelectedLocFrame is { Id: > 0 } f ? f.Id : null;
            if (locTypeCode == "POSITION" && (eqId is null || posId is null))
            {
                _notify.Warning("加工位映射请选择机台和工位。");
                return;
            }
            if (locTypeCode == "EQUIPMENT" && eqId is null)
            {
                _notify.Warning("机台映射请选择机台。");
                return;
            }
            if (locTypeCode == "FRAME" && frameId is null)
            {
                _notify.Warning("料架映射请选择料架。");
                return;
            }
            if (locTypeCode == "AREA" && string.IsNullOrWhiteSpace(locNameRaw))
            {
                _notify.Warning("区域映射请选择名称。");
                return;
            }

            var item = new LocationMapItem
            {
                Id = EditingLocId,
                LocType = locTypeCode,
                RcsCode = LocRcsCode.Trim(),
                RcsType = LocationDisplayLabels.RcsTypeFromZh(LocRcsType),
                LocName = locTypeCode == "AREA"
                    ? LocationDisplayLabels.AreaNameFromZh(locNameRaw)
                    : locNameRaw,
                EquipmentId = eqId,
                PositionId = posId,
                FrameId = frameId
            };
            await _locationMap.SaveAsync(item, "system");
            _notify.Success("位置映射已保存。");
            await RefreshLocationsAsync();
            NewLocation();
        }
        catch (Exception ex) { _notify.Error($"保存失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task DeleteLocationAsync()
    {
        if (EditingLocId <= 0) { _notify.Warning("请先选中一行。"); return; }
        try
        {
            await _locationMap.DeleteAsync(EditingLocId);
            _notify.Success("已删除（软删）。");
            await RefreshLocationsAsync();
            NewLocation();
        }
        catch (Exception ex) { _notify.Error($"删除失败：{ex.Message}"); }
    }

    private void ReportResult(RcsResult r)
    {
        if (r.Success)
        {
            Append($"< OK {r.ElapsedMs}ms {r.Message}");
            StatusMessage = $"成功 {r.ElapsedMs}ms";
            _notify.Success($"RCS 下发成功 {r.ElapsedMs}ms");
            return;
        }

        if (r.FailureKind is RcsFailureKind.RouteUnavailable or RcsFailureKind.ConfigurationUnavailable)
        {
            Append($"< 拒绝：{RouteUnavailableUiMessage}");
            StatusMessage = RouteUnavailableUiMessage;
            _notify.Warning(RouteUnavailableUiMessage);
            return;
        }

        Append($"< 失败 HTTP{r.HttpStatus} {r.Error ?? r.Message}");
        StatusMessage = r.Error ?? r.Message ?? "失败";
        _notify.Warning($"RCS 未成功：{r.Error ?? r.Message}（报文已入流水）");
    }

    [RelayCommand]
    private void ClearTerminal() => _ui.Invoke(() => TerminalLines.Clear());

    private void Append(string line)
        => _ui.Invoke(() =>
        {
            TerminalLines.Add(line);
            while (TerminalLines.Count > 200) TerminalLines.RemoveAt(0);
        });
}
