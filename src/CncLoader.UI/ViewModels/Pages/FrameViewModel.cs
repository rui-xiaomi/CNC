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
/// 料架管理：料架列表 + 绑定关系（一架两用）+ 槽位与电极分层追踪 + 电极反查（按原型一比一）。
/// </summary>
public sealed partial class FrameViewModel : PageViewModelBase
{
    private readonly IFrameService _service;
    private readonly ICurrentUser _user;

    public FrameViewModel(IFrameService service, ICurrentUser user)
    {
        _service = service;
        _user = user;
        Frames = new ObservableCollection<FrameListItem>();
        Bindings = new ObservableCollection<FrameBindRow>();
        Layers = new ObservableCollection<SlotLayerVm>();
        _ = ReloadAsync();
    }

    public override string Key => "frame";
    public override string Title => "料架管理";

    public ObservableCollection<FrameListItem> Frames { get; }
    public ObservableCollection<FrameBindRow> Bindings { get; }
    public ObservableCollection<SlotLayerVm> Layers { get; }

    [ObservableProperty] private FrameListItem? _selectedFrame;
    [ObservableProperty] private string _bindingsTitle = "绑定关系";
    [ObservableProperty] private string _slotsTitle = "槽位与电极追踪";
    [ObservableProperty] private string _electrodeQuery = "";
    [ObservableProperty] private string _findResult = "";
    [ObservableProperty] private string _statusMessage = "";

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
                vm.Slots.Add(new SlotVm(s.Label, s.ElectrodeId, s.Occupied));
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
}

/// <summary>料架一层（标签 + 该层槽位）。</summary>
public sealed class SlotLayerVm
{
    public SlotLayerVm(string layerLabel) => LayerLabel = layerLabel;
    public string LayerLabel { get; }
    public ObservableCollection<SlotVm> Slots { get; } = new();
}

/// <summary>单个槽位（含占用/电极/反查高亮）。</summary>
public sealed partial class SlotVm : ObservableObject
{
    public SlotVm(string label, string? electrodeId, bool occupied)
    {
        Label = label;
        ElectrodeId = electrodeId;
        Occupied = occupied;
    }

    public string Label { get; }
    public string? ElectrodeId { get; }
    public bool Occupied { get; }
    /// <summary>占用槽显示电极ID，空槽显示「空」。</summary>
    public string ElectrodeText => Occupied ? ElectrodeId ?? "—" : "空";

    [ObservableProperty] private bool _isHighlighted;
}
