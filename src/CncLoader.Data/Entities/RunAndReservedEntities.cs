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
    [Column("ELECTRODE_ID")] public string? ElectrodeId { get; set; }
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

/// <summary>AGV 任务记录 MAS_AUTO_AGV_TASK。</summary>
[Table("MAS_AUTO_AGV_TASK")]
public class AgvTask
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("WORKLINE_ID")] public long WorkLineId { get; set; }
    [Column("TASK_TYPE")] public string TaskType { get; set; } = "0";
    [Column("TASK_STATUS")] public string TaskStatus { get; set; } = "0";
    [Column("FROM_FRAME_CODE")] public string FromFrameCode { get; set; } = "";
    [Column("TO_FRAME_CODE")] public string ToFrameCode { get; set; } = "";
    [Column("EQUIMENT_ID")] public long? EquipmentId { get; set; }
    [Column("CRAFTWORK_ID")] public long? CraftworkId { get; set; }
    [Column("SEND_TIME")] public DateTime SendTime { get; set; }
    [Column("FINISH_TIME")] public DateTime? FinishTime { get; set; }
    [Column("ERROR_MSG")] public string? ErrorMsg { get; set; }
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
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
