using System.Collections.ObjectModel;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>任务/报文面板：手动下发、取消、Redo、查询与列表刷新。</summary>
public sealed partial class RcsTaskBoardViewModel : ObservableObject
{
    internal const string RouteUnavailableUiMessage = "路由配置已禁用或不可用";

    private readonly IRcsPageCoordinator _page;

    internal RcsTaskBoardViewModel(IRcsPageCoordinator page)
    {
        _page = page;
        KindOptions = new[] { "搬运", "抓取", "识别" };
        MsgDirectionOptions = new[] { "全部", "出站", "入站" };
        MsgInterfaceOptions = new[] { "全部", "搬运下发", "定制任务", "取消任务", "查询任务",
            "状态回调", "扫码回调", "告警回调" };
        MsgLimitOptions = new[] { 50, 100, 200, 500 };
        Tasks = new ObservableCollection<RcsTaskRow>();
        Messages = new ObservableCollection<RcsMsgRow>();
    }

    public string[] KindOptions { get; }
    public string[] MsgDirectionOptions { get; }
    public string[] MsgInterfaceOptions { get; }
    public int[] MsgLimitOptions { get; }
    public ObservableCollection<RcsTaskRow> Tasks { get; }
    public ObservableCollection<RcsMsgRow> Messages { get; }

    [ObservableProperty] private string _selectedKind = "搬运";
    [ObservableProperty] private string _fromCode = "101101";
    [ObservableProperty] private string _toCode = "201101";
    [ObservableProperty] private int _priority = 5;
    [ObservableProperty] private int _srcNo = 101;
    [ObservableProperty] private int _srcPos = 101;
    [ObservableProperty] private int _dstNo = 201;
    [ObservableProperty] private int _dstPos = 101;
    [ObservableProperty] private string _grabData = "100";
    [ObservableProperty] private int _posStart = 101;
    [ObservableProperty] private int _identifyCount = 3;
    [ObservableProperty] private RcsTaskRow? _selectedTask;
    [ObservableProperty] private string _operateTaskId = "";
    [ObservableProperty] private string _taskQuery = "";
    [ObservableProperty] private string _msgFilterDirection = "全部";
    [ObservableProperty] private string _msgFilterInterface = "全部";
    [ObservableProperty] private string _msgFilterTaskId = "";
    [ObservableProperty] private int _msgLimit = 100;
    [ObservableProperty] private bool _autoRefreshMessages = true;
    [ObservableProperty] private RcsMsgRow? _selectedMessage;

    public bool ShowTransitParams => SelectedKind is "搬运" || SelectedKind.StartsWith("搬运");
    public bool ShowGrabParams => SelectedKind is "抓取" || SelectedKind.StartsWith("抓取");
    public bool ShowIdentifyParams => SelectedKind is "识别" || SelectedKind.StartsWith("识别");
    public bool ShowToCode => !ShowIdentifyParams;
    public string FromLabelText => ShowIdentifyParams ? "料架站" : (ShowGrabParams ? "源站" : "起点");
    public string ToLabelText => ShowGrabParams ? "目标站" : "终点";
    public bool HasSelectedTask => SelectedTask is not null;

    partial void OnSelectedKindChanged(string value)
    {
        if (ShowGrabParams
            && string.Equals(FromCode, "101101", StringComparison.Ordinal)
            && string.Equals(ToCode, "201101", StringComparison.Ordinal))
        {
            FromCode = "101";
            ToCode = "201";
        }
        OnPropertyChanged(nameof(ShowTransitParams));
        OnPropertyChanged(nameof(ShowGrabParams));
        OnPropertyChanged(nameof(ShowIdentifyParams));
        OnPropertyChanged(nameof(ShowToCode));
        OnPropertyChanged(nameof(FromLabelText));
        OnPropertyChanged(nameof(ToLabelText));
    }

