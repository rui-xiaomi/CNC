using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CncLoader.Common.Configuration;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Options;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// RCS 页面协调器：组合连接/任务/映射/换架/终端子 VM，持有会话与派工门禁。
/// </summary>
public sealed partial class RcsViewModel : PageViewModelBase, IRcsPageCoordinator, IDisposable
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
    private string? _verifiedConnectionKey;
    private bool _suppressPauseSideEffects;
    private readonly Dictionary<long, string> _frameCodes = new();
    private readonly DispatcherTimer _taskRefreshTimer;
    private int _taskRefreshBusy;
    private bool _disposed;

    public RcsConnectionViewModel Connection { get; }
    public RcsTaskBoardViewModel TaskBoard { get; }
    public RcsLocationMapViewModel LocationMapPanel { get; }
    public RcsChangeFrameViewModel ChangeFramePanel { get; }
    public RcsTerminalViewModel Terminal { get; }

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

        Connection = new RcsConnectionViewModel(this);
        TaskBoard = new RcsTaskBoardViewModel(this);
        LocationMapPanel = new RcsLocationMapViewModel(this);
        ChangeFramePanel = new RcsChangeFrameViewModel(this);
        Terminal = new RcsTerminalViewModel(this);

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

        // 与料架/看板一致：停留本页定时拉库。自动派工落库不经回调，只靠回调会漏掉 DISPATCHED 行。
        _taskRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _taskRefreshTimer.Tick += OnTaskRefreshTick;

        _ = InitializeAsync();
    }

    IRcsTaskService IRcsSessionServices.Rcs => _rcs;
    ILocationMapService IRcsSessionServices.LocationMap => _locationMap;
    IEquipmentConfigService IRcsSessionServices.Equipment => _equipment;
    IFrameService IRcsSessionServices.Frames => _frames;
    IChangeFrameOrchestrator IRcsSessionServices.ChangeFrame => _changeFrame;
    IRcsConnectionConfigService IRcsSessionServices.ConnConfig => _connConfig;
    IRcsRuntimeConfig IRcsSessionServices.Runtime => _runtime;
    IRcsCallbackListener IRcsSessionServices.CallbackListener => _callbackListener;
    IPositionScheduler IRcsSessionServices.Scheduler => _scheduler;
    ICurrentUser IRcsSessionServices.User => _user;
    IManagedDispatchRouteResolver IRcsSessionServices.RouteResolver => _routeResolver;
    IRoutingAvailabilityValidator IRcsSessionServices.RoutingValidator => _routingValidator;
    IUserNotificationService IRcsUiBridge.Notify => _notify;
    IUiDispatcher IRcsUiBridge.Ui => _ui;
    RcsOptions IRcsSessionServices.Options => _options;
    long IRcsSessionServices.WorkLineId => _workLineId;
    long IRcsSessionServices.AgvId { get => _agvId; set => _agvId = value; }
    long IRcsSessionServices.ConnectionConfigId { get => _connectionConfigId; set => _connectionConfigId = value; }
    string IRcsSessionServices.LineCode => _lineCode;
    string? IRcsSessionServices.VerifiedConnectionKey { get => _verifiedConnectionKey; set => _verifiedConnectionKey = value; }
    string IRcsSessionServices.LiveConnectionKey => ConnectionKey(Connection.BaseUrl, Connection.ClientCode);
    Dictionary<long, string> IRcsSessionServices.FrameCodes => _frameCodes;
    void IRcsUiBridge.Append(string line) => Append(line);
    void IRcsSessionServices.RefreshDispatchGateHint() => RefreshDispatchGateHint();
    void IRcsSessionServices.InvalidateConnectionVerification(string reason) => InvalidateConnectionVerification(reason);
    void IRcsSessionServices.LoadConnectionFromRuntime() => LoadConnectionFromRuntime();
    bool IRcsSessionServices.ConfirmDangerousRcs(string title, string detail) => ConfirmDangerousRcs(title, detail);
    bool IRcsSessionServices.EnsureManualDispatchAllowed(string action) => EnsureManualDispatchAllowed(action);
    void IRcsUiBridge.ReportResult(RcsResult r) => ReportResult(r);
    void IRcsUiBridge.ReplaceOnUi<T>(ObservableCollection<T> target, IReadOnlyList<T> items) => ReplaceOnUi(target, items);
    Task IRcsSessionServices.RefreshTasksAsync() => TaskBoard.RefreshTaskListAsync();
    Task IRcsSessionServices.RefreshMessagesAsync() => TaskBoard.RefreshMessageListAsync();
    Task IRcsSessionServices.RefreshLocationsAsync() => LocationMapPanel.RefreshListAsync();
    Task IRcsSessionServices.RefreshChangeFrameTransactionsAsync() => ChangeFramePanel.RefreshTransactionsAsync();
    string IRcsSessionServices.ConnectionKey(string? baseUrl, string? clientCode) => ConnectionKey(baseUrl, clientCode);

    private void LoadConnectionFromRuntime()
    {
        Connection.ApplyRuntimeSnapshot(_runtime.Snapshot());
        Connection.EffectiveBaseUrl = _runtime.Snapshot().BaseUrl;
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
        => $"{(baseUrl ?? "").Trim()}|{(clientCode ?? "").Trim()}";

    private void InvalidateConnectionVerification(string reason)
    {
        if (_verifiedConnectionKey is null)
        {
            RefreshDispatchGateHint();
            return;
        }
        _verifiedConnectionKey = null;
        if (Connection.ConnectionHealthBrushKey == "OkBrush")
        {
            Connection.ConnectionHealthText = "需重新测试";
            Connection.ConnectionHealthBrushKey = "WarnBrush";
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
            || !string.Equals(_verifiedConnectionKey, ConnectionKey(Connection.BaseUrl, Connection.ClientCode), StringComparison.Ordinal))
        {
            DispatchGateHint = "请先测试真实 RCS 连接（修改地址/clientCode 后需重新测试）";
            return;
        }
        DispatchGateHint = "";
    }

    [ObservableProperty] private string _rcsModeText = "";
    [ObservableProperty] private string _rcsModeBrushKey = "IdleBrush";
    [ObservableProperty] private string _rcsModeHint = "";
    [ObservableProperty] private bool _pauseAutoDispatch;
    [ObservableProperty] private string _autoDispatchStatusText = "";
    [ObservableProperty] private string _autoDispatchStatusBrushKey = "OkBrush";
    [ObservableProperty] private string _dispatchGateHint = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

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
        if (_disposed) return;
        Append($"↩ {e.Source} {e.TaskId} error_code={e.ErrorCode} → {e.TaskState}");
        _ = RefreshOnCallbackAsync();
    }

    private void OnScanResultReceived(object? sender, RcsScanResultEvent e)
    {
        if (_disposed) return;
        Append($"↩ scanTaskStatus {e.TaskId} 料架 {e.Code} 扫得 {e.Products.Count} 码 → {RcsErrorCode.ToTaskState(e.ErrorCode)}");
        _ = RefreshOnCallbackAsync();
    }

    private void OnWarnReceived(object? sender, RcsWarnEvent e)
    {
        if (_disposed) return;
        Append($"⚠ warnCallback 车{e.RobotCode} {e.WarnContent}");
        if (TaskBoard.AutoRefreshMessages) _ = TaskBoard.RefreshMessageListAsync();
    }

    private void OnTaskRefreshTick(object? sender, EventArgs e)
    {
        if (_disposed || IsBusy) return;
        _ = RefreshOnCallbackAsync();
    }

    private async Task RefreshOnCallbackAsync()
    {
        if (Interlocked.Exchange(ref _taskRefreshBusy, 1) != 0) return;
        try
        {
            await TaskBoard.RefreshTaskListAsync();
            if (TaskBoard.AutoRefreshMessages) await TaskBoard.RefreshMessageListAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _taskRefreshBusy, 0);
        }
    }

    public override string Key => "rcs";
    public override string Title => "RCS 任务管理";
    public override string Description => "RCS 对接：任务下发、跟踪、报文流水与位置映射。";

    public override void OnNavigatedTo(object? argument)
    {
        if (argument is string id && !string.IsNullOrWhiteSpace(id))
            _ = TaskBoard.FocusTaskAsync(id);
    }

    /// <summary>激活：立即刷任务列表并启动 1.5s 定时器（隐藏页不刷库）。</summary>
    public override void OnActivated()
    {
        if (_disposed) return;
        _taskRefreshTimer.Start();
        _ = RefreshOnCallbackAsync();
    }

    /// <summary>失活：停定时器。</summary>
    public override void OnDeactivated() => _taskRefreshTimer.Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _taskRefreshTimer.Stop();
        _taskRefreshTimer.Tick -= OnTaskRefreshTick;
        _callbacks.TaskStatusReceived -= OnTaskStatusReceived;
        _callbacks.ScanResultReceived -= OnScanResultReceived;
        _callbacks.WarnReceived -= OnWarnReceived;
        _changeFrame.ProgressChanged -= OnChangeFrameProgress;
        _workLineService.WorkLinesChanged -= OnWorkLinesChanged;
    }

    private void OnWorkLinesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ = RefreshWorkLineContextAsync();
    }

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
            if (!_disposed)
                _workLineService.WorkLinesChanged += OnWorkLinesChanged;
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
                Connection.ApplyRuntimeSnapshot(cfg);
            }
            else
                LoadConnectionFromRuntime();
        }
        catch { LoadConnectionFromRuntime(); }

        Connection.RefreshListenHint(_callbackListener);
        Connection.EffectiveBaseUrl = _runtime.BaseUrl;
        RefreshModeBanner();
        RefreshDispatchGateHint();

        await LocationMapPanel.LoadRefOptionsAsync();
        await TaskBoard.RefreshTaskListAsync();
        await TaskBoard.RefreshMessageListAsync();
        await LocationMapPanel.RefreshListAsync();
    }

    private bool ConfirmDangerousRcs(string title, string detail)
    {
        var msg =
            $"当前 RCS BaseUrl：{_runtime.BaseUrl}\n{detail}\n\n请确认现场人员、设备和路径已经清场";
        if (_notify.Confirm(msg, title))
            return true;
        Append($"> 用户取消{title}（未发 RCS）");
        return false;
    }

    private bool EnsureManualDispatchAllowed(string action)
    {
        if (!_options.SchedulerEnabled || _scheduler.IsAutoDispatchPaused) return true;
        var msg = $"自动派工运行中，禁止{action}：请先开启「暂停自动派工」";
        _notify.Warning(msg);
        StatusMessage = msg;
        Append($"> 拒绝{action}：自动派工运行中");
        return false;
    }

    private void OnChangeFrameProgress(object? sender, ChangeFrameProgressEvent e)
    {
        if (_disposed) return;
        ChangeFramePanel.OnProgress(e);
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

    private void ReplaceOnUi<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
        => _ui.Invoke(() =>
        {
            target.Clear();
            foreach (var i in items) target.Add(i);
        });

    private void Append(string line) => Terminal.Append(line);
}
