# Findings — UI 布局优化（看板 / 料架 / RCS）

## 决策（用户确认「都改」）
1. 监控看板对齐原型：机台卡（门/安全 + 双工位 + accent 刻度）+ 右侧实时告警流；KPI 保留；「最近加工记录」让位给告警流。
2. 料架页左右分栏：左=列表+绑定，右=槽位网格+校正/盘点底栏，消除纵向挤压。
3. RCS Tab 收口：默认「任务列表」→「报文流水」→「连接&下发」→「位置映射」。

## 数据源
- 机台卡：`ISignalStateStore` 按 EquipmentId 聚合 PositionStatus + MachineStatus；机台名经 `IEquipmentConfigService.GetEquipmentOptionsAsync` 缓存。
- 告警流：`IAlarmEventService.GetAlarmsAsync(unhandledOnly:false, limit)` + MarkHandled；AlarmRaised/AlarmsChanged 刷新。
- 料架/RCS：仅 XAML 结构与 Tab 顺序，业务命令不变。
