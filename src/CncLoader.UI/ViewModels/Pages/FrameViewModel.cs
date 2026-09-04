using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.UI.Services;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>
/// 料架管理：料架列表 + 绑定关系（新增/改角色/解绑）+ 槽位与物料分层追踪 + 物料反查
/// + 人工校正（⑥a）+ 发起盘点（⑥c）。列表占用数与槽位随节拍动态刷新（原地更新，不丢选中/编辑）。
/// </summary>
public sealed partial class FrameViewModel : PageViewModelBase
{
    private readonly IFrameService _service;
    private readonly ISlotAccountService _slots;
    private readonly IInventoryService _inventory;
    private readonly IPositionScheduler _scheduler;
    private readonly ICurrentUser _user;
    private readonly IUserNotificationService _notify;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _refreshTimer;

    public FrameViewModel(IFrameService service, ISlotAccountService slots, IInventoryService inventory,
        IPositionScheduler scheduler, ICurrentUser user, IUserNotificationService notify,
        IUiDispatcher ui, IDialogService dialogs)
    {
        _service = service;
        _slots = slots;
        _inventory = inventory;
        _scheduler = scheduler;
        _user = user;
        _notify = notify;
        _ui = ui;
        _dialogs = dialogs;
        Frames = new ObservableCollection<FrameRowVm>();
        Bindings = new ObservableCollection<FrameBindRow>();
        Layers = new ObservableCollection<SlotLayerVm>();
        EquipmentOptions = new ObservableCollection<NamedOption>();
        // 人工校正仅允许空/占用/锁定；预记只能由派工 Reserve 创建（展示仍见 SlotVm）。
        SlotStateOptions = new[] { "空(0)", "占用(1)", "锁定(2)" };
        BindRoleOptions = new[]
        {
            new RoleOption("0", "上料架"), new RoleOption("1", "下料架"),
            new RoleOption("2", "中转架"), new RoleOption("3", "NG架")
        };
        _selectedBindRole = BindRoleOptions[0];
        _inventory.InventoryCompleted += OnInventoryCompleted;

        // 动态刷新：随节拍原地刷新列表占用数 + 当前料架槽位（不丢选中项/校正框焦点）
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _refreshTimer.Tick += async (_, _) => await RefreshTickAsync();

        _ = InitAsync();
    }

    public override string Key => "frame";
    public override string Title => "料架管理";

    public ObservableCollection<FrameRowVm> Frames { get; }
    public ObservableCollection<FrameBindRow> Bindings { get; }
    public ObservableCollection<SlotLayerVm> Layers { get; }
    public ObservableCollection<NamedOption> EquipmentOptions { get; }
    public string[] SlotStateOptions { get; }
    public RoleOption[] BindRoleOptions { get; }

    [ObservableProperty] private FrameRowVm? _selectedFrame;
    [ObservableProperty] private string _bindingsTitle = "绑定关系";
    [ObservableProperty] private string _slotsTitle = "槽位与物料追踪";
    [ObservableProperty] private string _materialQuery = "";
    [ObservableProperty] private string _findResult = "";
    [ObservableProperty] private string _statusMessage = "";
    /// <summary>仅显示绑定了 NG 角色的料架。</summary>
    [ObservableProperty] private bool _showNgOnly;

    // 绑定编辑（Task B）
    [ObservableProperty] private NamedOption? _selectedBindEquipment;
    [ObservableProperty] private RoleOption _selectedBindRole;

    // 人工校正（第四阶段⑥a）
    [ObservableProperty] private SlotVm? _selectedSlot;
    [ObservableProperty] private string _correctMaterial = "";
    [ObservableProperty] private string _correctSlotState = "空(0)";

    // 发起盘点（第四阶段⑥c）
    [ObservableProperty] private int _inventoryPosStart = 101;
    [ObservableProperty] private int _inventoryCount = 5;

    private List<FrameListItem> _allFrames = new();

