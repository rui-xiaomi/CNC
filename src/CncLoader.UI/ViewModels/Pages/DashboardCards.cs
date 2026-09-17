using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>产线流工序节点（看板顶部横向总览）。末尾可用 IsEndFork 表示下料/NG 分叉。</summary>
public sealed partial class FlowNodeVm : ObservableObject
{
    public FlowNodeVm(string key, string title, long? equipmentId, bool isAnchor, string? equipmentNo = null)
    {
        Key = key;
        Title = title;
        EquipmentId = equipmentId;
        IsAnchor = isAnchor;
        EquipmentCode = equipmentNo ?? (equipmentId is long id ? $"EQ{id:D2}" : "");
    }

    public string Key { get; }
    public long? EquipmentId { get; }
    public bool IsAnchor { get; }
    public bool IsEquipment => !IsAnchor && EquipmentId is not null && !IsEndFork;

    [ObservableProperty] private string _equipmentCode = "";

    /// <summary>终点分叉：主卡=下料，副卡=NG。</summary>
    public bool IsEndFork { get; set; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _aggregateDisplay = "—";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _stateBadge = "idle";
    [ObservableProperty] private bool _showArrowAfter;
    [ObservableProperty] private bool _arrowActive;
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _seg1Text = "—";
    [ObservableProperty] private string _seg1Badge = "idle";
    [ObservableProperty] private string _seg2Text = "—";
    [ObservableProperty] private string _seg2Badge = "idle";

    [ObservableProperty] private string _forkSecondaryTitle = "NG 出站";
    [ObservableProperty] private string _forkSecondaryAggregate = "不良出站";
    [ObservableProperty] private string _forkSecondarySummary = "不良出站";
    [ObservableProperty] private string _forkSecondaryBadge = "ng";
    [ObservableProperty] private bool _unloadArrowActive;
    [ObservableProperty] private bool _ngArrowActive;
}

/// <summary>机台治具卡（看板签名元素）。</summary>
public sealed partial class MachineCardVm : ObservableObject
{
    public MachineCardVm(long equipmentId, string name, string? equipmentNo = null)
    {
        EquipmentId = equipmentId;
        Name = name;
        EquipmentCode = equipmentNo ?? $"EQ{equipmentId:D2}";
        Positions = new ObservableCollection<PositionCardVm>();
    }

    public long EquipmentId { get; }
    public ObservableCollection<PositionCardVm> Positions { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _equipmentCode = "";

    public void UpdateIdentity(string name, string equipmentCode)
    {
        Name = name;
        EquipmentCode = equipmentCode;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DoorText))]
    [NotifyPropertyChangedFor(nameof(DoorBadge))]
    private bool? _doorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SafeText))]
    [NotifyPropertyChangedFor(nameof(SafeBadge))]
    private bool? _safe;

    [ObservableProperty] private bool _plcOnline;
    [ObservableProperty] private bool _isHighlighted;

    public string DoorText => DoorOpen switch { true => "门 开", false => "门 闭", _ => "门 未知" };
    public string SafeText => Safe switch { true => "安全", false => "不安全", _ => "安全 未知" };
    public string DoorBadge => DoorOpen == true ? "alarm" : DoorOpen == false ? "ok" : "idle";
    public string SafeBadge => Safe == true ? "ok" : Safe == false ? "alarm" : "idle";

    public void Update(bool plcOnline, bool? safe, bool? doorOpen)
    {
        PlcOnline = plcOnline;
        Safe = safe;
        DoorOpen = doorOpen;
    }
}

/// <summary>加工位色块卡（挂在机台卡下）。</summary>
public sealed partial class PositionCardVm : ObservableObject
{
    public PositionCardVm(long equipmentId, long positionId, PositionState state, string? materialId,
        string? statusDetail,
        bool plcOnline, bool? safe, bool? doorOpen)
    {
        EquipmentId = equipmentId;
        PositionId = positionId;
        _state = state;
        _materialId = materialId;
        _statusDetail = statusDetail;
        _plcOnline = plcOnline;
        _safe = safe;
        _doorOpen = doorOpen;
    }

