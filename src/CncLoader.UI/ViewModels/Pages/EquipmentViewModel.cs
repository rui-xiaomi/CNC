using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.UI.Views.Dialogs;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 机台管理：列表（按工序过滤）+ 加工位（自动 2 位）+ 关联料架（按原型一比一）。
/// 加工位随机台新增自动建 2 个；点位映射在 PLC 页维护。机台/料架绑定的新增配置作为后续表单接入点。
/// </summary>
public sealed partial class EquipmentViewModel : PageViewModelBase
{
    private readonly IEquipmentConfigService _service;
    private readonly ICurrentUser _user;

    public EquipmentViewModel(IEquipmentConfigService service, ICurrentUser user)
    {
        _service = service;
        _user = user;
        Equipments = new ObservableCollection<EquipmentListItem>();
        CraftFilters = new ObservableCollection<NamedOption>();
        Positions = new ObservableCollection<PositionItem>();
        FrameBindings = new ObservableCollection<EquipmentFrameBinding>();
        _ = InitializeAsync();
    }

    public override string Key => "eq";
    public override string Title => "机台管理";

    public ObservableCollection<EquipmentListItem> Equipments { get; }
    public ObservableCollection<NamedOption> CraftFilters { get; }
    public ObservableCollection<PositionItem> Positions { get; }
    public ObservableCollection<EquipmentFrameBinding> FrameBindings { get; }

    [ObservableProperty] private NamedOption? _selectedCraftFilter;
    [ObservableProperty] private EquipmentListItem? _selectedEquipment;
    [ObservableProperty] private string _positionsTitle = "加工位（自动 2 位）";
    [ObservableProperty] private string _statusMessage = "";

    private async Task InitializeAsync()
    {
        try
        {
            var crafts = await _service.GetCraftworkOptionsAsync();
            CraftFilters.Clear();
            CraftFilters.Add(new NamedOption(0, "全部工序"));
            foreach (var c in crafts) CraftFilters.Add(c);
            SelectedCraftFilter = CraftFilters.FirstOrDefault();
        }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
    }

    partial void OnSelectedCraftFilterChanged(NamedOption? value) => _ = ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        var equipments = await _service.GetByCraftAsync(SelectedCraftFilter?.Id);
        Equipments.Clear();
        foreach (var e in equipments) Equipments.Add(e);
        SelectedEquipment = Equipments.FirstOrDefault();
    }

    partial void OnSelectedEquipmentChanged(EquipmentListItem? value)
    {
        if (value is null)
        {
            Positions.Clear();
            FrameBindings.Clear();
            PositionsTitle = "加工位（自动 2 位）";
            return;
        }
        PositionsTitle = $"加工位（自动 2 位）· {value.No}";
        _ = LoadDetailAsync(value.Id);
    }

    private async Task LoadDetailAsync(long equipmentId)
    {
        var positions = await _service.GetPositionsAsync(equipmentId);
        Positions.Clear();
        foreach (var p in positions) Positions.Add(p);

        var binds = await _service.GetFrameBindingsAsync(equipmentId);
        FrameBindings.Clear();
        foreach (var b in binds) FrameBindings.Add(b);
    }

    [RelayCommand]
    private async Task AddEquipmentAsync()
    {
        try
        {
            var crafts = await _service.GetCraftworkOptionsAsync();
            if (crafts.Count == 0)
            {
                HandyControl.Controls.Growl.Warning("请先在工序管理页创建工序，再新增机台。");
                return;
            }
            var plcs = await _service.GetPlcOptionsAsync();
            var nextNo = await _service.SuggestNextNoAsync();

            var dlg = new EquipmentEditDialog(crafts, plcs, nextNo, SelectedCraftFilter?.Id)
            {
                Owner = Application.Current?.MainWindow
            };
            if (dlg.ShowDialog() != true || dlg.CreateResult is null) return;

            var id = await _service.CreateEquipmentAsync(dlg.CreateResult, _user.Name);
            HandyControl.Controls.Growl.Success($"机台 {dlg.CreateResult.No} 已新增，并自动创建 2 个加工位。");
            StatusMessage = $"机台 {dlg.CreateResult.No} 已新增";
            await ReloadAsync();
            SelectedEquipment = Equipments.FirstOrDefault(e => e.Id == id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"新增失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task EditEquipmentAsync(EquipmentListItem? row)
    {
        if (row is null) return;
        try
        {
            var edit = await _service.GetByIdAsync(row.Id);
            if (edit is null)
            {
                HandyControl.Controls.Growl.Warning("该机台不存在或已删除。");
                await ReloadAsync();
                return;
            }
            var crafts = await _service.GetCraftworkOptionsAsync();
            var plcs = await _service.GetPlcOptionsAsync();
            var dlg = new EquipmentEditDialog(crafts, plcs, edit)
            {
                Owner = Application.Current?.MainWindow
            };
            if (dlg.ShowDialog() != true || dlg.EditResult is null) return;

            await _service.UpdateAsync(dlg.EditResult, _user.Name);
            HandyControl.Controls.Growl.Success($"机台 {dlg.EditResult.Name} 已更新。");
            StatusMessage = $"机台 {dlg.EditResult.Name} 已更新";
            await ReloadAsync();
            SelectedEquipment = Equipments.FirstOrDefault(e => e.Id == row.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"更新失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ConfigureFrameAsync()
    {
        if (SelectedEquipment is null)
        {
            HandyControl.Controls.Growl.Warning("请先选择机台。");
            return;
        }
        try
        {
            var frames = await _service.GetFrameOptionsAsync();
            var current = await _service.GetFrameBindingIdsAsync(SelectedEquipment.Id);
            var dlg = new FrameBindDialog(
                $"{SelectedEquipment.Name} ({SelectedEquipment.No})", frames,
                current.UploadFrameId, current.DownloadFrameId)
            {
                Owner = Application.Current?.MainWindow
            };
            if (dlg.ShowDialog() != true) return;

            await _service.SetFrameBindingAsync(SelectedEquipment.Id, dlg.UploadFrameId, dlg.DownloadFrameId, _user.Name);
            HandyControl.Controls.Growl.Success("关联料架已更新。");
            StatusMessage = "关联料架已更新";
            await LoadDetailAsync(SelectedEquipment.Id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"配置失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteEquipmentAsync(EquipmentListItem? row)
    {
        if (row is null) return;
        try
        {
            var check = await _service.CheckDeleteAsync(row.Id);
            if (!check.CanDelete)
            {
                HandyControl.Controls.Growl.Warning(check.Message);
                StatusMessage = check.Message;
                return;
            }
            var msg = $"确认删除机台 {row.Name}（{row.No}）？\n将级联软删其 2 个加工位；软删后列表不再显示，可在 DB 恢复。";
            if (HandyControl.Controls.MessageBox.Show(msg, "删除机台二次确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteAsync(row.Id, _user.Name);
            HandyControl.Controls.Growl.Success($"机台 {row.Name} 已删除。");
            StatusMessage = $"机台 {row.Name} 已删除";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"删除失败：{ex.Message}");
        }
    }
}
