using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Signals;
using CncLoader.UI.Views.Dialogs;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// PLC 管理页：多 PLC 连接 + 读/写 + 通信告警。
/// 点位映射独立成「点位映射」页，本页不再耦合点位维护。
/// </summary>
public sealed partial class PlcViewModel : PageViewModelBase, IDisposable
{
    private readonly IPlcCatalogService _catalog;
    private readonly IPlcConnectionService _connections;
    private readonly IPlcOperationService _operations;
    private readonly IPlcPointManagementService _points;
    private readonly IDeviceLogStore _logStore;
    private readonly IAlarmEventService _alarms;
    private readonly ICurrentUser _user;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _heartbeatTimer;
    private long _heartbeat;

    public PlcViewModel(IPlcCatalogService catalog, IPlcConnectionService connections,
        IPlcOperationService operations, IPlcPointManagementService points,
        IDeviceLogStore logStore, IAlarmEventService alarms, ICurrentUser user,
        PointMappingViewModel pointMapping)
    {
        _catalog = catalog;
        _connections = connections;
        _operations = operations;
        _points = points;
        _logStore = logStore;
        _alarms = alarms;
        _user = user;
        PointMapping = pointMapping;

        PlcRows = new ObservableCollection<PlcRowVm>();
        ReadRows = new ObservableCollection<ReadRowVm>();
        LogLines = new ObservableCollection<string>();
        AlarmLines = new ObservableCollection<string>();
        EquipmentOptions = new ObservableCollection<EquipmentOption>();
        WriteSignals = new ObservableCollection<WriteSignalOption>();

        _connections.ConnectionChanged += (_, plcId) => _ = RefreshPlcRowAsync(plcId);
        // 点位映射 tab 改完点位后，自动刷新本页读/写面板（免去重选 PLC）。
        PointMapping.PointsChanged += (_, plcId) => _ = OnPointsChangedAsync(plcId);
        _logStore.LogAppended += (_, row) => PrependLog(row.DisplayLine);
        _alarms.AlarmRaised += (_, row) => PrependAlarm($"{row.Time:HH:mm:ss} [{row.Level}] {row.Message}");

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _pollTimer.Tick += async (_, _) => await ReadOnceAsync();

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _heartbeatTimer.Tick += (_, _) => HeartbeatCounter = (++_heartbeat).ToString("D6");
        _heartbeatTimer.Start();

        _ = InitializeAsync();
    }

    public override string Key => "plc";
    public override string Title => "PLC 管理";
    public override string Description => "多 PLC 连接管理、点位实时读取、测试启动信号下发（二次确认+流水）、通信告警。";

    /// <summary>点位映射子模块（按原型并入 PLC 页内「点位映射」tab，不再单列导航）。</summary>
    public PointMappingViewModel PointMapping { get; }

    public ObservableCollection<PlcRowVm> PlcRows { get; }
    public ObservableCollection<ReadRowVm> ReadRows { get; }
    public ObservableCollection<string> LogLines { get; }
    public ObservableCollection<string> AlarmLines { get; }
    public ObservableCollection<EquipmentOption> EquipmentOptions { get; }
    public ObservableCollection<WriteSignalOption> WriteSignals { get; }
    public int[] WriteValueOptions { get; } = [SignalConventions.StartValue, SignalConventions.StopValue];

    [ObservableProperty] private PlcRowVm? _selectedPlc;
    [ObservableProperty] private EquipmentOption? _selectedEquipment;
    [ObservableProperty] private WriteSignalOption? _selectedWriteSignal;
    [ObservableProperty] private int _writeValue = SignalConventions.StartValue;
    [ObservableProperty] private string _onlineSummary = "0 / 0 在线";
    [ObservableProperty] private bool _isPolling;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _heartbeatCounter = "000000";

    partial void OnSelectedPlcChanged(PlcRowVm? value)
    {
        if (value is null) return;
        SelectedEquipment = EquipmentOptions.FirstOrDefault(e => e.PlcId == value.PlcId);
        _ = LoadReadRowsAsync(value.PlcId);
        _ = LoadWriteSignalsAsync(value.PlcId);
        _ = LoadLogsAsync(value.PlcId);
    }

