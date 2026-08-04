# P0-5｜调度路由未过滤软删配置，可能向已下线线体/机台派工

> 此内容由 AI 在分拣期间生成。

Type: bug  
Priority: P0  
Status: ready-for-agent  
Labels: dispatch, routing, soft-delete, equipment, safety, ready-for-agent  
Feature: p0-5-soft-deleted-routing-protection

---

## 1. 问题陈述

调度关键路径在解析「机台 → 工序 → 线体」时不检查配置表 `STATE`，且进程内 `_lineCache` / 点位缓存在软删/禁用后不会自动失效。管理列表已过滤 `STATE='0'`，但自动派工仍可能：

1. 使用已禁用/软删的 WorkLine / Craftwork / Equipment 生成 taskId 并下发 RCS；
2. 在 P0-2「先预记后下发」窗口内，配置被运维软删后仍继续下发（无最终活动门禁）；
3. Redo/Redispatch 按落库 From/To 原样重发，不复核配置活动态。

原始体检结论（C5 / 分级 P0-5）：`GetWorkLineByEquipmentAsync` 查 Equipment/Craftwork/WorkLine 均无 `State == Active`；全局无 EF QueryFilter（P2-1，本期不混入）。

**对照契约：**

- `CONTEXT.md` / `AGENTS.md`：软删用 `STATE='0'/'1'`（0 启用 / 1 禁用），查询须过滤。
- `docs/sql/cnc_schema.sql`：配置表 `STATE` COMMENT 多为 `0=启用 1=禁用`。
- 管理页列表普遍 `Where(State == "0")`，调度反查未对齐。

---

## 2. 实体活动条件表

> 禁止混表语义。下列「活动」仅指**配置软删/启用位**；与告警 STATE、槽位 SLOT_STATE、RCS TASK_STATE 无关。

| 实体 / 表 | 字段 | 值语义（源码+SQL） | 软删/禁用操作实际改什么 | 管理列表是否过滤 | 调度/路由查询是否过滤（修复前） | 备注 |
|---|---|---|---|---|---|---|
| WorkLine / `MAS_AUTO_WORKLINECONFIGS` | `STATE` | `'0'`=活动；`'1'`=禁用/软删 | `DeleteAsync`→`State=Disabled`；`SaveAsync`→`ToState(Enabled)` | 是 | **否**（`GetWorkLineByEquipmentAsync`） | UI Enabled 映射到 STATE |
| Craftwork / `MAS_AUTO_WORKLINE_CRAFTWORK` | `STATE` | 同上 | 同上 | 是 | **部分** | 编辑可禁用而子机台仍活动 |
| Equipment / `MAS_AUTO_WORKLINE_EQUIMENT` | `STATE` | 同上 | `DeleteAsync`→`State=1` + 级联软删 Positions | 是 | **否**（源机台反查） | `EQUIMENT_STATE` 不是软删位 |
| EquipmentPosition / `MAS_AUTO_EQUIMENT_POSITION` | `STATE` | 同上 | 机台删除级联置 `'1'` | `GetPositionsAsync` 滤 | 调度位来自 PLC 点位缓存 | — |
| PlcPoint / `MAS_AUTO_PLC_POINT` | `STATE` | 同上 | `Delete`→`State=1` | 是 | **是**（装载时）；运行期不重载 | 最终门禁须再读权威配置 |
| LOCATION_MAP / `MAS_AUTO_LOCATION_MAP` | `STATE` | 同上 | `DeleteAsync`→`State=1` | 是 | **是**（`Resolve*`） | 缺失与禁用均不可派工 |
| Frame / `MAS_AUTO_FRAME` | `STATE` | 同上 | `SoftDeleteAsync`→`'1'` | 是 | 经 Bind/Map 间接 | — |
| FrameBind / `MAS_AUTO_FRAME_BIND` | `STATE` | 同上 | 多数路径物理删（P2-2） | 读侧滤 | **是**（读侧） | 本期不改物理删语义 |
| AlarmEvent.`STATE` / FrameSlot.`SLOT_STATE` | — | **不同语义** | — | — | — | **禁止套用配置 STATE 规则** |

**已锁定（D1）：** 配置实体活动条件为 `STATE == "0"`；`null`/空/未知 STATE fail-closed；UI 列表过滤不等于调度安全；不依赖导航属性自动过滤；禁止 `(1,"LINE")` 等硬编码回退。

**全局 QueryFilter：** 未配置。本期不引入（D9 / P2-1）。

---

## 3. 自动上料路由链（修复前）

```
DispatchLoopAsync（须 IsReconciled && !IsAutoDispatchPaused）
  └─ AllocateUploadsAsync → TryDispatchUploadAsync
       ├─ ResolveUploadPlanAsync（LOCATION_MAP / FrameBind 部分有滤）
       ├─ ResolveLineAsync → GetWorkLineByEquipmentAsync（无 STATE）+ 回退 LINE
       ├─ ReservationFirstDispatcher：ReserveTake → DispatchTransit（无活动复检）
       └─ 或命名区直接 DispatchTransitAsync
```

---

## 4. 自动下料 / 直接交接链（修复前）

```
EnqueueUnloadAsync → ResolveLineAsync（无 STATE）入队
DispatchOneAsync → ResolveUnloadTargetAsync（下一工序部分过滤；源/父线体不完整）
  → ReservationFirstDispatcher 或直接 DispatchTransit
```

直接交接经 `_expectedInbound` 预登记，属新外部执行，须纳入统一门禁（D2/D4）。

---

## 5. 手动派工 / Redo / Redispatch（修复前）

- 手动：自由文本 From/To，不查 LOCATION_MAP 活动态；WorkLineId 来自活动列表元数据。
- Redo/Redispatch：按落库参数重发，不复核配置 STATE。

**已锁定（D4）：** 以上全部为新外部执行，必须经最终门禁；手动 From/To 须解析到受管理的活动配置。

---

## 6. 已锁定决策 D1–D10

### D1｜活动路由的精确定义 — **已锁定**

