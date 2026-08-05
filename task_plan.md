# Task Plan — CNC 自动化上下料 WPF 客户端

## Goal（目标）
据 `docs/` 权威文档，从零开发 WPF 桌面客户端，管理线边 CNC 尺寸检测计量站的自动化上下料。客户端仅经 IP/TCP 与 PLC 通信（不直连机台），一机一 PLC、机台双加工位并行。分阶段交付，每阶段完成暂停演示等用户确认。

完整设计见已批准计划：`C:\Users\Administrator\.claude\plans\docs-robust-goose.md`。

## 技术栈（已确认）
.NET 8（net8.0-windows）· WPF + MVVM(CommunityToolkit.Mvvm) · Microsoft.Extensions.Hosting(DI) · EF Core 8 + Pomelo.EntityFrameworkCore.MySql 8.0.3 · PLC 通信协议可切换：NModbus(Modbus TCP) + 欧姆龙 FINS/UDP(手写) · 内置 Modbus TCP / FINS UDP 模拟器 · Serilog · MySQL 8.x(本机) 库名 `cnc_auto`。

## 关键设计决策（brainstorming 确认）
1. 操作人 = 轻量 `ICurrentUser`（本机用户名/配置项），不建用户表、不做登录。
2. PLC 采中央后台轮询中枢：HostedService 按点位表周期读 → Core 合成加工位状态 → 写入可观察 `ISignalStateStore`，各页/状态机订阅同一数据源。
3. 内置 Modbus TCP 模拟器进程内可启，按信号表预置 D 段寄存器，无真机端到端自测。

## 解决方案结构（6 工程分层）
- `CncLoader.App`：WPF 启动、DI/Host、全局异常、导航宿主
- `CncLoader.UI`：View + ViewModel、设计令牌 ResourceDictionary、控件样式
- `CncLoader.Core`：领域模型、SignalKey 枚举、状态合成/状态机、轮询中枢+状态仓接口
- `CncLoader.Communication`：IPlcClient/PlcConnectionManager/NModbus 实现/欧姆龙 FINS 实现/模拟器(Modbus+FINS)/外设测试客户端
- `CncLoader.Data`：CncDbContext、17 实体映射、仓储
- `CncLoader.Common`：日志、配置、加解密、ICurrentUser
- 依赖：App→UI→Core→(Data,Communication)→Common

---

## Phases

### Phase 1 — 基础架构搭建　Status: 已确认
完成后**必须暂停演示等用户确认**。
- [x] 1.1 建 sln + 6 工程，配置 TFM/UseWPF，加 NuGet 依赖，构建通过（0 警告 0 错误）
- [x] 1.2 Common：appsettings+AppOptions、Serilog、ISecretProtector(DPAPI)、ICurrentUser、连接串工厂
- [x] 1.3 全局异常处理（Dispatcher/AppDomain/TaskScheduler）+ OperationResult
- [x] 1.4 CncDbContext 映射 17 表 + PlcPointSource/DataHealthProbe；**实连验证✓：线体1/PLC3/机台3/加工位6/点位36/料架3**
- [x] 1.5 通信中枢：SignalKey、IPlcClient/PlcConnectionManager、NModbusPlcClient、IPlcPollingService+ISignalStateStore+StatusSynthesizer、ModbusTcpSimulator、IDeviceLogger
- [x] 1.6 主窗口自绘 chrome + 172px 扁平 8 项导航 + 状态条 + ContentControl 路由 + 设计令牌 + 控件样式 + 8 页 VM 占位 + 监控看板静态骨架
- [x] 1.7 Phase 1 验收：构建✓ 启动✓ 导航/路由✓ DB实连读种子✓ 3PLC模拟器建链3/3+轮询30点位✓ 端到端读寄存器✓ 全局异常兜底✓ 日志✓ 密码DPAPI密文✓
- [x] 1.8 字体 Inter/JetBrains Mono/Saira 已作为 .ttf 资源打包（pack:// 引用 + 系统兜底）

#### Phase 1 验收清单
1. dotnet build 全解决方案通过
2. 主窗口/标题栏/8 项导航/状态条按设计令牌渲染，接近原型
3. 8 页可切换，标题/高亮联动
4. 连本机 MySQL `cnc_auto` 读出种子数据（3 PLC/3 机台/点位）证明 ORM 打通
5. 全局异常被捕获并记日志
6. 内置模拟器可启动，NModbusPlcClient 连上并读到一个 D 寄存器
7. 日志文件生成；敏感配置密文存储

