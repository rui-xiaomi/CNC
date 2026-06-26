using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CncLoader.Data.Entities;

/// <summary>PLC 点位映射 MAS_AUTO_PLC_POINT（信号契约可配置落地）。</summary>
[Table("MAS_AUTO_PLC_POINT")]
public class PlcPoint
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("PLC_ID")] public long PlcId { get; set; }
    [Column("EQUIMENT_ID")] public long EquipmentId { get; set; }
    [Column("POSITION_ID")] public long? PositionId { get; set; }
    [Column("SIGNAL_KEY")] public string SignalKey { get; set; } = "";
    [Column("RW")] public string Rw { get; set; } = "0";
    [Column("REGISTER_ADDR")] public string RegisterAddr { get; set; } = "";
    [Column("IO_ADDR")] public string? IoAddr { get; set; }
    [Column("ON_VALUE")] public int OnValue { get; set; } = 1;
    [Column("OFF_VALUE")] public int OffValue { get; set; } = 2;
    [Column("DATA_LEN")] public int DataLen { get; set; } = 1;
    [Column("REMARK")] public string? Remark { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>料架主表 MAS_AUTO_FRAME。</summary>
[Table("MAS_AUTO_FRAME")]
public class Frame
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("FRAME_NAME")] public string FrameName { get; set; } = "";
    [Column("FRAME_CODE")] public string FrameCode { get; set; } = "";
    [Column("FRAME_IDENTIFY_CODE")] public string FrameIdentifyCode { get; set; } = "";
    [Column("LAYER_TOTAL")] public int LayerTotal { get; set; } = 1;
    [Column("SLOTS_PER_LAYER")] public int SlotsPerLayer { get; set; }
    [Column("SLOT_TOTAL")] public int SlotTotal { get; set; }
    [Column("FRAME_SET_INFO")] public string? FrameSetInfo { get; set; }
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>料架-机台绑定 MAS_AUTO_FRAME_BIND（一架两用）。</summary>
[Table("MAS_AUTO_FRAME_BIND")]
public class FrameBind
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("FRAME_ID")] public long FrameId { get; set; }
    [Column("EQUIMENT_ID")] public long EquipmentId { get; set; }
    /// <summary>0=该机台上料架，1=下料架。</summary>
    [Column("FRAME_ROLE")] public string FrameRole { get; set; } = "0";
    [Column("STATE")] public string State { get; set; } = "0";
    [Column("AUTHOR")] public string? Author { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}

/// <summary>料架槽位/电极 MAS_AUTO_FRAME_SLOT（层 + 层内位 + 电极绑定）。</summary>
[Table("MAS_AUTO_FRAME_SLOT")]
public class FrameSlot
{
    [Key, Column("ID1"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Column("FRAME_ID")] public long FrameId { get; set; }
    [Column("SLOT_NO")] public int SlotNo { get; set; }
    [Column("LAYER_NO")] public int LayerNo { get; set; } = 1;
    [Column("POS_IN_LAYER")] public int PosInLayer { get; set; } = 1;
    /// <summary>0=空,1=占用,2=锁定/不可用。</summary>
    [Column("SLOT_STATE")] public string SlotState { get; set; } = "0";
    [Column("ELECTRODE_ID")] public string? ElectrodeId { get; set; }
    [Column("BIND_TIME")] public DateTime? BindTime { get; set; }
    [Column("REMARK")] public string? Remark { get; set; }
    [Column("UPDATETIME")] public DateTime? UpdateTime { get; set; }
}
