using System.Collections.ObjectModel;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>位置映射面板：LOCATION_MAP 录入。保存必须写英文码。</summary>
public sealed partial class RcsLocationMapViewModel : ObservableObject
{
    internal static readonly NamedOption NoneOption = new(0, "（无）");

    private readonly IRcsPageCoordinator _page;
    private bool _suppressPositionReload;
    private bool _suppressLocTypeSideEffects;
    private bool _suppressCellNameFill;

    internal RcsLocationMapViewModel(IRcsPageCoordinator page)
    {
        _page = page;
        LocTypeOptions = new[] { "区域", "机台", "加工位", "料架" };
        LocFilterTypeOptions = new[] { "全部", "区域", "机台", "加工位", "料架" };
        RcsTypeOptions = new[] { "站点", "仓位", "料架站" };
        AreaNameOptions = new[] { "上料区", "下料区", "满架缓存区", "空架缓存区", "托盘回收区" };
        Locations = new ObservableCollection<LocationMapItem>();
        FilteredLocations = new ObservableCollection<LocationMapItem>();
        LocEquipmentOptions = new ObservableCollection<NamedOption>();
        LocPositionOptions = new ObservableCollection<NamedOption>();
        LocFrameOptions = new ObservableCollection<NamedOption>();
    }

    public string[] LocTypeOptions { get; }
    public string[] LocFilterTypeOptions { get; }
    public string[] RcsTypeOptions { get; }
    public string[] AreaNameOptions { get; }
    public ObservableCollection<LocationMapItem> Locations { get; }
    public ObservableCollection<LocationMapItem> FilteredLocations { get; }
    public ObservableCollection<NamedOption> LocEquipmentOptions { get; }
    public ObservableCollection<NamedOption> LocPositionOptions { get; }
    public ObservableCollection<NamedOption> LocFrameOptions { get; }

    [ObservableProperty] private LocationMapItem? _selectedLocation;
    [ObservableProperty] private string _locType = "区域";
    [ObservableProperty] private string _locRcsCode = "";
    [ObservableProperty] private string _locRcsType = "站点";
    [ObservableProperty] private string _locName = "";
    [ObservableProperty] private NamedOption? _selectedLocEquipment;
    [ObservableProperty] private NamedOption? _selectedLocPosition;
    [ObservableProperty] private NamedOption? _selectedLocFrame;
    [ObservableProperty] private long _editingLocId;
    [ObservableProperty] private string _locFilterType = "全部";
    [ObservableProperty] private string _locationCountText = "共 0 条";

    public bool ShowLocAreaName => LocType is "区域";
    public bool ShowLocRemarkName => LocType is not "区域";
    public bool ShowLocEquipment => LocType is "机台" or "加工位";
    public bool ShowLocPosition => LocType is "加工位";
    public bool ShowLocFrame => LocType is "料架";

