# P0-3：启动对账失败仍开启自动派工

Type: bug
Priority: P0
Status: closed
Labels: bug, P0, closed
Feature: p0-3-reconcile-fail-closed

## 问题

`PositionScheduler.StartAsync` 在 `ReconcileAsync` 各阶段（① / ①b / ② / ③）吞掉异常后，仍无条件置 `IsReconciled = true`、打「启动对账完成」日志，并启动 `_dispatchTask` 自动派工循环。这与 `docs/客户端开发文档.md` §6.3「对账完成前禁止自动派工」、`CONTEXT.md`「启动对账完成前不开自动派工」的 fail-closed 契约冲突。

### 源码锚点（2026-08-04 核查）

**当前错误调用链（已由源码确认，修复前）：**

```
IHost.StartAsync
  └─ PositionScheduler.StartAsync                    # IHostedService
       ├─ [SchedulerEnabled=false] → IsReconciled=true; return（不开循环）
       ├─ 装载点位缓存（catch Warning，空集可继续）
       ├─ await ReconcileAsync(ct)
       │    ├─ ① GetUnfinishedTaskIds + 绑回 ctx     # catch → LogWarning，不抛；仍继续后续阶段
       │    ├─ ①b SettleTerminalTasksOnReconcileAsync # catch → LogWarning；Query !Success 仅 Warning 跳过
       │    ├─ ② RollbackStaleReservations + PLC 门补落账 # catch → LogWarning
       │    └─ ③ PLC 有料无任务 → Alarm               # catch → LogWarning
       ├─ IsReconciled = true                         # 无条件（缺陷）
       ├─ Reconciled?.Invoke
       ├─ LogInformation("启动对账完成，开始自动派工")
       ├─ _loopTask = LoopAsync                       # 状态机主循环
       └─ _dispatchTask = DispatchLoopAsync           # 自动派工；循环内只看 IsAutoDispatchPaused
```

| 锚点 | 路径 | 行号（约） | 行为（修复前） |
|------|------|-----------|----------------|
| `StartAsync` | `src/CncLoader.Communication/State/PositionScheduler.cs` | 168–212 | 对账后无条件开闸 + 启双循环 |
| `IsReconciled` 赋值 #1 | 同上 | 174 | `SchedulerEnabled=false` 时置 true（不开派工循环） |
| `IsReconciled` 赋值 #2 | 同上 | 205 | `ReconcileAsync` 返回后无条件置 true |
| `Reconciled` 事件 | 同上 | 206；接口 `src/CncLoader.Core/State/IPositionScheduler.cs` 20–21 | 开闸时触发；**全仓 UI 无订阅** |
| `_dispatchTask` 启动 | `PositionScheduler.cs` | 211 | `Task.Run(DispatchLoopAsync)`；**不检查对账成败** |
| `DispatchLoopAsync` | 同上 | 1262–1287 | 仅 `IsAutoDispatchPaused` 门闩，**不读 `IsReconciled`** |
| `ReconcileAsync` | 同上 | 377–445 | 四阶段各自 try/catch `LogWarning`，失败后仍继续后续阶段 |
| ① | 同上 | 381–396 | `_taskStore.GetUnfinishedTaskIdsAsync` + 绑 `Dispatching` |
| ①b | 同上 | 398–408 → `SettleTerminalTasksOnReconcileAsync` 451–558 | query 终态收口；`!result.Success` → Warning 跳过（不抛） |
| ② | 同上 | 410–418 | `RollbackStaleReservationsAsync(unfinished)` + `SettleCompletedPendingWithPlcAsync` |
| ③ | 同上 | 420–444 | fresh HasMat；有料无任务 → Alarm |
| 注册 | `src/CncLoader.Communication/DependencyInjection/CommunicationServiceCollectionExtensions.cs` | 70–75 | Singleton + HostedService + `IPositionScheduler` |
| 配置框架 | `AppOptions` / `RcsOptions` + `AddCncCommon` `ValidateOnStart` | — | 须补 `ReconcileRetryIntervalMs` 的 Validate（`<=0` 启动失败）；禁止静默降级 |

**契约对照（文档）：**

- `docs/客户端开发文档.md` §6.3：原则「宁可保守停下」；步骤 5「以上全部完成后置对账完成标志，调度器方可开始自动派工」
- `CONTEXT.md` / `AGENTS.md`：启动对账完成前不开自动派工（安全设计，不是 bug）

## 风险验证（修复前）

