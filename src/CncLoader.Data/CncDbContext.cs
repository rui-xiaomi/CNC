using CncLoader.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Data;

/// <summary>
/// EF Core 上下文，映射 cnc_schema.sql 的表（含第四阶段 RCS 扩展）。
/// schema 以 SQL 脚本为权威、手工建库；本上下文仅做映射与读写，不使用 Migrations。
/// </summary>
public sealed class CncDbContext : DbContext
{
    public CncDbContext(DbContextOptions<CncDbContext> options) : base(options) { }

    // 配置
    public DbSet<WorkLineConfig> WorkLines => Set<WorkLineConfig>();
    public DbSet<WorkLinePlc> Plcs => Set<WorkLinePlc>();
    public DbSet<WorkLineAgv> Agvs => Set<WorkLineAgv>();
    public DbSet<Craftwork> Craftworks => Set<Craftwork>();
    public DbSet<Equipment> Equipments => Set<Equipment>();
    public DbSet<EquipmentPosition> Positions => Set<EquipmentPosition>();
    public DbSet<EquipmentCondition> Conditions => Set<EquipmentCondition>();

    // 信号
    public DbSet<PlcPoint> PlcPoints => Set<PlcPoint>();

    // 料架
    public DbSet<Frame> Frames => Set<Frame>();
    public DbSet<FrameBind> FrameBinds => Set<FrameBind>();
    public DbSet<FrameSlot> FrameSlots => Set<FrameSlot>();

    // 运行
    public DbSet<WorkRecord> WorkRecords => Set<WorkRecord>();
    public DbSet<AgvTask> AgvTasks => Set<AgvTask>();
    public DbSet<DeviceLog> DeviceLogs => Set<DeviceLog>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();

    // 第四阶段 RCS 对接
    public DbSet<LocationMap> LocationMaps => Set<LocationMap>();
    public DbSet<RcsMsgLog> RcsMsgLogs => Set<RcsMsgLog>();

    // 保留（本期不实现）
    public DbSet<EquipmentReport> EquipmentReports => Set<EquipmentReport>();
    public DbSet<EquipmentWorkData> EquipmentWorkDatas => Set<EquipmentWorkData>();
}
