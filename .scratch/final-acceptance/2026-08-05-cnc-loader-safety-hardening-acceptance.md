# CNC Loader 安全整改最终验收

| 字段 | 值 |
|------|-----|
| 日期 | 2026-08-05 |
| 项目路径 | `C:\Users\75626\Desktop\CNC` |
| 分支 | `main` @ `2244d57`（含本归档与 P0-5/P1 收口；相对 `origin/main` ahead，未 push） |
| 工作区 | 文档同步轮次可能另有未提交 docs 改动；业务代码以 `2244d57` 为准 |
| 本轮操作 | 代码级验收 + B1/B2 收口后已 commit `2244d57`；后续「文档同步」更新 AGENTS/CONTEXT/开发文档/联调清单等权威文档 |
| 证据来源 | 源码、`dotnet test`/`dotnet build`、`.scratch/**/issues/*.md`、本归档 |

---

## 1. 验收结论

**代码级最终整体验收通过，可以进入提交评审和真实设备联调。**

> **不是生产验收通过。** 真 MySQL / RCS / PLC、真实设备和长时间并发仍未验证。

### 1.1 阻塞收口（2026-08-05）

| # | 原阻塞 | 收口动作 | 状态 |
|---|--------|----------|------|
| B1 | P0-3 正文验收标准 checkbox 未勾选 | 按已有人工/自动化证据勾选 10 项；补充证据边界与「B1 清单收口」纪要；Status/Labels 保持 `closed` | **已解除** |
| B2 | `tests/CncLoader.Core.Tests/TestResults/r16r24.trx` | 用户明确授权后删除该单个普通文件（指定根路径 `CNC\TestResults\...` 不存在；实际删除路径见下） | **已解除** |

**B2 删除路径说明：** 指令中的 `C:\Users\75626\Desktop\CNC\TestResults\r16r24.trx` 不存在。经 `Resolve-Path` 确认唯一匹配文件为普通文件（非目录/非符号链接）：

`C:\Users\75626\Desktop\CNC\tests\CncLoader.Core.Tests\TestResults\r16r24.trx`

已 `Remove-Item -LiteralPath` 删除；删除后文件不存在；`git status` 不再显示该 `.trx`。空 `TestResults` 目录若残留则保留。

### 1.2 通过条件核对

- Core / Solution：`418/418` 全绿（**紧邻本次文档/卫生修正前的最终回归**；本轮未改代码/配置/测试，未重跑）
- `dotnet build CncLoader.sln`：`0` 警告 / `0` 错误（同上）
- `git diff --check`：干净（本轮卫生收口后复验）
- 8 个 P0/P1 Issue Header `Status` 均为 `closed`；验收清单已勾选；Labels 含 `closed`，不含 `ready-for-agent` / `needs-triage`
- 无 TestResults 临时产物进入 Git 状态；无 `.env`/证书/Token 出现在 status
- `.cursor/` 若仍未跟踪：非交付范围，不删除
- 源码静态核对（最终回归时）：未见新的 P0/P1 绕过
- 真 DB / RCS / PLC **未**验证 → 结论仅限**代码级**

---

## 2. 范围与基线

### 2.1 验收范围

| 编号 | 主题 | 本轮纳入 |
|------|------|----------|
| P0-1 | PLC HasMat 读取失败不得当作无料 | 是 |
| P0-2 | 槽位预记先于 RCS 下发 | 是 |
| P0-3 | 启动对账 fail-closed、重试、状态 UI | 是 |
| P0-4 | Callback 持久化成功后去重、single-flight | 是 |
| P0-5 | 软删配置不得参与新外部执行路由 | 是 |
| P0-6 | 盘点/人工校正不得覆盖 Reserved | 是 |
| P1-1 | 首次启动对账不得阻塞后续 HostedService | 是 |
| P1-2 | ForgetTask 不得留下 final seen 顺序僵尸 | 是 |
| 可选 P1/P2 | — | **不处理** |

### 2.2 代码基线结构

| 层 | 说明 |
|----|------|
| 已本地 commit | `dd4383e` — P0-1～P0-6 主体；`2244d57` — P0-5 typed/Claim、P1-1、P1-2、本归档与 Issue 收口（**均未 push**） |
| 文档同步 | AGENTS / CONTEXT / 开发文档 §6.3·§6.5·§5.7·§12 / 联调清单 / ADR-0002 附录 / `task_plan` / `findings`（与本轮结论对齐） |
| 本归档 | `.scratch/final-acceptance/2026-08-05-cnc-loader-safety-hardening-acceptance.md` |

### 2.3 本轮禁止事项（已遵守）

- 不 commit / push
- 不新增依赖
- 不操作真实 DB / RCS / PLC
- 不修改业务代码、配置、测试（仅新增本归档）

---

## 3. P0/P1 完成矩阵

### 3.1 Issue 状态审计总表

搜索范围：`.scratch/**/issues/*.md`（共 8 个目标 Issue）。

