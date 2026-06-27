using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.Signals;

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
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    [RelayCommand]
    private async Task DisconnectPlcAsync(PlcRowVm? row)
    {
        if (row is null) return;
        await _connections.DisconnectAsync(row.PlcId);
        await RefreshPlcRowAsync(row.PlcId);
        StatusMessage = $"{row.Name} 已断开";
    }

    [RelayCommand]
    private async Task ConnectAllAsync()
    {
        await _connections.ConnectAllAsync();
        await ReloadAllAsync();
        StatusMessage = "全部连接完成";
    }

    [RelayCommand]
    private async Task DisconnectAllAsync()
    {
        await _connections.DisconnectAllAsync();
        await ReloadAllAsync();
        StatusMessage = "全部已断开";
    }

    /// <summary>
    /// 原型保留的"新增 PLC"按钮。完整 CRUD 表单将随机台管理页（Phase 3）一并实现。
    /// </summary>
    [RelayCommand]
    private void AddPlc()
    {
        HandyControl.Controls.Growl.Info(
            "PLC 配置 CRUD（新增/编辑/删除）将随机台管理页在 Phase 3 一并实现。\n当前可在 cnc_schema.sql 种子数据或直接修改 MAS_AUTO_WORKLINE_PLC 表。");
    }

    [RelayCommand]
    private async Task ReadOnceAsync()
    {
        if (SelectedPlc is null) return;
        try
        {
            var results = await _operations.ReadPointsAsync(SelectedPlc.PlcId);
            var pointMeta = await _points.GetByPlcAsync(SelectedPlc.PlcId);
            var metaByAddr = pointMeta.ToDictionary(p => p.RegisterAddress, StringComparer.OrdinalIgnoreCase);
            ReadRows.Clear();
            foreach (var r in results)
            {
                metaByAddr.TryGetValue(r.RegisterAddress, out var meta);
                ReadRows.Add(new ReadRowVm(
                    meta?.SignalLabel ?? r.SignalLabel,
                    meta?.PositionName ?? "—",
                    r.RegisterAddress,
                    r.RawValue,
                    r.SemanticText,
                    r.IsOn));
            }
            StatusMessage = $"读取 {results.Count} 个点位";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
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

    private async Task LoadWriteSignalsAsync(long plcId)
    {
        var signals = await _points.GetWriteSignalsAsync(plcId);
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

        var idx = -1;
        for (var i = 0; i < PlcRows.Count; i++)
        {
            if (PlcRows[i].PlcId != plcId) continue;
            idx = i;
            PlcRows[i] = PlcRowVm.From(updated);
            break;
        }

        var online = plcs.Count(p => p.IsConnected);
        OnlineSummary = $"{online} / {plcs.Count} 在线";

        if (idx >= 0 && SelectedPlc?.PlcId == plcId)
            SelectedPlc = PlcRows[idx];
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
    int RawValue,
    string SemanticText,
    bool IsOn);
