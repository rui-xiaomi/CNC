using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CncLoader.Data.Entities;

/// <summary>加工/检测记录 MAS_AUTO_WORK_RECORD（检测结果 OK/NG 写入此处）。</summary>
[Table("MAS_AUTO_WORK_RECORD")]
public class WorkRecord
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("WORKLINE_ID")] public long WorkLineId { get; set; }
    [Column("CRAFTWORK_ID")] public long CraftworkId { get; set; }
    [Column("EQUIMENT_ID")] public long EquipmentId { get; set; }
    [Column("POSITION_CODE")] public string PositionCode { get; set; } = "";
    [Column("MATERIALCODE")] public string? MaterialCode { get; set; }
    [Column("ELECTRODE_ID")] public string? MaterialId { get; set; }
    [Column("UPLOAD_TIME")] public DateTime? UploadTime { get; set; }
    [Column("WORK_START_TIME")] public DateTime? WorkStartTime { get; set; }
    [Column("WORK_END_TIME")] public DateTime? WorkEndTime { get; set; }
    [Column("DOWNLOAD_TIME")] public DateTime? DownloadTime { get; set; }
    /// <summary>0=OK,1=NG,2=异常。</summary>
    [Column("WORK_RESULT")] public string WorkResult { get; set; } = "0";
    [Column("REMARK")] public string? Remark { get; set; }
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>AGV/RCS 任务记录 MAS_AUTO_AGV_TASK（第四阶段扩展：承载 RCS 任务全生命周期）。</summary>
[Table("MAS_AUTO_AGV_TASK")]
public class AgvTask
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>RCS 全局唯一 taskId（先落库后发送）。</summary>
    [Column("RCS_TASK_ID")] public string? RcsTaskId { get; set; }
    /// <summary>RCS 接口种类 transit/grab/identify/pallet_return/change_frame。</summary>
    [Column("RCS_KIND")] public string? RcsKind { get; set; }
    [Column("WORKLINE_ID")] public long WorkLineId { get; set; }
    /// <summary>0=上料 1=下料 2=转序。</summary>
    [Column("TASK_TYPE")] public string TaskType { get; set; } = "0";
    /// <summary>[legacy] 改用 TaskState。</summary>
    [Column("TASK_STATUS")] public string TaskStatus { get; set; } = "0";
    /// <summary>本系统态 CREATED/DISPATCHED/EXECUTING/COMPLETED/FAILED/CANCELED。</summary>
    [Column("TASK_STATE")] public string TaskState { get; set; } = "CREATED";
    /// <summary>RCS 原始 11 态。</summary>
    [Column("RCS_STATUS")] public string? RcsStatus { get; set; }
    /// <summary>优先级 1-10 大者优先。</summary>
    [Column("PRIORITY")] public int Priority { get; set; } = 5;
    [Column("FROM_FRAME_CODE")] public string FromFrameCode { get; set; } = "";
    [Column("TO_FRAME_CODE")] public string ToFrameCode { get; set; } = "";
    [Column("EQUIMENT_ID")] public long? EquipmentId { get; set; }
    [Column("POSITION_ID")] public long? PositionId { get; set; }
    [Column("CRAFTWORK_ID")] public long? CraftworkId { get; set; }
    /// <summary>关联工件/物料码。</summary>
    [Column("ELECTRODE_ID")] public string? MaterialId { get; set; }
    /// <summary>换架事务 ID（关联先拉后送任务对）。</summary>
    [Column("TXN_ID")] public string? TxnId { get; set; }
    /// <summary>下发参数快照（position/param）。</summary>
    [Column("REQ_PARAM")] public string? ReqParam { get; set; }
    /// <summary>落库时间。</summary>
    [Column("SEND_TIME")] public DateTime SendTime { get; set; }
    /// <summary>下发成功时间。</summary>
    [Column("DISPATCH_TIME")] public DateTime? DispatchTime { get; set; }
    [Column("FINISH_TIME")] public DateTime? FinishTime { get; set; }
    /// <summary>redo 次数（同 taskId 幂等重发）。</summary>
    [Column("REDO_COUNT")] public int RedoCount { get; set; }
    /// <summary>取消后人工处理确认 0=未确认 1=已确认。</summary>
    [Column("CANCEL_MANUAL_FLAG")] public string CancelManualFlag { get; set; } = "0";
    [Column("ERROR_MSG")] public string? ErrorMsg { get; set; }
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>逻辑位置↔RCS点位编码映射 MAS_AUTO_LOCATION_MAP（station/cell 双层）。</summary>
[Table("MAS_AUTO_LOCATION_MAP")]
public class LocationMap
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>逻辑位置类型 EQUIPMENT/POSITION/FRAME/AREA。</summary>
    [Column("LOC_TYPE")] public string LocType { get; set; } = "AREA";
    [Column("EQUIMENT_ID")] public long? EquipmentId { get; set; }
    [Column("POSITION_ID")] public long? PositionId { get; set; }
    [Column("FRAME_ID")] public long? FrameId { get; set; }
    /// <summary>逻辑位置名称（缓存区/备料区/托盘回收区等命名点）。</summary>
    [Column("LOC_NAME")] public string? LocName { get; set; }
    /// <summary>RCS 点位编码 如 101(station) / 601203(cell)。</summary>
    [Column("RCS_CODE")] public string RcsCode { get; set; } = "";
    /// <summary>点位类型 shelf/cell/station。</summary>
    [Column("RCS_TYPE")] public string RcsType { get; set; } = "station";
    [Column("REMARK")] public string? Remark { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>RCS 双向报文流水 MAS_AUTO_RCS_MSG_LOG。</summary>
[Table("MAS_AUTO_RCS_MSG_LOG")]
public class RcsMsgLog
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>方向 OUT=出站请求 IN=入站回调。</summary>
    [Column("DIRECTION")] public string Direction { get; set; } = "OUT";
    /// <summary>接口名 transitTask/.../pushTaskStatus/...。</summary>
    [Column("INTERFACE_NAME")] public string? InterfaceName { get; set; }
    [Column("URL")] public string? Url { get; set; }
    [Column("TASK_ID")] public string? TaskId { get; set; }
    [Column("REQUEST_BODY")] public string? RequestBody { get; set; }
    [Column("RESPONSE_BODY")] public string? ResponseBody { get; set; }
    [Column("COST_MS")] public int? CostMs { get; set; }
    /// <summary>0=成功 1=失败。</summary>
    [Column("RESULT")] public string Result { get; set; } = "0";
    [Column("ERROR_MSG")] public string? ErrorMsg { get; set; }
    [Column("CREATE_TIME")] public DateTime? CreateTime { get; set; }
}

