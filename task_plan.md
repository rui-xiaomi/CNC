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

### Phase 1 — 基础架构搭建　Status: 待用户确认（全部验收项通过）
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

### Phase 2 — PLC 管理模块（优先）　Status: pending
连接管理 / 在线离线检测 / 读操作界面 / 写操作界面(危险二次确认+先落流水) / 点位映射维护 / 通信日志+告警 / 多 PLC 切换。借中枢与模拟器自测。

### Phase 3 — 配置管理模块　Status: pending
线体/工序/机台/加工位/料架管理页（独立页面、下拉建层级、非树），含一架两用绑定与电极分层槽位追踪+反查。

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