| # | 风险 | 结论 | 证据分级 |
|---|------|------|----------|
| R1 | 对账任一阶段抛异常后，是否仍可能设置 `IsReconciled=true` | **是** | **已由源码确认** |
| R2 | 是否仍可能启动自动派工循环 | **是** | **已由源码确认** |
| R3 | 是否会出现 Warning 后继续输出「对账完成」 | **是** | **已由源码确认** |

**衍生后果（合理推断）：** ① 失败后 `unfinished` 为空 → ② 可能误滚在途预记。锁定决策 D1「本轮立即停止」消除该路径。

**非全局失败（D8 已锁定）：**

- 单个已被可靠识别的账实不符 → 对应工位 Alarm，**不等于**全局对账失败。
- `SchedulerEnabled=false` 置 `IsReconciled=true`：故意短路，不开调度循环（保持）。
- `CancellationToken` 取消：正常停止重试，**不记**业务失败。

## UI 可观测性（修复前现状 → 目标）

| 能力 | 修复前 | 目标（D6） |
|------|--------|-----------|
| `IsReconciled` | 接口有；UI 无读 | 保持；失败期间必须为 false |
| 对账状态/失败原因 | 无 | 新增 `ReconciliationState`、`ReconciliationFailureReason` |
| 看板文案 | 无对账失败提示 | 「启动对账失败，自动派工已锁定：{原因}；系统将自动重试」；成功后恢复正常 |
| Growl | — | **禁止**循环/重复 Growl |
| Surface | — | **不新增** Surface |

## 已确认决策

### P0-3「启动对账失败仍开启自动派工」— 决策纪要（2026-08-04 锁定）

**底线：** 对账必须 fail-closed；成功前禁止状态推进循环与自动派工；外围 PLC/RCS 监控可继续；自动重试直至成功或应用停止。

| # | 决策 |
|---|------|
| D1 | **必须 fail-closed**。任一阶段失败后 `IsReconciled` 保持 false；禁止输出「对账完成」日志；本轮立即停止，不允许带着不完整结果继续后续阶段。 |
| D2 | 失败期间：**PLC 周期轮询继续**；**RCS 回调宿主、任务跟踪、告警接收继续**。`PositionScheduler` 的**状态推进循环**与**自动派工循环均不得启动**。不产生新的自动上下料任务（外围继续监控，调度器不开闸）。 |
| D3 | 对账成功前**绝对禁止**启动 `_dispatchTask`。`DispatchLoopAsync` **必须**增加 `IsReconciled` 防御检查。失败期间**不得积压**新的 `UploadRequested` 或下料队列请求。 |
| D4 | **自动重试**对账。配置 `App:Rcs:ReconcileRetryIntervalMs`，默认 **5000**，必须 **>0**，Options 启动校验失败（禁止静默降级）。不另开多个重试器；由 `PositionScheduler` 内**单一顺序循环**重试；**不设最大次数**，直到成功或应用停止。每次失败写 Warning，**不弹重复 Growl**。 |
| D5 | 本期**不新增**人工「重试对账」按钮；自动重试即恢复路径。未来人工入口另开 Issue。 |
| D6 | 不新增 Surface。`IPositionScheduler` 增加只读：`IsReconciled`、`ReconciliationState`（`Reconciling` / `Failed` / `Succeeded`）、`ReconciliationFailureReason`。既有看板/状态区显示「启动对账失败，自动派工已锁定：{原因}；系统将自动重试」；成功后恢复正常文案。日志须含失败阶段、重试次数、下一次重试时间。不用循环 Growl。 |
| D7 | 重试成功后**自动** `IsReconciled=true`；状态推进循环与 `_dispatchTask` **各启动一次**，禁止重复启动；`Reconciled` **仅首次成功开闸触发一次**；输出「启动对账完成，开始自动派工」。 |
| D8 | 以下任一视为失败并**立即终止本轮**：① 查询/绑定抛异常或无法获得可信完整集合；①b `QueryAsync` 抛异常、`Success=false`、响应无法解析或终态收口失败；② 陈旧预记查询或回滚失败；③ PLC 对账所需点位装载、fresh 读取或账实核对过程抛异常导致无法确认本轮完整完成。`CancellationToken` 取消不记业务失败，直接停止重试。单个可靠识别的账实不符仍置对应工位 Alarm，**不等于**全局失败。 |

**ADR：** 不新建。属实现回归到 §6.3 / CONTEXT 既有契约；自动重试为 Issue 级恢复策略，非新 ADR。

## 实现约束