| Issue | 路径 | Status | Labels 当前 | 验收清单 | Comments 关闭证据 | 审计结论 |
|-------|------|--------|-------------|---------|-------------------|----------|
| P0-1 | `.scratch/p0-1-hasmat-recheck/issues/01-hasmat-fresh-unknown.md` | `closed` | 含 `closed`，无 `ready-for-agent`/`needs-triage` | 已勾选 `[x]` | 有「验收关闭」 | 通过 |
| P0-2 | `.scratch/p0-2-reservation-first/issues/01-reserve-before-rcs-dispatch.md` | `closed` | 含 `closed` | 已勾选 `[x]` | 有「验收关闭」 | 通过 |
| P0-3 | `.scratch/p0-3-reconcile-fail-closed/issues/01-reconcile-failure-opens-dispatch.md` | `closed` | 含 `closed` | **已勾选 `[x]`（B1 收口 2026-08-05）** | 有「验收关闭」+「B1 清单收口」 | 通过 |
| P0-4 | `.scratch/p0-4-callback-dedupe-after-persist/issues/01-callback-dedupe-before-persistence.md` | `closed` | 含 `closed` | 已勾选 `[x]` | 有关闭纪要 | 通过 |
| P0-5 | `.scratch/p0-5-soft-deleted-routing-protection/issues/01-dispatch-uses-soft-deleted-config.md` | `closed` | 含 `closed` | 已勾选 `[x]` | 有「最终回归与验收关闭」 | 通过 |
| P0-6 | `.scratch/p0-6-reserved-slot-write-protection/issues/01-inventory-correction-overwrites-reservation.md` | `closed` | 含 `closed` | 已勾选 `[x]` | 有关闭纪要 | 通过 |
| P1-1 | `.scratch/p1-1-startup-reconcile-nonblocking/issues/01-initial-reconcile-blocks-host-startup.md` | ``closed`` | 含 `closed` | 已勾选 `[x]` | 有 GREEN/关闭 | 通过 |
| P1-2 | `.scratch/p1-2-callback-final-seen-order/issues/01-forget-task-leaves-stale-final-seen-order.md` | ``closed`` | 含 `closed` | 已勾选 `[x]` | 有 GREEN/关闭 | 通过 |

说明：Comments 历史中出现的「保持 `ready-for-agent`」为过程记录；**当前 Header Labels 均已不含** `ready-for-agent` / `needs-triage`。

### 3.2 分项详情

#### P0-1 — HasMat fresh 未知不得当作无料

| 字段 | 内容 |
|------|------|
| 原始风险 | `ReadHasMatFreshAsync` 忽略 `Error`，把失败读成明确有/无料，误过 Loaded/Unloaded、写 `POS_TEST_START`、误落账 |
| 最终生产行为 | `HasMatReading.From`：`Error!=null` → `null`（未知）；未知 Hold 至阈值后 Alarm；确认态才写 TestStart / Confirm |
| 关键生产文件 | `HasMatRecheckTracker.cs`；`PositionScheduler.ReadHasMatFreshAsync`；`AppOptions.HasMatRecheckFailThreshold`；看板 `StateDetail` 展示 |
| 关键测试文件 | `HasMatRecheckTrackerTests.cs` 及 Scheduler 相关用例 |
| 自动化证据 | Core 全量 418/418；HasMat 相关包含在全量中 |
| 人工证据 | Issue 关闭纪要：模拟器路径已注；真 PLC 未做 |
| 未验证项 | 真 PLC 读失败注入 |
| Issue / Status | 上表；`closed` |

#### P0-2 — 槽位预记先于 RCS 下发

| 字段 | 内容 |
|------|------|
| 原始风险 | 先发 RCS 再预记 → orphan 任务窗口 |
| 最终生产行为 | `ReservationFirstDispatcher`：预生成 taskId → Reserve → 成功才 RCS；预记失败不下发；下发失败回滚预记 |
| 关键生产文件 | `ReservationFirstDispatcher.cs`；`PositionScheduler` 派工路径；ADR `docs/adr/0002-槽位预记先于RCS下发.md` |
| 关键测试文件 | `ReservationFirstDispatcherTests.cs` |
| 自动化证据 | Reservation 过滤套件 45/45；全量 418 |
| 人工证据 | Issue：本地模拟器可见「先预记再下发」节拍 |
| 未验证项 | 真 RCS 下发失败后的预记回滚联调 |
| Issue / Status | `closed` |

#### P0-3 — 启动对账 fail-closed

| 字段 | 内容 |
|------|------|
| 原始风险 | `ReconcileAsync` 吞异常后仍 `IsReconciled=true` 并启动派工 |
| 最终生产行为 | `StartupReconcileCoordinator` 阶段短路；失败保持未开闸并重试；成功才单次开闸；派工入口检查 `IsReconciled`；看板对账状态条 |
| 关键生产文件 | `StartupReconcileCoordinator.cs`；`PositionScheduler`；`ReconciliationState*`；`DashboardViewModel` / `PageTemplates.xaml`；`ReconcileRetryIntervalMs` |
| 关键测试文件 | `StartupReconciliationTests.cs`；`PositionSchedulerStartupTests.cs`；Dashboard 绑定测试 |
| 自动化证据 | Reconciliation/Startup 过滤 **48/48** |
| 人工证据 | 见 §9（双模拟器成功路径） |
| 未验证项 | 真故障注入失败锁闸人工观感；DPI/多显示器 |
| Issue / Status | Header `closed`；正文验收 checkbox **已勾选**（B1 已解除） |