新派工引用的每个配置实体必须满足真实活动条件。统一配置表语义：

- `STATE == "0"` → 活动
- `STATE == "1"` → 禁用/软删

须按真实字段分别校验：WorkLine、Craft、Equipment、LOCATION_MAP、PlcPoint、FrameBind，以及新派工实际引用的其他配置实体。

约束：

1. null、空值、未知 STATE 一律 fail-closed。
2. 不得把告警 STATE、SLOT_STATE 等套用此规则。
3. UI 列表已过滤 ≠ 调度安全。
4. 不依赖导航属性自动过滤。
5. 不允许默认 `(1, "LINE")` 或其他硬编码路由回退。
6. 路由缺失与路由已禁用都属于不可派工。

### D2｜只阻断新的外部执行 — **已锁定**

保护范围：所有尚未向 RCS 发出的新执行。

继续允许：已 Dispatched/Executing 的状态回调；completed/failed/cancelled 收口；PLC 复核；Confirm/Rollback；启动对账读取历史任务。禁止因配置后来软删而使已执行任务无法收口。

仍视为新外部执行、必须重新校验：新自动上/下料；直接交接；手动派工；Redo；Redispatch；本地 Created 但尚未真正下发的任务。

### D3｜父子与端点必须全活动 — **已锁定**

完整路由链须全部活动：源 Equipment/Craft/WorkLine；目标 Equipment/Craft/WorkLine；From/To LOCATION_MAP；使用的 PlcPoint；使用的 FrameBind/料架绑定；其他真实依赖。

任一项已禁用、不存在、父子关系异常、指向禁用父项 → 拒绝新派工。不能只校验末端 Point/LocationMap，也不能因子项仍启用而忽略父 WorkLine/Craft 已禁用。

### D4｜所有新派工入口统一门禁 — **已锁定**

适用于：自动上料、自动下料、直接交接、手动自由文本 From/To、Redo、Redispatch。

手动：From/To 必须解析到受管理的活动配置；UI 下拉只是体验层；参数直传也须经最终门禁。RCS 连接测试/诊断若不创建搬运任务，可保留为独立诊断，不与生产派工混用。

Redo/Redispatch：重新向 RCS 发送即新执行；路由已下线时明确拒绝；不修改原历史任务记录；不清除原失败/终态证据；不因拒绝创建新的可执行 RCS 任务。

### D5｜缓存不是安全权威 — **已锁定**

`_lineCache`、`_positions` 等仅用于候选生成/性能优化。最终活动判断必须读取权威当前配置。软删后无需重启即可阻止新下发。最终校验失败时淘汰/刷新相关缓存。缓存刷新失败时 fail-closed。不新增未经验证的 TTL 作为唯一安全措施。

### D6｜预记前与下发前双门禁 — **已锁定**

第一道（预记前）：路由无效 → 不预记、不创建 RCS 任务、不写 POS_TEST_START，返回 RouteUnavailable。

第二道（预记成功后、调用 RCS 前）：再读权威配置；仍活动才允许调用 RCS；已失效则不调用 RCS，并用 P0-2 既有补偿路径回滚槽位预记、taskId 关联、本地 Pending/Dispatched 状态及其他前置副作用。

并发：本期不能把 DB 软删与外部 HTTP 做成同一事务；至少保证「软删发生在最终门禁之前」一定拒发；最终门禁紧邻 RCS 调用；同进程配置禁用路径如有可复用生命周期锁可用于线性化；不要在未分析前锁表或持 DB 事务跨 HTTP。「最终检查后、HTTP 调用前被外部 DB 直接修改」列为残余限制。

### D7｜最终门禁位置与返回契约 — **已锁定**

增加统一路由可用性验证契约（名称沿用项目风格，例如 `IRoutingAvailabilityValidator`；是否抽取由 RED 阶段基于真实代码决定）。

最终门禁位于不可绕过的生产发送边界：`ReservationFirstDispatcher` 调用 `IRcsTaskService` 前；Redo/Redispatch 真实发送边界；手动派工真实发送边界。

结果至少区分：`Available`、`EquipmentDisabled`、`CraftDisabled`、`WorkLineDisabled`、`PointDisabled`、`LocationMapDisabled`、`FrameBindDisabled`、`NotFound`、`InvalidRelationship`、`ConfigurationUnavailable`。

禁止：返回默认 LINE；返回第一条任意线体；只记 Warning 后继续；由 UI 自行判断后直接下发。

### D8｜拒发反馈 — **已锁定**

自动调度：Warning 汇总/限频；不弹 Growl；不置工位 Alarm；不写 POS_TEST_START；不预记或及时回滚；当前 tick 跳过该候选。

手动/Redo/Redispatch：返回明确失败；UI Warning/Error，不显示 Success；文案表达「路由配置已禁用或不可用」；不泄露完整内部配置或敏感 RCS 地址。

缓存失效：记录一次可定位 Warning；避免每 tick 同内容刷屏。

### D9｜不引入全局 QueryFilter — **已锁定**

本期只修：调度关键查询、路由验证、新执行最终门禁、必要的缓存失效。不增加全局 EF QueryFilter。不建 ADR/PRD。P2-1 继续作为独立架构议题。

### D10｜异常数据与历史任务 — **已锁定**

新派工：孤儿 Equipment、禁用父 WorkLine/Craft、未知 STATE、缺 Point/Map/Bind、无法解析自由文本路由 → 全部 fail-closed。

历史任务：已 Dispatched/Executing 回调继续；Confirm/Rollback 按 taskId/方向收口；不要求历史配置重新启用；对账可读取禁用配置关联的历史任务，但不得用它创建新执行。

缓存中的旧候选可被读到，但最终门禁必须拒绝；拒绝后应从候选缓存淘汰/刷新。

---

## 7. 错误时序（须被测试证伪）

### 时序 A（TOCTOU：预记后软删）

```
配置查询返回活动
  → 槽位预记成功
  → 运维软删/禁用线体或机台
  → 第二道门禁读权威配置 → RouteUnavailable
  → 不调用 RCS，回滚预记（P0-2 补偿）
```

