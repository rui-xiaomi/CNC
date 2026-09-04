using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>线体管理：左列表 + 右编辑表单（按原型一比一）。
/// 业务链：线体→工序→机台→PLC；线体不直接关联 PLC，故编辑表单无 PLC 字段。</summary>
public sealed partial class WorkLineViewModel : PageViewModelBase
{
    private readonly IWorkLineService _service;
    private readonly ICurrentUser _user;
    private readonly IUserNotificationService _notify;

    public WorkLineViewModel(IWorkLineService service, ICurrentUser user,
        IUserNotificationService notify)
    {
        _service = service;
        _user = user;
        _notify = notify;
        Lines = new ObservableCollection<WorkLineListItem>();
        _ = InitializeAsync();
    }

    public override string Key => "line";
    public override string Title => "线体管理";

    public ObservableCollection<WorkLineListItem> Lines { get; }
    public string[] ScanOptions { get; } = ["否", "是"];

    [ObservableProperty] private WorkLineListItem? _selectedLine;
    [ObservableProperty] private string _editTitle = "编辑";
    [ObservableProperty] private long _editId;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editCode = "";
    [ObservableProperty] private string _editComputer = "";
    [ObservableProperty] private string _editComputerIp = "";
    [ObservableProperty] private string _editPlanNum = "";
    [ObservableProperty] private string _selectedScan = "否";
    [ObservableProperty] private string _statusMessage = "";

    private async Task InitializeAsync()
    {
        try { await ReloadAsync(); }
        catch (Exception ex) { StatusMessage = $"加载失败：{ex.Message}"; }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        var lines = await _service.GetAllAsync();
        Lines.Clear();
        foreach (var l in lines) Lines.Add(l);
        SelectedLine = Lines.FirstOrDefault();
    }

    partial void OnSelectedLineChanged(WorkLineListItem? value)
    {
        if (value is null) return;
        _ = LoadEditAsync(value.Id);
    }

    private async Task LoadEditAsync(long id)
    {
        var model = await _service.GetByIdAsync(id);
        if (model is null) return;
        EditId = model.Id;
        EditName = model.Name;
        EditCode = model.Code;
        EditComputer = model.Computer ?? "";
        EditComputerIp = model.ComputerIp ?? "";
        EditPlanNum = model.PlanNum?.ToString() ?? "";
        SelectedScan = model.ScanEnabled ? "是" : "否";
        EditTitle = $"编辑 · {model.Code}";
    }

    [RelayCommand]
    private void NewLine()
    {
        SelectedLine = null;
        EditId = 0;
        EditName = "";
        EditCode = "";
        EditComputer = "";
        EditComputerIp = "";
        EditPlanNum = "";
        SelectedScan = "否";
        EditTitle = "新增线体";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditCode))
        {
            _notify.Warning("线体名称与编码为必填项。");
            return;
        }
        var model = new WorkLineEditModel
        {
            Id = EditId,
            Name = EditName.Trim(),
            Code = EditCode.Trim(),
            Computer = string.IsNullOrWhiteSpace(EditComputer) ? null : EditComputer.Trim(),
            ComputerIp = string.IsNullOrWhiteSpace(EditComputerIp) ? null : EditComputerIp.Trim(),
            PlanNum = long.TryParse(EditPlanNum, out var n) ? n : null,
            ScanEnabled = SelectedScan == "是",
            Enabled = true
        };
        try
        {
            var id = await _service.SaveAsync(model, _user.Name);
            _notify.Success($"线体 {model.Code} 已保存");
            StatusMessage = $"线体 {model.Code} 已保存";
            await ReloadAsync();
            SelectedLine = Lines.FirstOrDefault(l => l.Id == id);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _notify.Error($"保存失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (SelectedLine is not null) _ = LoadEditAsync(SelectedLine.Id);
        else NewLine();
    }

    [RelayCommand]
    private async Task DeleteLineAsync(WorkLineListItem? row)
    {
        if (row is null) return;
        try
        {
            var check = await _service.CheckDeleteAsync(row.Id);
            if (!check.CanDelete)
            {
                _notify.Warning(check.Message);
                StatusMessage = check.Message;
                return;
            }
            var msg = $"确认删除线体 {row.Name}（{row.Code}）？\n软删后列表不再显示，可在 DB 恢复。";
            if (!_notify.Confirm(msg, "删除线体二次确认")) return;
            await _service.DeleteAsync(row.Id, _user.Name);
            _notify.Success($"线体 {row.Name} 已删除。");
            StatusMessage = $"线体 {row.Name} 已删除";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _notify.Error($"删除失败：{ex.Message}");
        }
    }
}
