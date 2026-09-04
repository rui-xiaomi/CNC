using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.UI.Services;

namespace CncLoader.Core.Tests.UI;

/// <summary>
/// 通知记录桩：不弹任何视觉元素，只把「操作员会看到什么」记下来供断言。
/// </summary>
internal sealed class RecordingNotify : IUserNotificationService
{
    public List<string> Successes { get; } = new();
    public List<string> Infos { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Alerts { get; } = new();
    public List<string> Confirmations { get; } = new();

    /// <summary>二次确认的应答（默认确认）。</summary>
    public bool ConfirmAnswer { get; set; } = true;

    public void Success(string message) => Successes.Add(message);
    public void Info(string message) => Infos.Add(message);
    public void Warning(string message) => Warnings.Add(message);
    public void Error(string message) => Errors.Add(message);
    public void Alert(string message, string title) => Alerts.Add(message);

    public bool Confirm(string message, string title)
    {
        Confirmations.Add(message);
        return ConfirmAnswer;
    }
}

/// <summary>
/// UI 线程调度桩：一律同步直跑，让 headless 测试能在断言前观察到集合/属性变化。
/// </summary>
internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action) => action();
    public void Post(Action action) => action();
}

/// <summary>
/// 对话框桩：默认全部返回 null（等同用户取消），需要走「确认保存」分支时按需赋值。
/// </summary>
internal sealed class StubDialogService : IDialogService
{
    public PlcEditModel? PlcResult { get; set; }
    public FrameCreateModel? FrameCreateResult { get; set; }
    public FrameEditModel? FrameEditResult { get; set; }
    public EquipmentCreateModel? EquipmentCreateResult { get; set; }
    public EquipmentEditModel? EquipmentEditResult { get; set; }
    public FrameBindSelection? FrameBindResult { get; set; }

    public int EditPlcCount { get; private set; }
    public int CreateFrameCount { get; private set; }
    public int EditFrameCount { get; private set; }
    public int CreateEquipmentCount { get; private set; }
    public int EditEquipmentCount { get; private set; }
    public int BindFramesCount { get; private set; }

    public PlcEditModel? EditPlc(PlcEditModel? existing, long suggestedPlcId,
        IReadOnlyList<EquipmentOption> equipments, long? boundEquipmentId)
    {
        EditPlcCount++;
        return PlcResult;
    }

    public FrameCreateModel? CreateFrame()
    {
        CreateFrameCount++;
        return FrameCreateResult;
    }

    public FrameEditModel? EditFrame(FrameEditModel existing)
    {
        EditFrameCount++;
        return FrameEditResult;
    }

    public EquipmentCreateModel? CreateEquipment(IReadOnlyList<NamedOption> crafts,
        IReadOnlyList<NamedOption> plcs, string suggestedNo, long? preselectCraftId)
    {
        CreateEquipmentCount++;
        return EquipmentCreateResult;
    }

    public EquipmentEditModel? EditEquipment(IReadOnlyList<NamedOption> crafts,
        IReadOnlyList<NamedOption> plcs, EquipmentEditModel existing)
    {
        EditEquipmentCount++;
        return EquipmentEditResult;
    }

    public FrameBindSelection? BindFrames(string equipmentDisplay, IReadOnlyList<NamedOption> frames,
        long? currentUploadId, long? currentDownloadId)
    {
        BindFramesCount++;
        return FrameBindResult;
    }
}
