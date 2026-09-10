---
description: 
alwaysApply: true
---

# 项目规范

## 项目
智造云枢 LineOS：线边 CNC 尺寸检测计量站的自动化上下料 WPF 客户端，经 PLC / RCS 调度 AGV，服务车间现场联调与量产运行。

## 技术栈
.NET 8（`net8.0-windows`）· WPF + MVVM（CommunityToolkit.Mvvm）· Microsoft.Extensions.Hosting · EF Core 8 + Pomelo MySQL · NModbus（Modbus TCP）+ 手写欧姆龙 FINS/UDP · 内置 PLC/RCS 模拟器 · Serilog · MySQL 8.x（库名 `cnc_auto`）· 解决方案 `CncLoader.sln`（6 工程分层 + `tests/CncLoader.Core.Tests`）。

## 环境约束
- 目标为车间工控机 / Windows 桌面：需同时安装 **.NET 8 Desktop + ASP.NET Core 8** 运行时（RCS 回调与模拟器内嵌 Kestrel）。
- 客户端**只经 IP/TCP 与 PLC 通信**，不直连机台；RCS 为 HTTP 出站 + 本机回调入站。
- 演示默认 `Plc.UseSimulator` / `Rcs.UseSimulator` 为 `true`；现场联调必须改为 `false` 并核对真实 IP / LOCATION_MAP / 料架绑定。
- 单实例 Mutex：`Global\CncLoader.App.SingleInstance`；第二实例静默退出，勿当闪退误判。

## 命令
- 开发：`dotnet run --project src/CncLoader.App`
- 构建：`dotnet build CncLoader.sln`
- 单测：`dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj`（或 `dotnet test CncLoader.sln`）；联调以模拟器 + `docs/演示实操手册.md` 为准
- **一键验证（必填，合并前必过）：`dotnet build CncLoader.sln`（0 警告 0 错误）+ 相关单测全绿**

## 评审 checklist
- 并发：PLC 客户端同 `plcId` 请求须串行；UI 改 `ObservableCollection` / Growl 必须经 Dispatcher；派工队列 Enqueue/Dequeue 须同锁
- 外部交互：RCS `BaseUrl`/`ClientCode`/回调端口、LOCATION_MAP 编码、PLC 点位地址须与现场一致；缺 LOCATION_MAP **禁止**生成 `FRAME-{id}` 假码下发
- 安全底线：LOADED/UNLOADED 须 RCS 完成 **且** PLC HasMat **确认态**复核通过才写 `POS_TEST_START`；HasMat 读失败/未知 fail-closed（Hold→Alarm），禁止当无料；Alarm 粘滞只能人工 `ResetAlarm`
- 派工门禁：自动派工须先槽位预记再 RCS（ADR-0002）；Redo / Redispatch / AutoRedo 预记已回滚须先再预记再下发（已有预记跳过；上料有物料码只锁该件）；启动对账成功前不开派工（fail-closed，失败按 `ReconcileRetryIntervalMs` 重试）；新外部执行须经受管路由且配置 `STATE=="0"`；盘点/人工校正不得覆盖 `SLOT_STATE=Reserved`；HasMat 未知不得落账也不得回滚预记（对账①b 与运行期同一决策，不回 WaitLoad）
- 回调：持久化成功后才 final seen；失败/取消释放；`ForgetTask` 须同步移除顺序结构中的键
- 配置双源：RCS 出站优先读 `MAS_AUTO_WORKLINE_AGV`；`CallbackHost`/`CallbackPort` 变更需重启；`Version`/`TokenCode` 仅 appsettings
- 资源：HostedService / CTS / 回调 Kestrel / PLC 连接正确释放；禁止空 catch 吞通信异常（回调处理器故意不回 5xx 除外）；首次启动对账不得阻塞后续 HostedService 启动
- 密钥：`appsettings.json` 禁止提交明文 DB 密码；现场用 DPAPI（`PasswordProtected=true`）

