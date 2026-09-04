using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;

namespace CncLoader.UI.Services;

/// <summary>
/// 模态编辑对话框接缝。ViewModel 只表达「要编辑什么」并拿回结果，不再自己 <c>new Window</c>、
/// 设 Owner、调 <c>ShowDialog()</c>——那样 ViewModel 在无视觉树时无法构造，命令逻辑也无法单测。
/// 返回 null 表示用户取消。
/// </summary>
public interface IDialogService
{
    PlcEditModel? EditPlc(PlcEditModel? existing, long suggestedPlcId,
        IReadOnlyList<EquipmentOption> equipments, long? boundEquipmentId);

    FrameCreateModel? CreateFrame();

    FrameEditModel? EditFrame(FrameEditModel existing);

    EquipmentCreateModel? CreateEquipment(IReadOnlyList<NamedOption> crafts,
        IReadOnlyList<NamedOption> plcs, string suggestedNo, long? preselectCraftId);

    EquipmentEditModel? EditEquipment(IReadOnlyList<NamedOption> crafts,
        IReadOnlyList<NamedOption> plcs, EquipmentEditModel existing);

    /// <summary>配置机台的上/下料架绑定。返回 null 表示取消。</summary>
    FrameBindSelection? BindFrames(string equipmentDisplay, IReadOnlyList<NamedOption> frames,
        long? currentUploadId, long? currentDownloadId);
}

/// <summary>料架绑定对话框的选择结果；null 表示该角色不绑定。</summary>
public sealed record FrameBindSelection(long? UploadFrameId, long? DownloadFrameId);
