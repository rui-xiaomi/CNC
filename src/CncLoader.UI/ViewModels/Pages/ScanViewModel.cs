using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CncLoader.Core.Abstractions;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>扫码枪管理页：连通性监听（仅测试，不纳入来料校验）。按原型一比一还原。</summary>
public sealed partial class ScanViewModel : PageViewModelBase, IDisposable
{
    private readonly IScanListenerService _service;
    private readonly IUserNotificationService _notify;
    private readonly IUiDispatcher _ui;

    public ScanViewModel(IScanListenerService service, IUserNotificationService notify, IUiDispatcher ui)
    {
        _service = service;
        _notify = notify;
        _ui = ui;
        ModeOptions = new[] { "TCP 服务端（被动接收）", "TCP 客户端（主动连）" };
        ScanRows = new ObservableCollection<ScanRowVm>();

        _service.ScanReceived += OnScanReceived;
        _service.ConnectedCountChanged += OnConnectedChanged;
    }

    public override string Key => "scan";
    public override string Title => "扫码枪管理";
    public override string Description => "扫码枪连通性测试，仅测试，不纳入来料校验。";

    public ObservableCollection<ScanRowVm> ScanRows { get; }
    public string[] ModeOptions { get; }

    [ObservableProperty] private string _selectedMode = "TCP 服务端（被动接收）";
    [ObservableProperty] private string _port = "9100";
    [ObservableProperty] private bool _isListening;
    [ObservableProperty] private int _connectedCount;
    [ObservableProperty] private string _statusBadgeText = "未监听";
    [ObservableProperty] private string _statusBadgeBrushKey = "IdleBrush";
    [ObservableProperty] private string _statusMessage = "";

    public bool CanStart => !IsListening;
    public bool CanStop => IsListening;

    partial void OnIsListeningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        StatusBadgeText = value ? $"已连接 {ConnectedCount}" : "未监听";
        StatusBadgeBrushKey = value ? "OkBrush" : "IdleBrush";
    }

    partial void OnConnectedCountChanged(int value)
    {
        if (IsListening) StatusBadgeText = $"已连接 {value}";
    }

    [RelayCommand]
    private async Task StartListenAsync()
    {
        if (!int.TryParse(Port, out var p) || p <= 0 || p > 65535)
        {
            _notify.Warning("监听端口需为 1..65535。");
            return;
        }
        var ok = await _service.StartAsync(p);
        if (ok)
        {
            IsListening = true;
            StatusMessage = $"已监听端口 {p}";
            _notify.Success($"扫码枪开始监听端口 {p}");
        }
        else
        {
            StatusMessage = "启动监听失败";
            _notify.Error("启动监听失败，请检查端口是否被占用。");
        }
    }

    [RelayCommand]
    private async Task StopListenAsync()
    {
        await _service.StopAsync();
        IsListening = false;
        ConnectedCount = 0;
        StatusMessage = "已停止监听";
        _notify.Info("扫码枪监听已停止");
    }

    private void OnScanReceived(ScanRecord rec)
    {
        _ui.Invoke(() =>
        {
            ScanRows.Insert(0, new ScanRowVm(rec.Time.ToString("HH:mm:ss"), rec.Source, rec.Content));
            while (ScanRows.Count > 50) ScanRows.RemoveAt(ScanRows.Count - 1);
        });
    }

    private void OnConnectedChanged(int v)
    {
        _ui.Invoke(() => ConnectedCount = v);
    }

    public void Dispose()
    {
        _service.ScanReceived -= OnScanReceived;
        _service.ConnectedCountChanged -= OnConnectedChanged;
        _ = _service.StopAsync();
    }
}

public sealed record ScanRowVm(string Time, string Source, string Content);