    partial void OnSelectedTaskChanged(RcsTaskRow? value)
    {
        if (!string.IsNullOrWhiteSpace(value?.RcsTaskId))
            OperateTaskId = value.RcsTaskId!;
        OnPropertyChanged(nameof(HasSelectedTask));
    }

    [RelayCommand]
    private Task DispatchAsync() => DispatchCoreAsync();

    [RelayCommand]
    private Task CancelAsync() => CancelCoreAsync();

    [RelayCommand]
    private Task RedoAsync() => RedoCoreAsync();

    [RelayCommand]
    private Task ConfirmCancelHandledAsync() => ConfirmCancelHandledCoreAsync();

    [RelayCommand]
    private Task QueryAsync() => QueryCoreAsync();

    [RelayCommand]
    private Task FindTaskAsync() => FocusTaskAsync(TaskQuery);

    [RelayCommand]
    private Task RefreshTasksAsync() => RefreshTaskListAsync();

    [RelayCommand]
    private Task RefreshMessagesAsync() => RefreshMessageListAsync();

    [RelayCommand]
    private void ShowTaskError(RcsTaskRow? row)
    {
        if (string.IsNullOrWhiteSpace(row?.ErrorMsg)) return;
        _page.Notify.Alert(row.ErrorMsg, "任务错误");
    }

    [RelayCommand]
    private void ShowMessageError(RcsMsgRow? row)
    {
        if (string.IsNullOrWhiteSpace(row?.Error)) return;
        _page.Notify.Alert(row.Error, "报文错误");
    }

    public Task DispatchCoreAsync() => DispatchInnerAsync();
    public Task CancelCoreAsync() => CancelInnerAsync();
    public Task RedoCoreAsync() => RedoInnerAsync();
    public Task ConfirmCancelHandledCoreAsync() => ConfirmCancelInnerAsync();
    public Task QueryCoreAsync() => QueryInnerAsync();

    public void ShowTaskErrorRow(RcsTaskRow? row) => ShowTaskError(row);
    public void ShowMessageErrorRow(RcsMsgRow? row) => ShowMessageError(row);

    private async Task DispatchInnerAsync()
    {
        if (!_page.EnsureManualDispatchAllowed("手动下发")) return;
        if (!TryValidateManualDispatch(out var from, out var to, out var error))
        {
            _page.Notify.Warning(error);
            _page.StatusMessage = error;
            return;
        }

        if (!_page.Options.UseSimulator)
        {
            if (_page.VerifiedConnectionKey is null
                || !string.Equals(_page.VerifiedConnectionKey, _page.LiveConnectionKey, StringComparison.Ordinal))
            {
                const string gate = "请先测试真实 RCS 连接";
                _page.Notify.Warning(gate);
                _page.StatusMessage = gate;
                _page.RefreshDispatchGateHint();
                return;
            }

            var confirmMsg =
                $"当前 RCS BaseUrl：{_page.Runtime.BaseUrl}\n" +
                $"任务类型：{SelectedKind}\n" +
                $"起点：{from}\n" +
                $"终点：{(ShowIdentifyParams ? "（识别无终点）" : to)}\n" +
                $"优先级：{Priority}\n\n" +
                "请确认现场人员、设备和路径已经清场";
            if (!_page.Notify.Confirm(confirmMsg, "真实 RCS 下发确认"))
            {
                _page.Append("> 用户取消真实 RCS 下发（未落库、未发 HTTP）");
                return;
            }
        }

        _page.IsBusy = true;
        try
        {
            RcsResult r;
            if (ShowGrabParams)
            {
                _page.Append($"> 抓取 {from} → {to} 孔位 {SrcNo}/{SrcPos}→{DstNo}/{DstPos}");
                r = await _page.Rcs.DispatchGrabAsync(new GrabDispatchArgs
                {
                    WorkLineId = _page.WorkLineId,
                    LineCode = _page.LineCode,
                    Priority = Priority,
                    SrcStation = from,
                    DstStation = to,
                    Items = new[] { new GrabItem { SrcNo = SrcNo, SrcPos = SrcPos, DstNo = DstNo, DstPos = DstPos, Data = GrabData } }
                });
            }
            else if (ShowIdentifyParams)
            {
                _page.Append($"> 识别 {from} 起始 {PosStart} 数量 {IdentifyCount}");
                r = await _page.Rcs.DispatchIdentifyAsync(new IdentifyDispatchArgs
                {
                    WorkLineId = _page.WorkLineId,
                    LineCode = _page.LineCode,
                    Priority = Priority,
                    Station = from,
                    PosStart = PosStart,
                    Count = IdentifyCount
                });
            }
            else
            {
                if (!await TryValidateManagedTransitRouteAsync(from, to))
                    return;
                _page.Append($"> 搬运 {from} → {to}");
                r = await _page.Rcs.DispatchTransitAsync(new TransitDispatchArgs
                {
                    WorkLineId = _page.WorkLineId,
                    LineCode = _page.LineCode,
                    TaskType = "2",
                    Priority = Priority,
                    FromCode = from,
                    ToCode = to
                });
            }
            _page.ReportResult(r);
        }
        catch (Exception ex)
        {
            _page.Notify.Error($"下发异常：{ex.Message}");
            _page.StatusMessage = ex.Message;
        }
        finally
        {
            _page.IsBusy = false;
            await RefreshTaskListAsync();
            await RefreshMessageListAsync();
            if (Tasks.FirstOrDefault()?.RcsTaskId is { } newestId)
            {
                OperateTaskId = newestId;
                SelectedTask = Tasks.FirstOrDefault(t => t.RcsTaskId == newestId);
            }
        }
    }