### 时序 B（查询未过滤）

```
调度查询未过滤 STATE（修复前）
  → 得到软删父/子配置
  → 预记并下发
```
修复后：第一道门禁即拒，不预记。

### 时序 C（缓存粘滞）

```
缓存仍有旧路由
  → 最终门禁读权威配置拒绝
  → 淘汰/刷新相关缓存
  → 本 tick 跳过（自动）
```

---

## 8. 与 P0-2 / P0-3 的关系

- 保持 ADR-0002：先预记后下发；失败回滚。D6 第二道失败走同一补偿路径。
- 保持 P0-3：对账未完成不开自动派工；对账可读历史任务，不得据此创建新执行（D2/D10）。
- 不得削弱 P0-2 的 taskId 一致与并发选槽语义。

---

## 9. 已执行任务收口边界

| 动作 | 是否新外部执行 | 行为 |
|---|---|---|
| 回调 / query 终态 | 否 | 继续 |
| Confirm / Rollback | 否 | 按 taskId/方向继续 |
| PLC 复核 / POS_TEST_START（已在途收口） | 否 | 继续；不因配置软删主动跳过收口 |
| 新自动/手动派工 | 是 | 双门禁 |
| Redo / Redispatch | 是 | 门禁拒绝则不重发 |
| Created 未下发 | 是 | 下发前须校验 |

---

## 10. UI / 日志目标（对齐 D8）

| 路径 | 目标 |
|---|---|
| 自动 | 限频 Warning；无 Growl；无 Alarm；跳过候选 |
| 手动/Redo/Redispatch | 明确失败；Warning/Error；无 Success；文案「路由配置已禁用或不可用」 |
| 缓存失效 | 一次可定位 Warning |

---

## 11. 测试接缝与约束

- 直接打真实查询 Service / Dispatcher / Redo / 手动命令。
- 使用**手工 fake**（手写测试替身）。
- 预记后禁用用 TCS/gate 确定性交错；不使用 `Thread.Sleep`。
- 不访问真 RCS / PLC / MySQL。
- 不引入 mock 包或 EF InMemory。
- 真 MySQL 并发作为后续验证。

当前调度测试中 `FakeEquipment.GetWorkLineByEquipmentAsync` 恒返回 `(1,"LINE")`，实现时须替换/扩展为可注入活动态场景。

---

## 12. RED 顺序（已锁定）

### 第一组——查询与解析

| # | 场景 | 归属 |
|---|---|---|
| R1 | Equipment `STATE=1` → `GetWorkLineByEquipmentAsync` 返回不可用 | 仓储查询 |
| R2 | Equipment 活动、Craft 禁用 → 不可用 | 仓储查询 |
| R3 | Equipment/Craft 活动、WorkLine 禁用 → 不可用 | 仓储查询 |
| R4 | 父子缺失/关系异常 → 不可用 | 仓储查询 |
| R5 | 全部活动 → 返回真实 WorkLine | 仓储查询 |
| R6 | 禁止 `(1,"LINE")` 默认回退 | 领域 |
| R7 | NextProcess 查询不返回禁用 Equipment/Craft/WorkLine | 仓储查询 |
| R8 | LocationMap/Point/FrameBind 原有过滤保持 | 仓储/领域回归 |

### 第二组——派工门禁

| # | 场景 | 归属 |
|---|---|---|
| R9 | 自动上料路由无效 → 不预记、不下发 | 领域 |
| R10 | 自动下料路由无效 → 不预记、不下发 | 领域 |
| R11 | 直接交接路由无效 → 不预记、不下发 | 领域 |
| R12 | 预记成功后路由被禁用 → 不下发并完整回滚 | 领域+并发（TCS/gate） |
| R13 | 全部活动 → 原 P0-2 预记后下发顺序保持 | 领域回归 |
| R14 | 拒发不写 POS_TEST_START | 领域 |
| R15 | 缓存仍有旧路由时最终门禁仍拒绝 | 领域 |

### 第三组——其他入口与历史收口

| # | 场景 | 归属 |
|---|---|---|
| R16 | 手动自由文本指向禁用配置 → 拒绝且无 Success | ViewModel/领域 |
| R17 | Redo 路由禁用 → 不重发 | 领域 |
| R18 | Redispatch 路由禁用 → 不重发 | 领域 |
| R19 | 配置禁用后无需重启即可阻止下一次新派工 | 领域 |
| R20 | 已 Dispatched/Executing 回调仍能落库和 Confirm/Rollback | 领域回归 |
| R21 | 对账可收口历史任务但不开新执行 | 领域回归 |
| R22 | 未知 STATE/孤儿关系 fail-closed | 仓储/领域 |
| R23 | 拒绝结果能区分实体类型（D7 枚举） | 领域 |
| R24 | 自动路径不弹 Growl、不置 Alarm | 领域/ViewModel |

**第一个 RED：** 从真实方法 `EquipmentConfigService.GetWorkLineByEquipmentAsync` 开始（R1）。

---

## 13. 验收标准

### 功能

- [ ] `GetWorkLineByEquipmentAsync`：Equipment/Craft/WorkLine 任一非活动或关系异常 → 不可用；全部活动返回真实线体
- [ ] 废除 `(1,"LINE")` 静默回退；Resolve 失败 = 不可派工
- [ ] NextProcess 不返回禁用 Equipment/Craft/WorkLine
- [ ] 预记前门禁：无效路由不预记、不创建 RCS、不写 POS_TEST_START
- [ ] 预记后门禁：失效则不调用 RCS 并完整回滚（P0-2 补偿）
- [ ] 自动上/下料、直接交接、手动、Redo、Redispatch 统一门禁
- [ ] 已 Dispatched/Executing 回调与 Confirm/Rollback 不被软删阻断
- [ ] 对账可读历史任务但不创建新执行
- [ ] 拒绝结果区分 D7 实体类型；缓存旧路由最终仍拒绝并淘汰
- [ ] RED R1–R24 全绿；原 P0-1～P0-4 / P0-6 回归通过