### Phase 2 — PLC 管理模块（优先）　Status: 待用户确认（全部验收项通过 + PLC 配置 CRUD 补全）
连接管理 / 在线离线检测 / 读操作界面 / 写操作界面(危险二次确认+先落流水) / 点位映射维护 / 通信日志+告警 / 多 PLC 切换 / **PLC 配置 CRUD（新增/编辑/删除+引用校验）**。借中枢与模拟器自测。

- [x] 2.1 Core：IPlcCatalogService/IPlcConnectionService/IPlcOperationService/IPlcPointManagementService/IDeviceLogStore/IAlarmEventService + 信号表模板
- [x] 2.2 Data：PlcCatalogService/PlcPointManagementService/DeviceLogStore(落库)/AlarmEventService
- [x] 2.3 Communication：PlcConnectionService/PlcOperationService(写先落流水)/CompositeDeviceLogger/PlcEndpointResolver
- [x] 2.4 UI：PlcViewModel 全功能 + 页面模板（PLC列表/读面板/写面板/点位映射/告警/流水）
- [x] 2.5 Phase 2 验收：构建✓ 启动✓ 3PLC在线✓ 读点位✓ 写二次确认+流水✓ 点位导入/删除✓ 告警落库✓
- [x] 2.6 PLC 配置 CRUD 补全：IPlcCatalogService 加 SuggestNextPlcId/CheckDelete/Delete；PlcEditDialog（PlcId 新增可编辑/编辑只读）；列表操作列加 编辑/删除 按钮；删除前引用校验+二次确认+软删；构建 0 警告 0 错误✓ 服务层 SQL 等价验证✓ 种子 DB 还原✓

#### Phase 2 验收清单
1. dotnet build 全解决方案通过（0 警告 0 错误）
2. PLC 管理页：3 台 PLC 列表、连接状态灯、逐台/全部建链断开
3. 读面板：选机台 → 单次读/周期轮询 → 原始值+语义值（ON 绿 OFF 灰）
4. 写面板：选测试启动信号 → 二次确认 → 先落 MAS_AUTO_DEVICE_LOG → 下发 → 回读校验
5. 点位映射：列表展示、信号表批量导入、软删除
6. 通信流水实时展示；通信失败写入 MAS_AUTO_ALARM_EVENT
7. 多 PLC 切换（机台下拉/列表选中联动）
8. **PLC 配置 CRUD**：新增（建议 PlcId+唯一性校验）/编辑（PlcId 只读，在线则断开）/删除（引用校验+二次确认+软删 STATE='1'）三路径均走通，DB 种子保持 plc=3 无残留

### Phase 3 — 配置管理模块　Status: 待用户确认（按原型一比一开发完成 + 删除 CRUD 补全）
线体/工序/机台/加工位/料架管理页（独立页面、下拉建层级、非树），含一架两用绑定与电极分层槽位追踪+反查。

- [x] 3.1 Core：ConfigModels（线体/工序/机台/料架 DTO）+ IWorkLineService/ICraftworkService/IEquipmentConfigService/IFrameService
- [x] 3.2 Data：WorkLineService/CraftworkService（读+保存+删除）、EquipmentConfigService（列表+加工位+关联料架+删除）、FrameService（料架+绑定+分层槽位）；DI 注册
- [x] 3.3 UI：WorkLine/Craftwork/Equipment/Frame 全功能 VM（列表/编辑/保存/删除/级联过滤下拉/电极反查高亮）
- [x] 3.4 UI：PageTemplates 按 prototype 一比一还原四页 DataTemplate（移除占位）+ 4 个展示转换器
- [x] 3.5 Phase 3 验收：构建 0 警告 0 错误✓ 启动✓ DB 实连读种子（线体1/工序2/机台3/加工位6/料架3）✓ 四页 UIAutomation 截图核对与原型一致✓
- [x] 3.6 删除 CRUD 补全：三服务加 CheckDelete/Delete（引用校验+软删）；机台删除级联软删其 2 个加工位；UI 三 VM 加 DeleteLine/DeleteCraft/DeleteEquipment 命令，列表加"删除"按钮；线体编辑表单 VerticalAlignment=Stretch 与列表底部对齐，保存/取消按钮 DockPanel.Dock=Bottom 贴底，内容顶对齐自适应
- [x] 3.7 删除校验验证：种子线体1 被 2 道工序引用→禁删✓ 种子工序1 被 3 台机台引用→禁删✓ 种子机台1 被 12 个点位引用→禁删✓ 孤立工序可删路径（INSERT→check=0→软删→还原 active=2）✓