- 复用 `tests/CncLoader.Core.Tests`；包仅限已有 NUnit 三件套；**不引入** mock 包、FlaUI、coverlet、STA 扩展、CI。
- 抽取小型对账门闩/协调器到 Core（或等价可测接缝）+ 手写 fake；避免整机 Host。
- 最小改动开闸/重试控制流；不借机重构整个 `PositionScheduler`。
- `ReconcileRetryIntervalMs`：缺失默认 5000；`<=0` 须 Validate 启动失败，禁止降级。
- 不处理其他 P0/P1/P2；不改 RCS 协议 / PLC 点位地址；不放宽 Alarm 粘滞（工位级账实 Alarm 规则保持）。
- 不新增人工重试按钮（D5）。

## Tracer Bullet / RED 测试接缝

**接缝（对齐 P0-1/P0-2）：**

1. Core 抽取可测协调器/门闩，覆盖：阶段短路（失败后不跑后续）、开闸条件、重试节拍决策、首次开闸幂等（循环只启一次 / `Reconciled` 一次）。
2. `ReconcileAsync`（或等价）返回结构化结果；失败立即终止本轮后续阶段。
3. `StartAsync`：未成功前不启 `_loopTask` / `_dispatchTask`；单一顺序循环按 `ReconcileRetryIntervalMs` 重试；成功路径按 D7 开闸一次。
4. `DispatchLoopAsync` 入口检查 `IsReconciled`（D3 防御）。
5. 测试工程继续引用 Common+Core；手写 fake；不强制整机 Host。

**RED 至少覆盖：**

1. ① / ①b / ② / ③ 任一失败时，后续阶段不执行。
2. 失败时 `IsReconciled == false`。
3. 失败时两个调度循环（状态推进 + 自动派工）均不启动。
4. `Query.Success == false` 视为失败。
5. 自动重试成功后只开闸一次。
6. 连续失败不会重复启动循环或重复触发 `Reconciled`。
7. 取消时停止重试，不记失败。
8. 配置缺失默认 5000；`<=0` 启动校验失败。
9. 失败原因和失败阶段可读取。
10. `DispatchLoopAsync` 对 `IsReconciled` 有防御门禁。

## GREEN 范围

- 对账 fail-closed + 阶段短路 + 单一重试循环 + 首次成功开闸。
- `IPositionScheduler` 状态字段 + 看板既有展示位文案（D6）。
- `DispatchLoopAsync` 防御门闩；失败期间不积压新自动派工请求（D3）。
- Options：`ReconcileRetryIntervalMs` + Validate。
- RED 1–10 全绿；`dotnet build CncLoader.sln` 0 警告 0 错误。
- 不重构整个调度器；不新增 Surface / 人工重试按钮 / Growl 风暴。

## 验收标准

- [ ] RED 1–10 全部绿
- [ ] 任一阶段失败：本轮后续阶段不执行；`IsReconciled=false`；无「对账完成」日志；双循环未启动
- [ ] `Query.Success=false` 记全局失败并短路
- [ ] 自动重试：单一循环、间隔配置、无限次直至成功或取消；连续失败不重复启循环、不重复 `Reconciled`、不弹循环 Growl
- [ ] 取消停止重试且不记业务失败
- [ ] 成功：`IsReconciled=true`；双循环各启一次；`Reconciled` 一次；输出「启动对账完成，开始自动派工」
- [ ] `DispatchLoopAsync` 含 `IsReconciled` 防御检查；失败期间不积压新 UploadRequested / 下料队列请求
- [ ] UI：既有展示位可见失败文案与原因；成功后恢复；无新 Surface
- [ ] `ReconcileRetryIntervalMs` 默认 5000；`<=0` 启动校验失败
- [ ] `dotnet test` 测试工程通过；`dotnet build CncLoader.sln` 0 警告 0 错误

## 验证

- `dotnet test tests/CncLoader.Core.Tests`
- `dotnet build CncLoader.sln`
- 模拟器或手写 fake：注入 ①/①b 失败 → 观察锁定与重试；恢复后仅一次开闸
- 真现场故障注入列为后续非阻塞回归

## 范围外

- 不建父 PRD、不新建 ADR
- 不引入 FlaUI / coverlet / CI / mock 框架 / STA 扩展
- 不新增人工「重试对账」按钮（另开 Issue）
- 不处理其他分级问题；不实现 `Plc.MaxReconnectAttempts`

## 依赖

- 无 — 决策已锁定；可立即开始（无父 PRD）
- 契约 SSOT = 本 Issue「已确认决策」+ `docs/客户端开发文档.md` §6.3 + `CONTEXT.md`
- 允许测试依赖：仅现有 NUnit 三件套

## 阻塞项

- 无 — 可立即开始

## Comments

> 此内容由 AI 在分拣期间生成。