### UI / 日志

- [ ] 自动：限频 Warning；无 Growl；无 Alarm
- [ ] 手动/Redo/Redispatch：明确失败；无 Success；文案含「路由配置已禁用或不可用」

### 构建

- [ ] `dotnet test tests/CncLoader.Core.Tests` 通过
- [ ] `dotnet build CncLoader.sln` 0 警告 0 错误

---

## 14. 非目标

- 不引入全局 EF QueryFilter（P2-1）
- 不改 FRAME_BIND 物理删除语义（P2-2）
- 不新建 ADR / PRD / Surface
- 不改 RCS 协议名、`excuteTask`、LOCATION_MAP 假码策略
- 不改 Alarm 粘滞（其他原因）、对账 fail-closed、P0-2 预记顺序本身
- 不宣称消除「最终门禁后、HTTP 前」跨系统极小窗口
- 不「纠正」EQUIMENT 等历史拼写

---

## 15. 残余限制 / 未验证

- 最终检查通过后、HTTP 调用发出前，外部进程直接改 DB 的极小窗口（跨系统竞态，本期不声称消除）
- 外部直改数据库绕过应用 Delete/Save 路径的运营风险
- 缓存刷新实现细节与失败路径的现场验证
- 真 MySQL 双连接软删 vs 下发并发
- 真实 RCS / PLC / 完整 UI Growl 观感

---

## 16. Agent 简报

**类别：** bug  
**摘要：** 为所有新外部执行增加配置活动双门禁与可区分拒发结果；历史在途任务继续收口；废除 LINE 硬编码回退。

**当前行为：**
- `GetWorkLineByEquipmentAsync` 不滤 Equipment/Craftwork/WorkLine 的 `STATE`
- `ResolveLineAsync` 查不到时回退 `(1,"LINE")` 并继续派工
- `_lineCache` / `_positions` 在配置软删后可继续参与派工决策
- `ReservationFirstDispatcher` 预记与下发之间无配置活动复检
- 手动自由文本、Redo、Redispatch 不复核活动路由
- 自动路径对「配置下线」无专用限频 Warning 语义（且缺 Map 时会 Alarm；本期对配置禁用按 D8 不置 Alarm）

**期望行为：**
- D1：仅 `STATE=="0"` 为活动；null/空/未知 fail-closed；禁止硬编码回退
- D2/D4：一切新外部执行（含手动/Redo/Redispatch/Created 未下发）统一门禁；历史收口继续
- D3：源/目标父子与 Map/Point/Bind 全活动
- D5：缓存非权威；最终读当前配置；失败淘汰缓存；无需重启生效
- D6：预记前 + RCS 前双门禁；第二道失败走 P0-2 回滚
- D7：生产发送边界不可绕过；结果区分实体类型
- D8：自动限频 Warning、无 Growl、无 Alarm；手动/Redo 明确失败无 Success
- D9：无全局 QueryFilter；无 ADR/PRD
- D10：孤儿/未知/缺依赖 fail-closed；对账不创建新执行

**关键接口：**
- `IEquipmentConfigService.GetWorkLineByEquipmentAsync` — 活动过滤；不可用时不得假装有线体
- `GetNextProcessEquipmentsAsync` — 禁用父子不得进入候选
- 路由可用性验证契约（如 `IRoutingAvailabilityValidator`；是否抽取由实现决定）— 返回 D7 区分结果
- `ReservationFirstDispatcher` / 等价生产发送边界 — RCS 调用前最终门禁
- `IRcsTaskService.RedoAsync` / `RedispatchAsync` — 发送前校验；拒绝不改历史证据
- 手动派工真实发送边界 — From/To 解析到活动配置
- `IPositionScheduler.ResolveLineAsync` / 缓存失效 — 废除 LINE 回退；校验失败淘汰缓存

**验收标准：**
- [ ] §13 功能 / UI / 构建全部勾选
- [ ] RED R1–R24 全绿
- [ ] 原 P0-1～P0-4、P0-6 回归通过

**范围外：** 见 §14。

**实现前必读：**
1. 本 Issue §6 已锁定 D1–D10 与 §12 RED 顺序
2. `CONTEXT.md` 软删约定；`docs/adr/0002-槽位预记先于RCS下发.md`
3. P0-2 / P0-3 Issue（预记顺序与对账门禁，勿回退）

**实现顺序建议：**
1. RED 第一组：从 `GetWorkLineByEquipmentAsync`（R1）开始
2. RED 第二组：双门禁与回滚（R9–R15）
3. RED 第三组：手动/Redo/历史收口（R16–R24）
4. 绿测后实现；保持 P0-2 顺序与 P0-3 对账契约

---

## 17. ADR / PRD

| 产物 | 本期 |
|---|---|
| ADR | **不建** |
| PRD / 新 Surface | **不建** |
| 全局 QueryFilter | **不做**（P2-1） |

---

## 18. 风险验证表（源码结论，修复前）

| # | 风险 | 结论 |
|---|---|---|
| 1 | 软删 Equipment 仍可反查线体 | **成立** |
| 2 | 软删/禁用 WorkLine 仍可返回 | **成立** |
| 3 | 调度用已禁用配置创建新 RCS | **条件成立** |
| 4 | 预记后软删仍下发 | **成立**（结构） |
| 5 | 手动绕过列表过滤 | **成立** |
| 6 | Redo/Redispatch 重发已下线路由 | **成立** |
| 7 | 回调因软删无法收口 | **不成立**（须保持） |
| 8 | 专用「路由已下线」反馈 | **缺失** |
| 9 | 无最终原子门禁（TOCTOU） | **成立** |
| 10 | 软删与调度同事务 | **不成立** |

---

## Comments

> 此内容由 AI 在分拣期间生成。

### 源码分拣纪要（2026-08-04）

**状态：** 新建 → `needs-triage`

**已读：** AGENTS/CONTEXT、docs/README、开发文档软删与 STATE、演示手册删除路径、P0-2 Issue + ADR-0002、P0-3 对账门禁、体检 C5/P0-5 与 P2-1 分级说明、测试 Fake 模式。