#### Phase 3 验收清单
1. dotnet build 全解决方案 0 警告 0 错误
2. 线体管理：列表 + 右编辑表单（名称/编码/电脑/IP/计划数/扫码枪），保存回写 MAS_AUTO_WORKLINECONFIGS；线体不直接关联 PLC（业务链 线体→工序→机台→PLC）
3. 工序管理：列表按线体过滤 + 编辑表单，保存回写 MAS_AUTO_WORKLINE_CRAFTWORK
4. 机台管理：列表按工序过滤 + 加工位（自动 2 位）+ 关联料架（上/下料架，一架两用）+ 编辑（编号只读，改名称/编码/类型/工序/PLC）
5. 料架管理：料架列表 + 绑定关系（一架两用）+ 分层槽位电极追踪 + 电极反查高亮
6. 全部页读真实种子数据；线体/工序保存走 Growl + 状态条反馈
7. 机台新增（模态对话框，保存自动建 2 加工位+绑PLC）/料架新增（预建层×每层槽位）/关联料架配置（写 MAS_AUTO_FRAME_BIND）均真写库——已实测：eq3→4(EQ04+工位1/2)、frame3→4(测试料架 2×5 预建10槽)，验证后已清理测试行还原种子
8. **删除 CRUD**：线体/工序/机台 列表"操作"列加删除按钮 → CheckDelete 引用校验 → 不可删 Growl.Warning 提示引用数 → 可删 MessageBox 二次确认 → 软删 State='1'；机台删除级联软删其 2 个加工位。线体编辑表单与列表底部对齐、内容自适应、保存/取消按钮贴底

### Phase 4 — RCS 对接 + 核心上下料流程　Status: 已完成（待用户确认；现场项归 Phase 6）
【v2 改写】上料/下料由"直接执行"改为"下发 RCS 任务 → 回调/轮询跟踪 → PLC 复核 → 写启动"三段式；含 RCS 通信层（RcsClient 4 出站 + RcsCallbackHost 3 回调 + 报文流水 + 状态映射 + RcsSimulator）、双加工位并行状态机改造、电极槽位账目（盘点制）、加工记录。契约见开发文档 §12 与 `docs/agv对外接口.docx`。