### 决策纪要（2026-08-04）

瑞小米锁定 D1–D8：

1. **fail-closed + 阶段短路**：任一阶段失败 → `IsReconciled` 保持 false、禁「对账完成」日志、本轮立即停、不带着残缺结果继续后续阶段。
2. **外围继续 / 调度器不开闸**：PLC 轮询、RCS 回调/跟踪/告警继续；`PositionScheduler` 状态推进与自动派工双循环均不启动；不产生新自动上下料任务。
3. **派工双保险**：成功前禁止启 `_dispatchTask`；`DispatchLoopAsync` 增加 `IsReconciled` 防御；失败期间不积压 UploadRequested / 下料队列。
4. **自动重试**：`App:Rcs:ReconcileRetryIntervalMs` 默认 5000（>0，ValidateOnStart）；调度器内单一顺序循环；无最大次数；Warning 日志、无重复 Growl；无人工重试按钮。
5. **可观察**：扩展 `ReconciliationState` / `ReconciliationFailureReason`；看板既有区显示锁定文案；日志含阶段/次数/下次重试时间；无新 Surface。
6. **成功开闸一次**：自动 `IsReconciled=true`；双循环各启一次；`Reconciled` 仅一次；打「启动对账完成，开始自动派工」。
7. **失败边界**：①/①b（含 `Success=false`/不可解析/收口失败）/②/③「无法完整核对」= 全局失败；取消≠失败；单个可靠账实不符=工位 Alarm≠全局失败。

状态：`needs-triage` → `ready-for-agent`。不建 ADR/PRD。

### RED 证据（2026-08-04）

> 此内容由 AI 在分拣期间生成。

**状态：** 保持 `ready-for-agent`（本轮仅 RED，未进入 GREEN，未关闭）。

**测试接缝（生产行为未接线）：**

- 新增 `src/CncLoader.Core/State/StartupReconcileCoordinator.cs`
- 刻意镜像当前 `PositionScheduler.ReconcileAsync` / `StartAsync` 缺陷：阶段失败后继续、始终返回成功、`MapQueryResult(!Success)` 当可继续、`DecideIsReconciled` 无条件 true
- **未**修改 `PositionScheduler` / UI / Options；未引入 fail-closed / 重试 / 开闸修复

**新增测试文件：** `tests/CncLoader.Core.Tests/State/StartupReconciliationTests.cs`

**测试名称（7）：**

1. `RunAsync_阶段一失败时_后续阶段不得执行`
2. `RunAsync_阶段一b失败时_后续阶段不得执行`
3. `RunAsync_阶段二失败时_阶段三不得执行`
4. `RunAsync_阶段三失败时_本轮应对账失败`
5. `DecideIsReconciled_任一阶段失败后_应保持未开闸`
6. `MapQueryResult_QuerySuccess为false时_应判定对账阶段失败`
7. `RunAsync_阶段一b返回显式失败时_后续阶段不得执行`

**最小命令：**

```text
dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj --filter "FullyQualifiedName~Reconciliation"
```

**结果：** 失败 7，通过 0，总计 7。

**全量工程：**

```text
dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
```

**结果：** 失败 7，通过 22，总计 29（P0-1/P0-2 既有用例未被削弱）。

**关键失败信息 → 证明的缺陷：**

| 失败现象 | 证明 |
|----------|------|
| ① 失败后仍调用 ①b/②/③；`Succeeded=True` | 阶段不短路，带着不完整结果继续 |
| ①b/② 失败后仍执行后续阶段 | 同上 |
| ③ 失败后 `Succeeded=True`、`FailedPhase=null` | 失败不向上反映为对账失败 |
| `DecideIsReconciled` 在失败轮次仍返回 true | 镜像 StartAsync 无条件 `IsReconciled=true` |
| `MapQueryResult(Success=false)` 仍 `Succeeded=True` | Query 失败被当成可继续 |

**本轮未覆盖的 Issue RED 5–10：** 双循环不启动、自动重试开闸幂等、取消语义、`ReconcileRetryIntervalMs` 配置校验、失败原因可读字段完整接线、`DispatchLoopAsync` 防御门禁（留待后续 RED/GREEN）。

### GREEN 第一阶段证据（2026-08-04）

> 此内容由 AI 在分拣期间生成。

**状态：** 保持 `ready-for-agent`（未关闭；自动重试 / Options / UI 未做）。

**生产接线路径：**

