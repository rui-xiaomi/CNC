using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Core.Abstractions;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>AGV 管理页：连通性测试（仅测试，不纳入调度）。按原型一比一还原。</summary>
public sealed partial class AgvViewModel : PageViewModelBase
{
    private readonly IAgvTestService _service;

    public AgvViewModel(IAgvTestService service)
    {
        _service = service;
        CommWayOptions = new[] { "HTTP REST", "Socket" };
        TerminalLines = new ObservableCollection<string>();
    }

    public override string Key => "agv";
    public override string Title => "AGV 管理";
    public override string Description => "AGV 连通性测试，仅测试，不纳入调度。";

    public ObservableCollection<string> TerminalLines { get; }
    public string[] CommWayOptions { get; }

    [ObservableProperty] private string _name = "AGV-1";
    [ObservableProperty] private string _selectedCommWay = "HTTP REST";
    [ObservableProperty] private string _endpoint = "http://192.168.1.30:8080";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _resultBadgeText = "未测试";
    [ObservableProperty] private string _resultBadgeBrushKey = "IdleBrush";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _statusMessage = "";

    private AgvCommWay Way => SelectedCommWay == "Socket" ? AgvCommWay.Socket : AgvCommWay.HttpRest;

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            HandyControl.Controls.Growl.Warning("请填 AGV 地址。");
            return;
        }
        IsTesting = true;
        TerminalLines.Clear();
        AppendTerminal($"> {(Way == AgvCommWay.Socket ? "TCP" : "GET")} {Endpoint}");
        try
        {
            var cfg = new AgvTestConfig
            {
                Name = Name,
                CommWay = Way,
                Endpoint = Endpoint.Trim()
            };
            var r = await _service.TestConnectionAsync(cfg);
            IsConnected = r.Connected;
            if (r.Connected)
            {
                ResultBadgeText = $"连通 {r.ElapsedMs}ms";
                ResultBadgeBrushKey = "OkBrush";
                AppendTerminal($"< 200 OK {r.ElapsedMs}ms");
                if (!string.IsNullOrEmpty(r.RawResponse)) AppendTerminal($"< {r.RawResponse}");
                StatusMessage = $"连通成功 {r.ElapsedMs}ms";
                HandyControl.Controls.Growl.Success($"AGV {Name} 测试连接成功 {r.ElapsedMs}ms");
            }
            else
            {
                ResultBadgeText = "不通";
                ResultBadgeBrushKey = "AlarmBrush";
                AppendTerminal($"< ERROR {r.Error}");
                StatusMessage = r.Error ?? "测试失败";
                HandyControl.Controls.Growl.Error($"AGV 测试失败：{r.Error}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            HandyControl.Controls.Growl.Error($"测试异常：{ex.Message}");
        }
        finally { IsTesting = false; }
    }

    private void AppendTerminal(string line)
    {
        Application.Current?.Dispatcher.Invoke(() => TerminalLines.Add(line));
    }
}