    public long EquipmentId { get; }
    public long PositionId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateDisplay))]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    [NotifyPropertyChangedFor(nameof(IsAlarm))]
    [NotifyPropertyChangedFor(nameof(IsVerdict))]
    [NotifyPropertyChangedFor(nameof(VerdictText))]
    private PositionState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaterialText))]
    private string? _materialId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCancelHold))]
    private string? _statusDetail;

    public bool HasCancelHold => CancelHoldDisplay.IsHold(StatusDetail);

    public bool IsAlarm => State == PositionState.Alarm;
    public bool IsVerdict => State is PositionState.DoneOk or PositionState.DoneNg or PositionState.Alarm;
    public string VerdictText => State switch
    {
        PositionState.DoneOk => "OK",
        PositionState.DoneNg => "NG",
        PositionState.Alarm => "ALM",
        _ => ""
    };
    public string MaterialText => string.IsNullOrWhiteSpace(MaterialId) ? "—" : MaterialId!;

    [ObservableProperty] private bool _plcOnline;
    [ObservableProperty] private bool? _safe;
    [ObservableProperty] private bool? _doorOpen;

    public string EquipmentText => $"EQ{EquipmentId}";
    public string PositionText => $"工位{PositionId}";
    public string StateDisplay => StatusDetail ?? PositionStateNames.ToDisplay(State);
    public string StateBadge => State switch
    {
        PositionState.Offline => "offline",
        PositionState.Alarm => "alarm",
        PositionState.Processing => "run",
        PositionState.DoneOk => "ok",
        PositionState.DoneNg => "ng",
        PositionState.Dispatching or PositionState.Transporting => "run",
        PositionState.Loaded => "warn",
        _ => "idle"
    };

    public void Update(PositionState state, string? materialId, string? statusDetail,
        bool plcOnline, bool? safe, bool? doorOpen)
    {
        State = state;
        MaterialId = materialId;
        StatusDetail = statusDetail;
        PlcOnline = plcOnline;
        Safe = safe;
        DoorOpen = doorOpen;
    }
}

/// <summary>看板底部最近加工记录行。</summary>
public sealed class WorkRecordFeedItem
{
    public long Id { get; init; }
    public DateTime? SortTime { get; init; }
    public string TimeText { get; init; } = "";
    public string EquipmentText { get; init; } = "";
    public string PositionText { get; init; } = "";
    public string MaterialText { get; init; } = "";
    public string ResultText { get; init; } = "";
    public string ResultBadge { get; init; } = "idle";
    public int SortElapsed { get; init; }
    public string ElapsedText { get; init; } = "";

    public static WorkRecordFeedItem From(WorkRecordRow r, string equipmentName)
    {
        var (resultText, badge) = r.WorkResult switch
        {
            "0" => ("OK", "ok"),
            "1" => ("NG", "ng"),
            "2" => ("异常", "alarm"),
            _ => ("进行中", "run")
        };
        var sortTime = r.WorkEndTime ?? r.WorkStartTime;
        return new WorkRecordFeedItem
        {
            Id = r.Id,
            SortTime = sortTime,
            TimeText = sortTime?.ToString("HH:mm:ss") ?? "—",
            EquipmentText = string.IsNullOrWhiteSpace(equipmentName) ? $"EQ{r.EquipmentId:D2}" : equipmentName,
            PositionText = FormatPosition(r.PositionCode),
            MaterialText = string.IsNullOrWhiteSpace(r.MaterialId) ? "—" : r.MaterialId!,
            ResultText = resultText,
            ResultBadge = badge,
            SortElapsed = r.ElapsedSeconds ?? -1,
            ElapsedText = r.ElapsedSeconds is int s ? $"{s}s" : "—"
        };
    }

    private static string FormatPosition(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "—";
        var c = code.Trim();
        if (c.StartsWith("POS-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(c.AsSpan("POS-".Length), out var idDash))
            return $"工位{idDash}";
        if (c.StartsWith("POS", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(c.AsSpan(3), out var id))
            return $"工位{id}";
        var p = c.LastIndexOf('P');
        if (p >= 0 && p + 1 < c.Length && int.TryParse(c.AsSpan(p + 1), out var idP))
            return $"工位{idP}";
        return c;
    }
}

/// <summary>看板右侧告警流条目。</summary>
public sealed partial class AlarmFeedItem : ObservableObject
{
    public long Id { get; init; }
    public DateTime Time { get; init; }
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
    public string AlarmType { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHandled))]
    [NotifyPropertyChangedFor(nameof(LevelBadge))]
    [NotifyPropertyChangedFor(nameof(StateTag))]
    private string _state = "0";

    public bool IsHandled => State is "1" or "已处理";
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelBadge => Level switch
    {
        "严重" or "1" => "alarm",
        "警告" or "2" => "warn",
        _ => IsHandled ? "idle" : "warn"
    };
    public string StateTag => IsHandled ? "已处理" : "未处理";

    public static AlarmFeedItem From(AlarmRow r) => new()
    {
        Id = r.Id,
        Time = r.Time,
        Level = r.Level,
        Message = r.Message,
        AlarmType = r.AlarmType,
        State = r.State
    };
}