- [x] 4.0 DB schema 变更（Session 19）：迁移脚本 `docs/sql/migration_phase4_rcs.sql`（幂等+回滚）+ 同步 `cnc_schema.sql`——`MAS_AUTO_AGV_TASK` 扩展 RCS 全生命周期列（RCS_TASK_ID/RCS_KIND/RCS_STATUS/TASK_STATE/PRIORITY/DISPATCH_TIME/REDO_COUNT/CANCEL_MANUAL_FLAG/POSITION_ID/ELECTRODE_ID/TXN_ID/REQ_PARAM）；新增 `MAS_AUTO_LOCATION_MAP`/`MAS_AUTO_RCS_MSG_LOG`；`FRAME_SLOT` 加 BIND_SOURCE/LAST_VERIFY_TIME；`FRAME_BIND.FRAME_ROLE` 语义扩展 0/1/2/3=上料/下料/中转/NG；EF 实体同步。已应用到本机 cnc_auto（MySQL 8.4）并校验幂等；`数据库文档.md` 同步。
- [x] 4.1 步骤①（Session 20）：Core `Rcs/`（RcsModels/IRcsClient/IRcsMessageLog/IRcsTaskStore/ILocationMapService/IRcsTaskService/RcsTaskId）；Communication `Rcs/`（RcsClient 4 出站接口+公共字段+超时10s+网络重试≤3+报文流水；RcsTaskService 先落库→下发，transit/grab/identify/cancel/redo/query）；Data（RcsMessageLog/RcsTaskStore/LocationMapService）；Common RcsOptions + appsettings.Rcs；UI RcsViewModel + RCS 页 4 tab（连接&下发/任务/报文/位置映射）+ 导航"RCS 任务"。**完成标志达成**：harness 验证 4 接口 JSON 与 §12/docx 一致、先落库、报文流水✓。
- [x] 4.2 步骤②（Session 22）：`RcsCallbackHost` 内嵌 Kestrel（`WebApplication.CreateSlimBuilder`，监听 `RcsOptions.CallbackHost:CallbackPort`，端口占用等启动失败仅记日志不阻断）3 回调（pushTaskStatus/scanTaskStatus/warnCallback）；`RcsCallbackProcessor` 解析原始报文→落库(IN 报文流水)+幂等去重(push/scan 按 taskId+error_code、warn 按 robotCode+beginTime+warnContent)+任务态推进(0→COMPLETED/9→CANCELED/其它→FAILED)+scan 派发 products+warnCallback 落 ALARM(RCS_WARN, 级别严重)+派发内部事件；Core 加回调契约/事件模型/`IRcsCallbackNotifier`(RcsCallbackNotifier 单例事件总线)/`IRcsCallbackProcessor`；`IAlarmEventService.RaiseRcsWarnAsync`；应答统一 `{"taskId":"..."}`（warn 无 taskId 应答空串）；UI RcsViewModel 订阅三事件（终端追加+刷新任务/报文+告警 Growl）。**完成标志达成**：临时 harness 起 Kestrel 用 docx §3.5/3.6/3.7 示例报文跑通——ack 含 taskId、态映射正确、push/warn 幂等去重、scan products=3、warn→2 条 RCS_WARN、IN 报文均落库；构建 0 警告 0 错误；harness 已清理。
- [x] 4.3 步骤③（Session 23）：`RcsSimulator`（Communication/Simulation，`IHostedService`，仅 `UseSimulator=true` 生效）——`WebApplication.CreateSlimBuilder` 在 `RcsOptions.BaseUrl` 端口监听 4 出站接口（transit/excute/cancel/query），收到即应答 `{Success:true}`，随后按可配延时（`SimulatorMinDelayMs`~`MaxDelayMs`）回推：搬运/抓取→pushTaskStatus、识别→scanTaskStatus（products 按 param 起始孔位×数量生成、含 code）；失败率 `SimulatorFailureRate`→error_code=1、取消（收 cancelTask 或 `SimulatorCancelRate`）→error_code=9；回推目标 0.0.0.0 时走 127.0.0.1 环回。**完成标志达成**：临时 harness 起真实回调宿主+3 个模拟器（成功/失败/取消）闭环——transit→COMPLETED、identify→scan products=3 且 COMPLETED、fail=1→FAILED、下发即取消→CANCELED，全 PASS；构建 0 警告 0 错误；harness 已清理。
- [x] 4.4 步骤④（Session 24）：任务跟踪器（`RcsTaskTracker` HostedService，`TrackerEnabled` 守卫）——①兜底轮询：每 `PollIntervalMs` 取 `GetUnfinishedTaskIdsAsync` → 批量 queryTask（condition IN taskId 列表）→ 解析 `items[].status` → `RcsStatusMapper` 11→5 映射推进态，与回调冲突以 queryTask 为准；RCS 查无此任务 → `RaiseRcsTaskNotFoundAsync`。②自动 redo：订阅 `notifier.TaskStatusReceived`，FAILED → `TryIncrementRedoIfUnderAsync(MaxAutoRedo)` 原子递增 → `RedispatchAsync`（同 taskId 幂等重发），超限 → `RaiseRcsRedoLimitAsync`。③取消工单：CANCELED → `RaiseRcsTaskCanceledAsync` + UI「确认取消已处理」按钮 → `ConfirmCancelHandledAsync`（CANCEL_MANUAL_FLAG=1，锁点位留步骤⑤）。Core 加 `RcsStatusMapper`/`RcsStatus` 11 态常量；`RcsTaskStatusEvent` 加 `Source`(callback/poll/autoRedo)；`IRcsTaskStore.TryIncrementRedoIfUnderAsync` 原子 CAS；`IRcsTaskService.RedoAsync` 拆为 `RedispatchAsync`+`BuildAndSendAsync`；`IAlarmEventService` 加 3 个 RCS 任务告警；`RcsOptions.MaxAutoRedo=3`/`TrackerEnabled`；模拟器 `queryTask` 实装按内存表回 `items[]`。**完成标志达成**：harness 真 tracker+processor+notifier+service + 假 client/store/log/alarms 10 项全 PASS（poll underway→EXECUTING / completed→COMPLETED / FAILED→自动 redo REDO_COUNT+1 / 超限→告警 / CANCELED→告警+确认）；构建 0 警告 0 错误；harness 已清理。
- [x] 4.5 步骤⑤（Session 25）：状态机改造——
  - Core：`PositionState` 加 `Dispatching`/`Transporting`；`DispatchItem`/`PositionPhase`/`IDispatchQueue`/`IRouteResolver`/`IPositionScheduler`/`IPlcWriteHook` 抽象。
  - Communication：`PriorityDispatchQueue`（priority 降序+同优先级 FIFO）；`RouteResolver`（LOCATION_MAP 解析 LOAD_AREA↔加工位 cell↔UNLOAD_AREA）；`PositionScheduler` HostedService——每加工位独立状态机并行（双位并行），§7 状态机驱动 WAIT_LOAD→DISPATCHING→TRANSPORTING→(PLC fresh 复核)→LOADED→写 POS_TEST_START=1→PROCESSING→DONE_OK/NG→入下料队→DISPATCHING→TRANSPORTING→(复核有料=OFF)→UNLOADED→写 POS_TEST_START=2→WAIT_LOAD；LOADED/UNLOADED 双条件（RCS completed 且 PLC 复核通过），复核不过 ALARM 不写启动（安全底线）；优先级队列（下料 priority=8 > 上料 5，紧急下料 > 常规上料）；§6.3 启动对账（未完结任务绑定回加工位，对账完成前不派工）；`ResetAlarmAsync` 人工恢复。
  - `CncMachineSimulator`（PLC sim 之上叠加加工位节拍语义）：RCS 上料 COMPLETED→置 HasMat=ON；`IPlcWriteHook` 收 POS_TEST_START=1→延时 2s 置 PosOk=ON；RCS 下料 COMPLETED→置 HasMat=OFF/AllowLoad=ON；POS_TEST_START=2→复位。`SkipMaterialArrival` 注入"复核不过"。
  - `RcsOptions.SchedulerEnabled`/`SchedulerIntervalMs`(500)；`RcsResult.TaskId` 供调度器绑定；`RcsTaskRow.TaskType` 供对账区分阶段；`PlcPollingService` 不再合成 PositionStatus（调度器为唯一权威）。
  - **完成标志达成**：harness 真 scheduler+Modbus sim+CncMachineSimulator+RcsSimulator+回调宿主+处理器+notifier+服务 + 假 store/log/alarms/points/location —— 阶段1 跑通 ≥10 完整节拍（WAIT_LOAD→...→WAIT_LOAD），阶段2 注入 SkipMaterialArrival → 复核不过 ALARM 触发；构建 0 警告 0 错误；harness 已清理。§6.2 工序间流转/中转架/NG分流/水位/换架/空托盘/盘点留步骤⑥。
