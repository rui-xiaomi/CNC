using System.Windows;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.UI.Views.Dialogs;

namespace CncLoader.UI.Services;

/// <summary>
/// 生产实现：构造真实 Window、统一挂 Owner 为主窗口（保证 CenterOwner 与模态归属），
/// 并把 <c>DialogResult</c> 折成「结果或 null」。
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    public PlcEditModel? EditPlc(PlcEditModel? existing, long suggestedPlcId,
        IReadOnlyList<EquipmentOption> equipments, long? boundEquipmentId)
    {
        var dialog = new PlcEditDialog(existing, suggestedPlcId, equipments, boundEquipmentId);
        return ShowModal(dialog) ? dialog.Result : null;
    }

    public FrameCreateModel? CreateFrame()
    {
        var dialog = new FrameEditDialog();
        return ShowModal(dialog) ? dialog.Result : null;
    }

    public FrameEditModel? EditFrame(FrameEditModel existing)
    {
        var dialog = new FrameEditDialog(existing);
        return ShowModal(dialog) ? dialog.EditResult : null;
    }

    public EquipmentCreateModel? CreateEquipment(IReadOnlyList<NamedOption> crafts,
        IReadOnlyList<NamedOption> plcs, string suggestedNo, long? preselectCraftId)
    {
        var dialog = new EquipmentEditDialog(crafts, plcs, suggestedNo, preselectCraftId);
        return ShowModal(dialog) ? dialog.CreateResult : null;
    }

    public EquipmentEditModel? EditEquipment(IReadOnlyList<NamedOption> crafts,
        IReadOnlyList<NamedOption> plcs, EquipmentEditModel existing)
    {
        var dialog = new EquipmentEditDialog(crafts, plcs, existing);
        return ShowModal(dialog) ? dialog.EditResult : null;
    }

    public FrameBindSelection? BindFrames(string equipmentDisplay, IReadOnlyList<NamedOption> frames,
        long? currentUploadId, long? currentDownloadId)
    {
        var dialog = new FrameBindDialog(equipmentDisplay, frames, currentUploadId, currentDownloadId);
        return ShowModal(dialog)
            ? new FrameBindSelection(dialog.UploadFrameId, dialog.DownloadFrameId)
            : null;
    }

    private static bool ShowModal(Window dialog)
    {
        dialog.Owner = Application.Current?.MainWindow;
        return dialog.ShowDialog() == true;
    }
}
