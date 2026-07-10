using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>工序管理：列表（按线体过滤）+ 编辑表单（按原型一比一）。</summary>
public sealed partial class CraftworkViewModel : PageViewModelBase
{
    private readonly ICraftworkService _service;
    private readonly IWorkLineService _workLines;
    private readonly ICurrentUser _user;

    public CraftworkViewModel(ICraftworkService service, IWorkLineService workLines, ICurrentUser user)
    {
        _service = service;
        _workLines = workLines;
        _user = user;
        Crafts = new ObservableCollection<CraftworkListItem>();
        LineFilters = new ObservableCollection<NamedOption>();
        LineOptions = new ObservableCollection<NamedOption>();
        _workLines.WorkLinesChanged += (_, _) => _ = ReloadLineOptionsAsync();
        _ = InitializeAsync();
    }

    public override string Key => "craft";
    public override string Title => "工序管理";

    public ObservableCollection<CraftworkListItem> Crafts { get; }
    /// <summary>列表顶部过滤下拉（含「全部线体」）。</summary>
    public ObservableCollection<NamedOption> LineFilters { get; }
    /// <summary>编辑表单「所属线体」下拉。</summary>
    public ObservableCollection<NamedOption> LineOptions { get; }
    public string[] TypeOptions { get; } = ["品质检测", "加工"];
    public string[] YesNoOptions { get; } = ["是", "否"];

    [ObservableProperty] private NamedOption? _selectedLineFilter;
    [ObservableProperty] private CraftworkListItem? _selectedCraft;
    [ObservableProperty] private string _editTitle = "编辑";
    [ObservableProperty] private long _editId;
    [ObservableProperty] private NamedOption? _selectedEditLine;
    [ObservableProperty] private string _editNo = "";
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _selectedType = "品质检测";
    [ObservableProperty] private string _editSort = "1";
    [ObservableProperty] private string _editPrior = "0";
    [ObservableProperty] private string _selectedAutoSend = "是";
    [ObservableProperty] private string _statusMessage = "";

    private async Task InitializeAsync()
    {
        try
        {
            await ReloadLineOptionsAsync();
            SelectedLineFilter ??= LineFilters.FirstOrDefault();
        }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
    }

    /// <summary>线体改名/增删后刷新过滤与编辑下拉，尽量保持当前选中 Id。</summary>
    private async Task ReloadLineOptionsAsync()
    {
        try
        {
            var keepFilterId = SelectedLineFilter?.Id;
            var keepEditId = SelectedEditLine?.Id;
            var lines = await _service.GetWorkLineOptionsAsync();
            LineFilters.Clear();
            LineFilters.Add(new NamedOption(0, "全部线体"));
            LineOptions.Clear();
            foreach (var l in lines) { LineFilters.Add(l); LineOptions.Add(l); }
            SelectedLineFilter = (keepFilterId is long fid ? LineFilters.FirstOrDefault(l => l.Id == fid) : null)
                                 ?? LineFilters.FirstOrDefault();
            if (keepEditId is long eid)
                SelectedEditLine = LineOptions.FirstOrDefault(l => l.Id == eid) ?? SelectedEditLine;
        }
        catch (Exception ex) { StatusMessage = $"刷新线体选项失败：{ex.Message}"; }
    }

    partial void OnSelectedLineFilterChanged(NamedOption? value) => _ = ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        var crafts = await _service.GetByLineAsync(SelectedLineFilter?.Id);
        Crafts.Clear();
        foreach (var c in crafts) Crafts.Add(c);
        SelectedCraft = Crafts.FirstOrDefault();
    }

    partial void OnSelectedCraftChanged(CraftworkListItem? value)
    {
        if (value is null) return;
        _ = LoadEditAsync(value.Id);
    }

    private async Task LoadEditAsync(long id)
    {
        var model = await _service.GetByIdAsync(id);
        if (model is null) return;
        EditId = model.Id;
        SelectedEditLine = LineOptions.FirstOrDefault(l => l.Id == model.WorkLineId);
        EditNo = model.No;
        EditName = model.Name;
        SelectedType = model.IsQuality ? "品质检测" : "加工";
        EditSort = model.Sort.ToString();
        EditPrior = model.Prior.ToString();
        SelectedAutoSend = model.AutoSend ? "是" : "否";
        EditTitle = $"编辑 · {model.No}";
    }

    [RelayCommand]
    private void NewCraft()
    {
        SelectedCraft = null;
        EditId = 0;
        SelectedEditLine = LineOptions.FirstOrDefault();
        EditNo = "";
        EditName = "";
        SelectedType = "品质检测";
        EditSort = "1";
        EditPrior = "0";
        SelectedAutoSend = "是";
        EditTitle = "新增工序";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedEditLine is null || string.IsNullOrWhiteSpace(EditName))
        {
            HandyControl.Controls.Growl.Warning("所属线体与工序名称为必填项。");
            return;
        }
        var model = new CraftworkEditModel
        {
            Id = EditId,
            WorkLineId = SelectedEditLine.Id,
            No = string.IsNullOrWhiteSpace(EditNo) ? $"CW{DateTime.Now:HHmmss}" : EditNo.Trim(),
            Name = EditName.Trim(),
            IsQuality = SelectedType == "品质检测",
            Sort = long.TryParse(EditSort, out var s) ? s : 0,
            Prior = long.TryParse(EditPrior, out var p) ? p : 0,
            AutoSend = SelectedAutoSend == "是",
            Enabled = true
        };
        try
        {
            var id = await _service.SaveAsync(model, _user.Name);
            HandyControl.Controls.Growl.Success($"工序 {model.No} 已保存");
            StatusMessage = $"工序 {model.No} 已保存";
            await ReloadAsync();
            SelectedCraft = Crafts.FirstOrDefault(c => c.Id == id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"保存失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (SelectedCraft is not null) _ = LoadEditAsync(SelectedCraft.Id);
        else NewCraft();
    }

    [RelayCommand]
    private async Task DeleteCraftAsync(CraftworkListItem? row)
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
            var msg = $"确认删除工序 {row.Name}（{row.No}）？\n软删后列表不再显示，可在 DB 恢复。";
            if (HandyControl.Controls.MessageBox.Show(msg, "删除工序二次确认",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _service.DeleteAsync(row.Id, _user.Name);
            HandyControl.Controls.Growl.Success($"工序 {row.Name} 已删除。");
            StatusMessage = $"工序 {row.Name} 已删除";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"删除失败：{ex.Message}");
        }
    }
}