- [x] 4.6 步骤⑥a（Session 26）：槽位账目——`ISlotAccountService`（Reserve 预记/Confirm 落账/Rollback 回滚/GetOccupancy/SetSlot 人工校正/LocateElectrode 反查）+ `SlotAccountService` 实现（SLOT_STATE='3' 预记 + REMARK=taskId 跟踪 + 同架 SemaphoreSlim 互斥 + 落账后 REMARK 保留 taskId 供 redo 幂等）+ `SlotStates` 常量（0空/1占用/2锁定/3预记）；`SlotItem` 加 SlotNo/SlotState；UI 料架页加人工校正面板（点槽位→改电极码/状态→保存）+ 槽位卡按状态着色（占用 accent/预记 warn/锁定）。**完成标志达成**：harness 11 项全 PASS；构建 0 警告 0 错误；harness 已清理。
- [x] 4.6 步骤⑥b（Session 27）：换架任务对 + 空托盘回收——`IChangeFrameOrchestrator`（ChangeFrameAsync 先拉后送 + ProgressChanged 事件 + GetActiveTransactions）+ `ChangeFrameProgressEvent`/`ChangeFrameStep`/`FrameRole`；`IRcsTaskService.DispatchPalletReturnAsync`；`ChangeFrameOrchestrator` 实现（取绑定料架→解析站点 cell+缓存区 cell→TXN_ID→第一发拉旧架(priority=9)→订阅 notifier：第一发 completed→第二发送新架；CANCELED/redo 耗尽→告警+工单；完成移除事务）；`RcsOptions` 加 WaterFullThreshold/FullBufferArea/EmptyBufferArea/PalletReturnArea；UI RCS 页加换架/空托盘回收面板 + 进度终端提示。**完成标志达成**：harness 7 项全 PASS；构建 0 警告 0 错误；harness 已清理。
- [x] 4.6 步骤⑥c（Session 28）：盘点后台任务 + 水位监视器 + RCS 页换架事务展示——`IInventoryService`（StartInventoryAsync 发起 identifyQR + InventoryCompleted 事件 + GetActiveInventories）+ `InventoryResultEvent`/`InventoryTaskInfo`；`IWaterMonitorService`（CheckAsync 周期检查 + WaterLevelChanged）+ `WaterLevelEvent`；`ISlotAccountService` 加 `CorrectFromInventoryAsync`（products 按孔位顺序全量校正电极码 + LAST_VERIFY_TIME）+ `GetSlotsAsync` + `SlotRecord`。`InventoryService`（下发 identifyQR→订阅 notifier.ScanResultReceived→全量校正→InventoryCompleted）；`WaterMonitorService`（HostedService 周期检查占用→自动调 ChangeFrameAsync，已有换架进行中跳过）；UI 料架页加「发起盘点」面板 + RCS 页加「换架事务」DataGrid。**完成标志达成**：harness 7 项全 PASS；构建 0 警告 0 错误；harness 已清理。**简化**：水位监视器 FindBindingAsync 料架→机台角色反查为占位（需 IEquipmentConfigService 加 GetBindingByFrameAsync，步骤⑦/现场）；NG 处理页未单独建（料架页人工校正「置空」即释放 NG 架槽位，工件记录归档依赖步骤⑦ WORK_RECORD）。
- [x] 4.7 步骤⑦（Session 29）：加工记录 + 监控看板联动——
  - Core+Data：`IWorkRecordService`（RecordStartAsync 写 WORK_RECORD 进行中/RecordResultAsync 写结果+耗时/FindOpenByPositionAsync/GetRecentAsync/GetShiftStatsAsync）+ `WorkRecordService` 实现（重启残留自动补结为异常、REMARK 存 rcsTaskId 溯源、当班统计按今天 WORK_START_TIME）。
  - Communication：`PositionScheduler` 接入——LOADED→PROCESSING 调 `RecordStartAsync`（关联当前上料 taskId，存 ctx.WorkRecordId）；PROCESSING→DoneOk 调 `RecordResultAsync("0")`、DoneNg 调 `RecordResultAsync("1")`。`PositionContext` 加 `WorkRecordId`。
  - UI：`DashboardViewModel` 实时化（注入 ISignalStateStore+IWorkRecordService+IAlarmEventService，订阅 PositionChanged/MachineChanged）——加工位卡片列表（状态徽标+中文态）+ 当班 OK/NG/总数 + 未处理告警数 + 最近加工记录 DataGrid；`PageTemplates` 监控看板模板从静态骨架改为绑定（4 统计卡 + 加工位 ItemsControl + 最近记录表）；新增 `StateBadgeToBrushConverter`。
  - **完成标志达成**：harness 真 WorkRecordService + EF InMemory 5 项全 PASS（RecordStart 写进行中/FindOpenByPosition 命中/RecordResult 写 OK+耗时/当班统计 OK=2 NG=1 总数=3/重启残留自动补结异常(2)）；构建 0 警告 0 错误；harness 已清理。
  - **Phase 4 全部 7 步完成**（①~⑦）。