#### P0-4 — Callback 持久化后去重

| 字段 | 内容 |
|------|------|
| 原始风险 | 去重早于持久化 → 失败后同 key 不再处理，状态/告警丢失 |
| 最终生产行为 | `CallbackDeduplicationGate`：in-flight / final seen 分离；leader 持久化成功才 final seen；失败/取消释放；follower 等 leader，不提前成功；Host 仍 HTTP 200 + 内部 ACK |
| 关键生产文件 | `CallbackDeduplicationGate.cs`；`RcsCallbackProcessor.cs`；`RcsCallbackHost.cs`；`RcsCallbackAckResults.cs` |
| 关键测试文件 | `CallbackDeduplicationGateTests.cs`；`RcsCallbackProcessorDeduplicationTests.cs`；`RcsCallbackHostAckTests.cs` |
| 自动化证据 | Callback/Gate 过滤 **41/41** |
| 人工证据 | 无真 RCS 重推；仅自动测试 |
| 未验证项 | 真 RCS 重推 / 慢订阅者拖延 ACK |
| Issue / Status | `closed` |

#### P0-5 — 软删配置不得参与新外部执行

| 字段 | 内容 |
|------|------|
| 原始风险 | 软删 Equipment/Craft/WorkLine/Map/Bind 仍参与自动/手动/Redo/换架/盘点派工 |
| 最终生产行为 | `ConfigActivity.IsActive`（仅 `STATE=="0"`）；Resolver + Validator Pre/Final；typed `ManagedDispatchEndpoint`；AutoRedo Gate→Claim→Send；无 Skip / LINE fallback / 直连 Client |
| 关键生产文件 | `ManagedDispatchRouteResolver.cs`；`RoutingAvailabilityValidator.cs`；`ManagedDispatchEndpoint.cs`；`IFrameRoutingStore`/`FrameRoutingStore`；`RcsTaskService.cs`；`RcsTaskTracker.cs`；`ChangeFrameOrchestrator.cs`；`InventoryService.cs`；`AutoRedoClaim.cs`；`RcsTaskStore.TryClaimAutoRedoAsync` |
| 关键测试文件 | 大量 `tests/.../Routing/*`（typed / ChangeFrame / Inventory / PalletReturn / Tracker / Wiring / SoftDelete 等） |
| 自动化证据 | Routing 过滤 **248/248**；ChangeFrame/Inv/Pallet **63/63**；Tracker/AutoRedo **23/23** |
| 人工证据 | Issue 关闭审计 12 项静态核对通过；真设备换架/盘点未做 |
| 未验证项 | 真 MySQL Claim 双连接；真实换架/盘点 |
| Issue / Status | `closed` |

#### P0-6 — 盘点/人工校正不得覆盖 Reserved

| 字段 | 内容 |
|------|------|
| 原始风险 | 盘点/人工校正无条件覆盖 `SLOT_STATE=Reserved` |
| 最终生产行为 | 外部写条件 `SLOT_STATE != Reserved`；人工目标态 Reserved 拒绝；异常预记 Warning；冲突结果类型化 |
| 关键生产文件 | `SlotAccountStore.cs`；`SlotAccountService.cs`；`InventoryService`；`FrameViewModel` 校正路径 |
| 关键测试文件 | `SlotAccountReservedProtectionTests.cs`；`InventoryReservedSlotProtectionTests.cs`；`FrameReservedSlotStructuralProtectionTests.cs`；UI Frame 校正测试 |
| 自动化证据 | Reservation/Slot 过滤 **45/45** |
| 人工证据 | Issue 关闭纪要含 Growl/盘点文案约定；真设备未做 |
| 未验证项 | 真并发盘点 vs 预记 |
| Issue / Status | `closed` |

#### P1-1 — 首次对账不阻塞 HostedService

| 字段 | 内容 |
|------|------|
| 原始风险 | `StartAsync` 同步 await 首次对账，挂起阻塞后续 HostedService |
| 最终生产行为 | 装载点位后原子启动唯一 `_reconcileWorkflowTask` 并立即返回；lifecycle 用自有 CTS；成功前 fail-closed 不变 |
| 关键生产文件 | `PositionScheduler.StartAsync` / `ReconcileWorkflowAsync` / `StopAsync` |
| 关键测试文件 | `PositionSchedulerNonBlockingStartupTests.cs`（+ 既有 Startup/Reconciliation） |
| 自动化证据 | Reconciliation/Startup **48/48**（含 NonBlocking） |
| 人工证据 | 无单独人工；依赖双模拟器启动成功路径 |
| 未验证项 | Stop 被外部忽略 CT 的调用拖慢 |
| Issue / Status | `closed` |

