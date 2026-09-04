using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 点位映射独立页：维护 MAS_AUTO_PLC_POINT。
/// 按 PLC 过滤展示，支持信号表批量导入、行内编辑保存、软删除。
/// </summary>
public sealed partial class PointMappingViewModel : PageViewModelBase
{
    private readonly IPlcCatalogService _catalog;
    private readonly IPlcPointManagementService _points;
    private readonly ICurrentUser _user;
    private readonly IUserNotificationService _notify;

    public PointMappingViewModel(
        IPlcCatalogService catalog,
        IPlcPointManagementService points,
        ICurrentUser user,
        IUserNotificationService notify)
    {
        _catalog = catalog;
        _points = points;
        _user = user;
        _notify = notify;

        PlcFilters = new ObservableCollection<PlcFilterOption>();
        EquipmentOptions = new ObservableCollection<EquipmentOption>();
        PointRows = new ObservableCollection<PlcPointRow>();

        _ = InitializeAsync();
    }

    /// <summary>点位发生增删改后触发（参数为受影响的 PlcId），供 PLC 页刷新读/写面板。</summary>
    public event EventHandler<long>? PointsChanged;

    public override string Key => "point";
    public override string Title => "点位映射";
    public override string Description => "维护 MAS_AUTO_PLC_POINT：按 PLC 过滤、行内编辑、信号表批量导入。新增机台只需新增点位即可纳入轮询。";

    public ObservableCollection<PlcFilterOption> PlcFilters { get; }
    public ObservableCollection<EquipmentOption> EquipmentOptions { get; }
    public ObservableCollection<PlcPointRow> PointRows { get; }

    [ObservableProperty] private PlcFilterOption? _selectedPlc;
    [ObservableProperty] private EquipmentOption? _selectedEquipment;
    [ObservableProperty] private PlcPointRow? _selectedPoint;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private int _totalCount;

    partial void OnSelectedPlcChanged(PlcFilterOption? value)
    {
        if (value is null) return;
        SelectedEquipment = EquipmentOptions.FirstOrDefault(e => e.PlcId == value.PlcId);
        _ = LoadPointsAsync(value.PlcId);
    }

    partial void OnSelectedEquipmentChanged(EquipmentOption? value)
    {
        if (value is null) return;
        if (SelectedPlc?.PlcId != value.PlcId)
            SelectedPlc = PlcFilters.FirstOrDefault(p => p.PlcId == value.PlcId);
    }

    private async Task InitializeAsync()
    {
        await ReloadFiltersAsync();
        if (PlcFilters.Count > 0) SelectedPlc = PlcFilters[0];
    }

    [RelayCommand]
    private async Task ReloadFiltersAsync()
    {
        var plcs = await _catalog.GetAllAsync();
        var equipments = await _catalog.GetEquipmentsAsync();

        PlcFilters.Clear();
        foreach (var p in plcs) PlcFilters.Add(new PlcFilterOption(p.PlcId, p.Name));

        EquipmentOptions.Clear();
        foreach (var e in equipments) EquipmentOptions.Add(e);
    }

    [RelayCommand]
    private async Task ReloadPointsAsync()
    {
        if (SelectedPlc is null) return;
        await LoadPointsAsync(SelectedPlc.PlcId);
    }

    [RelayCommand]
    private async Task ImportSignalTableAsync()
    {
        if (SelectedEquipment is null || SelectedPlc is null)
        {
            StatusMessage = "请先选择 PLC 与机台";
            return;
        }
        try
        {
            var added = await _points.ImportSignalTableAsync(
                SelectedEquipment.Id, SelectedPlc.PlcId, _user.Name);
            await LoadPointsAsync(SelectedPlc.PlcId);
            NotifyPointsChanged();
            StatusMessage = added > 0 ? $"已导入 {added} 个点位" : "点位已存在，无需导入";
            _notify.Success(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
            _notify.Error(StatusMessage);
        }
    }

    [RelayCommand]
    private async Task SaveSelectedAsync()
    {
        if (SelectedPoint is null)
        {
            StatusMessage = "请先在列表中选中要保存的行";
            _notify.Warning(StatusMessage);
            return;
        }
        try
        {
            await _points.SavePointAsync(SelectedPoint, _user.Name);
            if (SelectedPlc is not null) await LoadPointsAsync(SelectedPlc.PlcId);
            NotifyPointsChanged();
            StatusMessage = "点位已保存";
            _notify.Success(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            _notify.Error(StatusMessage);
        }
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        if (PointRows.Count == 0)
        {
            StatusMessage = "当前没有可保存的点位";
            _notify.Warning(StatusMessage);
            return;
        }
        try
        {
            var rows = PointRows.ToList();
            foreach (var row in rows)
                await _points.SavePointAsync(row, _user.Name);
            if (SelectedPlc is not null) await LoadPointsAsync(SelectedPlc.PlcId);
            NotifyPointsChanged();
            StatusMessage = $"已保存全部 {rows.Count} 个点位";
            _notify.Success(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            _notify.Error(StatusMessage);
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedPoint is null || SelectedPoint.Id <= 0) return;
        var label = string.IsNullOrEmpty(SelectedPoint.PositionName)
            ? $"{SelectedPoint.SignalLabel} ({SelectedPoint.RegisterAddress})"
            : $"{SelectedPoint.PositionName} · {SelectedPoint.SignalLabel} ({SelectedPoint.RegisterAddress})";
        if (!_notify.Confirm($"确认删除点位 {label}？", "确认")) return;
        try
        {
            await _points.DeletePointAsync(SelectedPoint.Id, _user.Name);
            if (SelectedPlc is not null) await LoadPointsAsync(SelectedPlc.PlcId);
            NotifyPointsChanged();
            StatusMessage = "点位已删除";
            _notify.Success(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
            _notify.Error(StatusMessage);
        }
    }

    private async Task LoadPointsAsync(long plcId)
    {
        var points = await _points.GetByPlcAsync(plcId);
        PointRows.Clear();
        foreach (var p in points) PointRows.Add(p);
        TotalCount = PointRows.Count;
        StatusMessage = $"共 {TotalCount} 个点位";
    }

    /// <summary>通知订阅者（PLC 页）点位已变更，触发读/写面板刷新。</summary>
    private void NotifyPointsChanged()
    {
        if (SelectedPlc is not null) PointsChanged?.Invoke(this, SelectedPlc.PlcId);
    }
}

public sealed record PlcFilterOption(long PlcId, string Name);