    partial void OnSelectedEquipmentChanged(EquipmentOption? value)
    {
        if (value is null) return;
        if (SelectedPlc?.PlcId != value.PlcId)
            SelectedPlc = PlcRows.FirstOrDefault(p => p.PlcId == value.PlcId);
    }

    partial void OnIsPollingChanged(bool value)
    {
        if (value) _pollTimer.Start();
        else _pollTimer.Stop();
    }

    private async Task InitializeAsync()
    {
        await ReloadAllAsync();
        await LoadLogsAsync(null);
        var alarmRows = await _alarms.GetRecentAsync(10);
        foreach (var a in alarmRows.Reverse())
            PrependAlarm($"{a.Time:HH:mm:ss} [{a.Level}] {a.Message}");
    }

    [RelayCommand]
    private async Task ReloadAllAsync()
    {
        var plcs = await _catalog.GetAllAsync();
        var equipments = await _catalog.GetEquipmentsAsync();

        PlcRows.Clear();
        foreach (var p in plcs) PlcRows.Add(PlcRowVm.From(p));

        EquipmentOptions.Clear();
        foreach (var e in equipments) EquipmentOptions.Add(e);

        var online = plcs.Count(p => p.IsConnected);
        OnlineSummary = $"{online} / {plcs.Count} 在线";

        if (SelectedPlc is null && PlcRows.Count > 0)
            SelectedPlc = PlcRows[0];
        else if (SelectedPlc is not null)
            SelectedPlc = PlcRows.FirstOrDefault(p => p.PlcId == SelectedPlc.PlcId);
    }