#### P1-2 — ForgetTask 不留 final seen 顺序僵尸

| 字段 | 内容 |
|------|------|
| 原始风险 | Forget 只删 Dictionary、Queue 留僵尸 → 容量淘汰误删 live key |
| 最终生产行为 | `Dictionary<string, LinkedListNode<string>>` + `LinkedList`；Forget 双删；淘汰只删 live First；容量默认 4000 |
| 关键生产文件 | `CallbackDeduplicationGate.cs` |
| 关键测试文件 | `CallbackDeduplicationGateForgetTests.cs` |
| 自动化证据 | Callback/Gate **41/41**（含 Forget） |
| 人工证据 | 无 |
| 未验证项 | 进程重启后 final seen 丢失（架构限制） |
| Issue / Status | `closed` |

---

## 4. 核心安全不变量

本轮对照当前工作区源码静态核对（2026-08-05）：

| # | 不变量 | 核对结果 | 主要锚点 |
|---|--------|----------|----------|
| 1 | PLC 读取失败 fail-closed | **成立** | `HasMatReading.From`；`ReadHasMatFreshAsync` 检查 `r.Error` / catch→`null` |
| 2 | RCS 下发前 Reserved 预记；失败按既有规则回滚 | **成立** | `ReservationFirstDispatcher` + Scheduler 派工；ADR-0002 |
| 3 | 对账成功前 `IsReconciled=false`、不启派工循环、派工入口拒绝 | **成立** | `TryOpenGateAfterSuccess`；`DispatchLoopAsync` / 派工入口 `!IsReconciled` 返回 |
| 4 | 对账 workflow 与 attempt 各最多一个 | **成立** | 单一 `_reconcileWorkflowTask` 创建点；Coordinator / 测试计数契约 |
| 5 | Callback：leader 持久化成功后 final seen；失败/取消释放；follower 不提前成功；Forget 双删 | **成立** | `CallbackDeduplicationGate.ExecuteAsync` / `ForgetTask` / `RemoveFinalSeen_NoLock` |
| 6 | 外部槽位写 `STATE != Reserved`；人工目标 Reserved 三层拒绝 | **成立** | `SlotAccountStore` 条件 `ExecuteUpdateAsync`；`SlotAccountService.SetSlotAsync` / 校正分类 |
| 7 | 路由仅 `STATE=="0"`；unknown/null fail-closed；新外部执行经 Resolver/Validator Final；无 Skip/LINE fallback/直连 Client | **成立** | `ConfigActivity.IsActive`；`RcsTaskService` 全入口；`src` 无 `SkipManagedRouteGate`；无 `(1,"LINE")` 回退 |
| 8 | AutoRedo：Gate→原子 Claim→Send；路由拒发不消耗次数 | **成立** | `RcsTaskService` 注释与顺序；`TryClaimAutoRedoAsync`；`AutoRedoClaimRules` |
| 9 | callback、Confirm/Rollback、Query/对账继续历史收口 | **成立** | P0-5 审计 + 源码：收口路径不经新执行 Create 门禁 |
| 10 | TestConnection 不错误接入派工门禁 | **成立** | `RcsViewModel.TestConnectionAsync` → `QueryAsync` only |

---

## 5. 生产改动文件清单

> 证据命令：`git status --short`、`git diff --name-status`、`git diff --stat`、`git show dd4383e --name-status`。  
> **不含** `bin/`、`obj/`、`TestResults` 业务摘要。  
> **当前改动尚未全部 commit；已有本地 commit 未 push。**

### 5.1 Core

| 文件 | 状态 | 摘要 |
|------|------|------|
| `src/CncLoader.Core/State/HasMatRecheckTracker.cs` | commit `dd4383e` | HasMat 三态读取与连续未知 Alarm |
| `src/CncLoader.Core/State/StartupReconcileCoordinator.cs` | commit | 对账轮次 fail-closed / 开闸决策 |
| `src/CncLoader.Core/State/ReconciliationState.cs` 等 | commit | 对账状态快照与 UI 绑定模型 |
| `src/CncLoader.Core/State/IPositionScheduler.cs` | commit | 暴露对账状态 / `IsReconciled` |
| `src/CncLoader.Core/Rcs/ReservationFirstDispatcher.cs` | commit | 预记优先编排 |
| `src/CncLoader.Core/Rcs/ISlotAccountStore.cs` / `SlotMutationResult.cs` / `InventoryCorrectionResult.cs` | commit | 槽位外部写与校正结果类型 |
| `src/CncLoader.Core/Config/ConfigActivity.cs` | commit | 配置活动精确 `STATE=="0"` |
| `src/CncLoader.Core/Rcs/IManagedDispatchRouteResolver.cs` / `IRoutingAvailabilityValidator.cs` | commit + 工作区增补 | 受管路由解析/校验契约；Validator 增加 Operation 上下文 |
| `src/CncLoader.Core/Abstractions/IEquipmentRoutingStore.cs` 等路由 Store | commit | 设备/点位/地图/料架结构权威读 |
| `src/CncLoader.Core/Abstractions/IFrameRoutingStore.cs` | **未跟踪新增** | 料架 STATE 活动读接缝 |
| `src/CncLoader.Core/Rcs/ManagedDispatchEndpoint.cs` | **未跟踪新增** | typed 端点 Kind / 工厂 |
| `src/CncLoader.Core/Rcs/AutoRedoClaim.cs` | **未跟踪新增** | AutoRedo Claim 结果与候选态规则 |
| `src/CncLoader.Core/Rcs/IRcsTaskService.cs` | 工作区修改 | 类型化派工/重派契约收紧 |
| `src/CncLoader.Core/Rcs/IRcsTaskStore.cs` | 工作区修改 | `TryClaimAutoRedoAsync` |
| `src/CncLoader.Core/Rcs/RcsModels.cs` | 工作区修改 | 路由/派工相关模型补充 |
| `src/CncLoader.Core/Abstractions/IUserNotificationService.cs` | commit | 用户通知抽象（预记冲突等） |