/// <summary>设备通信流水 MAS_AUTO_DEVICE_LOG。</summary>
[Table("MAS_AUTO_DEVICE_LOG")]
public class DeviceLog
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("DEVICE_TYPE")] public string DeviceType { get; set; } = "PLC";
    [Column("DEVICE_ID")] public long? DeviceId { get; set; }
    [Column("ACTION")] public string Action { get; set; } = "READ";
    [Column("REGISTER_ADDR")] public string? RegisterAddr { get; set; }
    [Column("REQUEST_DATA")] public string? RequestData { get; set; }
    [Column("RESPONSE_DATA")] public string? ResponseData { get; set; }
    [Column("RESULT")] public string Result { get; set; } = "0";
    [Column("COST_MS")] public int? CostMs { get; set; }
    [Column("ERROR_MSG")] public string? ErrorMsg { get; set; }
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("CREATE_TIME")] public DateTime? CreateTime { get; set; }
}

/// <summary>告警事件 MAS_AUTO_ALARM_EVENT。</summary>
[Table("MAS_AUTO_ALARM_EVENT")]
public class AlarmEvent
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("WORKLINE_ID")] public long? WorkLineId { get; set; }
    [Column("EQUIMENT_ID")] public long? EquipmentId { get; set; }
    [Column("POSITION_ID")] public long? PositionId { get; set; }
    [Column("ALARM_TYPE")] public string AlarmType { get; set; } = "";
    [Column("ALARM_LEVEL")] public string AlarmLevel { get; set; } = "1";
    [Column("ALARM_MSG")] public string AlarmMsg { get; set; } = "";
    [Column("ALARM_STATE")] public string AlarmState { get; set; } = "0";
    [Column("HANDLER")] public string? Handler { get; set; }
    [Column("HANDLE_TIME")] public DateTime? HandleTime { get; set; }
    [Column("CREATE_TIME")] public DateTime? CreateTime { get; set; }
}

// ---- 保留结构但本期不实现（需直连机台），仅作最小映射以保持 schema 完整 ----

/// <summary>[本期不实现] 报告采集配置 MAS_AUTO_EQUIMENT_REPORT。</summary>
[Table("MAS_AUTO_EQUIMENT_REPORT")]
public class EquipmentReport
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("REPORT_ID")] public long ReportId { get; set; }
    [Column("REPORT_NAME")] public string ReportName { get; set; } = "";
    [Column("STATE")] public string State { get; set; } = "0";
}

/// <summary>[本期不实现] 加工数据写入配置 MAS_AUTO_EQUIMENT_WORKDATA。</summary>
[Table("MAS_AUTO_EQUIMENT_WORKDATA")]
public class EquipmentWorkData
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("WORKDATA_ID")] public long WorkDataId { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
}