    [RelayCommand]
    private async Task ConnectPlcAsync(PlcRowVm? row)
    {
        if (row is null) return;
        try
        {
            StatusMessage = $"正在连接 {row.Name}…";
            await _connections.ConnectAsync(row.PlcId);
            await RefreshPlcRowAsync(row.PlcId);
            StatusMessage = $"{row.Name} 已连接";
            HandyControl.Controls.Growl.Success($"{row.Name} 已连接");
            if (SelectedPlc?.PlcId == row.PlcId)
                await ReadOnceAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Warning($"{row.Name} 连接失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DisconnectPlcAsync(PlcRowVm? row)
    {
        if (row is null) return;
        await _connections.DisconnectAsync(row.PlcId);
        await RefreshPlcRowAsync(row.PlcId);
        StatusMessage = $"{row.Name} 已断开";
        HandyControl.Controls.Growl.Info($"{row.Name} 已断开");
        if (SelectedPlc?.PlcId == row.PlcId)
            await ShowDisconnectedReadRowsAsync(row.PlcId);
    }

    [RelayCommand]
    private async Task ConnectAllAsync()
    {
        await _connections.ConnectAllAsync();
        await ReloadAllAsync();
        StatusMessage = "全部连接完成";
        HandyControl.Controls.Growl.Success("全部 PLC 连接完成");
        if (SelectedPlc is not null)
            await ReadOnceAsync();
    }

    [RelayCommand]
    private async Task DisconnectAllAsync()
    {
        await _connections.DisconnectAllAsync();
        await ReloadAllAsync();
        StatusMessage = "全部已断开";
        HandyControl.Controls.Growl.Info("全部 PLC 已断开");
        if (SelectedPlc is not null)
            await ShowDisconnectedReadRowsAsync(SelectedPlc.PlcId);
    }

    /// <summary>新增 PLC：弹出对话框，PlcId 取建议值，保存后刷新列表。</summary>
    [RelayCommand]
    private async Task AddPlcAsync()
    {
        try
        {
            var suggestedId = await _catalog.SuggestNextPlcIdAsync();
            var equipments = await _catalog.GetEquipmentsAsync();
            var dlg = new PlcEditDialog(suggestedId, equipments) { Owner = Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true || dlg.Result is null) return;

            await _catalog.SaveAsync(dlg.Result, _user.Name);
            if (dlg.Result.BoundEquipmentId is not null)
                await _catalog.BindEquipmentAsync(dlg.Result.PlcId, dlg.Result.BoundEquipmentId, _user.Name);
            HandyControl.Controls.Growl.Success($"PLC {dlg.Result.Name} 已新增。");
            StatusMessage = $"PLC {dlg.Result.Name} 已新增";
            await ReloadAllAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"新增失败：{ex.Message}");
        }
    }

    /// <summary>编辑 PLC：PlcId 只读，保存后若在线则断开（避免配置与连接不同步）。</summary>
    [RelayCommand]
    private async Task EditPlcAsync(PlcRowVm? row)
    {
        if (row is null) return;
        try
        {
            var edit = await _catalog.GetByIdAsync(row.PlcId);
            if (edit is null)
            {
                HandyControl.Controls.Growl.Warning("该 PLC 不存在或已删除。");
                await ReloadAllAsync();
                return;
            }
            var suggestedId = await _catalog.SuggestNextPlcIdAsync();
            // 查 PLC 当前被哪台机台引用（一机一 PLC，业务链机台→PLC）；机台下拉含"未绑定"，可在 PLC 侧反向改机台绑定
            var equipments = await _catalog.GetEquipmentsAsync();
            var bound = equipments.FirstOrDefault(e => e.PlcId == row.PlcId);
            var dlg = new PlcEditDialog(edit, suggestedId, equipments, bound?.Id) { Owner = Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true || dlg.Result is null) return;

            if (_connections.IsConnected(row.PlcId))
            {
                await _connections.DisconnectAsync(row.PlcId);
                StatusMessage = "配置已变更，已断开连接，请重新连接。";
            }
            await _catalog.SaveAsync(dlg.Result, _user.Name);
            await _catalog.BindEquipmentAsync(row.PlcId, dlg.Result.BoundEquipmentId, _user.Name);
            // 编辑会断开连接；保存后自动重连，避免界面显示未连接又触发读点位告警。
            try
            {
                await _connections.ConnectAsync(row.PlcId);
                StatusMessage = $"PLC {dlg.Result.Name} 已更新并重连";
            }
            catch (Exception cex)
            {
                StatusMessage = $"PLC {dlg.Result.Name} 已更新，重连失败：{cex.Message}";
            }
            HandyControl.Controls.Growl.Success($"PLC {dlg.Result.Name} 已更新。");
            await ReloadAllAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"更新失败：{ex.Message}");
        }
    }

    /// <summary>删除 PLC：先做引用校验，可删则二次确认后软删（State='1'）。</summary>
    [RelayCommand]
    private async Task DeletePlcAsync(PlcRowVm? row)
    {
        if (row is null) return;
        try
        {
            var check = await _catalog.CheckDeleteAsync(row.PlcId);
            if (!check.CanDelete)
            {
                HandyControl.Controls.Growl.Warning(check.Message);
                StatusMessage = check.Message;
                return;
            }
            var msg = $"确认删除 PLC {row.Name}（PlcId={row.PlcId}）？\n删除后不可在列表中显示（软删，可在 DB 恢复）。";
            if (HandyControl.Controls.MessageBox.Show(msg, "删除 PLC 二次确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            if (_connections.IsConnected(row.PlcId))
                await _connections.DisconnectAsync(row.PlcId);

            await _catalog.DeleteAsync(row.PlcId, _user.Name);
            HandyControl.Controls.Growl.Success($"PLC {row.Name} 已删除。");
            StatusMessage = $"PLC {row.Name} 已删除";
            await ReloadAllAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"删除失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ReadOnceAsync()
    {
        if (SelectedPlc is null) return;
        // 未连接：刷新表格为「未连接」，不弹告警（避免刷屏）；旧值必须清掉。
        if (!_connections.IsConnected(SelectedPlc.PlcId))
        {
            await ShowDisconnectedReadRowsAsync(SelectedPlc.PlcId);
            StatusMessage = $"{SelectedPlc.Name} 未连接";
            return;
        }
        try
        {
            // 管理页读面板展示全部映射点位（含写信号）；IsWrite 只描述方向，不互斥分流。
            var results = await _operations.ReadPointsAsync(SelectedPlc.PlcId, readOnlySignals: false);
            var pointMeta = await _points.GetByPlcAsync(SelectedPlc.PlcId);
            var metaByAddr = pointMeta.ToDictionary(p => p.RegisterAddress, StringComparer.OrdinalIgnoreCase);
            ReadRows.Clear();
            foreach (var r in results)
            {
                metaByAddr.TryGetValue(r.RegisterAddress, out var meta);
                var failed = !string.IsNullOrEmpty(r.Error);
                ReadRows.Add(new ReadRowVm(
                    meta?.SignalLabel ?? r.SignalLabel,
                    meta?.PositionName ?? "—",
                    r.RegisterAddress,
                    failed ? null : r.RawValue,
                    failed ? (r.SemanticText is "未连接" or "读取失败" ? r.SemanticText : "读取失败") : r.SemanticText,
                    !failed && r.IsOn));
            }
            var failN = results.Count(r => !string.IsNullOrEmpty(r.Error));
            StatusMessage = failN == 0
                ? $"读取 {results.Count} 个点位"
                : $"读取完成：失败 {failN}/{results.Count}";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    /// <summary>按点位表填充「未连接」行，清掉上次成功读到的旧值。</summary>
    private async Task ShowDisconnectedReadRowsAsync(long plcId)
    {
        var pointMeta = await _points.GetByPlcAsync(plcId);
        var reads = PlcManagementIoSet.ForManualIo(pointMeta);
        ReadRows.Clear();
        foreach (var p in reads)
        {
            ReadRows.Add(new ReadRowVm(
                p.SignalLabel,
                p.PositionName ?? "—",
                p.RegisterAddress,
                null,
                "未连接",
                false));
        }
    }

    [RelayCommand]
    private void TogglePolling() => IsPolling = !IsPolling;

    [RelayCommand]
    private async Task WriteSignalAsync()
    {
        if (SelectedWriteSignal is null || SelectedPlc is null) return;

        var msg = $"确认向 {SelectedWriteSignal.DisplayName} 写入值 {WriteValue}？\n此操作将驱动真实机台动作。";
        if (HandyControl.Controls.MessageBox.Show(msg, "写 PLC 二次确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var result = await _operations.WriteWithConfirmAsync(
            SelectedPlc.PlcId, SelectedWriteSignal.RegisterAddress, WriteValue, _user.Name);

        if (result.Error is not null)
        {
            StatusMessage = result.Error;
            HandyControl.Controls.Growl.Error(result.Error);
        }
        else if (result.Verified)
        {
            StatusMessage = $"写入成功，回读={result.ReadBackValue}";
            HandyControl.Controls.Growl.Success($"写入成功，回读={result.ReadBackValue}");
        }
        else
        {
            StatusMessage = $"已写入但回读不一致：{result.ReadBackValue}";
            HandyControl.Controls.Growl.Warning($"已写入但回读不一致：{result.ReadBackValue}");
        }
        await LoadLogsAsync(SelectedPlc.PlcId);
    }

    [RelayCommand]
    private async Task VerifyWriteAsync()
    {
        if (SelectedWriteSignal is null || SelectedPlc is null) return;
        var result = await _operations.VerifyWriteAsync(
            SelectedPlc.PlcId, SelectedWriteSignal.RegisterAddress, WriteValue);
        StatusMessage = result.Verified
            ? $"回读校验 OK = {result.ReadBackValue}"
            : $"回读={result.ReadBackValue}，期望={WriteValue}";
    }

    private async Task LoadReadRowsAsync(long plcId)
    {
        await ReadOnceAsync();
        _ = plcId;
    }

    /// <summary>
    /// 点位映射变更后刷新读/写面板。点位映射 tab 内有独立 PLC 过滤，可能与顶部所选 PLC 不一致，
    /// 故将读/写面板切到“刚编辑的那台 PLC”：不同则切换（触发完整重载），相同则就地重载。
    /// </summary>
    private async Task OnPointsChangedAsync(long plcId)
    {
        var target = PlcRows.FirstOrDefault(p => p.PlcId == plcId);
        if (target is null) return;
        if (SelectedPlc?.PlcId != plcId)
        {
            SelectedPlc = target; // 触发 OnSelectedPlcChanged → 自动重载读行/写信号/日志
            return;
        }
        await LoadWriteSignalsAsync(plcId);
        await ReadOnceAsync();
    }

    private async Task LoadWriteSignalsAsync(long plcId)
    {
        var points = await _points.GetByPlcAsync(plcId);
        var eqName = EquipmentOptions.FirstOrDefault(e => e.PlcId == plcId)?.DisplayName;
        var signals = PlcManagementIoSet.ToWriteOptions(points, eqName);
        WriteSignals.Clear();
        foreach (var s in signals) WriteSignals.Add(s);
        SelectedWriteSignal = WriteSignals.FirstOrDefault();
    }

    private async Task LoadLogsAsync(long? plcId)
    {
        var rows = await _logStore.GetRecentAsync(plcId, 30);
        LogLines.Clear();
        foreach (var r in rows) LogLines.Add(r.DisplayLine);
    }

    private async Task RefreshPlcRowAsync(long plcId)
    {
        var plcs = await _catalog.GetAllAsync();
        var updated = plcs.FirstOrDefault(p => p.PlcId == plcId);
        if (updated is null) return;

        var online = plcs.Count(p => p.IsConnected);
        var summary = $"{online} / {plcs.Count} 在线";
        var row = PlcRowVm.From(updated);

        // ConnectionChanged 可能来自后台 IO 线程；ObservableCollection 必须在 UI 线程改。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        await dispatcher.InvokeAsync(() =>
        {
            // 替换列表项会让 DataGrid 把 SelectedItem 置空（连带 SelectedPlc=null），
            // 故在替换前先记住是否选中，替换后无条件恢复，避免选中丢失导致单次读/写无目标。
            var wasSelected = SelectedPlc?.PlcId == plcId;

            var idx = -1;
            for (var i = 0; i < PlcRows.Count; i++)
            {
                if (PlcRows[i].PlcId != plcId) continue;
                idx = i;
                PlcRows[i] = row;
                break;
            }

            OnlineSummary = summary;

            if (idx >= 0 && wasSelected)
                SelectedPlc = PlcRows[idx];
        });
    }

    private void PrependLog(string line)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            LogLines.Insert(0, line);
            while (LogLines.Count > 50) LogLines.RemoveAt(LogLines.Count - 1);
        });
    }

    private void PrependAlarm(string line)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            AlarmLines.Insert(0, line);
            while (AlarmLines.Count > 20) AlarmLines.RemoveAt(AlarmLines.Count - 1);
        });
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _heartbeatTimer.Stop();
    }
}

public sealed partial class PlcRowVm : ObservableObject
{
    public long PlcId { get; init; }
    public string Name { get; init; } = "";
    public string? EquipmentName { get; init; }
    public string? EquipmentNo { get; init; }
    /// <summary>对应机台显示文本，如「内长宽 (EQ01)」，无机台时为 "—"。</summary>
    public string EquipmentText => string.IsNullOrEmpty(EquipmentName)
        ? "—"
        : string.IsNullOrEmpty(EquipmentNo) ? EquipmentName! : $"{EquipmentName} ({EquipmentNo})";
    public string Ip { get; init; } = "";
    public int Port { get; init; }
    public string Protocol { get; init; } = "";
    public bool IsConnected { get; init; }
    public string StatusText { get; init; } = "";
    public string StatusBrushKey { get; init; } = "IdleBrush";
    public string HeartbeatText { get; init; } = "000000";

    public static PlcRowVm From(PlcListItem p) => new()
    {
        PlcId = p.PlcId,
        Name = p.Name,
        EquipmentName = p.EquipmentName,
        EquipmentNo = p.EquipmentNo,
        Ip = p.Ip,
        Port = p.Port,
        Protocol = p.Protocol,
        IsConnected = p.IsConnected,
        StatusText = p.IsConnected ? "已连接" : p.LinkState switch
        {
            PlcLinkState.Faulted => "故障",
            PlcLinkState.Connecting => "连接中",
            _ => "离线"
        },
        StatusBrushKey = p.IsConnected ? "OkBrush" : p.LinkState == PlcLinkState.Faulted ? "AlarmBrush" : "IdleBrush",
        HeartbeatText = p.IsConnected ? Random.Shared.Next(1000, 999999).ToString("D6") : "------"
    };
}

public sealed record ReadRowVm(
    string SignalLabel,
    string PositionName,
    string RegisterAddress,
    int? RawValue,
    string SemanticText,
    bool IsOn);
