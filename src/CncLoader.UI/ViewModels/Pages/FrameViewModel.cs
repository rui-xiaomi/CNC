using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.UI.Views.Dialogs;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 料架管理：料架列表 + 绑定关系（一架两用）+ 槽位与电极分层追踪 + 电极反查 + 人工校正（⑥a）+ 发起盘点（⑥c）。
/// </summary>
public sealed partial class FrameViewModel : PageViewModelBase
{
    private readonly IFrameService _service;
    private readonly ISlotAccountService _slots;
    private readonly IInventoryService _inventory;
    private readonly ICurrentUser _user;

    public FrameViewModel(IFrameService service, ISlotAccountService slots, IInventoryService inventory, ICurrentUser user)
    {
        _service = service;
        _slots = slots;
        _inventory = inventory;
        _user = user;
        Frames = new ObservableCollection<FrameListItem>();
        Bindings = new ObservableCollection<FrameBindRow>();
        Layers = new ObservableCollection<SlotLayerVm>();
        SlotStateOptions = new[] { "空(0)", "占用(1)", "锁定(2)", "预记(3)" };
        _inventory.InventoryCompleted += OnInventoryCompleted;
        _ = ReloadAsync();
    }

    public override string Key => "frame";
    public override string Title => "料架管理";

    public ObservableCollection<FrameListItem> Frames { get; }
    public ObservableCollection<FrameBindRow> Bindings { get; }
    public ObservableCollection<SlotLayerVm> Layers { get; }
    public string[] SlotStateOptions { get; }

    [ObservableProperty] private FrameListItem? _selectedFrame;
    [ObservableProperty] private string _bindingsTitle = "绑定关系";
    [ObservableProperty] private string _slotsTitle = "槽位与电极追踪";
    [ObservableProperty] private string _electrodeQuery = "";
    [ObservableProperty] private string _findResult = "";
    [ObservableProperty] private string _statusMessage = "";

    // 人工校正（第四阶段⑥a）
    [ObservableProperty] private SlotVm? _selectedSlot;
    [ObservableProperty] private string _correctElectrode = "";
    [ObservableProperty] private string _correctSlotState = "空(0)";

    // 发起盘点（第四阶段⑥c）
    [ObservableProperty] private int _inventoryPosStart = 101;
    [ObservableProperty] private int _inventoryCount = 5;