### 5.2 Data

| 文件 | 状态 | 摘要 |
|------|------|------|
| `SlotAccountStore.cs` / `SlotAccountService.cs` | commit | 条件更新保护 Reserved；校正/人工写分类 |
| `ConfigServices.cs` | commit | 调度反查三层活动 fail-closed；去 LINE 回退 |
| `ManagedDispatchRouteResolver.cs` | commit + 工作区 | 活动 Map 解析；typed 端点 |
| `RoutingAvailabilityValidator.cs` | commit + 工作区 | Final/Pre 权威校验；ChangeFrame Bind/Frame |
| `Equipment/LocationMap/PlcPoint/FrameStructure` RoutingStore | commit | 路由权威查询实现 |
| `FrameRoutingStore.cs` | **未跟踪新增** | Frame STATE 快照 |
| `RcsTaskStore.cs` | commit + 工作区 | AutoRedo 原子 Claim（affected-row） |
| `DataServiceCollectionExtensions.cs` | commit + 工作区 | 注册 FrameRoutingStore 等 |
| `LocationMapService.cs` / `PlcPointSource.cs` | commit | 软删过滤对齐 |

### 5.3 Communication / RCS

| 文件 | 状态 | 摘要 |
|------|------|------|
| `CallbackDeduplicationGate.cs` | commit + 工作区（P1-2） | 持久化后 final seen；LinkedList 无僵尸 Forget |
| `RcsCallbackProcessor.cs` / `RcsCallbackHost.cs` / `RcsCallbackAckResults.cs` | commit | single-flight 接线与 ACK |
| `RcsTaskService.cs` | commit + 工作区 | Pre/Final 门禁；AutoRedo Gate→Claim→Send；typed 端点 |
| `RcsTaskTracker.cs` | commit + 工作区 | AutoRedo 走 Service 门禁顺序 |
| `PositionScheduler.cs` | commit + 工作区（P1-1） | fail-closed 对账；后台单一 workflow；派工门闩 |
| `ChangeFrameOrchestrator.cs` | commit + 工作区 | 换架经受管路由，禁 LINE 回退 |
| `InventoryService.cs` | commit + 工作区 | 盘点路径路由门禁 + Reserved 冲突 |

### 5.4 App / WPF

| 文件 | 状态 | 摘要 |
|------|------|------|
| `DashboardViewModel.cs` / `PageTemplates.xaml` | commit | 启动对账状态条 |
| `EquipmentViewModel.cs` | commit | HasMat/状态细节展示 |
| `FrameViewModel.cs` | commit | 校正/Reserved 冲突 UX |
| `RcsViewModel.cs` | commit | 手动/Redo 门禁失败文案；TestConnection 仍只 Query |
| `HandyControlUserNotificationService.cs` / UI DI | commit | Growl 通知实现 |
| `CncLoader.App` | 无业务代码 diff（仅配置见下） | — |

### 5.5 配置

| 文件 | 状态 | 摘要（仅键名/安全布尔，无密钥） |
|------|------|----------------------------------|
| `src/CncLoader.App/appsettings.json` | 工作区修改 | 新增键 `Rcs:ReconcileRetryIntervalMs` = `5000`；既有 `WaterMonitorEnabled=false`、`InventoryAutoEnabled=false` 未改本轮语义 |
| `AppOptions.cs` / Common DI Validate | commit | `HasMatRecheckFailThreshold`、`ReconcileRetryIntervalMs`（`<=0` 启动失败） |
| `*.sln` / `*.csproj` | commit 已纳入测试工程；**工作区 `git diff -- *.sln *.csproj` 无额外变更** | 新增 `tests/CncLoader.Core.Tests` |

**敏感值：** 本归档未读取、未复制连接串 / Token / 密码。

### 5.6 `.scratch` Issue / 验收资料

