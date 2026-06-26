using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CncLoader.Data.Entities;

// 配置类实体。表/字段名沿用既有 Oracle schema（含历史拼写 CARFTWORK_ID / EQUIMENT_）。
// CHAR(1) 标志位以 string 映射（'0'/'1'）。仅映射当前会用到的列；其余可空列留待后续按需补充。

/// <summary>线体主表 MAS_AUTO_WORKLINECONFIGS。</summary>
[Table("MAS_AUTO_WORKLINECONFIGS")]
public class WorkLineConfig
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("WORKMACHINE_LINE")] public string WorkMachineLine { get; set; } = "";
    [Column("WORKLINE_CODE")] public string WorkLineCode { get; set; } = "";
    [Column("WORKLINE_COMPUTER")] public string? WorkLineComputer { get; set; }
    [Column("WORKLINE_COMPUTER_IP")] public string? WorkLineComputerIp { get; set; }
    [Column("WORKLINE_COMPUTER_PORT")] public int? WorkLineComputerPort { get; set; }
    [Column("PLAN_WORKNUM")] public long? PlanWorkNum { get; set; }
    [Column("SCAN_STATE")] public string ScanState { get; set; } = "0";
    [Column("PLC_ID")] public long PlcId { get; set; }
    [Column("AGV_ID")] public long AgvId { get; set; }
    [Column("MATERIALCODE")] public string? MaterialCode { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>PLC 配置 MAS_AUTO_WORKLINE_PLC（一机一 PLC，IP 各异）。</summary>
[Table("MAS_AUTO_WORKLINE_PLC")]
public class WorkLinePlc
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("PLC_ID")] public long PlcId { get; set; }
    [Column("PLC_NAME")] public string? PlcName { get; set; }
    [Column("PLC_CONNECT_TYPE")] public string PlcConnectType { get; set; } = "客户端";
    [Column("PLC_COMPUTER_IP")] public string PlcComputerIp { get; set; } = "";
    [Column("PLC_COMPUTER_PORT")] public int? PlcComputerPort { get; set; }
    [Column("PLC_READ_WAY")] public string? PlcReadWay { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>线体 AGV 配置 MAS_AUTO_WORKLINE_AGV（仅测试用，口令密文）。</summary>
[Table("MAS_AUTO_WORKLINE_AGV")]
public class WorkLineAgv
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("AGV_ID")] public long AgvId { get; set; }
    [Column("AGV_NAME")] public string AgvName { get; set; } = "";
    [Column("AGV_CORRESPOND_WAY")] public string AgvCorrespondWay { get; set; } = "";
    [Column("AGV_COMPUTER_IP")] public string AgvComputerIp { get; set; } = "";
    [Column("AGV_COMPUTER_PORT")] public int? AgvComputerPort { get; set; }
    [Column("AGV_COMPUTER_USRNAME")] public string? AgvComputerUsername { get; set; }
    [Column("AGV_COMPUTER_PASSWORD")] public string? AgvComputerPassword { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>工序表 MAS_AUTO_WORKLINE_CRAFTWORK。</summary>
[Table("MAS_AUTO_WORKLINE_CRAFTWORK")]
public class Craftwork
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("WORKLINE_ID")] public long WorkLineId { get; set; }
    [Column("CRAFTWORK_NO")] public string CraftworkNo { get; set; } = "";
    [Column("CRAFTWORK_NAME")] public string CraftworkName { get; set; } = "";
    [Column("SF_QUALITY")] public string SfQuality { get; set; } = "0";
    [Column("SF_AUTO_SEND")] public string SfAutoSend { get; set; } = "0";
    [Column("SF_COST_STAT")] public string? SfCostStat { get; set; }
    [Column("CRAFTWORK_NODE")] public long? CraftworkNode { get; set; }
    [Column("CRAFTWORK_PRIOR")] public long? CraftworkPrior { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>机台表 MAS_AUTO_WORKLINE_EQUIMENT（CARFTWORK_ID 为历史拼写）。</summary>
[Table("MAS_AUTO_WORKLINE_EQUIMENT")]
public class Equipment
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("CARFTWORK_ID")] public long CraftworkId { get; set; }
    [Column("PLC_ID")] public long? PlcId { get; set; }
    [Column("EQUIMENT_NO")] public string EquipmentNo { get; set; } = "";
    [Column("EQUIMENT_NAME")] public string EquipmentName { get; set; } = "";
    [Column("EQUIMENT_CODE")] public string EquipmentCode { get; set; } = "";
    [Column("EQUIMENT_TYPE")] public string EquipmentType { get; set; } = "";
    [Column("EQUIMENT_TYPE_NAME")] public string EquipmentTypeName { get; set; } = "";
    [Column("EQUIMENT_WORK_TYPE")] public string EquipmentWorkType { get; set; } = "";
    [Column("EQUIMENT_STATE")] public string? EquipmentState { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>加工位表 MAS_AUTO_EQUIMENT_POSITION（每机台 2 个，独立并行）。</summary>
[Table("MAS_AUTO_EQUIMENT_POSITION")]
public class EquipmentPosition
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("EQUIMENT_ID")] public long EquipmentId { get; set; }
    [Column("POSITION_NAME")] public string PositionName { get; set; } = "";
    [Column("POSITION_CODE")] public string PositionCode { get; set; } = "";
    [Column("POSITION_WORK_STATE")] public string PositionWorkState { get; set; } = "0";
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>机台标准状态字典 MAS_AUTO_EQUIMENT_CONDITION。</summary>
[Table("MAS_AUTO_EQUIMENT_CONDITION")]
public class EquipmentCondition
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("CONDITION_CODE")] public string ConditionCode { get; set; } = "";
    [Column("CONDITION_NAME")] public string ConditionName { get; set; } = "";
    [Column("CONDITION_DESC")] public string? ConditionDesc { get; set; }
    [Column("SORT_NO")] public int SortNo { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}