    partial void OnLocTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowLocAreaName));
        OnPropertyChanged(nameof(ShowLocRemarkName));
        OnPropertyChanged(nameof(ShowLocEquipment));
        OnPropertyChanged(nameof(ShowLocPosition));
        OnPropertyChanged(nameof(ShowLocFrame));
        if (_suppressLocTypeSideEffects) return;

        if (value is "区域")
        {
            _suppressPositionReload = true;
            try
            {
                SelectedLocEquipment = NoneOption;
                SelectedLocPosition = NoneOption;
                SelectedLocFrame = NoneOption;
            }
            finally { _suppressPositionReload = false; }
            _page.ReplaceOnUi(LocPositionOptions, new[] { NoneOption });
            if (string.IsNullOrWhiteSpace(LocName) || !AreaNameOptions.Contains(LocName))
                LocName = AreaNameOptions[0];
            LocRcsType = "站点";
        }
        else if (value is "料架")
        {
            _suppressPositionReload = true;
            try
            {
                SelectedLocEquipment = NoneOption;
                SelectedLocPosition = NoneOption;
            }
            finally { _suppressPositionReload = false; }
            _page.ReplaceOnUi(LocPositionOptions, new[] { NoneOption });
            if (LocRcsType is "站点") LocRcsType = "料架站";
        }
        else if (value is "机台")
        {
            _suppressPositionReload = true;
            try
            {
                SelectedLocPosition = NoneOption;
                SelectedLocFrame = NoneOption;
            }
            finally { _suppressPositionReload = false; }
        }
        else if (value is "加工位")
        {
            _suppressPositionReload = true;
            try { SelectedLocFrame = NoneOption; }
            finally { _suppressPositionReload = false; }
            if (LocRcsType is "料架站") LocRcsType = "仓位";
        }
    }

    partial void OnLocFilterTypeChanged(string value) => ApplyFilter();

    partial void OnLocRcsCodeChanged(string value)
    {
        if (!_suppressCellNameFill) TryAutoFillCellLocName();
    }

    partial void OnLocRcsTypeChanged(string value)
    {
        if (!_suppressCellNameFill) TryAutoFillCellLocName();
    }

    partial void OnSelectedLocFrameChanged(NamedOption? value)
    {
        if (!_suppressCellNameFill) TryAutoFillCellLocName();
    }

    partial void OnSelectedLocEquipmentChanged(NamedOption? value)
    {
        if (_suppressPositionReload) return;
        _ = ReloadPositionsForEquipmentAsync(value?.Id ?? 0, preferPositionId: null);
    }

    partial void OnSelectedLocationChanged(LocationMapItem? value)
    {
        if (value is null) return;
        EditingLocId = value.Id;
        if (value.FrameId is long fid && !string.IsNullOrWhiteSpace(value.FrameCode))
            _page.FrameCodes[fid] = value.FrameCode;

        _suppressLocTypeSideEffects = true;
        _suppressCellNameFill = true;
        try
        {
            LocType = LocationDisplayLabels.LocTypeToZh(value.LocType);
            LocRcsCode = value.RcsCode;
            LocRcsType = LocationDisplayLabels.RcsTypeToZh(value.RcsType);
            LocName = value.LocType == "AREA"
                ? LocationDisplayLabels.AreaNameToZh(value.LocName)
                : (value.LocName ?? "");
        }
        finally
        {
            _suppressLocTypeSideEffects = false;
            _suppressCellNameFill = false;
        }

        _suppressPositionReload = true;
        try
        {
            SelectedLocEquipment = LocEquipmentOptions.FirstOrDefault(x => x.Id == (value.EquipmentId ?? 0)) ?? NoneOption;
            SelectedLocFrame = LocFrameOptions.FirstOrDefault(x => x.Id == (value.FrameId ?? 0)) ?? NoneOption;
        }
        finally { _suppressPositionReload = false; }
        TryAutoFillCellLocName();
        _ = ReloadPositionsForEquipmentAsync(value.EquipmentId ?? 0, value.PositionId);
    }

    public static IReadOnlyList<NamedOption> PrependNone(IReadOnlyList<NamedOption> items)
    {
        var list = new List<NamedOption>(items.Count + 1) { NoneOption };
        list.AddRange(items);
        return list;
    }

    public async Task LoadRefOptionsAsync()
    {
        try
        {
            var eqs = await _page.Frames.GetEquipmentOptionsAsync();
            var frames = await _page.Equipment.GetFrameOptionsAsync();
            _page.ReplaceOnUi(LocEquipmentOptions, PrependNone(eqs));
            _page.ReplaceOnUi(LocFrameOptions, PrependNone(frames));
            _page.ReplaceOnUi(LocPositionOptions, new[] { NoneOption });
        }
        catch (Exception ex) { _page.StatusMessage = $"位置映射下拉加载失败：{ex.Message}"; }
    }

    public void TryAutoFillCellLocName()
    {
        if (LocType is not "料架" || LocRcsType is not "仓位") return;
        var frameId = SelectedLocFrame?.Id ?? 0;
        if (frameId <= 0 || !_page.FrameCodes.TryGetValue(frameId, out var shelf)) return;
        if (!RcsCellCode.TryParse(LocRcsCode, shelf, out var layer, out var pos)) return;
        LocName = RcsCellCode.FormatSlotLabel(layer, pos);
    }

    public async Task ReloadPositionsForEquipmentAsync(long equipmentId, long? preferPositionId)
    {
        try
        {
            IReadOnlyList<NamedOption> opts = new[] { NoneOption };
            if (equipmentId > 0)
            {
                var positions = await _page.Equipment.GetPositionsAsync(equipmentId);
                opts = PrependNone(positions.Select(p => new NamedOption(p.Id, $"{p.Name}({p.Code})")).ToList());
            }
            _page.ReplaceOnUi(LocPositionOptions, opts);
            var pick = preferPositionId is > 0
                ? LocPositionOptions.FirstOrDefault(x => x.Id == preferPositionId.Value) ?? NoneOption
                : NoneOption;
            _suppressPositionReload = true;
            try { SelectedLocPosition = pick; }
            finally { _suppressPositionReload = false; }
        }
        catch (Exception ex) { _page.StatusMessage = $"工位下拉加载失败：{ex.Message}"; }
    }

    public void ApplyFilter()
    {
        IEnumerable<LocationMapItem> q = Locations;
        if (LocFilterType is not "全部" and not null and not "")
        {
            var code = LocationDisplayLabels.LocTypeFromZh(LocFilterType);
            q = q.Where(x => x.LocType == code);
        }
        var list = q.ToList();
        _page.ReplaceOnUi(FilteredLocations, list);
        LocationCountText = LocFilterType is "全部" or null or ""
            ? $"共 {Locations.Count} 条"
            : $"共 {list.Count} / {Locations.Count} 条";
    }

    [RelayCommand]
    private void NewLocation() => NewLocationCore();

    [RelayCommand]
    private Task SaveLocationAsync() => SaveAsync();

    [RelayCommand]
    private Task DeleteLocationAsync() => DeleteAsync();

    [RelayCommand]
    private Task RefreshLocationsAsync() => RefreshListAsync();

    public void NewLocationCore()
    {
        EditingLocId = 0;
        SelectedLocation = null;
        _suppressLocTypeSideEffects = true;
        try
        {
            LocType = "区域";
            LocRcsCode = "";
            LocRcsType = "站点";
            LocName = AreaNameOptions[0];
        }
        finally { _suppressLocTypeSideEffects = false; }
        _suppressPositionReload = true;
        try
        {
            SelectedLocEquipment = NoneOption;
            SelectedLocFrame = NoneOption;
            SelectedLocPosition = NoneOption;
        }
        finally { _suppressPositionReload = false; }
        _page.ReplaceOnUi(LocPositionOptions, new[] { NoneOption });
        OnPropertyChanged(nameof(ShowLocAreaName));
        OnPropertyChanged(nameof(ShowLocRemarkName));
        OnPropertyChanged(nameof(ShowLocEquipment));
        OnPropertyChanged(nameof(ShowLocPosition));
        OnPropertyChanged(nameof(ShowLocFrame));
    }

    public async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(LocRcsCode)) { _page.Notify.Warning("请填 RCS 编码。"); return; }
        try
        {
            var locTypeCode = LocationDisplayLabels.LocTypeFromZh(LocType);
            var rcsTypeCode = LocationDisplayLabels.RcsTypeFromZh(LocRcsType);
            var locNameRaw = string.IsNullOrWhiteSpace(LocName) ? null : LocName.Trim();
            if (locTypeCode == "FRAME" && rcsTypeCode == "cell" && SelectedLocFrame is { Id: > 0 } frame)
            {
                if (!_page.FrameCodes.TryGetValue(frame.Id, out var shelf))
                {
                    var edit = await _page.Frames.GetFrameForEditAsync(frame.Id);
                    shelf = edit?.Code;
                    if (!string.IsNullOrWhiteSpace(shelf))
                        _page.FrameCodes[frame.Id] = shelf;
                }
                if (!string.IsNullOrWhiteSpace(shelf)
                    && RcsCellCode.TryParse(LocRcsCode.Trim(), shelf, out var layer, out var pos))
                    locNameRaw = RcsCellCode.FormatSlotLabel(layer, pos);
            }
            long? eqId = ShowLocEquipment && SelectedLocEquipment is { Id: > 0 } e ? e.Id : null;
            long? posId = ShowLocPosition && SelectedLocPosition is { Id: > 0 } p ? p.Id : null;
            long? frameId = ShowLocFrame && SelectedLocFrame is { Id: > 0 } f ? f.Id : null;
            if (locTypeCode == "POSITION" && (eqId is null || posId is null))
            {
                _page.Notify.Warning("加工位映射请选择机台和工位。");
                return;
            }
            if (locTypeCode == "EQUIPMENT" && eqId is null)
            {
                _page.Notify.Warning("机台映射请选择机台。");
                return;
            }
            if (locTypeCode == "FRAME" && frameId is null)
            {
                _page.Notify.Warning("料架映射请选择料架。");
                return;
            }
            if (locTypeCode == "AREA" && string.IsNullOrWhiteSpace(locNameRaw))
            {
                _page.Notify.Warning("区域映射请选择名称。");
                return;
            }

            var item = new LocationMapItem
            {
                Id = EditingLocId,
                LocType = locTypeCode,
                RcsCode = LocRcsCode.Trim(),
                RcsType = rcsTypeCode,
                LocName = locTypeCode == "AREA"
                    ? LocationDisplayLabels.AreaNameFromZh(locNameRaw)
                    : locNameRaw,
                EquipmentId = eqId,
                PositionId = posId,
                FrameId = frameId
            };
            await _page.LocationMap.SaveAsync(item, "system");
            _page.Notify.Success("位置映射已保存。");
            await RefreshListAsync();
            NewLocationCore();
        }
        catch (Exception ex) { _page.Notify.Error($"保存失败：{ex.Message}"); }
    }

    public async Task DeleteAsync()
    {
        if (EditingLocId <= 0) { _page.Notify.Warning("请先选中一行。"); return; }
        try
        {
            await _page.LocationMap.DeleteAsync(EditingLocId);
            _page.Notify.Success("已删除（软删）。");
            await RefreshListAsync();
            NewLocationCore();
        }
        catch (Exception ex) { _page.Notify.Error($"删除失败：{ex.Message}"); }
    }

    internal async Task RefreshListAsync()
    {
        try
        {
            var keepId = SelectedLocation?.Id ?? EditingLocId;
            var rows = await _page.LocationMap.GetAllAsync();
            _page.FrameCodes.Clear();
            foreach (var row in rows)
            {
                if (row.FrameId is long fid && !string.IsNullOrWhiteSpace(row.FrameCode))
                    _page.FrameCodes[fid] = row.FrameCode;
            }
            _page.ReplaceOnUi(Locations, rows);
            ApplyFilter();
            if (keepId > 0)
                SelectedLocation = FilteredLocations.FirstOrDefault(x => x.Id == keepId)
                    ?? Locations.FirstOrDefault(x => x.Id == keepId);
        }
        catch (Exception ex) { _page.StatusMessage = $"位置映射加载失败：{ex.Message}"; }
    }
}