| 路径 | 状态 | 摘要 |
|------|------|------|
| `.scratch/p0-1-…` ~ `p0-6-…` | commit 已纳入；P0-5 Issue 工作区继续增补关闭纪要 | P0 Issue SSOT |
| `.scratch/p1-1-startup-reconcile-nonblocking/` | **未跟踪** | P1-1 Issue（`closed`） |
| `.scratch/p1-2-callback-final-seen-order/` | **未跟踪** | P1-2 Issue（`closed`） |
| `.scratch/final-acceptance/2026-08-05-cnc-loader-safety-hardening-acceptance.md` | **本轮新增** | 本文 |

### 5.7 工作区其他未跟踪（非本安全整改交付物）

| 路径 | 处理 |
|------|------|
| `.cursor/skills/**`、`.cursor/templates/**` | 技能/模板资料；**不纳入安全改动清单**；非密钥；不删除 |
| `tests/.../TestResults/r16r24.trx` | **已用户授权删除（B2 解除）**；不再出现于 git status |

---

## 6. 测试改动文件清单

### 6.1 已在 `dd4383e` 的测试工程（节选）

`CncLoader.Core.Tests` 工程整体新增于该 commit，覆盖 HasMat、ReservationFirst、Reconciliation、Callback、Routing SoftDelete、Reserved 槽位、Dashboard 绑定、Frame UI 校正等。

### 6.2 工作区修改 / 新增测试（相对 HEAD）

| 文件 | 状态 | 摘要 |
|------|------|------|
| `CallbackDeduplicationGateForgetTests.cs` | 新增 | P1-2 Forget/容量/僵尸 11 项 |
| `PositionSchedulerNonBlockingStartupTests.cs` | 新增 | P1-1 StartAsync 非阻塞 10 项 |
| `PositionSchedulerStartupTests.cs` | 修改 | 对齐单一 workflow 计数语义 |
| `ChangeFrameRoutingGateTests.cs` | 新增 | ChangeFrame Final/Bind 门禁 |
| `InventoryRoutingGateTests.cs` | 新增 | 盘点路由门禁 |
| `PalletReturnRoutingGateTests.cs` | 新增 | 回库门禁 |
| `RcsAuxiliaryOperationRoutingGateTests.cs` | 新增 | Grab/Identify 等辅助操作 |
| `RcsTaskServiceTypedEndpointTests.cs` | 新增 | typed 端点发送边界 |
| `TypedManagedRouteResolverTests.cs` / `TypedEndpointSeedShapes.cs` | 新增 | Resolver typed 形状 |
| `RcsTaskTrackerRoutingGateTests.cs` | 新增 | Tracker AutoRedo 顺序 |
| `RcsSendBoundaryInventoryTests.cs` | 新增 | 发送边界盘点 |
| `ProductionRoutingGateWiringTests.cs` | 新增 | 生产 DI/接线审计 |
| `FakeFrameRoutingStore.cs` | 新增 | 测试 Fake |
| `DispatchRoutingGateFakes.cs` / `ManualReplayRoutingGateFakes.cs` 等 | 修改 | 适配 typed endpoint / Validator |
| `RoutingSoftDeleteQueryTests.cs` 等既有 Routing 测试 | 修改 | 对齐最终契约 |
| `RcsCallbackTestFakes.cs` | 小改 | 回调测试夹具 |

---

## 7. 配置与 DI 变更

| 项 | 说明 |
|----|------|
| 配置键 | `Rcs:ReconcileRetryIntervalMs`（默认/appsettings `5000`；`<=0` Validate 失败） |
| 配置键 | `HasMatRecheckFailThreshold`（默认 6） |
| 安全布尔（保持） | `WaterMonitorEnabled=false`；`InventoryAutoEnabled=false`（联调前策略，非本轮翻转） |
| DI | `IManagedDispatchRouteResolver` / `IRoutingAvailabilityValidator` / 各 RoutingStore / `IFrameRoutingStore` / `ISlotAccountStore` / `IUserNotificationService` / HostedService `PositionScheduler` |
| 模拟器 | 本轮未改 UseSimulator 键值；人工证据基于双模拟器成功路径 |
| sln/csproj 工作区 | 相对 HEAD **无** 额外 diff |

---

## 8. 自动化验证结果

### 8.1 本轮命令（2026-08-05）

| # | 命令 | 结果 |
|---|------|------|
| 1 | `dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj` | **Failed: 0, Passed: 418, Skipped: 0, Total: 418** |
| 2 | `dotnet test CncLoader.sln` | 仅执行 Core.Tests；**418/418**（无额外测试项目） |
| 3 | `dotnet build CncLoader.sln` | **0 警告 / 0 错误** |
| 4 | `git diff --check` | **干净**（exit 0；仅有 CRLF 提示，无 whitespace error） |

> **沿用说明（B1/B2 收口轮次）：** 上表 1–3 及 §8.2 过滤结果来自**紧邻本次文档/卫生修正前的最终回归**。本轮仅勾选 P0-3 Issue、删除授权 `.trx`、更新本归档，**未改业务代码/配置/测试，未重跑测试**。`git diff --check` 与 `git status` 在收口后复验。