```text
PositionScheduler.StartAsync
  → ReconcileAsync → StartupReconcileCoordinator.RunAsync(①/①b/②/③)
  → 失败：IsReconciled=false；Warning(阶段+原因)；不启 Loop/_dispatchTask；不触发 Reconciled；无「对账完成」日志
  → 成功：IsReconciled=true；Reconciled×1；「对账完成」；StateLoopStartCount++ / DispatchLoopStartCount++ 各启一次
DispatchLoopAsync / ProbeDispatchOnce / TryDispatchUpload / DispatchOne：IsReconciled 防御门禁
SettleTerminalTasksOnReconcileAsync：Query.Success=false → MapQueryResult 失败（不再跳过继续）
```

**失败时关闭的门：**

- `IsReconciled=false`
- 状态推进循环不启动（`StateLoopStartCount=0`）
- `_dispatchTask` 不启动（`DispatchLoopStartCount=0`）
- `Reconciled` 不触发
- 不输出「启动对账完成，开始自动派工」
- 派工探测不得消费队列 / 不得调用 `DispatchTransit`

**测试命令与结果：**

```text
dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj --filter "FullyQualifiedName~Reconciliation"
→ 失败 0，通过 10（原 7 协调器 + 3 真实接线）

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 32（含原 22 个 P0-1/P0-2）

dotnet build CncLoader.sln
→ 0 警告，0 错误
```

**仍未实现：** 自动重试循环、`ReconcileRetryIntervalMs` Options 校验、`ReconciliationState` / UI 看板文案、人工重试按钮。

### GREEN 第二阶段证据（2026-08-04）

> 此内容由 AI 在分拣期间生成。

**状态：** 保持 `ready-for-agent`（未关闭；看板 UI 接线未做；无人工重试按钮）。

#### RED→GREEN 证据

**RED（实现前，功能未接线）：**

```text
dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj --filter "FullyQualifiedName~Reconciliation|FullyQualifiedName~对账重试|FullyQualifiedName~AppOptionsTests"
→ 失败 7，通过 16（关键：WaitingForRetry/FailureReason 未置；重试 Delay 永不进入；<=0 配置未 Validate）
```

关键失败原因：
- `对账重试间隔<=0` 未抛 `OptionsValidationException`
- 失败后 `ReconciliationState` 仍为 `Reconciling`、`FailureReason=null`
- 等待重试/连续失败用例因无重试循环对 `DelayOverride` 超时

**GREEN（实现后）：**

```text
dotnet test ... --filter "FullyQualifiedName~Reconciliation"
→ 失败 0，通过 17

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 43（原 32 + 本阶段新增）

dotnet build CncLoader.sln
→ 0 警告，0 错误
```

#### 配置路径与默认值

- 路径：`App:Rcs:ReconcileRetryIntervalMs`
- 默认：`5000`（`RcsOptions` 属性默认值；缺失配置不降级为非法值）
- 校验：`AddCncCommon` → `Validate(... > 0)` + `ValidateOnStart`；`<=0` 启动抛 `OptionsValidationException`，禁止静默改回默认
- `appsettings.json` 增加示例值 `5000`

#### 重试生命周期

```text
PositionScheduler.StartAsync
  → 首次 TryReconcileOnceAsync（同步于 StartAsync，不无限阻塞）
  → 成功：TryOpenGateAfterSuccess（幂等）
  → 取消：直接 return（不记 WaitingForRetry / FailureReason）
  → 失败：置 WaitingForRetry + FailureReason；EnsureReconcileRetryLoopStarted（至多 1 个后台任务）
       └─ ReconcileRetryLoopAsync：Delay(ReconcileRetryIntervalMs) → 再对账 → 成功开闸 / 失败继续 / 取消退出
StopAsync：Cancel CTS + ObserveAsync(_loopTask/_dispatchTask/_reconcileRetryTask)
```

- 无并行对账；无限重试至成功或应用停止
- 每次失败 Warning（阶段/原因/间隔/已失败次数）；无 Growl
- Cancellation：不记 Warning 业务失败、不继续重试

#### 状态转换

`NotStarted → Reconciling → WaitingForRetry ⇄ Reconciling → Succeeded`

- 取消：不伪装失败；首次取消保持 `Reconciling`（未写入 WaitingForRetry）；等待重试中取消保持 `WaitingForRetry` + 原 FailureReason
- `SchedulerEnabled=false`：短路 `IsReconciled=true` / `Succeeded`（不开双循环，既有行为）

#### 幂等开闸证明

`TryOpenGateAfterSuccess`：`Interlocked.CompareExchange(_gateOpened,1,0)` 门闩