## 架构红线
- 分层依赖：`App → UI → Core → (Data | Communication) → Common`；禁止 UI 直连 EF / 直连 Socket；禁止 Communication 反向依赖 UI
- 业务状态机、路由、槽位账在 Communication/Core；UI 只绑 VM / 命令 / 展示标签转换
- 数据库仅经 `IDbContextFactory<CncDbContext>` 短生命周期仓储；禁止长生命周期 DbContext、禁止页面内拼 SQL
- PLC 读写经 `IPlcClient` / `PlcConnectionManager` / `IPlcOperationService`；写操作先落 `MAS_AUTO_DEVICE_LOG` 再下发
- 信号地址唯一真相源：`docs/测试机信号表.md`；其他文档只引用不复制

## 项目特有规则
- 重要：现场联调前关闭双模拟器；首轮保持 `WaterMonitorEnabled` / `InventoryAutoEnabled` 为 `false`
- 重要：成功 PLC 轮询读默认不落库、文件日志为 Debug（`PersistSuccessfulReads`）；写与失败仍落库；排查点位可临时开 Debug / `PersistSuccessfulReads=true`，联调完改回
- 重要：软删用 `STATE='0'/'1'`（仅 `"0"` 为活动）；列表/查询/调度路由/新外部执行均须过滤；删除配置前走 `CheckDelete*` 引用校验
- LOCATION_MAP / 显示层：DB 与 RCS 用英文码（如 `LOAD_AREA`）；UI 中文仅经 `LocationDisplayLabels` / `RcsDisplayLabels` 转换，保存必须写英文码
- RCS 接口名 `excuteTask`（历史拼写）与协议一致，禁止「修正」为 execute
- FINS 节点号 = IP 末段（硬编码约定）；现场节点号不符须先确认再改协议或换 Modbus
- commit 信息中文 Conventional Commits；commit/push 须用户确认；勿提交 `bin/`/`obj/`、明文口令、`tools/_app_*.txt`

## 已知坑 / 勿修复
- 表/字段历史拼写：`MAS_AUTO_WORKLINE_EQUIMENT`、`CARFTWORK_ID`、`EQUIMENT_*` 等沿用既有 schema，线上/种子已依赖，**严禁改名「纠正」**
- RCS 协议方法名 `excuteTask` 拼写错误为契约一部分，勿改
- Alarm 粘滞、启动对账完成前不开自动派工、缺 LOCATION_MAP 拒发、软删配置拒新派工、HasMat 未知 fail-closed：是安全设计，不是 bug
- PLC 自动重连已实现（`PlcReconnectService`，`Plc.AutoReconnectEnabled` 默认 `true`）：只对 `Faulted` 连接重连，人工 Disconnect 不重连；间隔从 `AutoReconnectInitialDelayMs`（默认 2000ms）失败倍增到 `AutoReconnectMaxDelayMs`（默认 30000ms），**无次数上限**。原 `Plc.MaxReconnectAttempts` 配置项从未被代码读取，已移除，勿再加回。重连成功前仍 fail-closed，不恢复派工
- 演示 SQL / `tools/FrameSeedReset` 会清空槽位（含预记）：仅测试库，**禁止在现场生产库执行**

## 参考文档
- 改动产品/调度/RCS 流程前，必须先读 `docs/客户端开发文档.md`（唯一开发方案）
- 改动数据库 / 实体 / 建库脚本前，必须先读 `docs/数据库文档.md` 与 `docs/sql/cnc_schema.sql`
- 改动 UI / 交互 / 主题前，必须先读 `docs/UI设计文档.md`
- 改动 PLC 点位 / 信号语义前，必须先读 `docs/测试机信号表.md`
- 本地演示与现场联调步骤：`docs/演示实操手册.md`；联调配置：`docs/现场联调配置清单.md`
- P0/P1 安全门禁代码级验收：`.scratch/final-acceptance/2026-08-05-cnc-loader-safety-hardening-acceptance.md`
- 文档索引：`docs/README.md`；阶段进度：`task_plan.md`

## 范围外（请勿触碰）
- `docs/agv对接接口.docx`（外部契约原文，以开发文档 §12 合并结果为准，勿直接当实现源乱改）
- `MAS_AUTO_EQUIMENT_REPORT` / `MAS_AUTO_EQUIMENT_WORKDATA` 及机台程式上传直连字段：**本期不实现**
- 第三方支付/账号体系（本项目无登录用户表）
- 用户本机明文 `appsettings.json` 口令与未请求的 git commit/push/生产部署