### 决策锁定（2026-08-04）

> 此内容由 AI 在分拣期间生成。

**状态：** `needs-triage` → `ready-for-agent`  
**Labels：** 追加 `ready-for-agent`（移除 `needs-triage`）

**已锁定 D1–D10（维护者确认）：**

| # | 要点 |
|---|---|
| D1 | `STATE=="0"` 活动；未知 fail-closed；禁 LINE 回退；缺失=禁用=不可派工 |
| D2 | 只挡新外部执行；历史回调/Confirm/Rollback/对账读历史继续 |
| D3 | 源+目标父子与 Map/Point/Bind 全活动 |
| D4 | 自动/交接/手动/Redo/Redispatch 统一门禁 |
| D5 | 缓存非权威；最终读当前配置；失败淘汰；无需重启 |
| D6 | 预记前 + RCS 前双门禁；第二道失败走 P0-2 回滚 |
| D7 | 生产发送边界最终门禁；结果区分实体类型 |
| D8 | 自动：限频 Warning、无 Growl、无 Alarm；手动/Redo：明确失败无 Success |
| D9 | 无全局 QueryFilter；无 ADR/PRD |
| D10 | 孤儿/未知 fail-closed；对账不创建新执行 |

**RED：** R1–R24 三组顺序已锁定；第一个 RED = `GetWorkLineByEquipmentAsync`（R1）。

**残余限制：** 门禁后 HTTP 前极小窗口；外部直改 DB；缓存刷新现场验证；真 MySQL 并发未验证。

**下一步：** AFK Agent 按 §16 简报从 R1 开始写红测。

### 第一批 RED 纪要（R1–R8，2026-08-04）

> 此内容由 AI 在分拣期间生成。

**状态：** 保持 `ready-for-agent`（GREEN 尚未开始）

#### 查询接缝（行为保持，未加 STATE 过滤）

| 接缝 | 位置 | 说明 |
|---|---|---|
| `IEquipmentRoutingStore` | Core + `EquipmentRoutingStore` | 原样返回 Equipment/Craft/WorkLine/FrameBind（含 STATE） |
| `ILocationMapRoutingStore` | Core + Data | Resolve 路径原样读行；Service 仍滤 `STATE=="0"` |
| `IPlcPointRoutingStore` | Core + Data | 原样读点位；`PlcPointSource` 仍滤 `STATE=="0"` |
| `ProbeResolveLineAsync` | `PositionScheduler` internal | 真实调用 `ResolveLineAsync`（含 LINE 回退） |

真实 `EquipmentConfigService` / `LocationMapService` / `PlcPointSource` 经 DI 注册上述 Store。列表页查询未改。未引入全局 QueryFilter / Validator / 双门禁。

#### R1–R8 测试与结果

文件：`tests/CncLoader.Core.Tests/Routing/RoutingSoftDeleteQueryTests.cs`

| 测试 | 结果 | 实际现象 |
|---|---|---|
| `R1_DisabledEquipment_GetWorkLine_ReturnsNull` | **RED** | 仍返回 `WorkLineRef(10,"L10")` |
| `R2_DisabledCraft_GetWorkLine_ReturnsNull` | **RED** | 同上 |
| `R3_DisabledWorkLine_GetWorkLine_ReturnsNull` | **RED** | 同上 |
| `R4_MissingCraft_GetWorkLine_ReturnsNull_FailClosed` | PASS | 缺 Craft → null（已有） |
| `R4_MissingWorkLine_GetWorkLine_ReturnsNull_FailClosed` | PASS | 缺 WorkLine → null（已有） |
| `R4_UnknownEquipmentState_GetWorkLine_ReturnsNull_FailClosed` | **RED** | STATE=`"x"` 仍返回线体 |
| `R5_AllActive_GetWorkLine_ReturnsRealWorkLine` | PASS | 返回真实 LineId/Code |
| `R6_WhenGetWorkLineReturnsNull_ResolveLine_DoesNotFallbackToDefaultLine` | **RED** | 实际回退 `(1,"LINE")` |
| `R6_WhenEquipmentDisabled_ResolveLine_IsUnavailable` | **RED** | 仍解析为 `(10,"L10")`（GetWorkLine 未滤） |
| `R7_NextEquipmentDisabled_NotReturned` | PASS | Equipment 层已滤 |
| `R7_NextCraftDisabled_NotReturned` | PASS | Craft 层已滤 |
| `R7_ParentWorkLineDisabled_NextNotReturned` | **RED** | WorkLine STATE=1 仍返回下游机台 `2` |
| `R7_AllActive_ReturnsNextEquipment` | PASS | 正常返回 |
| `R7_Mixed_OnlyCompleteActiveChainReturned` | PASS | 禁用 Equipment 已排除 |
| `R8_LocationMap_Disabled_NotResolved` | PASS | 仅活动 cell |
| `R8_LocationMap_OnlyDisabled_ReturnsNull` | PASS | |
| `R8_PlcPoint_Disabled_NotLoaded` | PASS | |
| `R8_FrameBind_Disabled_NotReturned` | PASS | |

**过滤专项：** 18 测 → 失败 7 / 通过 11  
**全量：** 166 测 → 失败 7（均为上表 RED）/ 通过 159；原 148 全部继续通过。

#### NextProcess 缺失层

- 已过滤：下一工序 **Equipment**、**Craft**（`STATE=="0"`）
- **未过滤：父 WorkLine STATE**（R7 RED）
- 源 Equipment STATE：本批未单独断言（GetWorkLine R1 覆盖反查）

#### LINE 回退

`ProbeResolveLineAsync` → 真实 `ResolveLineAsync`：GetWorkLine=null 时写入并返回 `(1,"LINE")`（R6 RED）。

#### GREEN 状态

尚未开始：未添加 `STATE=="0"` 条件、未删除 LINE 回退、未做派工双门禁/缓存失效/UI。

### 查询层 GREEN 纪要（R1–R8，2026-08-04）

> 此内容由 AI 在实现期间生成。