- `IsReconciled` false→true 一次
- 状态推进 / dispatch 循环各启一次
- `Reconciled` 事件一次；「启动对账完成」日志一次；FailureReason 清空
- 测试：`TryOpenGateAfterSuccess_重复调用_只开闸一次`；`第一次失败第二次成功_自动重试并仅开闸一次`

#### 尚未完成（第二阶段结束时）

- 看板既有展示位接线 D6 文案（「启动对账失败，自动派工已锁定：{原因}；系统将自动重试」）
- 人工重试按钮（D5 明确不做）
- 真实现场 DB/RCS/PLC 故障注入联调

### GREEN 第三阶段证据（2026-08-04）— 看板状态接线

> 此内容由 AI 在分拣期间生成。

**状态：** 保持 `ready-for-agent`（未关闭；待人工运行验收布局/DPI/长时间重试）。

#### 状态通知契约

- `IPositionScheduler.ReconciliationStateChanged` → `EventHandler<ReconciliationSnapshot>`
- `ReconciliationStatePublisher`：状态或失败原因变化才通知；相同快照不重复；订阅方异常不破坏调度
- 取消不发布 WaitingForRetry；成功清空 FailureReason 并通知
- 保留 `Reconciled` 一次语义；Core/Communication **不**引用 WPF Dispatcher

#### ViewModel / XAML 接线位置

| 项 | 选择 | 理由 |
|----|------|------|
| ViewModel | `DashboardViewModel` | 已有 `IPositionScheduler`；监控看板是 D6 展示位 |
| XAML | `PageTemplates.xaml` → `DashboardViewModel` DataTemplate | 既有看板模板 |
| 区域 | KPI 与产线流之间的「启动对账状态」条（`DashPanel`） | 不重排整板；不占 KPI/告警/机台 Alarm 槽位 |
| 语义区分 | Warn/Run/Ok/Idle token；**不用** `AlarmBrush`/`DashAlarmBrush` | 与工位 Alarm、未处理告警 KPI、PLC 在线、RCS 连接灯分离 |

绑定属性：`ReconcileTitle` / `ReconcileSubText` / `ReconcileBrushKey` / `ReconcileSoftBrushKey` / `ReconcileDetailToolTip` / `IsReconcileGateOpen`  
展示逻辑：`ReconciliationStatusBinder` + `ReconciliationStatusPresentation`（纯 Core，XAML 无状态机 Converter）。

#### UI 文案映射

| State | 主文案 | 副文案 | Brush |
|-------|--------|--------|-------|
| NotStarted | 启动对账未开始 | 自动派工尚未开启 | Idle |
| Reconciling | 正在执行启动对账 | 自动派工已锁定 | Run |
| WaitingForRetry | 启动对账失败，正在重试 | 自动派工已锁定：{安全原因}；系统将自动重试 | Warn |
| Succeeded | 启动对账完成 | 自动派工已开启 | Ok |

副文案 `TextWrapping` + `TextTrimming` + `MaxHeight=36`；完整原因 ToolTip。无 Growl / 无新 Surface / 无 WPF-UI 新控件。

#### 测试及 build

```text
dotnet test ... --filter "FullyQualifiedName~Reconciliation"
→ 失败 0，通过 32

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 58

dotnet build CncLoader.sln
→ 0 警告，0 错误
```

静态检查：状态条无硬编码色值；`AutomationProperties.Name="启动对账状态"`；UI 未赋值 `IsReconciled`；对账路径无新增 Growl；Core/Communication 无 WPF Dispatcher。

#### 尚待人工运行验证

- 真实窗口布局 / DPI / 长失败原因换行观感
- 后台重试过程中看板实时刷新观感
- 真实现场 DB/RCS/PLC 故障注入联调
- 人工重试按钮（D5 不做）

### GREEN 硬化补丁（2026-08-04）— Stop 竞态 / FailureReason / DashboardVM

> 此内容由 AI 在分拣期间生成。

**状态：** 保持 `ready-for-agent`（未关闭）。

#### 审查 P1：StopAsync 与重试开闸竞态

修复前：`TryOpenGateAfterSuccess` 不检查 stopping；Stop 仅 Cancel CTS 后仍可完成开闸（`IsReconciled`/双循环/`Reconciled`）。

#### RED 复现

```text
dotnet test ... --filter "FullyQualifiedName~StopAsync先于开闸|FullyQualifiedName~WaitingForRetry进入Reconciling|FullyQualifiedName~DashboardViewModelReconciliation"
→ 失败 2：Stop 后仍 IsReconciled=true 且双循环=1；Reconciling 快照仍带旧 FailureReason
（DashboardViewModel 3 项已能绿，因不依赖本次生产缺陷）
```