    public bool TryValidateManualDispatch(out string from, out string to, out string error)
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

    public async Task<bool> TryValidateManagedTransitRouteAsync(string from, string to)
    {
        try
        {
            var resolved = await _page.RouteResolver.ResolveAsync(from, to);
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

            var pre = await _page.RoutingValidator.ValidateAsync(ctx);
            if (resolved.IsResolved && pre.IsAvailable)
                return true;

            _page.Notify.Warning(RouteUnavailableUiMessage);
            _page.StatusMessage = RouteUnavailableUiMessage;
            _page.Append($"> 拒绝下发：{RouteUnavailableUiMessage}");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _page.Notify.Warning(RouteUnavailableUiMessage);
            _page.StatusMessage = RouteUnavailableUiMessage;
            return false;
        }
    }

    private async Task CancelInnerAsync()
    {
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { _page.Notify.Warning("请填任务号。"); return; }
        var taskId = OperateTaskId.Trim();
        if (!_page.ConfirmDangerousRcs("取消 RCS 任务", $"任务号：{taskId}"))
            return;
        _page.IsBusy = true;
        try
        {
            _page.Append($"> cancelTask {taskId}");
            _page.ReportResult(await _page.Rcs.CancelAsync(taskId));
        }
        finally { _page.IsBusy = false; await RefreshTaskListAsync(); await RefreshMessageListAsync(); }
    }

    private async Task RedoInnerAsync()
    {
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { _page.Notify.Warning("请填任务号。"); return; }
        var taskId = OperateTaskId.Trim();
        if (!_page.EnsureManualDispatchAllowed("手动 Redo")) return;
        if (!_page.ConfirmDangerousRcs("重做 RCS 任务", $"任务号：{taskId}"))
            return;
        _page.IsBusy = true;
        try
        {
            _page.Append($"> 重试 {taskId}");
            _page.ReportResult(await _page.Rcs.RedoAsync(taskId));
        }
        finally { _page.IsBusy = false; await RefreshTaskListAsync(); await RefreshMessageListAsync(); }
    }

    private async Task ConfirmCancelInnerAsync()
    {
        if (string.IsNullOrWhiteSpace(OperateTaskId)) { _page.Notify.Warning("请填任务号。"); return; }
        try
        {
            await _page.Rcs.ConfirmCancelHandledAsync(OperateTaskId.Trim());
            _page.Notify.Success("已标记取消任务人工处理确认。");
            await RefreshTaskListAsync();
        }
        catch (Exception ex) { _page.Notify.Error($"确认失败：{ex.Message}"); }
    }