- [x] 4.8 上线缺口补齐（7 项）：
  - **绑定反查**：`IEquipmentConfigService` 加 `GetBindingByFrameAsync`/`GetFrameBindingByRoleAsync`/`GetNextProcessEquipmentsAsync`；`WaterMonitorService.FindBindingAsync` 用真实 FRAME_BIND 反查（空架取上料角色、满架取非上料角色），删占位 TODO。
  - **上料前校验**：`PositionScheduler.EnqueueUploadAsync` 入队前查上料架账面占用，`occupied=0` 保持 WaitLoad 等料（非告警）。
  - **槽位账双向接入**：`ISlotAccountService` 加 `ReserveTake/ConfirmTake/RollbackTake`（取料方向）+ `RollbackStaleReservationsAsync`；`ReservedSlot` 带出 electrodeId；`BIND_SOURCE` 记 `RESERVE_PUT/RESERVE_TAKE` 方向。调度器上料 LOADED→ConfirmTake、下料 UNLOADED→Confirm、Alarm 按方向回滚。
  - **OK/NG 全量分流**：`DispatchItem` 加 `UnloadTarget`/目标料架/目标机台工位/电极字段；`IRouteResolver` 加 `ResolvePositionCellAsync`/`ResolveFrameCellAsync`；`PositionScheduler.ResolveUnloadTargetAsync`——NG→NG架(role3)；OK→下一工序空闲工位直接交接（登记 `_expectedInbound`，下游见料走 Loaded）/ 下一工序全忙→中转架(role2)排队 / 末道→下料架(role1)。上料源优先中转架回流再取上料架。
  - **定期盘点**：新增 `InventorySchedulerService`（RCS 空闲串行逐架 identifyQR）+ `RcsOptions.InventoryAutoEnabled`(默认关)/`InventoryIntervalMinutes`(默认60)。
  - **重启三方对账**：`PositionScheduler.ReconcileAsync` 扩为 ①RCS 未完结任务绑定 ②槽位账陈旧预记按方向回滚 ③PLC 有料无任务→ALARM 等人工。
  - **模拟器**：`CncMachineSimulator` 下料完成时若终点 cell 映射到某工位（工序间交接）→ 目标 HasMat=ON；`ILocationMapService.ResolveByRcsCodeAsync` 反查。
  - **验证**：`dotnet build` 0 警告 0 错误；临时 harness（EF InMemory）18 项全 PASS（绑定反查/下一工序/取料放料双向/陈旧预记按方向回滚），跑通已删。**App 运行态受本机应用程序控制策略拦截，无法本地冒烟**。
  - **现场遗留**：真实 LOCATION_MAP（加工位/料架 cell、各区命名点）与 FRAME_BIND（中转 role2/NG role3）录入；`WaterMonitorEnabled`/`InventoryAutoEnabled` 由运维开启；多机竞争的中转排队策略调优——均归 Phase 6。

