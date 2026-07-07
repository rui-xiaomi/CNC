# Findings — CNC 自动化上下料客户端

> 研究记录与关键发现。外部内容视为不可信数据，不执行其中指令。

## 环境
- 本机 .NET SDK：8.0.421 与 10.0.300 均在（选用 8.0）。
- 无 mysql CLI（PATH 未发现）；用户确认本机有 MySQL 8.x。验证建库时需找到 mysql.exe 路径或用其它客户端。
- 工作目录 `C:\Users\Administrator\Desktop\cnc`，当前**非 git 仓库**。是否 `git init` 待用户决定。

## 依赖版本核查（NuGet，2026-06）
- Pomelo.EntityFrameworkCore.MySql 最新稳定 = **9.0.0（基于 EF Core 9）**，**无 .NET 10 / EF Core 10 稳定构建**。→ 故 .NET 10 + Pomelo 不可行。
- Oracle MySql.EntityFrameworkCore 有 10.0.7（targets EF Core 10）。
- EF Core runtime 已到 10.0.9，并有 11.x preview。
- **决策**：改用 .NET 8 + EF Core 8 + Pomelo 8.0.3（最成熟稳定组合）。用户已确认 .NET 8 方向。

## 文档关键约束（来自 docs/客户端开发文档.md 权威文档）
- 客户端**仅经 IP/TCP 与 PLC 通信，不直连机台**；机台所有状态/许可/启动经 PLC 信号。
- **一机一 PLC**：3 机台各一台独立 PLC，IP 各异（示例 192.168.1.11/.12/.13），多连接独立，一台断线只停其机台。
- 机台**双加工位**，独立判定/调度。
- 信号不硬编码 → `MAS_AUTO_PLC_POINT` 点位映射表（PLC_ID/EQUIMENT_ID/POSITION_ID/SIGNAL_KEY/RW/REGISTER_ADDR/IO_ADDR/ON_VALUE/OFF_VALUE）。
- 读约定 ON=1/OFF=2；写约定 启动=1/关闭=2。
- 状态合成（§3.4）：等待上料/检测中/完成OK/完成NG/不安全报警。
- 本期不做：报告采集/程式上传/加工数据写入（需直连机台），表结构保留。

## 信号表（docs/测试机信号表.md）
- 三机台 D 段：内长宽 D1000–D1102 / 平面度 D1200–D1302 / A基准 D1400–D1502。
- 每机台：10 读（门/安全/工位1·2 的 有料/允许上料/OK/NG）+ 2 写（工位1·2 测试启动）。
- 寄存器地址权威；IO 位仅展示。模拟器按此预置寄存器。

## 数据库（docs/sql/cnc_schema.sql）
- 库 `cnc_auto`，utf8mb4/InnoDB，17 表，主键 `ID1 BIGINT AUTO_INCREMENT`，状态位 CHAR(1)。
- legacy 命名需保留：`CARFTWORK_ID`（机台表关联工序）、`EQUIMENT_*`。
- 种子数据：1 线体 / 1 检测工序 / 3 机台 / 6 加工位 / 3 PLC / 36 点位 / 3 料架 + 槽位（一架两用、初始不放满）。
- 建库脚本可重复执行（开头 DROP 重建）。

## UI（docs/UI设计文档.md + prototype/index.html）
- 风格：数据密集仪表盘 + 瑞士极简，冷钢仪器风，禁 AI 味（无渐变/玻璃拟态/emoji/大留白）。
- 令牌：chrome #191D22 / bg #E8EBEE / surface #fff / border #CBD1D8 / fg #15181C / accent #135C96 / ok #16A34A / run #1F6FEB / warn #C2710C / alarm #D62828 / idle #6B7280；圆角 3px；间距 4/8/12/16。
- 字体：Saira Semi Condensed(标牌) / Inter(正文) / JetBrains Mono(机器数据)，打包 .ttf。
- 布局：自绘标题栏 + 172px 扁平 8 项导航(非树) + 状态条 + ContentControl。
- 8 页：监控看板/线体/工序/机台/PLC/AGV/扫码枪/料架。
- 签名元素：监控治具卡的"校准刻度"hairline 标尺。

