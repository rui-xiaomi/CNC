# Task Plan — CNC 自动化上下料 WPF 客户端

## Goal（目标）
据 `docs/` 权威文档，从零开发 WPF 桌面客户端，管理线边 CNC 尺寸检测计量站的自动化上下料。客户端仅经 IP/TCP 与 PLC 通信（不直连机台），一机一 PLC、机台双加工位并行。分阶段交付，每阶段完成暂停演示等用户确认。

完整设计见已批准计划：`C:\Users\Administrator\.claude\plans\docs-robust-goose.md`。

## 技术栈（已确认）
.NET 8（net8.0-windows）· WPF + MVVM(CommunityToolkit.Mvvm) · Microsoft.Extensions.Hosting(DI) · EF Core 8 + Pomelo.EntityFrameworkCore.MySql 8.0.3 · NModbus + 内置 Modbus TCP 模拟器 · Serilog · MySQL 8.x(本机) 库名 `cnc_auto`。

## 关键设计决策（brainstorming 确认）
1. 操作人 = 轻量 `ICurrentUser`（本机用户名/配置项），不建用户表、不做登录。
2. PLC 采中央后台轮询中枢：HostedService 按点位表周期读 → Core 合成加工位状态 → 写入可观察 `ISignalStateStore`，各页/状态机订阅同一数据源。
3. 内置 Modbus TCP 模拟器进程内可启，按信号表预置 D 段寄存器，无真机端到端自测。

## 解决方案结构（6 工程分层）
- `CncLoader.App`：WPF 启动、DI/Host、全局异常、导航宿主
- `CncLoader.UI`：View + ViewModel、设计令牌 ResourceDictionary、控件样式
- `CncLoader.Core`：领域模型、SignalKey 枚举、状态合成/状态机、轮询中枢+状态仓接口
- `CncLoader.Communication`：IPlcClient/PlcConnectionManager/NModbus 实现/模拟器/外设测试客户端
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

### Phase 4 — 核心上下料流程　Status: pending
加工位级状态机、双工位并行调度、信号合成状态、电极槽位流转、加工记录。

### Phase 5 — 外设测试页　Status: pending
AGV / 扫码枪连通性测试（仅测试）。

### Phase 6 — 联调验收　Status: pending
现场真机联调、异常场景、地址表复核、验收要点核对。

---

## Errors Encountered
| Error | Attempt | Resolution |
|-------|---------|------------|
| (none yet) | | |

## 自主边界提醒
删文件/改密钥配置/DB schema 变更/git commit/装全局依赖 等动手前先问用户。