确定性接缝：`BeforeOpenGateClaim` 内同步 `EnterStopping()`（无 Sleep/无概率并发）。

#### 同步策略与线性化点

- `_lifecycleLock` + `_stopping`
- **线性化点：** 取得 `_lifecycleLock`——`EnterStopping`（StopAsync 首步）或 `TryOpenGateAfterSuccess` 内整段声明
- 锁内：检查 `_stopping` → 置门闩/`IsReconciled`/`Succeeded`/清空原因 → **同时**创建双循环 Task（禁止半开闸）
- 锁外：`PublishReconcileState` / `Reconciled` / 成功日志（防订阅方重入死锁）
- 禁止「CAS 后再把门闩改回 0」

`WaitingForRetry → Reconciling`：进入时 `_reconciliationFailureReason = null` 后立即 Publish。

#### GREEN 结果

```text
dotnet test ... --filter "FullyQualifiedName~Reconciliation" → 0 失败 / 38 通过
dotnet test tests/CncLoader.Core.Tests/... → 0 失败 / 64 通过
dotnet build CncLoader.sln → 0 警告 / 0 错误
```

新增：`StopAsync先于开闸_*`、`开闸完整完成后Stop_*`、`WaitingForRetry进入Reconciling_*`、`DashboardViewModelReconciliationTests`（真实 VM 属性）。

#### 三项暂缓（后续 P1/P2 候选，本补丁不改）

1. 首次对账仍在 `StartAsync` 同步 await（挂起时拖死后序 HostedService）
2. Core 中中文 UI 文案 / Brush key 分层
3. `_cts` 链接 Host `StartAsync` 临时 token 的既有模式

### 成功路径人工验收（2026-08-04）

> 此内容由 AI 在分拣期间生成。

**状态：** 保持 `ready-for-agent`（未关闭；本轮仅双模拟器成功路径，未做失败注入）。

#### 启动方式

```text
dotnet run --no-build --project src/CncLoader.App
```

项目根：`C:\Users\75626\Desktop\CNC`。无临时故障注入环境变量；未改代码/配置；未强杀。

#### 双模拟器配置结论

- `Plc.UseSimulator=true`、`Rcs.UseSimulator=true`
- `WaterMonitorEnabled=false`、`InventoryAutoEnabled=false`
- 数据库目标：`localhost` / `cnc_auto`（本地/测试）
- 启动日志确认：RCS 模拟器已监听、FINS 模拟 PLC 3/3 在线、水位/自动盘点未启用

#### UI 人工检查结果（操作者确认全部通过）

1. 监控看板可见「启动对账状态」条（KPI 与产线流之间）
2. 主文案：`启动对账完成`；副文案：`自动派工已开启`
3. 成功色（非 Alarm 红）；文案完整无重叠/裁切；缩放后仍可读
4. 切页返回后状态保持成功；约 10s 无闪回「正在重试」
5. RCS 页已勾选「暂停自动派工」；正常关窗

#### 日志结果（相对启动前基线字节偏移分析）

| 检查项 | 结果 |
|--------|------|
| `启动对账完成，开始自动派工` | **恰好 1 次**（2026-08-04 14:26:53.688） |
| `启动对账失败，自动派工已锁定` | **0 次** |
| 关闭过程重复开闸 | **无** |
| 未处理异常 / 未观察 Task 异常 / 启动失败 | **0** |
| 进程退出 | `exit_code=0`（正常关闭） |

#### 范围说明

- 本轮**仅**成功路径人工验收通过。
- **失败→成功同进程恢复**仍由自动测试覆盖（见既有 `PositionSchedulerStartupTests` / Reconciliation 套件）；仓库无安全、无改配置的同进程失败注入恢复手段，失败锁闸人工验收另开有条件步骤。

## 验收关闭（2026-08-04）

**状态：** `ready-for-agent` → `closed`

**最终回归命令与结果：**

```text
dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj --filter "FullyQualifiedName~Reconciliation"
→ 失败 0，通过 38，跳过 0，总计 38

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 64，跳过 0，总计 64

dotnet build CncLoader.sln
→ 0 警告，0 错误

git diff --check
→ 无 whitespace error（exit 0）
```

**双模拟器人工成功路径：** 通过（看板主文案「启动对账完成」、副文案「自动派工已开启」；成功日志恰好 1 次；失败锁闸日志 0；正常关窗 `exit_code=0`）。详见上文「成功路径人工验收」。

**未执行人工失败注入的安全理由：**