## 待确认 / 风险
- mysql.exe 实际路径（验证建库时确定）。
- 三款字体 .ttf 来源（Google Fonts 可下；需放 UI/Assets/Fonts/）。
- NModbus 对 Modbus TCP holding register 读写 API 细节（实现 NModbusPlcClient 时查 find-docs）。
- PLC_ID 外键约束：`MAS_AUTO_WORKLINE_EQUIMENT.PLC_ID`(fk_eq_plc) 与 `MAS_AUTO_PLC_POINT.PLC_ID`(fk_point_plc) 引用 `MAS_AUTO_WORKLINE_PLC.PLC_ID`（业务键非主键 ID1）。
  - 删除 PLC 必须先校验 0 引用，否则外键断裂；本期走软删 `STATE='1'`，DB 行保留可恢复，列表过滤 `STATE='0'`。
  - 编辑 PLC 时 PlcId 只读，仅改名称/IP/端口/协议，避免外键断裂。
  - 新增 PLC 时 PlcId 唯一性校验（含已软删行），建议值 = `max(PLC_ID)+1`。
- 配置管理删除引用链：线体→工序→机台→(加工位/点位映射/料架绑定)。
  - 线体删除前校验 `MAS_AUTO_WORKLINE_CRAFTWORK.WORKLINE_ID` 引用；工序删除前校验 `MAS_AUTO_WORKLINE_EQUIMENT.CARFTWORK_ID`；机台删除前校验 `MAS_AUTO_PLC_POINT.EQUIMENT_ID` 与 `MAS_AUTO_FRAME_BIND.EQUIPMENT_ID`。
  - 加工位是机台自带（新增机台时自动建 2 个），删除机台时**级联软删**其加工位（不是外键引用，是附属子资源）。
  - 种子引用计数实测：线体1→2 道工序；工序1→3 台机台；机台1→12 个点位+0 条料架绑定。
- 配置列表查询必须过滤 `STATE='0'`：软删后不被读回。`GetAllAsync/GetByLineAsync/GetByCraftAsync/GetWorkLineOptionsAsync/GetCraftworkOptionsAsync` 均加 `.Where(x => x.State == "0")`。
- 业务链校正：**线体→工序→机台→PLC**。线体不直接关联 PLC；PLC 由机台绑定（`MAS_AUTO_WORKLINE_EQUIMENT.PLC_ID`）。`MAS_AUTO_WORKLINECONFIGS.PLC_ID` DB 列保留但 UI 不再编辑。
- FINS 协议（欧姆龙）接入要点与风险：
  - FINS 是应用层协议，可走 UDP(9600) 或 TCP(9600)，两者线格式不同、不通用；本期实现 FINS/UDP。现场若 PLC 只开 FINS/TCP 则需另补 TCP 客户端（抽象已留）。
  - **节点号假设（最大风险）**：当前自动推导 目的节点=PLC IP 末段、源节点=本机 IP 末段、网络号/单元号=0。欧姆龙以太网单元的 FINS 节点号若不等于 IP 末段，则“连上但读超时”。现场需确认节点号；不符则要把 网络号/节点号/单元号 做成每台 PLC 可配置项（落 DB）。
  - 仅支持 DM(D 区)字读写；CIO/W/H/A 区码已在 `OmronFinsPlcClient.WordAreaCodes` 预留但未启用，位寻址未做。
  - 三台机共用一台物理 PLC 时：保留三条 PLC 记录、IP 全设为同一台即可（三路独立 UDP，目的节点同为该 IP 末段，各读各的 DM 段，不冲突）。