    private async Task InitAsync()
    {
        await LoadEquipmentOptionsAsync();
        await ReloadAsync();
        _refreshTimer.Start();
    }

    private async Task LoadEquipmentOptionsAsync()
    {
        try
        {
            var eqs = await _service.GetEquipmentOptionsAsync();
            EquipmentOptions.Clear();
            foreach (var e in eqs) EquipmentOptions.Add(e);
            SelectedBindEquipment ??= EquipmentOptions.FirstOrDefault();
        }
        catch (Exception ex) { StatusMessage = $"机台下拉加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        try
        {
            _allFrames = (await _service.GetAllAsync()).ToList();
            ApplyFrameFilter(keepSelection: false);
        }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
    }

    partial void OnShowNgOnlyChanged(bool value) => ApplyFrameFilter(keepSelection: true);

    private void ApplyFrameFilter(bool keepSelection)
    {
        var keepId = keepSelection ? SelectedFrame?.Id : null;
        var source = ShowNgOnly ? _allFrames.Where(f => f.HasNgRole) : _allFrames;
        Frames.Clear();
        foreach (var f in source) Frames.Add(new FrameRowVm(f));
        SelectedFrame = keepId is long id
            ? Frames.FirstOrDefault(r => r.Id == id) ?? Frames.FirstOrDefault()
            : Frames.FirstOrDefault();
    }

    /// <summary>定时原地刷新：列表占用数 + 当前料架槽位状态，不重建集合，不丢选中/编辑。</summary>
    private async Task RefreshTickAsync()
    {
        try
        {
            _allFrames = (await _service.GetAllAsync()).ToList();
            var filtered = (ShowNgOnly ? _allFrames.Where(f => f.HasNgRole) : _allFrames).ToList();
            // 列表占用数原地更新（按 Id 匹配；数量变化才整体重载）
            if (filtered.Count != Frames.Count || filtered.Any(f => Frames.All(r => r.Id != f.Id)))
            {
                ApplyFrameFilter(keepSelection: true);
                return;
            }
            foreach (var f in filtered)
            {
                var row = Frames.FirstOrDefault(r => r.Id == f.Id);
                if (row is not null)
                {
                    row.Occupied = f.Occupied;
                    row.HasNgRole = f.HasNgRole;
                }
            }

            if (SelectedFrame is null) return;
            var detail = await _service.GetDetailAsync(SelectedFrame.Id);
            if (detail is null) return;

            // 槽位原地更新（按 SlotNo 匹配物料/状态）；层结构变化才重建
            var slotById = Layers.SelectMany(l => l.Slots).ToDictionary(s => s.SlotNo);
            if (detail.Slots.Count != slotById.Count)
            {
                RebuildLayers(detail);
            }
            else
            {
                foreach (var s in detail.Slots)
                    if (slotById.TryGetValue(s.SlotNo, out var vm)) vm.Update(s.MaterialId, s.SlotState);
            }

            var empty = detail.SlotTotal - detail.Occupied;
            SlotsTitle = $"槽位与物料追踪 · {detail.Name}（{detail.LayerTotal} 层 × {detail.SlotsPerLayer}，入库 {detail.Occupied} / {detail.SlotTotal}）";
            if (string.IsNullOrEmpty(MaterialQuery))
                FindResult = $"共 {detail.SlotTotal} 槽，已入库 {detail.Occupied} 个物料，空 {empty} 槽（允许不放满）";
        }
        catch { /* 刷新失败静默，下拍再试 */ }
    }

    [RelayCommand]
    private async Task AddFrameAsync()
    {
        try
        {
            var created = _dialogs.CreateFrame();
            if (created is null) return;

            var id = await _service.CreateFrameAsync(created, _user.Name);
            _notify.Success(
                $"料架 {created.Name} 已新增，预建 {created.LayerTotal * created.SlotsPerLayer} 个空槽位。");
            StatusMessage = $"料架 {created.Name} 已新增";
            await ReloadAsync();
            SelectedFrame = Frames.FirstOrDefault(f => f.Id == id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _notify.Error($"新增失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task EditFrameAsync()
    {
        if (SelectedFrame is null) { _notify.Warning("请先选中料架。"); return; }
        try
        {
            var edit = await _service.GetFrameForEditAsync(SelectedFrame.Id);
            if (edit is null) { _notify.Warning("料架不存在。"); return; }
            var edited = _dialogs.EditFrame(edit);
            if (edited is null) return;

            await _service.UpdateFrameAsync(edited, _user.Name);
            _notify.Success($"料架 {edited.Name} 已更新。");
            var keepId = SelectedFrame.Id;
            await ReloadAsync();
            SelectedFrame = Frames.FirstOrDefault(f => f.Id == keepId) ?? Frames.FirstOrDefault();
        }
        catch (Exception ex) { _notify.Error($"编辑失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task DeleteFrameAsync()
    {
        if (SelectedFrame is null) { _notify.Warning("请先选中料架。"); return; }
        try
        {
            var check = await _service.CheckDeleteFrameAsync(SelectedFrame.Id);
            if (!check.CanDelete) { _notify.Warning(check.Message); return; }
            if (!_notify.Confirm($"确认删除料架 {SelectedFrame.Name}？（软删，可恢复）", "删除确认")) return;

            await _service.DeleteFrameAsync(SelectedFrame.Id, _user.Name);
            _notify.Success($"料架 {SelectedFrame.Name} 已删除。");
            await ReloadAsync();
        }
        catch (Exception ex) { _notify.Error($"删除失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task BindEquipmentAsync()
    {
        if (SelectedFrame is null) { _notify.Warning("请先选中料架。"); return; }
        if (SelectedBindEquipment is null) { _notify.Warning("请选择要绑定的机台。"); return; }
        try
        {
            var eqId = SelectedBindEquipment.Id;
            await _service.BindEquipmentAsync(SelectedFrame.Id, eqId, SelectedBindRole.Code, _user.Name);
            _scheduler.InvalidateFrameBindingCache(eqId);
            _notify.Success($"已绑定：{SelectedBindEquipment.DisplayName} · {SelectedBindRole.Label}");
            await LoadDetailAsync(SelectedFrame.Id);
        }
        catch (Exception ex) { _notify.Error($"绑定失败：{ex.Message}"); }
    }

    [RelayCommand]
    private async Task UnbindAsync(FrameBindRow? row)
    {
        if (row is null || SelectedFrame is null) return;
        if (!_notify.Confirm($"确认解绑 {row.EquipmentDisplay} · {row.RoleText}？", "解绑确认")) return;
        try
        {
            await _service.UnbindAsync(row.BindId, _user.Name);
            _scheduler.InvalidateFrameBindingCache(row.EquipmentId);
            _notify.Success($"已解绑 {row.EquipmentDisplay} · {row.RoleText}");
            await LoadDetailAsync(SelectedFrame.Id);
        }
        catch (Exception ex) { _notify.Error($"解绑失败：{ex.Message}"); }
    }

    partial void OnSelectedFrameChanged(FrameRowVm? value)
    {
        if (value is null) return;
        _ = LoadDetailAsync(value.Id);
    }

    /// <summary>完整加载明细：重建槽位分层 + 绑定关系 + 标题（切换料架/绑定变化时用）。</summary>
    private async Task LoadDetailAsync(long frameId)
    {
        var detail = await _service.GetDetailAsync(frameId);
        if (detail is null) return;

        BindingsTitle = detail.Bindings.Count > 1
            ? $"绑定关系 · {detail.Name}（一架多用）"
            : $"绑定关系 · {detail.Name}";

        Bindings.Clear();
        foreach (var b in detail.Bindings) Bindings.Add(b);

        RebuildLayers(detail);

        var empty = detail.SlotTotal - detail.Occupied;
        FindResult = $"共 {detail.SlotTotal} 槽，已入库 {detail.Occupied} 个物料，空 {empty} 槽（允许不放满）";
    }

    private void RebuildLayers(FrameDetail detail)
    {
        SlotsTitle = $"槽位与物料追踪 · {detail.Name}（{detail.LayerTotal} 层 × {detail.SlotsPerLayer}，入库 {detail.Occupied} / {detail.SlotTotal}）";
        Layers.Clear();
        foreach (var layer in detail.Slots.GroupBy(s => s.LayerNo).OrderBy(g => g.Key))
        {
            var vm = new SlotLayerVm($"{layer.Key} 层");
            foreach (var s in layer.OrderBy(x => x.PosInLayer))
                vm.Slots.Add(new SlotVm(s.SlotNo, s.Label, s.MaterialId, s.SlotState));
            Layers.Add(vm);
        }
    }

    [RelayCommand]
    private void FindMaterial()
    {
        var id = MaterialQuery.Trim().ToUpperInvariant();
        SlotVm? hit = null;
        foreach (var layer in Layers)
        foreach (var slot in layer.Slots)
        {
            slot.IsHighlighted = !string.IsNullOrEmpty(id)
                && string.Equals(slot.MaterialId, id, StringComparison.OrdinalIgnoreCase);
            if (slot.IsHighlighted) hit = slot;
        }

        FindResult = hit is not null
            ? $"物料 {id} 当前位置：{SelectedFrame?.Name} · {hit.Label}"
            : $"未找到物料 {(string.IsNullOrEmpty(id) ? "(空)" : id)}（可能未入库或已取出）";
    }

    [RelayCommand]
    private void SelectSlot(SlotVm? slot)
    {
        if (slot is not null) SelectedSlot = slot;
    }

    partial void OnSelectedSlotChanged(SlotVm? value)
    {
        if (value is null) return;
        CorrectMaterial = value.MaterialId ?? "";
        // 预记槽仍正确展示；编辑框默认落到允许的选项（不提供「预记」人工目标）。
        CorrectSlotState = value.SlotState switch
        {
            "1" => "占用(1)",
            "2" => "锁定(2)",
            _ => "空(0)"
        };
    }

    [RelayCommand]
    private async Task CorrectSlotAsync()
    {
        if (SelectedFrame is null || SelectedSlot is null)
        {
            _notify.Warning("请先选中料架与槽位。");
            return;
        }
        try
        {
            var stateCode = CorrectSlotState switch
            {
                "占用(1)" => "1",
                "锁定(2)" => "2",
                _ => "0"
            };
            var material = string.IsNullOrWhiteSpace(CorrectMaterial) ? null : CorrectMaterial.Trim();
            if (stateCode == "0") material = null;
            var label = SelectedSlot.Label;
            var result = await _slots.SetSlotAsync(SelectedFrame.Id, SelectedSlot.SlotNo, material, stateCode, _user.Name);
            NotifySlotMutationResult(result, SlotMutationOp.Correct, label);
            await LoadDetailAsync(SelectedFrame.Id);
        }
        catch (Exception ex) { _notify.Error($"校正失败：{ex.Message}"); }
    }

    /// <summary>NG 闭环：选中槽一键置空释放（不向后流转）。</summary>
    [RelayCommand]
    private async Task ClearSelectedSlotAsync()
    {
        if (SelectedFrame is null || SelectedSlot is null)
        {
            _notify.Warning("请先选中料架与槽位。");
            return;
        }
        try
        {
            var label = SelectedSlot.Label;
            var result = await _slots.SetSlotAsync(SelectedFrame.Id, SelectedSlot.SlotNo, null, SlotStates.Empty, _user.Name);
            NotifySlotMutationResult(result, SlotMutationOp.Clear, label);
            CorrectMaterial = "";
            CorrectSlotState = "空(0)";
            await LoadDetailAsync(SelectedFrame.Id);
        }
        catch (Exception ex) { _notify.Error($"置空失败：{ex.Message}"); }
    }

    private enum SlotMutationOp { Correct, Clear }

    /// <summary>将 <see cref="SlotMutationResult"/> 映射为单次用户通知（不落 Core、不暴露内部 Message）。</summary>
    private void NotifySlotMutationResult(SlotMutationResult result, SlotMutationOp op, string slotLabel)
    {
        switch (result.Status)
        {
            case SlotMutationStatus.Updated:
                _notify.Success(op == SlotMutationOp.Clear
                    ? $"槽位 {slotLabel} 已置空释放。"
                    : $"槽位 {slotLabel} 已校正。");
                break;
            case SlotMutationStatus.Unchanged:
                _notify.Info("槽位状态无需修改");
                break;
            case SlotMutationStatus.ReservationConflict:
                _notify.Warning(op == SlotMutationOp.Clear
                    ? "槽位已被任务预记，不能清空"
                    : "槽位已被任务预记，不能人工校正");
                break;
            case SlotMutationStatus.InvalidTargetState:
                _notify.Warning("预记状态只能由派工流程创建，不能人工设置");
                break;
            case SlotMutationStatus.NotFound:
                _notify.Error("槽位不存在或已被删除");
                break;
            case SlotMutationStatus.ConcurrencyConflict:
                _notify.Warning("槽位状态已变化，请刷新后重试");
                break;
            case SlotMutationStatus.DatabaseError:
                _notify.Error("槽位操作失败，请查看日志");
                break;
            case SlotMutationStatus.Cancelled:
                // 静默：不记用户可见失败；命令 IsRunning 由 RelayCommand 自然收尾
                break;
            default:
                _notify.Error("槽位操作未完成，请刷新后重试");
                break;
        }
    }

    [RelayCommand]
    private async Task StartInventoryAsync()
    {
        if (SelectedFrame is null) { _notify.Warning("请先选中料架。"); return; }
        try
        {
            var taskId = await _inventory.StartInventoryAsync(SelectedFrame.Id, InventoryPosStart, InventoryCount, _user.Name);
            if (!string.IsNullOrEmpty(taskId))
                _notify.Info($"盘点已发起 {taskId}（约 3~4 分钟，完成自动刷新）");
            // 拒发（互斥/缺 LOCATION）走 InventoryCompleted FAILED → OnInventoryCompleted
        }
        catch (Exception ex) { _notify.Error($"盘点发起失败：{ex.Message}"); }
    }

    private void OnInventoryCompleted(object? sender, InventoryResultEvent e)
    {
        _ui.Invoke(() =>
        {
            NotifyInventoryFinalResult(e);
            if (e.State == "COMPLETED"
                && SelectedFrame is not null
                && SelectedFrame.Id == e.FrameId)
                _ = LoadDetailAsync(e.FrameId);
        });
    }

    /// <summary>最终盘点回写通知：只发一次；发起 Info 不算完成。</summary>
    private void NotifyInventoryFinalResult(InventoryResultEvent e)
    {
        var c = e.Correction;
        if (c is null)
        {
            if (e.State == "COMPLETED")
                _notify.Success($"盘点完成：校正 {e.CorrectedCount} 个物料");
            else if (e.State is "CANCELED" or "CANCELLED")
                return;
            else
                _notify.Warning($"盘点 {e.State}：{e.Error ?? ""}");
            return;
        }

        if (c.Status == InventoryCorrectionStatus.Cancelled)
            return;

        if (c.Status == InventoryCorrectionStatus.DatabaseError)
        {
            _notify.Error("盘点回写失败，请查看日志");
            return;
        }

        if (c.HasWarnings || c.Status == InventoryCorrectionStatus.CompletedWithWarnings)
        {
            if (c.ReservationConflictCount > 0 && c.UpdatedCount == 0
                && c.UnchangedCount == 0 && c.NotFoundCount == 0 && c.ConcurrencyConflictCount == 0)
            {
                _notify.Warning($"盘点完成：未更新，全部为预记槽（共 {c.ReservationConflictCount} 个）");
                return;
            }

            var parts = new List<string> { $"已更新 {c.UpdatedCount} 个" };
            if (c.ReservationConflictCount > 0)
                parts.Add($"跳过预记槽 {c.ReservationConflictCount} 个");
            if (c.NotFoundCount > 0)
                parts.Add($"未找到 {c.NotFoundCount} 个");
            if (c.ConcurrencyConflictCount > 0)
                parts.Add($"并发冲突 {c.ConcurrencyConflictCount} 个");
            if (c.UnchangedCount > 0)
                parts.Add($"无需修改 {c.UnchangedCount} 个");
            _notify.Warning("盘点完成：" + string.Join("，", parts));
            return;
        }

        _notify.Success($"盘点完成：已更新 {c.UpdatedCount} 个"
            + (c.UnchangedCount > 0 ? $"，无需修改 {c.UnchangedCount} 个" : ""));
    }
}

/// <summary>料架列表行 VM（占用数可观察，供动态刷新原地更新）。</summary>
public sealed partial class FrameRowVm : ObservableObject
{
    public FrameRowVm(FrameListItem item)
    {
        Id = item.Id;
        Name = item.Name;
        IdentifyCode = item.IdentifyCode;
        LayoutText = item.LayoutText;
        SlotTotal = item.SlotTotal;
        _occupied = item.Occupied;
        _hasNgRole = item.HasNgRole;
    }

    public long Id { get; }
    public string Name { get; }
    public string IdentifyCode { get; }
    public string LayoutText { get; }
    public int SlotTotal { get; }
    [ObservableProperty] private int _occupied;
    [ObservableProperty] private bool _hasNgRole;
}

/// <summary>绑定角色下拉项。</summary>
public sealed record RoleOption(string Code, string Label);

/// <summary>料架一层（标签 + 该层槽位）。</summary>
public sealed class SlotLayerVm
{
    public SlotLayerVm(string layerLabel) => LayerLabel = layerLabel;
    public string LayerLabel { get; }
    public ObservableCollection<SlotVm> Slots { get; } = new();
}

/// <summary>单个槽位（含占用/物料/反查高亮 + 人工校正选区）。物料/状态可观察，供动态刷新原地更新。</summary>
public sealed partial class SlotVm : ObservableObject
{
    public SlotVm(int slotNo, string label, string? materialId, string slotState)
    {
        SlotNo = slotNo;
        Label = label;
        _materialId = materialId;
        _slotState = slotState;
    }

    public int SlotNo { get; }
    public string Label { get; }

    [ObservableProperty] private string? _materialId;
    [ObservableProperty] private string _slotState;

    /// <summary>原地更新物料/状态（动态刷新用，联动派生属性）。</summary>
    public void Update(string? materialId, string slotState)
    {
        MaterialId = materialId;
        SlotState = slotState;
    }

    public bool Occupied => SlotState == "1";
    public bool Reserved => SlotState == "3";
    /// <summary>占用/预记显示物料ID，空槽显示「空」，锁定显示「锁」。</summary>
    public string MaterialText => SlotState switch
    {
        "1" or "3" => MaterialId ?? "—",
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

    partial void OnMaterialIdChanged(string? value) { OnPropertyChanged(nameof(MaterialText)); }
    partial void OnSlotStateChanged(string value)
    {
        OnPropertyChanged(nameof(Occupied));
        OnPropertyChanged(nameof(Reserved));
        OnPropertyChanged(nameof(MaterialText));
        OnPropertyChanged(nameof(StateBadge));
    }
}
