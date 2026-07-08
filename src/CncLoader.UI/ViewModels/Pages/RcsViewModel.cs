using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Options;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// RCS 任务管理页（第四阶段①骨架）：连接配置展示 + 手动下发测试（搬运/抓取/识别）+
/// 取消/redo/查询 + 任务列表 + 报文流水 + 位置映射(LOCATION_MAP)录入。
/// 后续步骤②③④在此基础上接入回调/模拟器/跟踪器。
/// </summary>
public sealed partial class RcsViewModel : PageViewModelBase
{
    private readonly IRcsTaskService _rcs;
    private readonly ILocationMapService _locationMap;
    private readonly IWorkLineService _workLineService;
    private readonly IRcsCallbackNotifier _callbacks;
    private readonly IChangeFrameOrchestrator _changeFrame;
    private readonly RcsOptions _options;

    private long _workLineId;
    private string _lineCode = "LINE";

    public RcsViewModel(IRcsTaskService rcs, ILocationMapService locationMap,
        IWorkLineService workLineService, IRcsCallbackNotifier callbacks, IChangeFrameOrchestrator changeFrame,
        IOptions<AppOptions> options)
    {
        _rcs = rcs;
        _locationMap = locationMap;
        _workLineService = workLineService;
        _callbacks = callbacks;
        _changeFrame = changeFrame;
        _options = options.Value.Rcs;

        KindOptions = new[] { "搬运 transit", "抓取 grab", "识别 identifyQR" };
        LocTypeOptions = new[] { "AREA", "EQUIPMENT", "POSITION", "FRAME" };
        RcsTypeOptions = new[] { "station", "cell", "shelf" };
        MsgDirectionOptions = new[] { "全部", "OUT", "IN" };
        MsgInterfaceOptions = new[] { "全部", "transitTask", "excuteTask", "cancelTask", "queryTask",
            "pushTaskStatus", "scanTaskStatus", "warnCallback" };
        MsgLimitOptions = new[] { 100, 500, 1000, 2000 };
        TerminalLines = new ObservableCollection<string>();
        Tasks = new ObservableCollection<RcsTaskRow>();
        Messages = new ObservableCollection<RcsMsgRow>();
        Locations = new ObservableCollection<LocationMapItem>();

        BaseUrl = _options.BaseUrl;
        ClientCode = _options.ClientCode;
        CallbackInfo = $"{_options.CallbackHost}:{_options.CallbackPort}";

        _callbacks.TaskStatusReceived += OnTaskStatusReceived;
        _callbacks.ScanResultReceived += OnScanResultReceived;
        _callbacks.WarnReceived += OnWarnReceived;

        _changeFrame.ProgressChanged += OnChangeFrameProgress;

        _ = InitializeAsync();
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
    public string[] RcsTypeOptions { get; }
    public string[] MsgDirectionOptions { get; }
    public string[] MsgInterfaceOptions { get; }
    public int[] MsgLimitOptions { get; }
    public ObservableCollection<string> TerminalLines { get; }
    public ObservableCollection<RcsTaskRow> Tasks { get; }
    public ObservableCollection<RcsMsgRow> Messages { get; }
    public ObservableCollection<LocationMapItem> Locations { get; }

    // 连接配置（只读展示；正式编辑在步骤⑥并入 MAS_AUTO_WORKLINE_AGV）
    [ObservableProperty] private string _baseUrl = "";
    [ObservableProperty] private string _clientCode = "";
    [ObservableProperty] private string _callbackInfo = "";

    // 手动下发表单
    [ObservableProperty] private string _selectedKind = "搬运 transit";
    [ObservableProperty] private string _fromCode = "601203";
    [ObservableProperty] private string _toCode = "603201";
    [ObservableProperty] private int _priority = 5;
    // 抓取参数（简化：单条 GrabItem）
    [ObservableProperty] private int _srcNo = 101;
    [ObservableProperty] private int _srcPos = 101;
    [ObservableProperty] private int _dstNo = 201;
    [ObservableProperty] private int _dstPos = 101;
    [ObservableProperty] private string _grabData = "100";
    // 识别参数
    [ObservableProperty] private int _posStart = 101;
    [ObservableProperty] private int _identifyCount = 3;
    // 取消/redo
    [ObservableProperty] private string _operateTaskId = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    // 报文流水筛选（服务端查询）+ 自动刷新开关
    [ObservableProperty] private string _msgFilterDirection = "全部";
    [ObservableProperty] private string _msgFilterInterface = "全部";
    [ObservableProperty] private string _msgFilterTaskId = "";
    [ObservableProperty] private int _msgLimit = 100;
    [ObservableProperty] private bool _autoRefreshMessages = true;

    // 换架/空托盘回收（第四阶段⑥b）
    [ObservableProperty] private string _changeFrameEquipmentId = "1";
    [ObservableProperty] private string _changeFrameRole = "上料架";
    public string[] ChangeFrameRoleOptions { get; } = new[] { "上料架", "下料架" };
    [ObservableProperty] private string _palletReturnFromCode = "P100";
    public ObservableCollection<ChangeFrameProgressEvent> ChangeFrameTransactions { get; } = new();

    // 位置映射编辑
    [ObservableProperty] private LocationMapItem? _selectedLocation;
    [ObservableProperty] private string _locType = "AREA";
    [ObservableProperty] private string _locRcsCode = "";
    [ObservableProperty] private string _locRcsType = "station";
    [ObservableProperty] private string _locName = "";
    [ObservableProperty] private string _locEquipmentId = "";
    [ObservableProperty] private string _locPositionId = "";
    [ObservableProperty] private string _locFrameId = "";
    [ObservableProperty] private long _editingLocId;

    private async Task InitializeAsync()
    {
        try
        {
            var lines = await _workLineService.GetAllAsync();
            var first = lines.FirstOrDefault();
            if (first is not null)
            {
                _workLineId = first.Id;
                _lineCode = string.IsNullOrWhiteSpace(first.Code) ? "LINE" : first.Code;
            }
        }
        catch { /* DB 未就绪：用默认 LINE */ }
        await RefreshTasksAsync();
        await RefreshMessagesAsync();
        await RefreshLocationsAsync();
    }

    [RelayCommand]
    private async Task DispatchAsync()
    {
        IsBusy = true;
        try
        {
            RcsResult r;
            if (SelectedKind.StartsWith("抓取"))
            {
                Append($"> grabTask {SrcNo}/{SrcPos} → {DstNo}/{DstPos}");
                r = await _rcs.DispatchGrabAsync(new GrabDispatchArgs
                {
                    WorkLineId = _workLineId,
                    LineCode = _lineCode,
                    Priority = Priority,
                    SrcStation = FromCode,
                    DstStation = ToCode,
                    Items = new[] { new GrabItem { SrcNo = SrcNo, SrcPos = SrcPos, DstNo = DstNo, DstPos = DstPos, Data = GrabData } }
                });
            }
            else if (SelectedKind.StartsWith("识别"))
            {
                Append($"> identifyQR {PosStart},{IdentifyCount} @ {FromCode}");
                r = await _rcs.DispatchIdentifyAsync(new IdentifyDispatchArgs
                {
                    WorkLineId = _workLineId,
                    LineCode = _lineCode,
                    Priority = Priority,
                    Station = FromCode,
                    PosStart = PosStart,
                    Count = IdentifyCount
                });
            }
            else
            {
                Append($"> transitTask {FromCode} → {ToCode}");
                r = await _rcs.DispatchTransitAsync(new TransitDispatchArgs
                {
                    WorkLineId = _workLineId,
                    LineCode = _lineCode,
                    TaskType = "2",
                    Priority = Priority,
                    FromCode = FromCode,
                    ToCode = ToCode
                });
            }
            ReportResult(r);
        }
        catch (Exception ex)
        {
            HandyControl.Controls.Growl.Error($"下发异常：{ex.Message}");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshTasksAsync();
            await RefreshMessagesAsync();
            // 自动回填最新 taskId，方便直接 redo/取消，无需手动复制。
            if (Tasks.FirstOrDefault()?.RcsTaskId is { } newestId) OperateTaskId = newestId;
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { HandyControl.Controls.Growl.Warning("请填 taskId。"); return; }
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
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { HandyControl.Controls.Growl.Warning("请填 taskId。"); return; }
        IsBusy = true;
        try
        {
            Append($"> redo {OperateTaskId}");
            ReportResult(await _rcs.RedoAsync(OperateTaskId.Trim()));
        }
        finally { IsBusy = false; await RefreshTasksAsync(); await RefreshMessagesAsync(); }
    }

    [RelayCommand]
    private async Task ConfirmCancelHandledAsync()
    {
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { HandyControl.Controls.Growl.Warning("请填 taskId。"); return; }
        try
        {
            await _rcs.ConfirmCancelHandledAsync(OperateTaskId.Trim());
            HandyControl.Controls.Growl.Success("已标记取消任务人工处理确认。");
            await RefreshTasksAsync();
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"确认失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task ChangeFrameAsync()
    {
        if (!long.TryParse(ChangeFrameEquipmentId?.Trim(), out var eqId) || eqId <= 0)
        {
            HandyControl.Controls.Growl.Warning("请填机台 ID（数字）。");
            return;
        }
        var role = ChangeFrameRole == "下料架" ? FrameRole.Unload : FrameRole.Upload;
        try
        {
            var txnId = await _changeFrame.ChangeFrameAsync(eqId, role, "operator");
            Append($"> 换架 {txnId}（机台{eqId} {ChangeFrameRole}）");
            HandyControl.Controls.Growl.Info($"换架已发起 {txnId}，进度见终端");
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"换架失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task PalletReturnAsync()
    {
        if (string.IsNullOrWhiteSpace(PalletReturnFromCode)) { HandyControl.Controls.Growl.Warning("请填回收点位编码。"); return; }
        try
        {
            var dest = (await _locationMap.ResolveAreaAsync(_options.PalletReturnArea))?.RcsCode;
            if (string.IsNullOrWhiteSpace(dest)) { HandyControl.Controls.Growl.Warning($"托盘回收区 {_options.PalletReturnArea} 未在 LOCATION_MAP 录入"); return; }
            Append($"> 空托盘回收 {PalletReturnFromCode} → {dest}");
            var r = await _rcs.DispatchPalletReturnAsync(0, null, PalletReturnFromCode.Trim(), dest, _workLineId, _lineCode, "operator");
            ReportResult(r);
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"回收失败：{ex.Message}"); }
    }

    private void OnChangeFrameProgress(object? sender, ChangeFrameProgressEvent e)
    {
        Append($"↻ 换架 {e.TxnId} {e.Step} {e.State}{(string.IsNullOrEmpty(e.Message) ? "" : " " + e.Message)}");
        if (e.State == "COMPLETED") HandyControl.Controls.Growl.Success($"换架 {e.TxnId} 完成");
        else if (e.State == "FAILED" || e.Step == ChangeFrameStep.Alarm) HandyControl.Controls.Growl.Warning($"换架 {e.TxnId} 异常：{e.Message}");
        _ = RefreshChangeFrameTransactionsAsync();
    }

    private async Task RefreshChangeFrameTransactionsAsync()
    {
        var rows = _changeFrame.GetActiveTransactions();
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            ChangeFrameTransactions.Clear();
            foreach (var r in rows) ChangeFrameTransactions.Add(r);
        });
        await Task.CompletedTask;
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
            var rows = await _rcs.GetRecentTasksAsync(100);
            ReplaceOnUi(Tasks, rows);
        }
        catch (Exception ex) { StatusMessage = $"任务加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshMessagesAsync()
    {
        try
        {
            var rows = await _rcs.QueryMessagesAsync(new RcsMsgQuery
            {
                Direction = MsgFilterDirection is "全部" or "" ? null : MsgFilterDirection,
                Interface = MsgFilterInterface is "全部" or "" ? null : MsgFilterInterface,
                TaskId = string.IsNullOrWhiteSpace(MsgFilterTaskId) ? null : MsgFilterTaskId.Trim(),
                Limit = MsgLimit
            });
            ReplaceOnUi(Messages, rows);
        }
        catch (Exception ex) { StatusMessage = $"报文加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task RefreshLocationsAsync()
    {
        try
        {
            var rows = await _locationMap.GetAllAsync();
            ReplaceOnUi(Locations, rows);
        }
        catch (Exception ex) { StatusMessage = $"位置映射加载失败：{ex.Message}"; }
    }

    /// <summary>把集合的整体替换 marshal 到 UI 线程（回调事件在后台线程触发，直接改 ObservableCollection 会抛跨线程异常）。</summary>
    private static void ReplaceOnUi<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Apply()
        {
            target.Clear();
            foreach (var i in items) target.Add(i);
        }
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.Invoke(Apply);
    }

    partial void OnSelectedLocationChanged(LocationMapItem? value)
    {
        if (value is null) return;
        EditingLocId = value.Id;
        LocType = value.LocType;
        LocRcsCode = value.RcsCode;
        LocRcsType = value.RcsType;
        LocName = value.LocName ?? "";
        LocEquipmentId = value.EquipmentId?.ToString() ?? "";
        LocPositionId = value.PositionId?.ToString() ?? "";
        LocFrameId = value.FrameId?.ToString() ?? "";
    }

    [RelayCommand]
    private void NewLocation()
    {
        EditingLocId = 0;
        SelectedLocation = null;
        LocType = "AREA";
        LocRcsCode = "";
        LocRcsType = "station";
        LocName = "";
        LocEquipmentId = "";
        LocPositionId = "";
        LocFrameId = "";
    }

    [RelayCommand]
    private async Task SaveLocationAsync()
    {
        if (string.IsNullOrWhiteSpace(LocRcsCode)) { HandyControl.Controls.Growl.Warning("请填 RCS 编码。"); return; }
        try
        {
            var item = new LocationMapItem
            {
                Id = EditingLocId,
                LocType = LocType,
                RcsCode = LocRcsCode.Trim(),
                RcsType = LocRcsType,
                LocName = string.IsNullOrWhiteSpace(LocName) ? null : LocName.Trim(),
                EquipmentId = ParseLong(LocEquipmentId),
                PositionId = ParseLong(LocPositionId),
                FrameId = ParseLong(LocFrameId)
            };
            await _locationMap.SaveAsync(item, "system");
            HandyControl.Controls.Growl.Success("位置映射已保存。");
            await RefreshLocationsAsync();
            NewLocation();
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"保存失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task DeleteLocationAsync()
    {
        if (EditingLocId <= 0) { HandyControl.Controls.Growl.Warning("请先选中一行。"); return; }
        try
        {
            await _locationMap.DeleteAsync(EditingLocId);
            HandyControl.Controls.Growl.Success("已删除（软删）。");
            await RefreshLocationsAsync();
            NewLocation();
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"删除失败：{ex.Message}"); }
    }

    private void ReportResult(RcsResult r)
    {
        if (r.Success)
        {
            Append($"< OK {r.ElapsedMs}ms {r.Message}");
            StatusMessage = $"成功 {r.ElapsedMs}ms";
            HandyControl.Controls.Growl.Success($"RCS 下发成功 {r.ElapsedMs}ms");
        }
        else
        {
            Append($"< 失败 HTTP{r.HttpStatus} {r.Error ?? r.Message}");
            StatusMessage = r.Error ?? r.Message ?? "失败";
            HandyControl.Controls.Growl.Warning($"RCS 未成功：{r.Error ?? r.Message}（报文已入流水）");
        }
    }

    private void Append(string line)
        => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            TerminalLines.Add(line);
            while (TerminalLines.Count > 200) TerminalLines.RemoveAt(0);
        });

    private static long? ParseLong(string s)
        => long.TryParse(s?.Trim(), out var v) && v > 0 ? v : null;
}
