using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.State;
using CncLoader.UI.Views.Dialogs;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 机台管理：列表（按工序过滤）+ 加工位（自动 2 位）+ 关联料架（按原型一比一）。
/// 加工位状态订阅 <see cref="ISignalStateStore"/> 实时刷新；点位映射在 PLC 页维护。
/// </summary>
public sealed partial class EquipmentViewModel : PageViewModelBase
{
    private readonly IEquipmentConfigService _service;
    private readonly ISignalStateStore _store;
    private readonly IPositionScheduler _scheduler;
    private readonly ICurrentUser _user;

    private readonly Dictionary<long, EquipmentPositionRowVm> _positionRows = new();
    private readonly DispatcherTimer _positionThrottle;
    private volatile bool _positionsDirty;
    private long _selectedEquipmentId;

    public EquipmentViewModel(IEquipmentConfigService service, ISignalStateStore store,
        IPositionScheduler scheduler, ICurrentUser user)
    {
        _service = service;
        _store = store;
        _scheduler = scheduler;
        _user = user;
        Equipments = new ObservableCollection<EquipmentListItem>();
        CraftFilters = new ObservableCollection<NamedOption>();
        Positions = new ObservableCollection<EquipmentPositionRowVm>();
        FrameBindings = new ObservableCollection<EquipmentFrameBinding>();
        _store.PositionChanged += OnPositionChanged;

        _positionThrottle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _positionThrottle.Tick += (_, _) => { if (_positionsDirty) { _positionsDirty = false; UpdatePositionStatesUi(); } };
        _positionThrottle.Start();

        _ = InitializeAsync();
    }

    public override string Key => "eq";
    public override string Title => "机台管理";

    public ObservableCollection<EquipmentListItem> Equipments { get; }
    public ObservableCollection<NamedOption> CraftFilters { get; }
    public ObservableCollection<EquipmentPositionRowVm> Positions { get; }
    public ObservableCollection<EquipmentFrameBinding> FrameBindings { get; }

    [ObservableProperty] private NamedOption? _selectedCraftFilter;
    [ObservableProperty] private EquipmentListItem? _selectedEquipment;
    [ObservableProperty] private string _positionsTitle = "加工位（自动 2 位）";
    [ObservableProperty] private string _statusMessage = "";

    private void OnPositionChanged(object? sender, PositionStatus ps)
    {
        if (ps.EquipmentId != _selectedEquipmentId) return;
        _positionsDirty = true;
    }

    private void UpdatePositionStatesUi()
    {
        foreach (var row in _positionRows.Values)
        {
            var live = _store.GetPosition(_selectedEquipmentId, row.Id);
            row.Update(live?.State ?? PositionState.Offline);
        }
    }

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
            _selectedEquipmentId = 0;
            _positionRows.Clear();
            Positions.Clear();
            FrameBindings.Clear();
            PositionsTitle = "加工位（自动 2 位）";
            return;
        }
        _selectedEquipmentId = value.Id;
        PositionsTitle = $"加工位（自动 2 位）· {value.No}";
        _ = LoadDetailAsync(value.Id);
    }

    private async Task LoadDetailAsync(long equipmentId)
    {
        var positions = await _service.GetPositionsAsync(equipmentId);
        _positionRows.Clear();
        Positions.Clear();
        foreach (var p in positions)
        {
            var live = _store.GetPosition(equipmentId, p.Id);
            var row = new EquipmentPositionRowVm(p.Id, p.Name, p.Code, live?.State ?? PositionState.Offline);
            _positionRows[p.Id] = row;
            Positions.Add(row);
        }

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
            _scheduler.InvalidateFrameBindingCache(SelectedEquipment.Id);
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

/// <summary>机台详情加工位行（实时状态来自 ISignalStateStore）。</summary>
public sealed partial class EquipmentPositionRowVm : ObservableObject
{
    public EquipmentPositionRowVm(long id, string name, string code, PositionState state)
    {
        Id = id;
        Name = name;
        Code = code;
        _state = state;
    }

    public long Id { get; }
    public string Name { get; }
    public string Code { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateDisplay))]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    private PositionState _state;

    public string StateDisplay => PositionStateNames.ToDisplay(State);
    public string StateBadge => State switch
    {
        PositionState.Offline => "offline",
        PositionState.Alarm => "alarm",
        PositionState.Processing => "run",
        PositionState.DoneOk => "ok",
        PositionState.DoneNg => "ng",
        PositionState.Dispatching or PositionState.Transporting => "run",
        _ => "idle"
    };

    public void Update(PositionState state) => State = state;
}