    private async Task QueryInnerAsync()
    {
        _page.IsBusy = true;
        try
        {
            _page.Append("> queryTask (最近未完结)");
            var r = await _page.Rcs.QueryAsync(new QueryTaskRequest { PageIndex = 1, PageSize = 50 });
            _page.ReportResult(r);
        }
        finally { _page.IsBusy = false; await RefreshMessageListAsync(); }
    }

    internal async Task RefreshTaskListAsync()
    {
        try
        {
            var keepId = SelectedTask?.RcsTaskId ?? OperateTaskId;
            var rows = await _page.Rcs.GetRecentTasksAsync(100);
            _page.ReplaceOnUi(Tasks, rows);
            if (!string.IsNullOrWhiteSpace(keepId))
            {
                var match = Tasks.FirstOrDefault(t =>
                    t.RcsTaskId == keepId || t.RcsRemoteId == keepId);
                _page.Ui.Invoke(() => SelectedTask = match);
            }
        }
        catch (Exception ex) { _page.StatusMessage = $"任务加载失败：{ex.Message}"; }
    }

    internal async Task RefreshMessageListAsync()
    {
        try
        {
            var keepId = SelectedMessage?.Id;
            var rows = await _page.Rcs.QueryMessagesAsync(new RcsMsgQuery
            {
                Direction = RcsDisplayLabels.DirectionFromZh(MsgFilterDirection),
                Interface = RcsDisplayLabels.InterfaceFromZh(MsgFilterInterface),
                TaskId = string.IsNullOrWhiteSpace(MsgFilterTaskId) ? null : MsgFilterTaskId.Trim(),
                Limit = MsgLimit,
                IncludeBodies = MsgLimit <= 100
            });
            _page.ReplaceOnUi(Messages, rows);
            if (keepId is long id)
                SelectedMessage = Messages.FirstOrDefault(m => m.Id == id);
        }
        catch (Exception ex) { _page.StatusMessage = $"报文加载失败：{ex.Message}"; }
    }

    internal async Task FocusTaskAsync(string? raw)
    {
        var q = raw?.Trim() ?? "";
        if (q.Length == 0)
        {
            _page.Notify.Warning("请填任务号。");
            return;
        }

        TaskQuery = q;
        OperateTaskId = q;
        await RefreshTaskListAsync();

        var hit = FindInList(q);
        if (hit is null)
        {
            var row = await _page.Rcs.GetByTaskIdAsync(q);
            if (row is not null)
            {
                _page.Ui.Invoke(() =>
                {
                    if (!Tasks.Any(t => t.RcsTaskId == row.RcsTaskId))
                        Tasks.Insert(0, row);
                });
                hit = FindInList(row.RcsTaskId ?? q) ?? row;
            }
        }

        _page.Ui.Invoke(() =>
        {
            SelectedTask = hit;
            if (hit is not null)
            {
                OperateTaskId = string.IsNullOrWhiteSpace(hit.RcsTaskId) ? q : hit.RcsTaskId;
                MsgFilterTaskId = OperateTaskId;
                _page.StatusMessage = $"已定位 {OperateTaskId}";
            }
            else
            {
                _page.StatusMessage = $"列表无此号，已填入操作框，可直接确认取消";
            }
        });
    }

    private RcsTaskRow? FindInList(string q)
        => Tasks.FirstOrDefault(t => t.RcsTaskId == q || t.RcsRemoteId == q)
           ?? Tasks.FirstOrDefault(t =>
               (!string.IsNullOrEmpty(t.RcsTaskId) && t.RcsTaskId.Contains(q, StringComparison.OrdinalIgnoreCase))
               || (!string.IsNullOrEmpty(t.RcsRemoteId) && t.RcsRemoteId.Contains(q, StringComparison.OrdinalIgnoreCase)));
}