    [RelayCommand]
    private async Task ReloadAsync()
    {
        try
        {
            var frames = await _service.GetAllAsync();
            Frames.Clear();
            foreach (var f in frames) Frames.Add(f);
            SelectedFrame = Frames.FirstOrDefault();
        }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task AddFrameAsync()
    {
        try
        {
            var dlg = new FrameEditDialog { Owner = Application.Current?.MainWindow };
            if (dlg.ShowDialog() != true || dlg.Result is null) return;

            var id = await _service.CreateFrameAsync(dlg.Result, _user.Name);
            HandyControl.Controls.Growl.Success(
                $"料架 {dlg.Result.Name} 已新增，预建 {dlg.Result.LayerTotal * dlg.Result.SlotsPerLayer} 个空槽位。");
            StatusMessage = $"料架 {dlg.Result.Name} 已新增";
            await ReloadAsync();
            SelectedFrame = Frames.FirstOrDefault(f => f.Id == id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"新增失败：{ex.Message}");
        }
    }

    partial void OnSelectedFrameChanged(FrameListItem? value)
    {
        if (value is null) return;
        _ = LoadDetailAsync(value.Id);
    }

    private async Task LoadDetailAsync(long frameId)
    {
        var detail = await _service.GetDetailAsync(frameId);
        if (detail is null) return;

        BindingsTitle = detail.Bindings.Count > 1
            ? $"绑定关系 · {detail.Name}（一架两用）"
            : $"绑定关系 · {detail.Name}";

        Bindings.Clear();
        foreach (var b in detail.Bindings) Bindings.Add(b);

        SlotsTitle = $"槽位与电极追踪 · {detail.Name}（{detail.LayerTotal} 层 × {detail.SlotsPerLayer}，初始入库 {detail.Occupied} / {detail.SlotTotal}）";

        Layers.Clear();
        foreach (var layer in detail.Slots.GroupBy(s => s.LayerNo).OrderBy(g => g.Key))
        {
            var vm = new SlotLayerVm($"{layer.Key} 层");
            foreach (var s in layer.OrderBy(x => x.PosInLayer))
                vm.Slots.Add(new SlotVm(s.SlotNo, s.Label, s.ElectrodeId, s.SlotState));
            Layers.Add(vm);
        }

        var empty = detail.SlotTotal - detail.Occupied;
        FindResult = $"共 {detail.SlotTotal} 槽，已入库 {detail.Occupied} 个电极，空 {empty} 槽（允许不放满）";
    }

    [RelayCommand]
    private void FindElectrode()
    {
        var id = ElectrodeQuery.Trim().ToUpperInvariant();
        SlotVm? hit = null;
        foreach (var layer in Layers)
        foreach (var slot in layer.Slots)
        {
            slot.IsHighlighted = !string.IsNullOrEmpty(id)
                && string.Equals(slot.ElectrodeId, id, StringComparison.OrdinalIgnoreCase);
            if (slot.IsHighlighted) hit = slot;
        }

        FindResult = hit is not null
            ? $"电极 {id} 当前位置：{SelectedFrame?.Name} · {hit.Label}"
            : $"未找到电极 {(string.IsNullOrEmpty(id) ? "(空)" : id)}（可能未入库或已取出）";
    }

    [RelayCommand]
    private void SelectSlot(SlotVm? slot)
    {
        if (slot is not null) SelectedSlot = slot;
    }

    partial void OnSelectedSlotChanged(SlotVm? value)
    {
        if (value is null) return;
        CorrectElectrode = value.ElectrodeId ?? "";
        CorrectSlotState = value.SlotState switch
        {
            "1" => "占用(1)",
            "2" => "锁定(2)",
            "3" => "预记(3)",
            _ => "空(0)"
        };
    }

    [RelayCommand]
    private async Task CorrectSlotAsync()
    {
        if (SelectedFrame is null || SelectedSlot is null)
        {
            HandyControl.Controls.Growl.Warning("请先选中料架与槽位。");
            return;
        }
        try
        {
            var stateCode = CorrectSlotState switch
            {
                "占用(1)" => "1",
                "锁定(2)" => "2",
                "预记(3)" => "3",
                _ => "0"
            };
            var electrode = string.IsNullOrWhiteSpace(CorrectElectrode) ? null : CorrectElectrode.Trim();
            if (stateCode == "0") electrode = null;
            await _slots.SetSlotAsync(SelectedFrame.Id, SelectedSlot.SlotNo, electrode, stateCode, _user.Name);
            HandyControl.Controls.Growl.Success($"槽位 {SelectedSlot.Label} 已校正。");
            await LoadDetailAsync(SelectedFrame.Id);
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"校正失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task StartInventoryAsync()
    {
        if (SelectedFrame is null) { HandyControl.Controls.Growl.Warning("请先选中料架。"); return; }
        try
        {
            var taskId = await _inventory.StartInventoryAsync(SelectedFrame.Id, InventoryPosStart, InventoryCount, _user.Name);
            if (!string.IsNullOrEmpty(taskId))
                HandyControl.Controls.Growl.Info($"盘点已发起 {taskId}（约 3~4 分钟，完成自动刷新）");
        }
        catch (Exception ex) { HandyControl.Controls.Growl.Error($"盘点发起失败：{ex.Message}"); }
    }

    private void OnInventoryCompleted(object? sender, InventoryResultEvent e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (e.State == "COMPLETED")
            {
                HandyControl.Controls.Growl.Success($"盘点完成 {e.TaskId}：校正 {e.CorrectedCount} 个电极");
                if (SelectedFrame is not null && SelectedFrame.Id == e.FrameId) _ = LoadDetailAsync(e.FrameId);
            }
            else
                HandyControl.Controls.Growl.Warning($"盘点 {e.State} {e.TaskId}：{e.Error ?? ""}");
        });
    }
}

/// <summary>料架一层（标签 + 该层槽位）。</summary>
public sealed class SlotLayerVm
{
    public SlotLayerVm(string layerLabel) => LayerLabel = layerLabel;
    public string LayerLabel { get; }
    public ObservableCollection<SlotVm> Slots { get; } = new();
}

/// <summary>单个槽位（含占用/电极/反查高亮 + 人工校正选区）。</summary>
public sealed partial class SlotVm : ObservableObject
{
    public SlotVm(int slotNo, string label, string? electrodeId, string slotState)
    {
        SlotNo = slotNo;
        Label = label;
        ElectrodeId = electrodeId;
        SlotState = slotState;
    }

    public int SlotNo { get; }
    public string Label { get; }
    public string? ElectrodeId { get; }
    public string SlotState { get; }
    public bool Occupied => SlotState == "1";
    public bool Reserved => SlotState == "3";
    /// <summary>占用/预记显示电极ID，空槽显示「空」，锁定显示「锁」。</summary>
    public string ElectrodeText => SlotState switch
    {
        "1" or "3" => ElectrodeId ?? "—",
        "2" => "锁",
        _ => "空"
    };
    /// <summary>状态徽标颜色键（UI 转 Brush）：occupied/reserved/locked/empty。</summary>
    public string StateBadge => SlotState switch
    {
        "1" => "occupied",
        "3" => "reserved",
        "2" => "locked",
        _ => "empty"
    };

    [ObservableProperty] private bool _isHighlighted;
}