### 8.2 P0/P1 代表性过滤回归（不得相加为额外总数）

过滤结果为**子集抽样**，彼此有重叠；**不以相加冒充额外覆盖**。权威全量仍为 **418**。

| 主题过滤 | Passed / Total |
|----------|----------------|
| Reconciliation / PositionSchedulerStartup / NonBlocking | 48 / 48 |
| Reservation / Slot / Reserved / InventoryCorrection | 45 / 45 |
| CallbackDeduplication / HostAck / Forget | 41 / 41 |
| Routing / SoftDelete / Typed / ManagedDispatch | 248 / 248 |
| ChangeFrame / InventoryRouting / PalletReturn | 63 / 63 |
| RcsTaskTracker / AutoRedo | 23 / 23 |

### 8.3 绕过扫描

- `src` 内 `SkipManagedRouteGate`：**无匹配**
- 路由 `(1,"LINE")` 硬编码回退：**未发现生产路径使用**

---

## 9. 人工验收证据

来源：P0-3 Issue「成功路径人工验收」（2026-08-04）及关闭纪要。**仅归档已有证据，不虚构未执行项。**

| 项 | 结果 |
|----|------|
| 双模拟器启动成功路径 | 通过（P0-3：`Plc.UseSimulator` / `Rcs.UseSimulator`；水位/自动盘点未启用） |
| UI 人工检查（P0-3 记载 1–5，操作者确认全部通过） | ① 看板可见「启动对账状态」条；② 主文案「启动对账完成」/ 副文案「自动派工已开启」；③ 成功色、文案完整可读；④ 切页返回状态保持、约 10s 无闪回重试；⑤ RCS 页已勾选「暂停自动派工」并正常关窗 |
| 验收项核对（P0-3 关闭纪要 1–14） | 纪要写明全部满足（自动测试 + 上述成功路径人工 + 代码审查） |
| 日志「启动对账完成，开始自动派工」 | **恰好 1 次**（2026-08-04 14:26:53.688） |
| 失败锁闸日志 | **0 次** |
| 重复开闸 | **0 次** |
| 未处理异常 | **0 次** |
| 正常关窗退出码 | **0** |

> 说明：仓库 Issue 中可核验的 UI 人工清单为 P0-3 的 **1–5** 项；关闭纪要另有 **1–14** 总验收核对。未发现独立成文的「UI 1–11」清单，故不虚构该项。

**明确未执行（不得当作已验）：**

- 真 MySQL 故障注入
- 真 RCS 重推
- 真 PLC
- 真设备换架 / 盘点
- DPI / 多显示器全面回归
- 长时间并发

---

## 10. 未验证项

| 类别 | 项 |
|------|-----|
| 真环境 | 真 MySQL / 真 RCS / 真 PLC 联调 |
| 真设备 | 换架、盘点、人工校正与预记并发 |
| Claim | 真 MySQL AutoRedo Claim affected-row 双连接集成 |
| 失败注入 | 对账失败锁闸人工观感（仅自动测试） |
| UI | DPI / 多显示器；长时间无限重试观感 |
| 并发 | 生产级长时间 callback / 派工并发 |
| 提交卫生 | B1/B2 已收口；提交前仍须排除 `bin/`/`obj/`/`.cursor/` 等非交付物 |

---

## 11. 已知风险与 P2

### 11.1 已接受残余风险

| 风险 | 说明 |
|------|------|
| Final→HTTP 极小窗口 | Final 通过后、HTTP 发出前配置变更窗口；无分布式锁 |
| AutoRedo 发送失败已耗次数 | Claim 成功后 Send 失败不回滚次数（D14） |
| Callback final seen 仅内存 | 容量 4000；重启丢失；进程内语义 |
| Confirm 幂等条件偏宽 | 历史收口策略，本期不收紧 |
| 慢 callback 订阅者可能拖延 ACK | Host 仍 200；处理时长受订阅者影响 |
| `LAST_VERIFY_TIME` 注释/实现差异 | 文档债，非本轮门禁绕过 |
| 历史 `BIND_SOURCE` 残留 | 异常预记已 Warning/拒绝外部校正 |

### 11.2 需现场验证

| 风险 | 说明 |
|------|------|
| 真 MySQL Claim 原子性 | 缺双连接集成证明 |
| 真 RCS 重推 / 真 PLC HasMat 失败 | 仅模拟器 + 单测 |
| 真换架 / 盘点 / 软删配置热失效 | 需现场配置操作 |
| Stop 可能被忽略 CT 的外部调用拖慢 | P1-1 已知生命周期边角 |

### 11.3 可选 P2（不立项 / 不阻塞本轮代码逻辑）

| 项 | 说明 |
|----|------|
| Core 中 UI 文案与 Brush key | P0-3 关闭纪要候选 |
| 生命周期 CTS 模式整理 | 同上 |
| PlcPoint 深度路由 | 本期 N/A |
| 历史 ChangeFrame 缺 Operation Context | 重放走 Transit 门禁 fail-closed；扩表需另立项 |