- 运行时 RCS `BaseUrl` 由 `MAS_AUTO_WORKLINE_AGV` 覆盖 appsettings/环境变量，`UseSimulator=false` 存在误连真实 RCS 风险；
- ①b `queryTask` 仅在存在未完结任务时触发，否则跳过 Query，失败注入可能未命中目标阶段；
- 决策：失败锁闸、自动重试、失败→成功、取消、幂等开闸、Stop 竞态均以自动测试证据验收。

**失败→成功恢复：** 由 `PositionSchedulerStartupTests` / Reconciliation 套件覆盖（含 `第一次失败第二次成功_自动重试并仅开闸一次` 等）。

**验收项核对（1–14）：** 全部满足（自动测试 + 成功路径人工验收 + 代码审查：无新 Surface / 无对账路径 Growl / 无 WPF-UI 外壳改造；测试接缝保持 `internal`）。

**未验证项：**

- 真实 DB / RCS / PLC 故障联调
- 长时间无限重试观感
- 首次对账调用长期挂起对后续 HostedService 的影响
- DPI / 多显示器全面回归

**后续候选（未修复，不阻塞关闭）：**

- P1：首次对账仍在 `StartAsync` 同步 await，长期挂起会阻塞后续 HostedService
- P2：Core 中存在 UI 文案与 Brush key
- P2：生命周期 CTS 模式可进一步整理

**结论：** 本 Issue 验收标准已满足，关闭。人工失败注入不阻塞关闭。

## 分拣笔记（2026-08-04）

**类别 / 状态：** bug → `closed`

**已关闭：** D1–D8 全部锁定；RED 1–10 与实现约束已对齐；无决策阻塞。

**Agent 接手前置：** 读本 Issue「已确认决策」+ RED 清单 + §6.3 / CONTEXT；先写 Core 门闩/协调器 RED，再接线 `PositionScheduler`。

---

## Agent 简报

**类别：** bug  
**摘要：** 启动对账必须 fail-closed：阶段失败立即终止本轮、保持未开闸并自动重试；成功前禁止状态推进与自动派工；外围 PLC/RCS 监控可继续。

**当前行为：**
`ReconcileAsync` ①/①b/②/③ 各自 catch 只打 Warning 并继续后续阶段；①b `Success=false` 跳过。`StartAsync` 无条件 `IsReconciled=true`、打「对账完成」、启动 `_loopTask`+`_dispatchTask`。`DispatchLoopAsync` 无 `IsReconciled` 门闩。UI 不可见对账失败。

**期望行为：**
- D1：阶段失败 → `IsReconciled=false`、禁「对账完成」、本轮短路后续阶段。
- D2：PLC 轮询 + RCS 回调/跟踪/告警继续；调度器双循环不启动；无新自动上下料。
- D3：成功前不启 `_dispatchTask`；`DispatchLoopAsync` 检查 `IsReconciled`；失败期间不积压派工请求。
- D4：`ReconcileRetryIntervalMs` 默认 5000（>0 Validate）；单一顺序重试至成功或取消；Warning、无 Growl。
- D5：无人工重试按钮。
- D6：`ReconciliationState`（Reconciling/Failed/Succeeded）+ `ReconciliationFailureReason`；看板文案「启动对账失败，自动派工已锁定：{原因}；系统将自动重试」；日志含阶段/次数/下次重试时间。
- D7：成功自动开闸一次：双循环各启一次、`Reconciled` 一次、「对账完成」日志。
- D8：①/①b（含 Query 失败）/②/③ 无法完整核对 = 全局失败；取消不记失败；单个账实不符=工位 Alarm。

**关键接口：**
- `PositionScheduler.StartAsync` / `ReconcileAsync` / `DispatchLoopAsync` / `LoopAsync`
- `IPositionScheduler`：`IsReconciled`、`ReconciliationState`、`ReconciliationFailureReason`、`Reconciled`
- `RcsOptions.ReconcileRetryIntervalMs` + Options Validate
- Core 对账门闩/协调器（名称以实现为准）
- 看板既有状态展示位（无新 Surface）

**测试工程：**
- `tests/CncLoader.Core.Tests`；NUnit；手写 fake；无 mock/FlaUI/coverlet/STA/CI
- RED 1–10 见上文

**验收标准：** 见正文清单（RED 全绿 + build 0 警告 0 错误 + 开闸幂等 + UI 文案）

**范围外：** 人工重试按钮、ADR/PRD、其他 P0、MaxReconnectAttempts、整调度器重构

**实现前必读 / PRD 绑定：** 无父 PRD。SSOT = 本 Issue 决策纪要 D1–D8 + RED/GREEN + `docs/客户端开发文档.md` §6.3 + `CONTEXT.md`。