**状态：** 保持 `ready-for-agent`（缓存失效 / 派工双门禁尚未开始）

#### 已完成

1. **三层活动过滤**：`ConfigActivity.IsActive`（精确 `STATE=="0"`；null/空/未知 fail-closed）。`GetWorkLineByEquipmentAsync` 校验 Equipment/Craft/WorkLine 全活动；Service 自身 fail-closed，不依赖 store 预过滤。
2. **NextProcess 父 WorkLine 过滤**：`GetNextProcessEquipmentsAsync` 补父线体活动条件；源 Equipment/Craft 与下游 Craft/Equipment 一并活动过滤；混合结果只返回完整活动链。
3. **ResolveLine 不可用契约**：`ResolveLineAsync` → `WorkLineRef?`；null = 不可用；上料跳过（不 Alarm / 不预记 / 不下发）；下料不入队。
4. **删除 LINE 回退**：废除 `(1,"LINE")`；`InventoryService` / `ChangeFrameOrchestrator` 同步 fail-closed。
5. **缓存写入约束**：仅成功活动路由写入 `_lineCache`；不可用结果不缓存。
6. **R1–R8 GREEN**：过滤专项 + 补充用例全部通过；全量 181；`dotnet build CncLoader.sln` 0 错误 0 警告；`git diff --check` 无 whitespace error。

#### 尚未完成（下一组）

- ~~缓存失效（软删后淘汰 `_lineCache` / 点位缓存）~~ → R9–R15 GREEN 已做线体缓存非权威
- ~~预记前/后双门禁与预记回滚协调~~ → R9–R15 GREEN 已完成
- 手动 / Redo / Redispatch 最终门禁
- UI 拒发反馈

### 第二批 RED 纪要（R9–R15 派工双门禁，2026-08-04）

> 此内容由 AI 在实现期间生成。

**状态：** 保持 `ready-for-agent`（GREEN 尚未开始：未加 Validator / 二次权威校验 / 缓存失效 / 预记后回滚协调）

#### 真实发送边界（还原）

| 路径 | 方法链 |
|---|---|
| 自动上料 | `ProbeDispatchOnce` → `AllocateUploadsAsync` → `TryDispatchUploadAsync` → `ResolveLineAsync` → `ReservationFirstDispatcher.ExecuteAsync(ReserveTake → DispatchTransitAsync TaskType=0 → RollbackTake)` |
| 自动下料（料架） | `ProbeEnqueueUnloadAsync`→`EnqueueUnloadAsync`→`ResolveLine` 入队 → `DispatchOneAsync` → `ResolveUnloadTarget` → `ExecuteAsync(ReserveAsync/PUT → DispatchTransit TaskType=1 → RollbackAsync)` |
| 直接交接 | 同上消费者；`UnloadTarget.NextMachineCell` → `ReserveUnload` 登记 `_expectedInbound` → `DispatchTransitAsync`；失败回滚 `TryRemove` |
| POS_TEST_START | **不在派工路径**；仅 `Loaded`/`Unloaded` 收口写 |

`ReservationFirstDispatcher` 参数：`taskId, reserveAsync, dispatchAsync, rollbackAsync`；内部顺序 Reserve → Dispatch →（失败）Rollback。预记成功后一律 `IRcsTaskService.DispatchTransitAsync`。

`_lineCache`：key=`equipmentId`，value=`WorkLineRef`；命中直接返回、**不再读权威配置**。

#### 预记后禁用交错

`TracingSlots.ReserveTake/ReserveAsync`：`ReserveCommit` 后、方法返回前同步回调将权威 STATE 置 `"1"` 并记 `DisableRoute`。无 Sleep、无生产静态钩子。

观测到的真实顺序（上/下料一致）：

`RouteQueryPre → RouteValidatePre → ReserveStart → ReserveCommit → DisableRoute → RcsDispatch`

缺 `RouteQueryFinal` / `RouteValidateFinal`；`RollbackCount=0`。

#### 缓存粘滞证据

- R15A：第一次 `ProbeResolveLine` 写缓存后禁用 WorkLine；第二次仍返回旧 `WorkLineRef`，Store 查询次数不增加（纯 CacheHit）。
- R15B：缓存未清时触发真实上料 → ReserveTake=1 且 RCS=1、无回滚。
- R11 缓存：源禁用后仍入队并交接/下发。

#### 最小 internal 接缝（行为不变）

`PositionScheduler`：`ProbeSeedPosition` / `ProbeEnqueueUnloadAsync` / `ProbeHasExpectedInbound` / `ProbeQueueCount` / `ProbeGetContext`。

#### 测试文件

- `tests/CncLoader.Core.Tests/Routing/DispatchRoutingGateTests.cs`（R9/R10/R12–R15）
- `tests/CncLoader.Core.Tests/Routing/DirectHandoffRoutingGateTests.cs`（R11）
- `tests/CncLoader.Core.Tests/Routing/DispatchRoutingGateFakes.cs`（可变权威 Store + 调用序）

#### PASS / RED

| # | 结果 | 现象 |
|---|---|---|
| R9 上料预记前（Eq/Craft/Line×3） | **PASS** | 查询层 GREEN：Reserve=0 RCS=0 PLC=0 无 Alarm |
| R10 下料预记前（×3） | **PASS** | 入队期 ResolveLine 拒：ReservePut=0 RCS=0 |
| R11 源 Eq/Line 禁用 | **PASS** | 不入队 |
| R11 目标 Eq 禁用 | **RED** | 无 `_expectedInbound`，但回退命名区 `ResolveUnload` 仍 RCS=1 |
| R11 目标 Line 禁用 | **PASS** | 同线拓扑下等同源 Line 禁用（入队拒） |
| R11 缓存粘滞源禁用 | **RED** | CacheHit 仍入队且 RCS=1 |
| R11 全活动交接基线 | **PASS** | `_expectedInbound` 含目标 identity；Args.EquipmentId=源 |
| R12 上/下料预记后禁用 | **RED** | 无 Final 查询；RCS=1；Rollback=0 |
| R13 全活动顺序 | **RED** | 实际 `Pre→Reserve→Rcs`，缺 Final |
| R14 预记前/后拒写 PLC | **PASS** | WriteCount=0（POS 不在派工路径；RCS 契约归 R12） |
| R15A/B 缓存 | **RED** | 粘滞绕过权威配置 |