### 11.4 需 ADR 才能改变的架构限制

| 限制 | 说明 |
|------|------|
| 预记先于 RCS（无 Outbox） | ADR-0002；不引入分布式事务 |
| Callback final seen 进程内 | 不引入分布式去重存储 |
| 软删活动语义 `STATE=="0"` | 与 schema 约定绑定；改活动码需 ADR |
| 自研 Shell 与 WPF-UI 关系 | ADR-0001；本轮不改 |

---

## 12. 提交前检查

| 检查 | 结果 |
|------|------|
| Core / Solution 全绿 | **是**（418/418，最终回归；本轮未重跑） |
| build 0/0 | **是**（同上） |
| `git diff --check` | **是**（B1/B2 收口后复验） |
| P0/P1 Header Status 均 closed | **是**（8/8） |
| 验收清单全部勾选 | **是**（含 P0-3；B1 已解除） |
| 无 TestResults 临时产物进入 Git 状态 | **是**（B2 已解除） |
| 敏感文件（`.env` / 证书 / 明文口令）出现在 git status | **未发现** |
| `.cursor/` | 若仍未跟踪：非交付，不删除、勿提交 |
| 改动清单覆盖 tracked + 业务相关 untracked | **是** |
| 业务代码本轮是否被验收操作改动 | **否**（仅 Issue + 归档 + 删除 `.trx`） |
| 是否已 commit 全部工作区 | **否**（`dd4383e` 仅部分；P1/P0-5 收尾仍未提交） |
| 是否已 push | **否**（`main` ahead 1） |

**提交前必做：** 排除 `bin/`/`obj/`/`.cursor/`；按需分批 commit（须用户确认）；即可进入提交评审。

---

## 13. 后续真实设备联调清单

1. 关闭双模拟器：`Plc.UseSimulator=false`、`Rcs.UseSimulator=false`；核对真实 IP / LOCATION_MAP / 料架绑定  
2. 确认 `WaterMonitorEnabled` / `InventoryAutoEnabled` 首轮保持 `false`  
3. 验证启动对账：成功开闸 1 次；人为制造 Query/DB 失败时锁闸与自动重试  
4. HasMat：拔网/错误地址时未知 Hold→Alarm，禁止误写 `POS_TEST_START`  
5. 预记优先：观察「预记→下发」；人为 RCS 失败确认预记回滚  
6. Callback：RCS 重推同 key，确认不双写、失败可重试  
7. 软删：禁用 Equipment/Craft/WorkLine/Map/Bind 后新派工拒发；历史 Confirm/Rollback 仍可收口  
8. Reserved：盘点/人工校正冲突；预记槽不被覆盖  
9. AutoRedo：路由拒发不耗次数；Claim 并发双客户端  
10. 换架 / 回库 / 盘点真实路径各跑通一次  
11. 正常关窗与 Stop 耗时观察  

---

## 附录 A — 本轮 git 证据快照（摘要）

```text
分支: main @ dd4383e [origin/main: ahead 1]

工作区已修改（节选）:
  M .scratch/p0-5-.../01-dispatch-uses-soft-deleted-config.md
  M src/CncLoader.App/appsettings.json  (+ReconcileRetryIntervalMs)
  M Communication: CallbackDeduplicationGate, RcsTaskService, RcsTaskTracker,
                   ChangeFrameOrchestrator, InventoryService, PositionScheduler
  M Core: IRcsTaskService, IRcsTaskStore, IRoutingAvailabilityValidator, RcsModels
  M Data: DataServiceCollectionExtensions, ManagedDispatchRouteResolver,
          RcsTaskStore, RoutingAvailabilityValidator
  M tests: 多份 Routing/State/Communication

未跟踪（业务相关）:
  ?? .scratch/p1-1-.../
  ?? .scratch/p1-2-.../
  ?? src/CncLoader.Core/Abstractions/IFrameRoutingStore.cs
  ?? src/CncLoader.Core/Rcs/AutoRedoClaim.cs
  ?? src/CncLoader.Core/Rcs/ManagedDispatchEndpoint.cs
  ?? src/CncLoader.Data/Repositories/FrameRoutingStore.cs
  ?? tests/.../CallbackDeduplicationGateForgetTests.cs
  ?? tests/.../PositionSchedulerNonBlockingStartupTests.cs
  ?? tests/.../Routing/*（多份新增）
  （原 ?? tests/.../TestResults/r16r24.trx — 已于 2026-08-05 用户授权删除，不再出现）

未跟踪（非交付）:
  ?? .cursor/skills/** 、 .cursor/templates/**
```

---

**归档人：** AI Agent（最终验收 + B1/B2 收口）  
**归档时间：** 2026-08-05  
**结论复述：** **代码级最终整体验收通过，可以进入提交评审和真实设备联调。** 非生产验收通过；真 MySQL/RCS/PLC 与长时间并发仍待现场验证。B1/B2 已解除。