> 注：现场联调项（FINS 节点号、RCS 回调 IP/端口与防火墙、缓存区/备料区/托盘回收区/中转架/NG架点位编码）留待 Phase 6，本阶段以模拟器自测 + 数据层 harness 为准。

### Phase 5 — 外设测试页　Status: 待用户确认（按原型一比一还原）
AGV / 扫码枪连通性测试（仅测试，不纳入调度/来料校验）。

- [x] 5.1 Core：`IExternalDeviceTestService` — `IAgvTestService`(TestConnectionAsync) + `IScanListenerService`(Start/Stop/ScanReceived 事件/GetRecent) + DTO(AgvTestConfig/AgvTestResult/ScanRecord/AgvCommWay)
- [x] 5.2 Communication：`AgvTestService`（HTTP REST GET + 可选 Basic Auth；Socket 模式 TCP 连一次；5s 超时）；`ScanListenerService`（TCP 服务端监听，按 CR/LF 拆行，维护最近 50 条 + 连接计数）
- [x] 5.3 UI：`AgvViewModel` 全功能（表单 5 字段 + 测试连接命令 + 终端结果 + 状态徽标"连通 Nms/不通"）；`ScanViewModel` 全功能（模式/端口 + 开始监听/停止 + 状态徽标"已连接 N" + 最近扫码表）；从 `PageViewModels.cs` 拆为独立文件
- [x] 5.4 UI 模板：`PageTemplates.xaml` 移除 AGV/扫码枪占位模板，按 `docs/prototype/index.html` §AGV/扫码枪 一比一还原（单 panel max-width 700、表单 + 测试结果终端、状态徽标 + 最近扫码表）；密码字段保留 PasswordBox 视觉但不绑（"仅测试连通"无需密码）；加 `InverseBoolConverter` 用于测试中按钮禁用
- [x] 5.5 Phase 5 验收：构建 0 警告 0 错误✓ 启动 App 无异常✓ DI 装配 IAgvTestService/IScanListenerService✓

#### Phase 5 验收清单
1. dotnet build 全解决方案 0 警告 0 错误
2. AGV 管理：表单（名称/通信方式 HTTP REST·Socket/地址）+ "测试连接"按钮
3. AGV 测试结果：状态徽标（连通 Nms 绿/不通 红）+ 终端展示 `> GET url` `< 200 OK Nms` `< {json}` 或 `< ERROR ...`
4. 扫码枪管理：表单（模式 TCP 服务端·TCP 客户端 / 监听端口）+ "开始监听/停止"按钮
5. 扫码枪连接/最近扫码：状态徽标"已连接 N"绿 + 表格 时间/来源/扫码内容（mono 字体）
6. 两页布局按原型一比一：单 panel max-width 700、表单字段左标签 84px、终端深色 TermBrush

### Phase 6 — 联调验收　Status: 进行中（现场为主；下列为可执行清单）
现场真机联调、异常场景、地址表复核、验收要点核对。依据 `docs/客户端开发文档.md` §10/§11 与 `docs/演示实操手册.md` §7。

**联调前配置（工位机）** — 主文档：`docs/现场联调配置清单.md`；示例：`src/CncLoader.App/appsettings.Field.example.json`
- [x] 配置对照表与示例 json 已写入文档（2026-07-13）；**现场实际改值仍待勾**
- [ ] `Plc.UseSimulator=false`、`Rcs.UseSimulator=false`；首轮 `WaterMonitorEnabled`/`InventoryAutoEnabled` 保持 `false`
- [ ] DB：`MAS_AUTO_WORKLINE_PLC` 真实 IP/协议；`MAS_AUTO_PLC_POINT` 与 `docs/测试机信号表.md` 一致（**待真机验证**）
- [ ] DB：`MAS_AUTO_LOCATION_MAP` + `MAS_AUTO_FRAME_BIND`（含中转 role2 / NG role3）与现场 RCS 编码一致
- [ ] RCS：`BaseUrl`/`ClientCode`/`CallbackHost|Port` 报备；防火墙放行；回调可达（改回调端口需重启 App）
- [ ] 安装 .NET 8 Desktop + ASP.NET Core 8；单实例 Mutex 勿双开
- [ ] 日志：默认成功读不落库；排障可临时 `MinimumLevel=Debug` + `PersistSuccessfulReads=true`，联调完改回