**过滤专项：** 19 测 → 失败 7 / 通过 12  
**全量：** 200 测 → 失败 7 / 通过 193；原 181 全部继续通过。失败均来自契约断言（非 NRE/超时/DI）。

#### 直接交接 identity

- 目标机台/工位在 `UnloadDecision.Dest*` / `_expectedInbound` key；`TransitDispatchArgs.EquipmentId` 仅为源。
- 目标禁用时 NextProcess 已滤掉目标，但命名区回退仍可下发——最终门禁须覆盖该回退路径。

#### 当时 GREEN 状态

（已由下方第二批 GREEN 纪要闭合。）

### 第二批 GREEN 纪要（R9–R15 派工双门禁，2026-08-04）

> 此内容由 AI 在实现期间生成。

**状态：** 保持 `ready-for-agent`（手动 / Redo / Redispatch 尚未开始）

#### Validator 契约

- `IRoutingAvailabilityValidator` / `DispatchRouteContext` / `RoutingAvailabilityResult` / `RoutingUnavailableReason`（Core）
- `RouteDependency` 区分 Required / NotApplicable / Missing（不用 null 兼表两义）
- 实现：`RoutingAvailabilityValidator`（Data，读 `IEquipmentRoutingStore` + `GetWorkLineByEquipmentAsync`）
- DI：`DataServiceCollectionExtensions` 注册真实实现；无默认 Available、无 null 跳过

#### Cache 权威规则

`ResolveLineAsync`：命中 `_lineCache` 后仍读权威；失败淘汰缓存；成功更新快照；异常 fail-closed。

#### Pre / Final 双门禁

顺序：`RouteValidatePre → Reserve → RouteValidateFinal → RcsDispatch`

- Pre：上/下料/交接在预记或 `_expectedInbound` 登记前
- Final：`ReservationFirstDispatcher` 新增必填 `validateFinalAsync`，紧邻 RCS 前
- 新状态：`ReservationFirstDispatchStatus.RouteUnavailable`（非 RCS 失败；走 P0-2 回滚）

#### 预记后回滚

Final 失败 → 不调用 RCS → `RollbackTake` / `RollbackAsync` / `_expectedInbound.TryRemove`；回滚失败保持 Alarm。

#### 直接交接修复

- 源+目标 Equipment/Craft/WorkLine 校验
- `HasSubsequentProcessAsync`：有后续工序但无活动目标 → 拒发，不回退命名区
- Final 失败清除 `_expectedInbound`

#### 验证

| 命令 | 结果 |
|---|---|
| filter DispatchRoutingGate\|DirectHandoffRoutingGate | 25 通过 |
| filter RoutingSoftDeleteQuery | 33 通过 |
| 全量 Core.Tests | **211 通过 / 0 失败** |
| `dotnet build CncLoader.sln` | 0 错误 0 警告 |
| `git diff --check` | 无 whitespace error |

R9–R15 原 19 条 + 补充用例全部 GREEN。

#### 尚未完成

- 手动 / Redo / Redispatch 最终门禁（R16–R18）
- UI 拒发文案
- 跨 DB/HTTP「Final 通过后、HTTP 发出前」极小窗口仍存在

### 第三批 RED 纪要（R16–R24 手动/重发/历史收口，2026-08-04）

> 此内容由 AI 在实现期间生成。

**状态：** 保持 `ready-for-agent`（GREEN 尚未开始：未给手动/Redo/Redispatch 注入 Validator、未改发送顺序/回调/对账）

#### 手动 / Redo / Redispatch 真实调用链

| 入口 | 调用链 |
|---|---|
| 手动派工页 | `RcsViewModel`（RCS 任务页）→ `DispatchCommand` → `DispatchAsync` |
| From/To 来源 | **自由文本** `FromCode`/`ToCode`（默认示例码）；`TryValidateManualDispatch` 仅非空/不相同/优先级；**不查** LOCATION_MAP / Equipment / WorkLine 活动态。下拉仅用于位置映射编辑，不驱动搬运 From/To |
| 手动发送 | `IRcsTaskService.DispatchTransitAsync` / `DispatchGrabAsync` / `DispatchIdentifyAsync` → `IRcsClient.TransitTaskAsync` / `ExcuteTaskAsync` |
| Redo | `RedoCommand` → `RcsTaskService.RedoAsync`：`GetByTaskId` → **`IncrementRedoAsync`（先改本地）** → `BuildAndSendAsync` → `FinishAsync` → 成功则 `ForgetTask` |
| Redispatch | `RcsTaskService.RedispatchAsync`（自动重发；无 Increment）：`GetByTaskId` → `BuildAndSendAsync` → `FinishAsync` → 成功 `ForgetTask` |
| 失败补偿 | Redo/Redispatch **无**路由失败补偿；RCS 失败走 `UpdateState=FAILED`。IncrementRedo 已在发送前发生 |
| UI 通知 | `ReportResult` → Success/Warning Growl + `StatusMessage`（本批加 `TestNotifications` 接缝，生产 null 仍走 Growl） |
| 历史 callback | `RcsCallbackProcessor` 不依赖 `RoutingValidator` |
| 启动对账 | `StartupReconcileCoordinator` / `PositionScheduler` 读未完结任务；**不**自动 Redo/Redispatch 新执行 |

#### R16–R24 测试文件

- `ManualDispatchRoutingGateTests.cs`
- `RcsTaskReplayRoutingGateTests.cs`
- `HistoricalTaskClosureRoutingTests.cs`
- 共享夹具 `ManualReplayRoutingGateFakes.cs`（复用 `MutableEquipmentRoutingStore` / `RoutingAvailabilityValidator` / P0-3/4/6 fake）

#### PASS / RED

