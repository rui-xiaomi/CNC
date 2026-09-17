using System.Collections.ObjectModel;
using CncLoader.Core.Rcs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>换架 / 空托盘回收面板。手动互锁由页面门禁统一判定。</summary>
public sealed partial class RcsChangeFrameViewModel : ObservableObject
{
    private readonly IRcsPageCoordinator _page;

    internal RcsChangeFrameViewModel(IRcsPageCoordinator page)
    {
        _page = page;
        ChangeFrameRoleOptions = new[] { "上料架", "下料架" };
        ChangeFrameTransactions = new ObservableCollection<ChangeFrameProgressEvent>();
    }

    public string[] ChangeFrameRoleOptions { get; }
    public ObservableCollection<ChangeFrameProgressEvent> ChangeFrameTransactions { get; }

    [ObservableProperty] private string _changeFrameEquipmentId = "1";
    [ObservableProperty] private string _changeFrameRole = "上料架";
    [ObservableProperty] private string _palletReturnFromCode = "P100";

    [RelayCommand]
    private Task ChangeFrameAsync() => ChangeFrameCoreAsync();

    [RelayCommand]
    private Task ConfirmNewFrameInPlaceAsync() => ConfirmNewFrameInPlaceCoreAsync();

    [RelayCommand]
    private Task PalletReturnAsync() => PalletReturnCoreAsync();

    public async Task ChangeFrameCoreAsync()
    {
        if (!long.TryParse(ChangeFrameEquipmentId?.Trim(), out var eqId) || eqId <= 0)
        {
            _page.Notify.Warning("请填机台 ID（数字）。");
            return;
        }
        var role = ChangeFrameRole == "下料架" ? FrameRole.Unload : FrameRole.Upload;
        if (!_page.EnsureManualDispatchAllowed("手动换架")) return;
        if (!_page.ConfirmDangerousRcs("换架", $"机台：{eqId}\n角色：{ChangeFrameRole}"))
            return;
        try
        {
            var txnId = await _page.ChangeFrame.ChangeFrameAsync(eqId, role, "operator");
            _page.Append($"> 换架 {txnId}（机台{eqId} {ChangeFrameRole}）");
            _page.Notify.Info($"换架已发起 {txnId}，进度见终端");
        }
        catch (Exception ex) { _page.Notify.Error($"换架失败：{ex.Message}"); }
    }

    public async Task ConfirmNewFrameInPlaceCoreAsync()
    {
        if (!long.TryParse(ChangeFrameEquipmentId?.Trim(), out var eqId) || eqId <= 0)
        {
            _page.Notify.Warning("请填机台 ID（数字）。");
            return;
        }
        var role = ChangeFrameRole == "下料架" ? FrameRole.Unload : FrameRole.Upload;
        try
        {
            await _page.ChangeFrame.ConfirmNewFrameInPlaceAsync(eqId, role, "operator");
            _page.Scheduler.SetEquipmentDispatchHold(eqId, false);
            _page.Append($"> 新架到位确认 机台{eqId} {ChangeFrameRole}，已解锁派工");
            _page.Notify.Success("已确认新架到位并解锁该机台自动派工");
        }
        catch (Exception ex) { _page.Notify.Error($"确认失败：{ex.Message}"); }
    }

    public async Task PalletReturnCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(PalletReturnFromCode)) { _page.Notify.Warning("请填回收点位编码。"); return; }
        if (!_page.EnsureManualDispatchAllowed("手动托盘回收")) return;
        try
        {
            var dest = (await _page.LocationMap.ResolveAreaAsync(_page.Options.PalletReturnArea))?.RcsCode;
            if (string.IsNullOrWhiteSpace(dest)) { _page.Notify.Warning($"托盘回收区 {_page.Options.PalletReturnArea} 未在 LOCATION_MAP 录入"); return; }
            if (!_page.ConfirmDangerousRcs("空托盘回收", $"起点：{PalletReturnFromCode.Trim()}\n终点：{dest}"))
                return;
            _page.Append($"> 空托盘回收 {PalletReturnFromCode} → {dest}");
            var r = await _page.Rcs.DispatchPalletReturnAsync(0, null, PalletReturnFromCode.Trim(), dest, _page.WorkLineId, _page.LineCode, "operator");
            _page.ReportResult(r);
        }
        catch (Exception ex) { _page.Notify.Error($"回收失败：{ex.Message}"); }
    }

    public void OnProgress(ChangeFrameProgressEvent e)
    {
        _page.Ui.Invoke(() =>
        {
            _page.Append($"↻ 换架 {e.TxnId} {e.Step} {e.State}{(string.IsNullOrEmpty(e.Message) ? "" : " " + e.Message)}");
            if (e.State == "COMPLETED") _page.Notify.Success($"换架 {e.TxnId} 完成");
            else if (e.State == "FAILED" || e.Step == ChangeFrameStep.Alarm) _page.Notify.Warning($"换架 {e.TxnId} 异常：{e.Message}");
        });
        _ = RefreshTransactionsAsync();
    }

    internal Task RefreshTransactionsAsync()
    {
        var rows = _page.ChangeFrame.GetActiveTransactions();
        _page.Ui.Invoke(() =>
        {
            ChangeFrameTransactions.Clear();
            foreach (var r in rows) ChangeFrameTransactions.Add(r);
        });
        return Task.CompletedTask;
    }
}