**联调顺序（每步验完再下一步）**
- [ ] ① 网络：RCS 出站 OK + 入站回调 OK（RCS 页测试回调）
- [ ] ② PLC 逐点读/写（写二次确认 + 回读）（**待真机验证**）
- [ ] ③ RCS 单任务手动下发 → 回调/轮询 → COMPLETED
- [ ] ④ 单加工位慢速全流程（人盯 AGV + PLC 双条件启动）
- [ ] ⑤ 双位并行 + 异常（断网/取消/redo 上限/复核不过 → Alarm → ResetAlarm）
- [ ] ⑥ 换架 / 盘点实测（确认水位/自动盘点开关策略后再开）
- [ ] ⑦ 按开发文档 §11 验收签字

**代码侧已就绪、勿在现场误跑**
- 审查高危修复已合入（队列/PLC 串行/Dispose 占闸/水位/_redo/删架校验/设备流水收敛）
- P0/P1 安全门禁已合入并完成代码级验收（见下方「安全门禁」）；单测工程 `tests/CncLoader.Core.Tests`
- 禁止现场执行 `refill_upload_frame_electrodes.sql` / `FrameSeedReset`

### 安全门禁 P0/P1（2026-08-05）　Status: 代码级验收通过（非生产验收）
归档：`.scratch/final-acceptance/2026-08-05-cnc-loader-safety-hardening-acceptance.md`  
ADR：`docs/adr/0002-槽位预记先于RCS下发.md`

- [x] P0-1 HasMat 读失败/未知 fail-closed（不得当无料）
- [x] P0-2 槽位预记先于 RCS 下发（ADR-0002）
- [x] P0-3 启动对账 fail-closed + `ReconcileRetryIntervalMs` 重试 + 看板状态
- [x] P0-4 回调持久化成功后 final seen / single-flight
- [x] P0-5 软删配置不得参与新外部执行路由；AutoRedo Gate→Claim→Send
- [x] P0-6 盘点/人工校正不得覆盖 Reserved
- [x] P1-1 首次启动对账不阻塞后续 HostedService
- [x] P1-2 ForgetTask 同步移除顺序结构（无僵尸淘汰）
- [x] 验证：Core 418/418；`dotnet build` 0 警告 0 错误
- [ ] 真 MySQL / RCS / PLC / 换架盘点 — **归 Phase 6 待真机验证**

### Phase 7 — 监控看板悬浮卡片视觉升级　Status: 已完成（待目视确认）
Spec：`docs/superpowers/specs/2026-07-10-dashboard-neon-float-design.md`  
Plan：`docs/superpowers/plans/2026-07-10-dashboard-neon-float-plan.md`  
范围：仅监控看板；克制光晕；装饰波形；矢量状态图标；看板专用青/红 token。

- [x] 7.1 Token + `DashboardStyles.xaml` + App 合并（`DashPanel` / `DashKpiCard` / 波形 / 光晕）
- [x] 7.2 `StateBadgeToIconConverter` + Geometry 映射
- [x] 7.3 Dashboard DataTemplate 换皮（KPI / 产线流 / 机台 / 右栏）
- [x] 7.4 修订 `docs/UI设计文档.md` 看板例外条款
- [x] 7.5 验收：`dotnet build` 0 警告 0 错误（目视需重启 App 确认）

#### Phase 7 验收清单
1. 其它页面观感不变
2. 默认卡片几乎无光晕；选中淡青；告警淡红
3. 状态圆点全部换成矢量图标
4. 波形底纹不挡文字
5. 构建通过 ✓

---

### 现场点到点联调页增强（2026-07-15）　Status: 已完成（待现场试用）
- [x] RCS 模式标识（`Rcs.UseSimulator` + 生效 BaseUrl）
- [x] 暂停自动派工 / 仅手动测试（进程内，PositionScheduler）
- [x] 手工点到点校验 + 真实 RCS 二次确认 + 连通门禁
- [x] 「测试回调」→「测试本机监听」文案；build 0/0

## Errors Encountered
| Error | Attempt | Resolution |
|-------|---------|------------|
| (none yet) | | |

## 自主边界提醒
删文件/改密钥配置/DB schema 变更/git commit/装全局依赖 等动手前先问用户。

## 已完成（归档）
- Phase 1 基础架构（已确认）
- Phase 2 PLC 管理（待用户确认验收）
- Phase 3 配置管理（待用户确认验收）
- Phase 4 RCS + 上下料全流程 ①~⑦ + 上线缺口 7 项（待用户确认；现场录入归 Phase 6）
- Phase 5 外设测试页（待用户确认验收）
- Phase 7 监控看板悬浮卡片（构建通过，待目视）
- 2026-07-13：审查高危 H1–H7/M4/M8/M11 + 设备流水收敛 + PLC Dispose 占 `_ioGate` + `AGENTS.md`/`reviewer` agent
- 2026-08-05：P0/P1 安全门禁代码级验收通过（真机联调仍归 Phase 6）