| # | 结果 | 现象 |
|---|---|---|
| R16 禁用/不可解析/陈旧下拉（×5） | **RED** | Validator=0；RCS≥1；Create≥1；Success 仍出现 |
| R16 全活动基线 | **PASS** | Send=1 Create=1 Success=1 |
| R16 测试连接不被拦 | **PASS** | Query=1 Transit=0 |
| R17 Redo 禁用 | **RED** | 先 IncrementRedo 再发送；ForgetTask；无 Validator；RCS=1 |
| R18 Redispatch 禁用 | **RED** | 无校验直接重发；历史态被 SetDispatched |
| R19 手动/Redo 即时生效 | **RED** | 同实例 STATE→1 后第二次仍 RCS+1；Store 查询不增 |
| R20 历史回调/Confirm/Rollback | **PASS** | 落库+去重；Validator 未调用；无新执行 |
| R21 对账只读历史 | **PASS** | Coordinator/Scheduler 接缝无 Dispatch/Redo |
| R22 异常数据 fail-closed | **RED** | 未知 STATE/孤儿/歧义仍下发 |
| R23 拒绝原因区分 | **PASS** | Validator 可区分 Eq/Craft/Line/FrameBind/NotFound/InvalidRel/LocationMap/ConfigUnavailable |
| R24 自动无 Growl/Alarm | **PASS** | Notify=0；普通拒发无 Alarm；回滚失败仍 Alarm |

**过滤专项：** 29 测 → 失败 17 / 通过 12  
**全量：** 240 测 → 失败 17（均为上表 RED 契约断言）/ 通过 223；原 211 全部继续通过。无 NRE/超时/DI 假红。

#### 禁用即时生效证据（RED）

同 `RcsTaskService`/`RcsViewModel`/`CountingValidator` 实例：第一次活动可下发；`SetWorkLineState/SetEquipmentState("1")` 后第二次仍 `SendCount+1`，权威 `Queries` 不增加 → 证明缺最终门禁与权威重读。

#### 历史收口未被阻断证据（PASS）

Equipment/WorkLine 已禁用时：`HandlePushTaskStatusAsync` 仍 `UpdateState`；重复 push 去重；`Confirm`/`Rollback`/`ConfirmTake` 按 taskId 执行；`Validator.CallCount` 不变；RCS 新发送=0。

#### 最小接缝（行为保持）

`RcsViewModel.TestNotifications`：生产 null → 仍 Growl；测试挂 `FakeNotifyCounter`，避免 headless Growl.Show NRE。

#### GREEN 状态

尚未开始：未注入手动/Redo/Redispatch Validator；未改发送顺序；未改 callback/对账；未加缓存失效。

### 第三组 GREEN 纪要（R16–R24 手动/Redo/Redispatch，2026-08-04）

> 此内容由 AI 在实现期间生成。

**状态：** 保持 `ready-for-agent`（最终审查未开始）

#### Managed Resolver 规则

- 契约：`IManagedDispatchRouteResolver` / `ManagedDispatchRouteResult`（Core）
- 实现：`ManagedDispatchRouteResolver`（Data）；权威读 `ILocationMapRoutingStore.FindByRcsCodeAsync`
- 解析顺序：仅 `LOCATION_MAP.RcsCode` **精确匹配**（本期无 Equipment/WorkLine 自由文本受管来源 → 明确拒绝）
- 零匹配 → `NotFound`；仅禁用/未知 STATE → `Disabled`（不得当 NotFound 后回退自由文本）；多条活动 → `Ambiguous`（不取 First）
- 成功时构造完整 `DispatchRouteContext`（源/目标 EquipmentId + PositionId + From/To），供 Validator 再查
- 不返回 RCS URL/Token；无默认 LINE/区域；无长期缓存

#### 手动 Pre/Final

- `RcsViewModel` 搬运：`Resolve` → `ValidatePre` → 早期 Warning；失败不调 Service
- `RcsTaskService.DispatchTransitAsync`：`ValidateFinal`（权威重读）→ Create → Send
- 拒发：RCS=0、Create=0、PLC=0、Success=0；文案 `路由配置已禁用或不可用`
- `TestConnection` 不经任务路由门禁
- 空托盘回收（AREA）`SkipManagedRouteGate=true`（无机台 identity）

#### Redo/Redispatch 副作用顺序

- 统一内部：`ResolveAndValidateForNewExecutionAsync`（Pre）→ `ValidateFinalAsync` → 既有发送
- **Redo**：Final 通过后才 `IncrementRedo` / `BuildAndSend` / 成功才 `ForgetTask`
- **Redispatch**：独立接线（不经 Redo）；拒发不改原任务、不 Forget、不 RCS
- 路由拒绝返回 `RcsFailureKind.RouteUnavailable` / `ConfigurationUnavailable`

#### 禁用即时生效

- Resolver/Validator 每次读权威 Store；同实例 STATE→1 后第二次立即拒发；不重启、不复用上次 Context

#### 历史收口隔离

- `RcsCallbackProcessor` / Confirm/Rollback / StartupReconcile / 已执行状态推进：**未**注入 Resolver/Validator
- R20/R21/R23/R24 保持 PASS

#### R16–R24 GREEN

| 组 | 结果 |
|---|---|
| 第三组原 29 条 | **全部 GREEN** |
| 补充 9 条 | Ambiguous / Disabled≠NotFound / Resolver 异常 / Final 失败副作用 / Redispatch 快照 / Pre+Final 各一次 / 全活动重发 / 即时生效 / callback Validator=0 |
| R1–R15 过滤 | 71 通过 |
| 全量 | **249** 通过（原 240 + 补充 9） |
| `dotnet build CncLoader.sln` | 0 错误 0 警告 |
| `git diff --check` | 无 whitespace error |

#### 未验证 / 残余

- 真 MySQL / 真 RCS / 真实 UI Growl 观感未验证
- Final→HTTP 极小窗口保留（跨 DB 直改）
- 自由文本历史任务若 From/To 无 LOCATION_MAP 将拒发（刻意 fail-closed）