## 第四阶段（RCS 对接）关键发现与决策
- **RCS 契约来源**：`docs/agv对外接口.docx`（4 出站 transitTask/excuteTask/cancelTask/queryTask + 3 回调 pushTaskStatus/scanTaskStatus/warnCallback），已合并进开发文档 §12（自足）。docx 为二进制，本机无 python，用 PowerShell 解压 `word/document.xml` 提取文本（文件被 Word 锁定需先复制到临时目录）。
- **分层接缝**（避免 Data↔Communication 循环依赖）：Core 定义接口——`IRcsClient`（HTTP 契约，Communication 实现）、`IRcsTaskStore`/`IRcsMessageLog`/`ILocationMapService`（DB，Data 实现）、`IRcsTaskService`（编排，Communication 实现，注入前述 Core 接口）。UI 只依赖 Core 接口，与既有模式一致。
- **taskId 格式**：`{线体Code}-{类型缩写TR/GR/ID/PR/CF}-{yyyyMMddHHmmss}-{4位序列}`，同秒序列递增；**先落库(CREATED)后发送**保证幂等，redo 复用同 taskId。
- **JSON 序列化**：用 `System.Text.Json` + `JsonPropertyName` 精确字段名（reqTime/clientCode/taskId/version/taskType/priority/container/position/param/commandType；queryTask 无公共字段仅 condition/pageIndex/pageSize）；`DefaultIgnoreCondition=WhenWritingNull`（cancelTask 不带 commandType）；`Encoder=UnsafeRelaxedJsonEscaping`（中文与内嵌引号按 `\"` 输出，非 `\u0022`，与 docx 示例一致、日志可读）。grabTask 的 `param` 是**内嵌 JSON 字符串**（GrabItem 数组序列化后作字符串值），identifyQR 的 `param` 为 `"起始孔位,数量"`。应答外层 `{Success,Message,Data}`，Success 兼容布尔与字符串 "true"；查询成功报文用小写 success。
- **状态**：本系统 6 态 CREATED/DISPATCHED/EXECUTING/COMPLETED/FAILED/CANCELED（RCS 11 态映射见开发文档 §4.5，步骤④实现）。TASK_STATUS(CHAR1) 保留兼容，改用 TASK_STATE(VARCHAR)。
- **FRAME_ROLE 语义扩展**：保持 `CHAR(1)`、取值域扩为 0/1/2/3=上料/下料/中转/NG（不改字段类型，兼容原 '0'/'1' 种子），代码里映射枚举。
- **本机环境（重要，与旧 session 不同）**：MySQL 为 **8.4**（服务 MySQL84，`C:\Program Files\MySQL\MySQL Server 8.4\bin\mysql.exe`），root 口令 `2580.wxr`（非旧 session 的 8.0.46/`Mas@2026`）。执行 SQL 用 `MYSQL_PWD` 环境变量避免口令进命令行。**`appsettings.json` 当前 Password 为空、PasswordProtected=false，连不上本机库**——运行 App 端到端（含 RCS 先落库写）前需配连库口令（建议 DPAPI 加密，勿明文提交）。
- **迁移幂等**：MySQL 8 不支持 `ADD COLUMN IF NOT EXISTS`，用临时存储过程 `sp_add_col_if_absent`/`sp_add_index_if_absent`（查 information_schema）守卫；新表用 `CREATE TABLE IF NOT EXISTS`。已验证重复执行 exit 0。
- **待办依赖**：步骤② RcsCallbackHost 需内嵌 Kestrel（`Microsoft.AspNetCore.App` 框架引用或 `Microsoft.Extensions.Hosting` + Kestrel 包），监听 IP/端口配置化、需报备 RCS 并开防火墙（Phase 6 现场项）。
- **步骤②（回调服务端）实现要点（Session 22）**：
  - **Kestrel 内嵌方式**：`CncLoader.Communication.csproj` 加 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`（net8.0 非 windows 库可用），用 `WebApplication.CreateSlimBuilder()` 构最小 Web 应用 + `ConfigureKestrel(k => k.Listen(ip,port) / ListenAnyIP(port))`，作为独立 `WebApplication` 由 `RcsCallbackHost : IHostedService` 的 StartAsync 起、StopAsync 停（与主 GenericHost 并存，各自 Kestrel/生命周期）。`ConfigureKestrel` 扩展在 `Microsoft.AspNetCore.Hosting` 命名空间（需 using）。`builder.Logging.ClearProviders()` 静默其自带日志避免与 Serilog 双写。**运行时依赖**：WPF App(net8.0-windows) 传递引用 AspNetCore.App，本机/现场需装 ASP.NET Core 8 运行时。
  - **回调契约（docx §3.5/3.6/3.7）**：pushTaskStatus `{taskId,version,data:{system:{error_code,msg}}}`（error_code 0成功/1错误/9取消）；scanTaskStatus 额外 `data.code`（被扫料架编号）+ `data.products[]`（按下发孔位顺序）；warnCallback `data[]`（robotCode/beginTime/warnContent/taskCode，无 taskId）。三者应答统一 `{"taskId":"..."}`（warn 无 taskId → 空串）。
  - **分层**：Core 定义 `IRcsCallbackProcessor`(处理，Communication 实现)/`IRcsCallbackNotifier`+`RcsCallbackNotifier`(事件总线，单例)/事件记录；处理器注入 `IRcsMessageLog`/`IRcsTaskStore`/`IAlarmEventService`(Core 接口，Data 实现)。回调只做"落库 IN + 幂等去重 + 态推进/告警 + 派发事件"短逻辑，长逻辑（跟踪/复核/账目）由订阅 notifier 事件的步骤④⑥处理。
  - **幂等去重**：内存有界 HashSet+Queue（容量 4000）；push/scan 键=`{iface}:{taskId}:{error_code}`，warn 键=`robotCode|beginTime|warnContent`。重复推送仍落 IN 报文（审计），但跳过态变更/告警/事件。
  - **健壮性**：所有 Handle* try/catch 吞异常并总返回应答报文，避免 RCS 侧收 5xx 而反复重推；解析失败也落 IN（success=false+error）。
- **步骤③（RcsSimulator）实现要点（Session 23）**：
  - 扮演 RCS 服务端，与步骤②回调宿主同进程双 Kestrel（模拟器监听 `BaseUrl` 端口、回调宿主监听 `CallbackPort`），形成本机全环回闭环：客户端 OUT→模拟器 ack→延时→模拟器回推 IN→回调宿主→处理器→notifier。
  - 回推 host：`CallbackHost` 为 `0.0.0.0`/空时改用 `127.0.0.1`（绑定任意 IP 但主动连接需具体环回地址）。
  - identify 的 products 按下发 `param="posStart,count"` 生成 count 个 `SIM{code}-{posStart+i}`，`code` 取 position[0].code，供步骤⑥盘点校正联调。
  - 可配项：延时区间 `SimulatorMinDelayMs`~`MaxDelayMs`、`SimulatorFailureRate`(→error_code=1)、`SimulatorCancelRate`(→9)；显式 cancelTask 优先（标记 SimTask.Canceled）。
  - 仅 `UseSimulator=true` 生效（守卫在 `StartAsync`）；现场对接真实 RCS 时置 false，同一 `RcsClient`/回调宿主不变。
- **步骤④（任务跟踪器）实现要点（Session 24）**：
  - **11→5 态映射**（§4.5）：uninitialized/queued/standby/blocked/delayed→DISPATCHED；underway→EXECUTING；completed→COMPLETED（PLC 复核留步骤⑤）；failed/error/skipped→FAILED（可 redo）；canceled/killed→CANCELED（工单）。未知态返回 null 不推进，等下一次轮询/回调。
  - **回调主通道 + queryTask 兜底**：回调（步骤②）即时推 FAILED/COMPLETED/CANCELED；跟踪器每 `PollIntervalMs`(2~5s) 批量 queryTask（condition IN 未完结 taskId 列表）兜底，**与回调冲突以 queryTask 为准**（poll 直接 UpdateStateAsync 覆盖）。RCS 查无此任务（items 未返回该 taskId）→ `RaiseRcsTaskNotFoundAsync` 告警人工。
  - **自动 redo 原子守卫**：`TryIncrementRedoIfUnderAsync(taskId, MaxAutoRedo)` 在 DB 行内 CAS——仅 REDO_COUNT<max 才 +1 并回 DISPATCHED/清错；返回 true 后跟踪器调 `RedispatchAsync`（同 taskId 幂等重发，不递增）。超限 → `RaiseRcsRedoLimitAsync` 告警人工。手动 redo（UI「redo」按钮）走 `RedoAsync`（始终递增，无上限守卫，用户显式触发）。
  - **取消工单**：CANCELED → `RaiseRcsTaskCanceledAsync`(ALARM_TYPE=RCS_CANCELED 严重) + UI「确认取消已处理」按钮 → `ConfirmCancelHandledAsync`(CANCEL_MANUAL_FLAG=1)。**锁点位（确认前不可对相关点位重新派工）由步骤⑤状态机实现**，本步骤只生成工单 + 确认入口。
  - **去重**：取消/查无/重做上限告警按 taskId 内存去重（终态后清理），避免轮询反复告警。回调侧已由处理器 dedup（步骤②）。
  - **事件源**：`RcsTaskStatusEvent.Source` = callback/poll/autoRedo，UI 终端区分显示。跟踪器轮询发现态变化时 RaiseTaskStatus(source=poll)→UI 自动刷新。
- **步骤⑤（状态机改造）实现要点（Session 25）**：
  - **架构**：`PlcPollingService` 只采集信号+MachineStatus，**不再合成 PositionStatus**；`PositionScheduler`（HostedService）是 PositionStatus 的**唯一权威**——主循环 500ms 每加工位独立驱动（双位并行），派工循环按优先级出队下发 RCS。
  - **状态机**（§7）：加 `Dispatching`/`Transporting` 中间态，上下料共用、context 区分阶段。LOADED/UNLOADED 双条件（RCS completed **且** PLC 复核通过）；复核不过 → ALARM **不写启动信号**（安全底线，防账实不符撞机）。
  - **PLC 复核 fresh 读**：Transporting+COMPLETED 时 `ReadHasMatFreshAsync` 经 `IPlcOperationService.ReadRegisterAsync` 直接读 PLC，**不依赖信号仓**——因 CncMachineSimulator 写 HasMat 与轮询读有 200ms 滞后，用信号仓会误判复核不过。
  - **优先级队列**：`PriorityDispatchQueue` 按 priority 降序+同优先级 FIFO；下料 priority=8 > 上料 5（紧急下料 > 常规上料，防机台等卸料阻塞）。RCS 单任务串行——派工循环逐条出队下发。
  - **§6.3 启动对账**：启动时 `GetUnfinishedTaskIdsAsync`→按 `TaskType`(0上料/1下料) 绑定回加工位上下文，态置 Dispatching；对账完成前不派工（`IsReconciled` 守卫）。`RcsTaskRow.TaskType` 为此新增字段。账实不符（PLC 有料但无对应任务）留步骤⑥细化，本步骤先保守停下。
  - **PLC 写钩子（`IPlcWriteHook`）**：调度器写 POS_TEST_START 成功后回调 CncMachineSimulator 驱动"检测→OK"语义。**不依赖寄存器轮询**——因 NModbus master-write 与 sim 内部 `ReadPoints` 索引约定不一致（master readback 自洽但 sim 内部读不到 master 写入值），改用 hook 回调绕过。生产环境不注册 IPlcWriteHook，调度器 `?.` 调用安全跳过。
  - **CncMachineSimulator**：在 ModbusTcpSimulator 寄存器之上叠加加工位节拍语义（RCS 上料完成→HasMat=ON；TestStart=1→2s 后 Ok=ON；下料完成→HasMat=OFF/AllowLoad=ON；TestStart=2→复位）。`SkipMaterialArrival` 注入"复核不过"演示。是步骤⑤演示能跑节拍的关键胶水。
  - **状态机驱动模式**：`ComputeNextState`（纯函数，按信号/rcs 态算 next）→ `ExecuteActions`（执行动作，**可改写 next**，如入队→Dispatching、写启动→Processing、复核→Loaded/Unloaded/Alarm）→ `SetState`。动作驱动的态转换必须由 ExecuteActions 改写 next，否则 SetState 会覆盖回 ComputeNextState 的值（曾致双重入队 bug）。
  - **§6.2 路由决策简化版**：`RouteResolver` 用 LOCATION_MAP 解析 LOAD_AREA↔加工位 cell↔UNLOAD_AREA；完整工序间流转/中转架/NG分流/多加工位选位策略留步骤⑥。
- **步骤⑥a（槽位账目）实现要点（Session 26）**：
  - **预记/落账/回滚时序**（§6.1 补充规则）：grab 任务选定孔位时 `ReserveAsync`（SLOT_STATE='3' 预记 + REMARK=taskId）；回调 completed + PLC 复核后 `ConfirmAsync`（→'1' 占用，落账）；取消/失败 `RollbackAsync`（→'0' 空，回滚）。redo 同 taskId 幂等——落账后 REMARK 保留 taskId + BindSource='RCS_CONFIRMED'，重复 Confirm 查到 '1' 态直接返回 true 不重复记账。
  - **避免 DB schema 变更**：用现有 `REMARK` 列存预记 taskId（无需加 RESERVED_TASK_ID 列）。代价：REMARK 字段被占用作审计；人工校正置空时清 REMARK。
  - **同架并发互斥**：`ConcurrentDictionary<long, SemaphoreSlim>` 按料架 ID 加锁，多加工位同时从一料架取料时 Reserve 串行选槽，不会两任务选同一电极（§6.1 并发保护）。
  - **槽位状态扩展**：`SlotStates` 0空/1占用/2锁定/3预记；`SlotItem` 加 SlotNo/SlotState 暴露完整状态；UI 槽位卡按状态着色（占用 accent/预记 warn/锁定），预记态可见便于排查"挂起"的占用意向。
  - **人工校正**：`SetSlotAsync` 直接改电极码/状态，处理账实不符后人工闭环（NG 处理/盘点差异/手动补录）。电极反查 `LocateElectrodeAsync` 跨全部料架查 ElectrodeId。
  - **⑥a 仅服务+UI，未接调度器**：当前调度器用 transitTask（cell 级、整架搬），不触发槽位账；后续若改 grabTask（电极级）或盘点/换架接入，调度器在 enqueue/回调处调 Reserve/Confirm/Rollback。水位监视器（⑥b）依赖 `GetOccupancyAsync`。
- **步骤⑥b（换架任务对）实现要点（Session 27）**：
  - **先拉后送**（§6.2 v2.2）：换架=一对 RCS 任务，①拉旧架(站点→缓存)→等①completed→②送新架(缓存→站点)。站点位置唯一，必须先拉空再送新。TXN_ID 关联两 taskId（`CF-yyyyMMddHHmmss-seq`），任务表 `MAS_AUTO_AGV_TASK.TXN_ID` 字段已存。
  - **失败回滚**（§6.2 v2.3）：第一发失败 redo≤3（RcsTaskTracker 自动）仍失败→告警+工单，**绑定不解除、原状保持**；第二发失败 redo≤3 仍失败→告警+工单，**锁定工序**（站点空置中间态，人工送架后界面点"新架到位"再绑定）。本编排用 `IsRedoExhausted(taskId)`（store.RedoCount>=MaxAutoRedo）判定 tracker 已放弃，避免与 tracker 的 redo 冲突。
  - **事件驱动**：编排订阅 `notifier.TaskStatusReceived`，按 pull/push taskId 匹配活动事务推进。不轮询。活动事务内存 ConcurrentDictionary 跟踪，完成/告警后移除。
  - **空托盘回收**（§6.2 v2.3 人工触发）：`DispatchPalletReturnAsync` = transitTask + Kind=PalletReturn，priority=8，点位→PALLET_RETURN 区。无自动触发、不建托盘账。UI 按钮人工发起。
  - **缓存区点位待现场确认**：FULL_BUFFER/EMPTY_BUFFER/PALLET_RETURN 区名配置化，需在 LOCATION_MAP 录入 cell 编码才能换架/回收；现场联调前随 cell 级清单一并录入。
  - **水位监视器自动触发留⑥c**：依赖槽位账接入调度器（grab 任务落账驱动占用变化），⑥b 只提供手动换架入口。
- **步骤⑥c（盘点+水位+换架事务展示）实现要点（Session 28）**：
  - **盘点全量校正**（§5.7/§6.2）：identifyQR 下发 param="起始孔位,数量" → scanTaskStatus 回调 `products[]` 按下发孔位顺序对应二维码值 → `CorrectFromInventoryAsync` 按 SlotNo 顺序从 posStart 起的 count 个槽位设电极码=products[i]+占用，空码清槽，所有槽位 `LAST_VERIFY_TIME=now`。`code` 字段（被扫料架编号）+ taskId 双重校验定位料架（本实现按 StartInventoryAsync 时绑定的 frameId，不依赖 code 反查）。
  - **盘点后台异步**：3~4 分钟/架、RCS 单任务串行——UI 发起后立即返回 taskId，回调到达经 `InventoryCompleted` 事件通知 UI 刷新（Dispatcher Invoke）。失败/取消经 `TaskStatusReceived` 兜底（scanResult 可能不来的场景）。
  - **水位监视器**：周期检查 `GetOccupancyAsync`，下料架剩余空槽≤`WaterFullThreshold` → 满架触发换架；上料架 empty=total → 空架触发换架。自动调 `ChangeFrameAsync`，已有该角色换架进行中（GetActiveTransactions 去重）跳过。**FindBindingAsync 占位**：料架→机台角色反查需 `IEquipmentConfigService.GetBindingByFrameAsync`（未实现，返回固定 (1,Unload)），步骤⑦/现场补。
  - **NG 处理简化**：未单独建 NG 页——料架页人工校正「置空(0)」即释放 NG 架槽位（覆盖 §6.2 NG 闭环的"释放槽位"动作）。工件记录归档（NG 标记+待处理项）依赖步骤⑦ WORK_RECORD。
  - **盘点互斥未做**：§6.2 约定"发起前检查队列无待处理搬运请求"，需调度器队列查询接口，步骤⑦/现场补。
- **步骤⑦（加工记录+看板）实现要点（Session 29）**：
  - **WORK_RECORD 时序**：调度器 LOADED→PROCESSING（写 POS_TEST_START=1 后）调 `RecordStartAsync`（关联上料 taskId 溯源）；PROCESSING→DoneOk/DoneNg 调 `RecordResultAsync("0"/"1")`；耗时由 WORK_END-WORK_START 算。`PositionContext.WorkRecordId` 持当前进行中记录主键。
  - **重启残留补结**：RecordStart 时若同加工位有未结束记录（WORK_RESULT 空）→ 自动补结为异常(2)，防 §6.3 对账时账实不符。
  - **监控看板实时化**：`DashboardViewModel` 订阅 `ISignalStateStore.PositionChanged/MachineChanged`——加工位卡片随调度器状态机变化；当班统计 `GetShiftStatsAsync` 按今天日期计 OK/NG；未处理告警 `IAlarmEventService.GetRecentAsync` 筛"未处理"。原 Phase 1 静态骨架替换为绑定。
  - **StateBadge→Brush**：`StateBadgeToBrushConverter` 把 StateBadge 字符串（offline/alarm/run/ok/ng/idle）转对应 Brush 资源（TryFindResource），看板状态灯复用设计令牌。
  - **Phase 4 闭环**：PLC 信号→轮询中枢→调度器状态机→RCS 任务下发/回调/轮询跟踪→PLC 复核收口→写 POS_TEST_START→加工记录→看板联动。槽位账目/换架/盘点服务就绪但未全接调度器（grab 任务级接入留现场/后续）。
