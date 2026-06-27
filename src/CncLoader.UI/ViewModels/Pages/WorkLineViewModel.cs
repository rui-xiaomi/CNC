using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>线体管理：左列表 + 右编辑表单（按原型一比一）。</summary>
public sealed partial class WorkLineViewModel : PageViewModelBase
{
    private readonly IWorkLineService _service;
    private readonly ICurrentUser _user;

    public WorkLineViewModel(IWorkLineService service, ICurrentUser user)
    {
        _service = service;
        _user = user;
        Lines = new ObservableCollection<WorkLineListItem>();
        PlcOptions = new ObservableCollection<NamedOption>();
        _ = InitializeAsync();
    }

    public override string Key => "line";
    public override string Title => "线体管理";

    public ObservableCollection<WorkLineListItem> Lines { get; }
    public ObservableCollection<NamedOption> PlcOptions { get; }
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
    [ObservableProperty] private NamedOption? _selectedPlcOption;
    [ObservableProperty] private string _statusMessage = "";

    private async Task InitializeAsync()
    {
        try
        {
            var plcs = await _service.GetPlcOptionsAsync();
            PlcOptions.Clear();
            foreach (var p in plcs) PlcOptions.Add(p);
            await ReloadAsync();
        }
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
        SelectedPlcOption = PlcOptions.FirstOrDefault(p => p.Id == model.PlcId);
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
        SelectedPlcOption = PlcOptions.FirstOrDefault();
        EditTitle = "新增线体";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditCode))
        {
            HandyControl.Controls.Growl.Warning("线体名称与编码为必填项。");
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
            PlcId = SelectedPlcOption?.Id ?? 0,
            Enabled = true
        };
        try
        {
            var id = await _service.SaveAsync(model, _user.Name);
            HandyControl.Controls.Growl.Success($"线体 {model.Code} 已保存");
            StatusMessage = $"线体 {model.Code} 已保存";
            await ReloadAsync();
            SelectedLine = Lines.FirstOrDefault(l => l.Id == id);
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
        if (SelectedLine is not null) _ = LoadEditAsync(SelectedLine.Id);
        else NewLine();
    }
}
