# P0-5｜调度路由未过滤软删配置，可能向已下线线体/机台派工

> 此内容由 AI 在分拣期间生成。

Type: bug
Priority: P0
Status: closed
Labels: dispatch, routing, soft-delete, equipment, safety, closed
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

> **补充：** D11–D15（类型化端点 / 废 Skip / Grab·Identify Final / Tracker 先门禁后计数 / 真生产边界测试）已于 2026-08-05 锁定，全文见 Comments「最终审查阻塞 + 端点模型分拣」。D1–D10 正文不改；PlcPoint 作为 RCS 端点 Final 依赖的范围收窄见 D15（本期 N/A）。

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

- [x] `GetWorkLineByEquipmentAsync`：Equipment/Craft/WorkLine 任一非活动或关系异常 → 不可用；全部活动返回真实线体
- [x] 废除 `(1,"LINE")` 静默回退；Resolve 失败 = 不可派工
- [x] NextProcess 不返回禁用 Equipment/Craft/WorkLine
- [x] 预记前门禁：无效路由不预记、不创建 RCS、不写 POS_TEST_START
- [x] 预记后门禁：失效则不调用 RCS 并完整回滚（P0-2 补偿）
- [x] 自动上/下料、直接交接、手动、Redo、Redispatch 统一门禁
- [x] 已 Dispatched/Executing 回调与 Confirm/Rollback 不被软删阻断
- [x] 对账可读历史任务但不创建新执行
- [x] 拒绝结果区分 D7 实体类型；缓存旧路由最终仍拒绝并淘汰
- [x] RED R1–R24 全绿；原 P0-1～P0-4 / P0-6 回归通过

### UI / 日志

- [x] 自动：限频 Warning；无 Growl；无 Alarm
- [x] 手动/Redo/Redispatch：明确失败；无 Success；文案含「路由配置已禁用或不可用」

### 构建

- [x] `dotnet test tests/CncLoader.Core.Tests` 通过
- [x] `dotnet build CncLoader.sln` 0 警告 0 错误

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
**摘要：** 为所有新外部执行增加类型化受管端点双门禁；废除 SkipManagedRouteGate 与 LINE 回退；Grab/Identify/盘点/换架/回收统一 Final；Tracker 先门禁后耗 RedoCount；历史收口继续。

**当前行为（相对 D11–D15；第一组类型化端点已 GREEN，其余仍缺口）：**
- Resolver 强制两端 `EquipmentId>0`，误杀合法无 Eq 的 AREA/FRAME
- `SkipManagedRouteGate` 使空托盘回收绕过 Final
- Grab/Identify/盘点无 Service 发送边界门禁
- Tracker `TryIncrementRedoIfUnderAsync` 先于路由 Final
- R9–R15 可用 `TracingTaskService` 假证 Final 安全
- D1–D10 查询层/双门禁骨架已落地（R1–R24 GREEN），但生产边界未闭合

**期望行为：**
- D1–D10：保持已锁定（活动态、双门禁、历史收口、无 QueryFilter/ADR）
- D11：类型化 POSITION/AREA/FRAME；N/A 由端点类型决定；AREA/FRAME 无 Eq 合法且必须 Final
- D12：删除 `SkipManagedRouteGate`；回收 From=活动 POSITION|FRAME，To=活动 AREA(`PALLET_RETURN`)
- D13：Grab/Identify/盘点在 `RcsTaskService` Final；不 Create/不 Excute 若拒发
- D14：统一 Service：Resolve→Pre→Final→原子 TryIncrement→Send
- D15：真 Service/Resolver/Validator + 无 Eq AREA/FRAME 种子；DI 防绕过
- PlcPoint：**本期 N/A**（非 RCS 端点）；`PointDisabled` 后续

**关键接口：**
- `IManagedDispatchRouteResolver` — 按 LocType 类型化解析；显式校验 RcsCode 活动唯一
- `IRoutingAvailabilityValidator` — 按端点类型 Required/N/A；调用方不得滥标 N/A
- `IRcsTaskService.DispatchTransitAsync` / `DispatchPalletReturnAsync` — 无 Skip；全 Transit 经 Final
- `DispatchGrabAsync` / `DispatchIdentifyAsync` — 发送边界类型化 Final
- `RcsTaskTracker` → Service 统一入口（先门禁后计数）
- `ReservationFirstDispatcher` + 真 `RcsTaskService` — R9–R15 回归

**验收标准：**
- [x] §13 功能 / UI / 构建 + D11–D15
- [x] RED R1–R24 保持绿；审查阻塞 RED 1–26 全绿
- [x] 全仓无 `SkipManagedRouteGate`（生产 `src` 已清；测试仅反射断言不存在）
- [x] 原 P0-1～P0-4、P0-6 回归通过
- [x] **不建** ADR/PRD（D9）

**范围外：** 见 §14；PlcPoint 生命周期策略本期不做。

**实现前必读：**
1. 本 Issue §6 D1–D10（已锁定）+ Comments「D11–D15 已锁定」与端点/操作矩阵
2. `CONTEXT.md` 软删约定；`docs/adr/0002-槽位预记先于RCS下发.md`
3. P0-2 / P0-3 Issue（预记顺序与对账门禁，勿回退）

**实现顺序建议：**
1. RED 第一组（端点类型与真实 Transit 1–10）：从 `ManagedDispatchRouteResolver.ResolveAsync` + 无 Eq AREA 种子开始
2. RED 第二组（去 Skip / Grab / Identify / 换架 / 回收 11–19）
3. RED 第三组（Tracker / DI 20–26）
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

### 最终审查阻塞 + 端点模型分拣（2026-08-05）

> 此内容由 AI 在最终实现审查后分拣期间生成。

**状态：** 保持 `ready-for-agent`（**不得进入最终回归**；D11–D15 **已锁定**，按审查阻塞 RED 1–26 继续 RED→GREEN）

**审查证据摘要：** 249 全绿**不能**覆盖真实生产边界——R9–R15 使用 `TracingTaskService`；手动测试种子全是带 `EquipmentId` 的 POSITION；生产种子 AREA/FRAME **合法地没有** `EquipmentId`；`SkipManagedRouteGate` 使空托盘回收绕过 Final。D1–D10 不变。

---

#### 阻塞项（关闭前必须；对齐 D11–D15）

| # | 阻塞 | 锁定决策 |
|---|---|---|
| B1 | `SkipManagedRouteGate` 真实 Transit 绕过 Final | **D12** 彻底删除 |
| B2 | Grab/Identify/盘点无发送边界门禁 | **D13** Service Final |
| B3 | Resolver 强制 `EquipmentId>0` 误杀 AREA/FRAME | **D11** 类型化端点 |
| B4 | R9–R15 未打真实 `RcsTaskService` | **D15** 真生产边界测试 |
| B5 | Tracker 先计数后门禁 | **D14** 先 Final 后原子消耗 |

**PlcPoint：** 本期 **N/A**（非 RCS 端点 Final 依赖）；不阻塞关闭。见 D15 / 下文。

---

#### 真实 LOCATION_MAP 端点模型（种子 + 实体）

表：`MAS_AUTO_LOCATION_MAP`（实体 `LocationMap`）。**无 WorkLineId / PointId 列**；线体经 Equipment→Craft→WorkLine 反查；PlcPoint 在 `MAS_AUTO_PLC_POINT`，**不是 RCS 路由端点**。`RcsCode` 索引**不是 UNIQUE**——唯一性必须在 Resolver 显式验证。

| LocType | 种子 RcsCode 例 | RcsType | STATE | EquipmentId | PositionId | FrameId | LocName |
|---|---|---|---|---|---|---|---|
| AREA | 601001 / 609001 / 650001 / 650002 / 650003 | station | 0 | **null（合法）** | null | null | LOAD_AREA / UNLOAD_AREA / FULL_BUFFER / EMPTY_BUFFER / PALLET_RETURN |
| POSITION | 601203…603204 | cell | 0 | **有** | **有** | null | null |
| FRAME shelf | 651001…651092 | shelf | 0 | **null（合法）** | null | **有** | 料架名 |
| FRAME cell | 653002…653092 | cell | 0 | **null（合法）** | null | **有** | 料架 cell 名 |
| EQUIPMENT | （种子未插入） | — | — | schema 允许 | — | — | — |

---

#### 端点类型 → 验证规则（**已锁定，D11**）

端点类型**只能**来自权威 `LOCATION_MAP` 持久化配置；调用方不得自行把缺失依赖标成 `NotApplicable`。未知 LocType、缺少类型必需字段、跨类型歧义 → `InvalidRelationship` / `NotFound` / `Ambiguous`，**不取第一条**。

##### POSITION

**Required：** LOCATION_MAP 存在且 `STATE=="0"`；`LocType=POSITION`；RcsCode 精确且在当前角色内活动唯一；EquipmentId 有效；PositionId 有效；Equipment/Craft/WorkLine 全部活动；父子关系正确。

**NotApplicable：** FrameId；FrameBind；AREA 专用字段；PlcPoint（本期 RCS 路由）。

##### AREA

**Required：** LOCATION_MAP 存在且 `STATE=="0"`；`LocType=AREA`（或种子等价 AREA 类型）；RcsCode 精确且活动唯一；LocName/区域角色属于当前操作允许值（由真实配置解析：LOAD_AREA / UNLOAD_AREA / FULL_BUFFER / EMPTY_BUFFER / PALLET_RETURN 等）。

**合法 NotApplicable：** EquipmentId；Equipment/Craft/WorkLine；PositionId；FrameId；PlcPoint。

**要求：** 无 EquipmentId 是合法模型；**不能**因此跳过 Resolver/Final；**不允许**硬编码默认 AREA 替代禁用/缺失配置。

##### FRAME

**Required：** LOCATION_MAP 存在且 `STATE=="0"`；`LocType=FRAME`（或种子等价）；FrameId 有效；Frame 实体存在且活动；RcsCode 精确且活动唯一；固定 shelf/cell 按真实 `RcsType` 保留。

**条件 Required：** 操作同时指定目标 Equipment/WorkLine 时，FrameBind 必须活动且关系一致。

**合法 NotApplicable：** EquipmentId（固定 FRAME Map 无 Eq 时）；PositionId；PlcPoint。

**要求：** 不允许任意自由文本伪装 FRAME；Frame/FrameBind 禁用 fail-closed。

---

#### 操作 × 端点角色矩阵（**已对齐 D11–D13**）

| 操作 | From | To | 新 RCS | 允许组合 | Pre | Final | 失败 |
|---|---|---|---|---|---|---|---|
| 自动上料 | AREA(`LOAD_AREA`) 或 FRAME(cell 回流) | POSITION | 是 | AREA→POSITION；FRAME→POSITION | Validator | Dispatcher+真 Service | 预记回滚；RouteUnavailable 不 Alarm |
| 自动下料（架） | POSITION | FRAME(cell) | 是 | POSITION→FRAME | 同上 | 同上 | 同上 |
| 自动下料（命名区） | POSITION | AREA(`UNLOAD_AREA`) | 是 | POSITION→AREA | 同上 | 同上 | 同上 |
| 直接交接 | POSITION | POSITION | 是 | POSITION→POSITION | 含 Dest | 同上 | 清 inbound |
| 手动搬运 | 受管码 | 受管码 | 是 | 上列生产组合；未出现组合默认拒 | VM Pre | Service Final | 不 Create；Warning；Success=0 |
| 空托盘回收 | **活动 POSITION 或活动 FRAME** | **活动 AREA 且角色=PALLET_RETURN** | 是 | POSITION→PALLET_RETURN；FRAME→PALLET_RETURN | Resolve+Pre | Final（**无 Skip**） | 不 Create/不 Transit |
| 手动抓取 | 操作允许的活动端点 | 同上 | 是 | 仅真实种子/调用链已出现的 station 组合；默认拒宽松兼容 | VM 可 Pre | **Service Final** | 不 Create/不 Excute |
| 手动识别 | 活动 FRAME(shelf/station) 等允许类型 | 单端 | 是 | 同左 | VM 可 Pre | **Service Final** | 同上 |
| 自动盘点 identifyQR | FRAME(shelf，缺则 station) | 单端 | 是 | FRAME 单端 | 业务 Resolve=Pre | **同一 Service Final** | Final 失败不得留「已发起」假态 |
| 换架 pull | FRAME(cell\|shelf) | AREA(`EMPTY_BUFFER`/`FULL_BUFFER`) | 是 | FRAME→AREA | 业务可 Pre | Service Final | 告警路径保持 |
| 换架 push | AREA(缓冲) | FRAME(cell\|shelf) | 是 | AREA→FRAME | 同上 | 同上 | 同上 |
| Redo | 落库 From/To | 落库 | 是 | 随原任务类型 | Resolve+Pre | Final→Increment→Send | 拒发不改历史 |
| Redispatch / Tracker | 落库 | 落库 | 是 | 同左 | **Service 统一** | Final→原子 TryIncrement→Send | 拒发不耗 RedoCount |
| TestConnection | — | — | **否** | — | — | 不走任务门禁 | — |
| 历史 callback/对账 | — | — | **否** | — | 不注入门禁 | — | 不得新建 RCS |

未在真实种子/调用链出现的组合：**默认拒绝**，不做宽松兼容。

---

#### 审查补充决策 D11–D15（**已锁定**，维护者确认 2026-08-05）

### D11｜类型化受管端点 — **已锁定**

Resolver 必须先从权威 LOCATION_MAP 解析端点类型，再按类型验证。端点类型只能来自持久化配置；调用方不能自行把缺失依赖标成 NotApplicable。规则见上表 POSITION/AREA/FRAME。未知 LocType、缺必需字段、跨类型歧义 → InvalidRelationship/NotFound/Ambiguous，不取第一条。RcsCode DB 非 UNIQUE → Resolver 显式验证活动唯一性。AREA/FRAME 无 EquipmentId 为合法模型，且必须有权威 Final。

### D12｜彻底删除 SkipManagedRouteGate — **已锁定**

- 删除 `TransitDispatchArgs.SkipManagedRouteGate`；删除所有 if-skip 分支；不保留 internal/public 等价逃生参数。
- 所有真实 Transit 新任务都必须 Resolve → Pre → Final。
- TestConnection 不创建任务，不受影响。

**空托盘回收：**

| 端 | 规则 |
|---|---|
| From | 只允许**活动 POSITION** 或**活动 FRAME**；必须精确解析；自由文本 / AREA / 未知类型 → 拒绝 |
| To | 必须是**活动 AREA**；区域角色必须为 **PALLET_RETURN**；不能使用缓存旧码或硬编码默认值 |

顺序：`Resolve From/To → ValidatePre → 构造内存请求 → ValidateFinal → RCS`。
失败：不 Create、不 Transit、不改本地任务；UI Warning；Success=0。

### D13｜Grab/Identify 发送边界统一门禁 — **已锁定**

`DispatchGrabAsync` / `DispatchIdentifyAsync` 必须在真实 `RcsTaskService` 发送边界执行类型化解析和 Final。

1. 每个具有路由意义的 code/station/shelf/from/to 都必须解析。
2. 允许端点类型由操作角色显式定义，不能由调用方传 bool 绕过。
3. 现有合法生产种子组合写入上表操作矩阵并保留。
4. 未出现组合默认拒绝。
5. 任一端点禁用/未知/歧义/关系异常：不 Create、不 Excute、返回 RouteUnavailable、不显示 Success。

盘点：`InventoryService.StartInventoryAsync` 的端点必须走同一 Service Final；业务层 Resolve 只作 Pre；Final 失败不得留下「盘点已成功发起」假状态。
手动：ViewModel 可 Pre；Service Final 权威；TestConnection 不走任务门禁。

### D14｜Tracker 先门禁后原子消耗 RedoCount — **已锁定**

禁止：`TryIncrementRedoIfUnderAsync → Resolver/Final → Redispatch`。

锁定为统一 Service 操作：

`Resolve → ValidatePre → ValidateFinal → 原子 TryIncrementRedoIfUnderAsync → RCS Send`

- 路由/解析/Final 失败：RedoCount 不增加；原任务状态/错误不变；不发送。
- 达 MaxRedo：不发送、不再增加。
- 校验成功后：原子抢占一次预算；仅抢占成功者发送；并发 Tracker 不得超预算。
- RCS 发送失败：本次已形成真实发送尝试，RedoCount 保持消耗（既有语义）。
- **不采用**「先增加、拒发后减回」竞态补偿。
- Tracker **不得**自行复制 Resolver/Validator，应调用 Service 统一入口。

### D15｜真实生产边界测试 — **已锁定**

R9–R15 及审查补充测试必须覆盖真实：`RcsTaskService`、`ManagedDispatchRouteResolver`、`RoutingAvailabilityValidator`、生产结果映射、**无 EquipmentId 的 AREA/FRAME 种子形状**。禁止仅用 `TracingTaskService` 证明 Final 安全。

必须覆盖：AREA→POSITION 上料；POSITION→AREA 下料；POSITION→POSITION 交接；FRAME↔AREA 换架；POSITION/FRAME→PALLET_RETURN 回收；任一端点 STATE=1 拒发；预记后 AREA/FRAME 禁用回滚且不 Alarm（回滚失败才 Alarm）；Grab/Identify/盘点合法与拒发；Tracker 拒发不耗 RedoCount；生产 DI 真实装配；无 null/AlwaysAvailable/Skip。

**PlcPoint（已锁定本期范围）：**

- 本期**不作为** RCS 端点 Final 依赖；
- `PointDisabled` 保留为后续能力，**不计入** P0-5 关闭门禁；
- 不删除现有 `PlcPointSource` 活动过滤；
- 不在本期扩展 PLC 点位生命周期策略。

**ADR/PRD：** 不建（维持 D9）。

---

#### 审查阻塞 RED 1–26（**已锁定分组顺序**）

**第一个 RED：** `ManagedDispatchRouteResolver.ResolveAsync` —— 无 EquipmentId 的活动 AREA 可解析（RED 1）。随后立刻接真 `RcsTaskService.DispatchTransitAsync` AREA→POSITION（RED 6）。

##### 第一组——端点类型与真实 Transit

| # | 场景 |
|---|---|
| 1 | 无 EquipmentId 的活动 AREA 可解析 |
| 2 | 无 EquipmentId 的活动 FRAME 可解析 |
| 3 | AREA/FRAME 禁用拒绝 |
| 4 | POSITION 缺 EquipmentId 拒绝 |
| 5 | 同码多行歧义拒绝 |
| 6 | 真 `RcsTaskService` 执行 AREA→POSITION 成功 |
| 7 | 真 `RcsTaskService` 执行 POSITION→AREA 成功 |
| 8 | 真 `RcsTaskService` 执行 FRAME↔AREA 成功 |
| 9 | 禁用端点 Final 拒发 |
| 10 | R9–R15 真 Service 回归（含预记后禁用回滚；RouteUnavailable≠Alarm） |

##### 第二组——移除 Skip 与新入口

| # | 场景 |
|---|---|
| 11 | PalletReturn POSITION→PALLET_RETURN 成功 |
| 12 | PalletReturn FRAME→PALLET_RETURN 成功 |
| 13 | From 未知 / From=AREA 拒绝 |
| 14 | To 非 PALLET_RETURN / To 禁用拒绝 |
| 15 | 全仓无 `SkipManagedRouteGate` |
| 16 | Grab 合法成功 / 禁用拒绝 |
| 17 | Identify 合法成功 / 禁用拒绝 |
| 18 | Inventory Identify Final 失败不 Create / 不 Excute |
| 19 | ChangeFrame pull/push 合法成功 / 禁用拒绝 |

##### 第三组——Tracker 与装配

| # | 场景 |
|---|---|
| 20 | 路由禁用时 RedoCount 不增加 |
| 21 | 活动路由仅增加一次并发送一次 |
| 22 | 达到上限不增加不发送 |
| 23 | 并发 Tracker 不超预算 |
| 24 | 发送失败仍消耗一次真实尝试 |
| 25 | 生产 DI 真实装配 Resolver/Validator/RcsTaskService |
| 26 | 所有新执行入口枚举无绕过 |

---

#### 249 测试未覆盖的真实生产边界（显式标记；D15 关闭前必须补齐）

- 无 EquipmentId 的 AREA/FRAME 合法派工与软删拒发
- `SkipManagedRouteGate` / 空托盘回收（POSITION|FRAME→PALLET_RETURN）
- Grab / Identify / 盘点发送边界
- 换架 pull/push
- Tracker 先门禁后计数
- 生产 DI 装配防绕过
- R9–R15 真 `RcsTaskService`（含 RouteUnavailable≠Alarm）

---

#### 锁定确认纪要（2026-08-05）

| # | 问题 | 锁定结论 |
|---|---|---|
| 1 | AREA/FRAME 无 EquipmentId | **合法模型**（D11） |
| 2 | SkipManagedRouteGate | **必须删除**（D12） |
| 3 | Grab/Identify | **必须纳入 Final**（D13） |
| 4 | Tracker | **必须先门禁后计数**（D14） |
| 5 | R9–R15 | **必须改打真实 RcsTaskService**（D15） |
| 6 | ADR/PRD | **不需要**（D9） |
| 7 | PlcPoint | **本期 N/A / 后续**（D15） |

### 审查补充第一组 RED 纪要（类型化端点 + 真 RcsTaskService，2026-08-05）

> 此内容由 AI 在 RED 期间生成。

**状态：** 保持 `ready-for-agent`（**GREEN 尚未开始**：未改 Resolver/Validator/RcsTaskService；未删 Skip）

#### 真实种子形状（测试常量 `TypedEndpointSeedShapes`）

对齐 `cnc_schema.sql` 六 b：

| 类型 | RcsCode 例 | LocType | RcsType | EquipmentId | PositionId | FrameId |
|---|---|---|---|---|---|---|
| AREA | 601001 / 609001 / 650001–650003 | AREA | station | **null** | null | null |
| POSITION | 601203 | POSITION | cell | 1 | 1 | null |
| FRAME shelf/cell | 651002 / 653002 | FRAME | shelf/cell | **null** | null | 2 |

禁止给 AREA/FRAME 伪造 EquipmentId。`FakeLocationMapForRouting.Seed` 按传入原样入账；新增 `DisableAfterFindCount` / `SnapshotByCode` 供 TOCTOU。

#### 测试文件

- `tests/CncLoader.Core.Tests/Routing/TypedManagedRouteResolverTests.cs` — 真 `ManagedDispatchRouteResolver`
- `tests/CncLoader.Core.Tests/Routing/RcsTaskServiceTypedEndpointTests.cs` — 真 `RcsTaskService` + Resolver + Validator + FakeRcsHttpClient
- `tests/CncLoader.Core.Tests/Routing/TypedEndpointSeedShapes.cs` — 种子形状
- `ManualReplayRoutingGateFakes.FakeLocationMapForRouting` — Find 计数/禁用钩子（测试专用）

#### Resolver / 真 Service 结果

| # | 场景 | 结果 | 实际现象 |
|---|---|---|---|
| RED1 | 活动 AREA 无Eq → Resolved | **RED** | `InvalidRelationship`（强制 EquipmentId） |
| RED2 | 活动 FRAME 无Eq → Resolved + FrameId | **RED** | 同上；并记录：Resolver 构造函数无 Frame 权威查询接缝 |
| 契约3 | AREA/FRAME STATE=1 → Disabled≠NotFound | **PASS** | |
| 契约4 | POSITION 缺 Eq → InvalidRelationship | **PASS** | 未因 AREA 规则放宽 |
| 契约5 | 双活动同码 / 跨类型同码 → Ambiguous；双禁用 → Disabled | **PASS** | |
| 契约5 | 一活动一禁用 → 应 Resolved | **RED** | 未 Ambiguous，但仍 InvalidRelationship（同源 RED1） |
| RED6 | 真 Service AREA→POSITION | **RED** | Create=0 Transit=0；Resolver 拒后未进 Validator |
| RED7 | 真 Service POSITION→AREA | **RED** | 同上 |
| RED8 | 真 Service FRAME→AREA | **RED** | 同上 |
| RED9 | 真 Service AREA→FRAME | **RED** | 同上 |
| 契约10 | POSITION→POSITION | **PASS** | 不回归 |
| Final | AREA/FRAME 禁用前拒发；WorkLine 禁用拒发；Redo TOCTOU 钩子 | **PASS** | Create=0 Send=0 |
| Audit | R9–R15 仍存在 `TracingTaskService` | **PASS** | 旧测未删；本批为补充真 Service RED |

**过滤：** 19 测 → **失败 7 / 通过 12**（失败均为契约 RED，非 NRE/DI/超时）
**全量：** **268** 测 → 失败 7 / 通过 **261**（原 249 全部继续通过）

#### 旧 TracingTaskService 缺口（未改）

`DispatchRoutingGateFakes.TracingTaskService` 仍服务 R9–R15：直接计 `DispatchTransitCount`，**不经** `ManagedDispatchRouteResolver`。本轮仅新增真 Service RED，不删除/不改写旧断言。

#### GREEN 状态

尚未开始：未改 `ManagedDispatchRouteResolver` / Validator / `RcsTaskService`；未删 `SkipManagedRouteGate`；未动 Grab/Identify/Tracker/DI。

#### 下一步（GREEN）

从 `ManagedDispatchRouteResolver.ResolveAsync` 类型化 POSITION/AREA/FRAME 开始，使 RED1/2/6–9 转绿；Validator 须识别 AREA/FRAME 的 Equipment NotApplicable。

### 审查补充第一组 GREEN 纪要（类型化端点 + 真 RcsTaskService，2026-08-05）

> 此内容由 AI 在 GREEN 期间生成。

**状态：** 保持 `ready-for-agent`（第一组 RED→GREEN 完成；**未删** `SkipManagedRouteGate`；Grab/Identify/Tracker/PalletReturn 仍阻塞）

#### EndpointKind 模型

- `ManagedEndpointKind`：Position / Area / Frame
- `ManagedDispatchEndpoint`：仅能经 `TryCreate(LocationMapRoutingRow)` 从持久化 `LocType` 映射；未知 LocType → `InvalidRelationship`
- `DispatchRouteContextFactory.FromEndpoints`：按 Kind 生成 Required/NotApplicable，调用方不能把 Position 的 Equipment 标成 N/A
- 不把 `null EquipmentId` 自动解释成 AREA/FRAME

#### AREA/FRAME 合法 N/A

- AREA：Equipment/Position/Frame 合法 null；不进 Equipment/Craft/WorkLine 查询
- FRAME：Equipment/Position 合法 null；须 `FrameId` + `IFrameRoutingStore` 活动态
- POSITION：仍强制 EquipmentId/PositionId；缺则 `InvalidRelationship`
- Validator：N/A 由 `FromEndpoint`/`ToEndpoint`.Kind 决定，非「所有 null 都忽略」
- FrameBind：通用 FRAME↔AREA 不因无 Equipment 误杀；仅机台上下文存在时校验 Bind

#### Frame 权威 Store

- 新增 `IFrameRoutingStore` / `FrameRoutingSnapshot` + 生产 `FrameRoutingStore`
- Resolver + Validator 均注入（非可选 null）
- DI：`AddCncData` 注册真实实现；无 AlwaysAvailable

#### 真 Service Pre/Final

`DispatchTransitAsync` 顺序：

`ResolvePre → ValidatePre → ResolveFinal → ValidateFinal → Create → RCS`

（`SkipManagedRouteGate` 仍存在，本轮不删）

#### RED→GREEN

| # | 场景 | 结果 |
|---|---|---|
| RED1 | 活动 AREA 无Eq → Resolved | **GREEN** |
| RED2 | 活动 FRAME 无Eq → Resolved + Frame 接缝 | **GREEN** |
| 契约5 一活动一禁用 | 唯一活动 AREA → Resolved | **GREEN** |
| RED6–9 | 真 Service AREA/FRAME 组合 | **GREEN** |
| 补充 | Frame 实体禁用/缺失、未知 LocType、Equipment 查询隔离、Final 前禁用、Pre+Final 序、DI 契约 | **PASS** |

**过滤专项：** TypedManagedRouteResolver + RcsTaskServiceTypedEndpoint → **31 通过**
**R1–R24 门禁过滤：** **45 通过**
**全量：** **280 通过**（原 249 + 本批类型化专项/补充）
**build：** `dotnet build CncLoader.sln` → 0 error 0 warning
**diff-check（src/tests）：** 无 whitespace error

#### 仍阻塞（不得进入最终回归）

- D12：删除 `SkipManagedRouteGate` + PalletReturn（**GREEN 已完成**，见下节 GREEN 纪要）
- D13：Grab / Identify / 盘点 Final
- D14：Tracker 先门禁后计数
- D15：R9–R15 改打真实 Service；换架业务入口收紧

#### 下一步

Grab / Identify / Inventory Final 门禁 **RED**（D12 GREEN 完成后）。

### 审查补充第二组 RED 第一部分纪要（删除 Skip + PalletReturn，2026-08-05）

> 此内容由 AI 在 RED 期间生成。本轮**未改**生产代码；**未删** Skip；**未进** GREEN。

**状态：** 保持 `ready-for-agent`

#### PalletReturn 真实链（还原）

| # | 环节 | 现状 |
|---|---|---|
| 1 | UI 入口 | `RcsViewModel.PalletReturnCommand` → `PalletReturnAsync` |
| 2 | From | `PalletReturnFromCode` 自由文本；`Trim()` 后下发 |
| 3 | To | `_locationMap.ResolveAreaAsync(_options.PalletReturnArea)` → 取活动 AREA 的 `RcsCode`；默认配置 `PALLET_RETURN` |
| 4 | ResolveArea 时机 | **仅 UI Pre**（构造内存请求前一次）；Service 内无 Final 重读 |
| 5 | Skip 设置 | `RcsTaskService.DispatchPalletReturnAsync` → `SkipManagedRouteGate = true`（全仓唯一赋值点） |
| 6 | Skip 跳过 | `DispatchTransitAsync` 内 Pre Resolve/Validate **与** Final Resolve/Validate **全部跳过** |
| 7 | 副作用序 | Create 本地任务 → `TransitTaskAsync` RCS（门禁前无预记） |
| 8 | 通知 | `ReportResult` → Success / RouteUnavailable Warning；空 From/缺 To 走 `Growl.Warning`（非 TestNotifications） |
| 9 | 缓存 | 同 VM 实例不缓存 Resolved 结论；但 Skip 使权威门禁根本不跑 |
| 10 | 其他 Skip=true | 仅 `DispatchPalletReturnAsync`；直接 `DispatchTransitAsync(..., Skip=true)` 亦可公共绕过 |

#### Skip 公共绕过证据

- 反射：`TransitDispatchArgs.SkipManagedRouteGate` 仍为 public
- RED2：未知 From/To + `Skip=true` → 当前仍 Create+RCS（无 Resolver）
- 契约3/4：POSITION/FRAME→PALLET_RETURN **能发送 Success**，但 Resolver/Validator **CallCount=0**（证明不经 Pre/Final）

#### 合法组合（期望 / 当前）

| 组合 | 期望 | 当前 |
|---|---|---|
| POSITION→PALLET_RETURN | Pre+Final + Create/RCS/Success 各 1 | 发送成功但 **门禁次数=0** → RED |
| FRAME→PALLET_RETURN | 同上 + Frame 活动校验 | 发送成功但 **门禁/Frame 未读** → RED |

#### 拒绝矩阵（期望 / 当前）

| # | 场景 | 期望 | 当前 |
|---|---|---|---|
| RED5 | From 未知 | 拒 | Skip 发送 → RED |
| RED6 | From=AREA | 拒 | Skip 发送 → RED |
| RED7 | From POSITION Map/线体禁用 | 拒 | Skip 发送 → RED |
| RED8 | From FRAME Map/实体禁用 | 拒 | Skip 发送 → RED |
| RED9 | To≠PALLET_RETURN（Service） | 拒 | 无角色策略+Skip → RED |
| RED9b | `DispatchOperationKind` | 须存在 | **缺失** → RED |
| RED10 | UI Resolve 后 To 禁用 | Final 拒 | Skip 用旧码直发 → RED |
| RED11 | 双活动同码 | Ambiguous 拒 | Skip 发送 → RED |
| RED12 | 同实例二次（禁用后） | 总 RCS=1 | 二次仍发 → RED |

#### Pre/Final 调用缺口

PalletReturn 路径：**Resolver=0 / Validator=0**（被 Skip 短路）。通用 Transit（非 Skip）已有 Pre+Final；空托盘回收未对齐。

#### 测试结果

| 过滤 | 结果 |
|---|---|
| `FullyQualifiedName~PalletReturnRoutingGate` | **失败 15 / 通过 1**（Chain 文档化 PASS；其余均为契约 RED） |
| 全量 Core.Tests | **失败 15 / 通过 281 / 总计 296**（原 280 全部继续通过） |

测试文件：`tests/CncLoader.Core.Tests/Routing/PalletReturnRoutingGateTests.cs`
夹具：`ManualReplayHarness.CreateForPalletReturn()` + `CountingManagedRouteResolver` + `DisableAreaAfterResolveCount`

#### GREEN 状态

**已完成**（见下节 GREEN 纪要）。本 RED 节保留为改前证据快照。

---

### 审查补充第二组 GREEN 第一部分纪要（删除 Skip + PalletReturn，2026-08-05）

> 此内容由 AI 在 GREEN 期间生成。D1–D15 正文未改；Status 保持 `ready-for-agent`。

**状态：** 保持 `ready-for-agent`（D12 Skip+PalletReturn **GREEN**；D13 Grab/Identify/Inventory **GREEN**；Tracker/ChangeFrame 仍未进入）

#### 生产改动

| 项 | 结果 |
|---|---|
| `TransitDispatchArgs.SkipManagedRouteGate` | **已删除**（属性 / if-skip / 赋值点全清；`src` 无残留） |
| `DispatchOperationKind` | 新增 `Transit` / `PalletReturn`；挂在 `TransitDispatchArgs.Operation` |
| `DispatchPalletReturnAsync` | 设置 `Operation=PalletReturn` + `Kind=PalletReturn`；走统一 `DispatchTransitAsync` |
| 门禁顺序 | `Resolve→ValidatePre→角色策略→ResolveFinal→ValidateFinal→角色策略→Create→RCS` |
| PalletReturn 角色 | From∈{Position,Frame}；To=AREA 且 `LocName` 精确等于 `PALLET_RETURN` |
| 绕过 | 无 Skip / AlwaysAvailable / 可选 Validator；普通 Transit 不按目标字符串放宽 |

#### 调用证据（成功路径）

- POSITION→PALLET_RETURN / FRAME→PALLET_RETURN：`Resolver.CallCount≥2`、`Validator.CallCount≥2`（Pre+Final）
- 拒发：Create=0、RCS=0、Success=0、Warning≥1；无 PLC / 预记副作用
- Red9：直接 `DispatchPalletReturnAsync(..., To=LOAD_AREA)` 仍拒（Service 角色策略，非仅 UI）

#### 测试结果

| 过滤 | 结果 |
|---|---|
| RED 基线（改前） | PalletReturn **失败 15 / 通过 1**；全量 **失败 15 / 通过 281 / 总计 296** |
| `FullyQualifiedName~PalletReturnRoutingGate` | **失败 0 / 通过 16** |
| 路由回归（Typed+TypedEndpoint+Manual+Replay+Dispatch+DirectHandoff+Historical+PalletReturn） | **失败 0 / 通过 101** |
| 全量 Core.Tests | **失败 0 / 通过 296 / 总计 296** |
| `dotnet build CncLoader.sln` | **0 error / 0 warning** |
| `git diff --check -- src tests` | **无 whitespace error**（Issue 历史段落既有 trailing whitespace 未动） |

#### 仍阻塞（不得进入最终回归）

- D13：Grab / Identify / 盘点 Final
- D14：Tracker 先门禁后计数
- D15：R9–R15 改打真实 Service；换架业务入口收紧

#### 下一步

Grab / Identify / Inventory Final 门禁 **RED**（见下节；本轮已锁定）。

---

### 审查补充第二组 RED 第二部分纪要（Grab / Identify / Inventory，2026-08-05）

> 此内容由 AI 在 RED 期间生成。**生产代码零修改**；未进 GREEN；未动 Tracker/ChangeFrame。

**状态：** 保持 `ready-for-agent`

#### 真实调用链（源码核对）

| 入口 | 参数 / 端点 | Create → RCS 顺序 | 门禁 |
|---|---|---|---|
| `DispatchGrabAsync` | `SrcStation` + `DstStation`（双端）；`Items` JSON | `CreateAsync` → `ExcuteTaskAsync(grabTask)` | **无** Resolve/Validate |
| `DispatchIdentifyAsync` | `Station`（单端）；`PosStart,Count` | `CreateAsync` → `ExcuteTaskAsync(identifyQR)` | **无** Resolve/Validate |
| `InventoryService.StartInventoryAsync` | `ResolveFrame(station)` 失败则 `ResolveFrame(shelf)` → `DispatchIdentifyAsync` | 业务 Pre 后同上 Identify | 业务 Pre 有 Map/线体检查；**Service Final 无** |

**盘点形态（当前生产唯一）：** 仅 Identify，**不调用 Grab**。无「Grab 成功后再 Identify」分阶段。

#### 盘点 / 辅助操作种子形状（对齐 `cnc_schema.sql` 六 b + `TypedEndpointSeedShapes`）

| LocType | LocName / 名 | RcsType | RcsCode 例 | EquipmentId | FrameId |
|---|---|---|---|---|---|
| AREA | LOAD_AREA / UNLOAD_AREA / FULL_BUFFER / EMPTY_BUFFER / PALLET_RETURN | station | 601001 / 609001 / 650001–003 | **null** | null |
| FRAME | 料架名 | shelf | 651002（中转） | **null** | 有 |
| FRAME | 料架 cell 名 | cell | 653002 | **null** | 有 |

Grab 合法基线测试用：AREA(station)→AREA(station)（LOAD→UNLOAD）。Identify / Inventory：FRAME(shelf)。

#### RED 用例与数字

| 过滤 | 结果 |
|---|---|
| `RcsAuxiliaryOperationRoutingGate\|InventoryRoutingGate` | **失败 18 / 通过 8 / 总计 26** |
| 既有回归（PalletReturn+TypedEndpoint+Manual+Replay+Historical） | **0 失败 / 61 通过** |
| 全量 Core.Tests | **失败 18 / 通过 304 / 总计 322**（原 296 全部继续通过） |
| `dotnet build CncLoader.sln` | **0 error / 0 warning** |
| `git diff --check -- src tests` | **无 whitespace error** |

#### PASS（8）— 基线 / 文档 / 业务 Pre

- Chain 文档化 Grab/Identify 与 Inventory 种子
- Grab / Identify / Inventory **全活动基线成功**（Create=1、Excute=1；AREA/FRAME 无 EqId）
- Inventory 启动前 Map 已禁用 → Create=0（业务 `ResolveFrame` Pre）
- Inventory 同实例二次禁用 → 业务 Pre 拒（无 Service Final）
- Inventory 不派发 Grab

#### RED（18）— Actual 摘要

| 契约 | Actual |
|---|---|
| Grab/Identify 须 Resolver/Validator≥1 | **CallCount=0**；顺序 `CreateTask→RcsExcute` |
| Grab/Identify 禁用/未知/歧义拒发 | **仍 Success + Create=1 + Excute=1** |
| Identify Frame 实体禁用/缺失 | **仍发送** |
| Identify AREA 角色拒绝 | **仍发送** |
| Grab/Identify DisableAfterFind TOCTOU | **Find 从未调用**；仍 Create |
| Grab 取消令牌 | **仍 Create+Excute**（忽略 CT） |
| Identify 同实例软删二次 | **二次仍 Excute**（总 2） |
| Inventory Service Final | Resolver/Validator=**0** |
| Inventory Pre 后禁 Map | **仍 Create+Excute** |
| 直调 Identify 禁用码 | **仍 Success**（证明 Service 自身无门禁） |

#### GREEN 所需最小约束（本轮不实现）

- `DispatchGrabAsync` / `DispatchIdentifyAsync` 发送边界：`ResolveCurrent/ValidateFinal → Create → Excute`
- 操作角色：Grab 双端受管（种子 station 组合）；Identify 单端 FRAME(shelf/station)；不得任意 AREA
- Inventory 继续业务 Pre；**权威在同一 Service Final**；Final 失败不得留「已发起」假态、不 Create/Excute
- 建议 GREEN：`DispatchOperationKind.Grab` / `Identify`（或等价不可伪造上下文）；**不改签名亦可**先按 args 内 station 做类型化 Resolve

#### 残余风险（记入未验证）

- Inventory 业务 Pre 缺 Map 会 `RaiseRcsTaskNotFound`（与 D8 后台不 Alarm 目标有差距）— **GREEN 已改为无 Alarm**
- 无 Grab→Identify 跨调用原子性需求（生产无此链）
- Final→HTTP 窗口；真 MySQL/RCS/UI 未测

#### 下一步

Grab / Identify / Inventory **GREEN**（见下节；本轮已完成）。

---

### 审查补充第二组 GREEN 第二部分纪要（Grab / Identify / Inventory Final，2026-08-05）

> 此内容由 AI 在 GREEN 期间生成。D1–D15 正文未改；Status 保持 `ready-for-agent`。

**状态：** 保持 `ready-for-agent`（D13 Grab/Identify/Inventory **GREEN 完成**；Tracker / ChangeFrame 专项仍未进入）

#### 实现方式

| 项 | 做法 |
|---|---|
| 安全边界 | `RcsTaskService.DispatchGrabAsync` / `DispatchIdentifyAsync` |
| 顺序（Final-only） | `ValidateFinalAsync`（内含 Resolve+Validate）→ 操作角色 → Create → Excute |
| 操作语义 | **方法固定策略**（不强加公共 bool / 不扩 `DispatchOperationKind`）；调用方无法 Skip |
| Grab 角色 | 双端均为 `AREA` 且 `LocName`∈配置角色（`IsConfiguredAreaRole`） |
| Identify 角色 | 单端（Station 作 From=To）必须为 `FRAME`；拒绝 AREA/POSITION |
| 复用 | `IManagedDispatchRouteResolver` / `IRoutingAvailabilityValidator` / `ManagedDispatchEndpoint` |
| Inventory | 仍调真实 `DispatchIdentifyAsync`；业务 Pre 保留；**路由不可用分支不 Alarm**（缺 Map / 无线体 / Service `RouteUnavailable`/`ConfigurationUnavailable`）；非路由 RCS 失败仍 Alarm |

#### 测试接缝

- Final-only TOCTOU：`DisableBeforeReturnOnFindCount`（与 Transit Pre+Final 的 `DisableAfterFindCount` 并存）
- Inventory 路由拒发：`RaiseCount=0` 断言

#### 验证数字

| 过滤 | 结果 |
|---|---|
| RED 基线（改前） | **失败 18 / 通过 8 / 总计 26** |
| `RcsAuxiliaryOperationRoutingGate\|InventoryRoutingGate` | **失败 0 / 通过 26** |
| 路由回归（PalletReturn+TypedEndpoint+Manual+Replay+Dispatch+DirectHandoff+Historical） | **失败 0 / 通过 86** |
| 全量 Core.Tests | **失败 0 / 通过 322 / 总计 322** |
| `dotnet build CncLoader.sln` | **0 error / 0 warning** |
| `git diff --check -- src tests` | **无 whitespace error** |

#### 调用证据（合法路径）

- Grab AREA→AREA / Identify FRAME(shelf)：`Resolver≥1`、`Validator≥1`；顺序含 `Resolve` 且在 `CreateTask` 前；Create=1、Excute=1
- 拒发：Create=0、Excute=0；Inventory 路由拒发 Alarm=0

#### 仍阻塞

- D14：Tracker 先门禁后计数
- D15：换架业务入口收紧 / R9–R15 真 Service 收口（若仍有缺口）

#### 下一步

ChangeFrame FRAME↔AREA 真实 Service 专项 **RED**（见下节；本轮已锁定）。

---

### 审查补充第三组 RED 纪要（ChangeFrame FRAME↔AREA，2026-08-05）

> 此内容由 AI 在 RED 期间生成。**生产代码零修改**；未进 GREEN；未动 Tracker；Status 保持 `ready-for-agent`。

**状态：** 保持 `ready-for-agent`（D13 Grab/Identify/Inventory **GREEN**；ChangeFrame **RED 锁定**；Tracker 仍未进入）

#### 真实 ChangeFrame 调用链（源码核对）

```
ChangeFrameOrchestrator.ChangeFrameAsync(equipmentId, role, author)
  1. IEquipmentConfigService.GetFrameBindingIdsAsync  → Upload(role0)/Unload(role1) FrameId（滤 STATE=0）
  2. ILocationMapService.ResolveFrame(frameId,"cell") ?? ResolveFrame(...,"shelf")
  3. ResolveArea(EmptyBufferArea|FullBufferArea)  // Upload→EMPTY_BUFFER；Unload→FULL_BUFFER
  4. GetWorkLineByEquipmentAsync（STATE 三层活动）
  5. IRcsTaskService.DispatchTransitAsync  // pull：FRAME→AREA；Kind=ChangeFrame；带 EquipmentId/TxnId
       └─ 真实 RcsTaskService：ResolvePre→ValidatePre→ResolveFinal→ValidateFinal→Create→TransitTask
  6. Progress=PullOld/RUNNING；失败 → Alarm + RaiseRcsTaskCanceled/NotFound

回调 RcsCallbackNotifier.TaskStatusReceived(pull Completed)
  → DispatchTransitAsync push：AREA→FRAME（From=Buffer，To=FrameCell）
  → Progress=PushNew / 失败 Alarm
```

| 段 | From | To | LocType | 备注 |
|---|---|---|---|---|
| pull Upload | FRAME cell/shelf | AREA `EMPTY_BUFFER` | FRAME→AREA | RcsCode 例 653002→650002；EqId=null 合法 |
| pull Unload | FRAME cell/shelf | AREA `FULL_BUFFER` | FRAME→AREA | 653003→650001 |
| push | AREA 缓冲 | FRAME cell | AREA→FRAME | 逆方向 |

- **最终发送：** 真实 `RcsTaskService.DispatchTransitAsync`（非直接 RCS Client；非 Grab/Identify）。
- **FRAME/AREA 选择：** Orchestrator 业务 Pre；Service Final 再 Resolve 权威 Map。
- **FrameBind：** 仅业务 Pre（`GetFrameBindingIdsAsync`）读取；**Final Context `SourceEquipmentId=0`**（`DispatchRouteContextFactory` 仅 Position 填 Eq），**Validator 不校验 Bind**。
- **顺序：** BindQuery → ResolveFrame/Area → WorkLine →（Service）Resolve→Validate×2 → Create → RcsTransit。
- **失败补偿：** pull 失败不预记槽位；Raise Alarm；无 P0-2 槽位回滚。Final 拒发时若业务已写 Progress Alarm，无「已派发」成功态。
- **通知：** 路由缺失/下发失败走 `RaiseRcsTaskNotFound` / `RaiseRcsTaskCanceled`（与 D8「自动拒发不 Alarm」有偏差）。

#### FRAME/AREA/FrameBind 种子模型

对齐 `cnc_schema.sql` 六 b + `TypedEndpointSeedShapes`：

| 实体 | 形状 |
|---|---|
| AREA EMPTY/FULL_BUFFER | LocType=AREA，RcsType=station，EquipmentId/PositionId/FrameId=**null** |
| FRAME cell/shelf | LocType=FRAME，FrameId 有，EquipmentId=**null** |
| FrameBind | Upload→FrameIdTransit；Unload→FrameIdDownload；STATE=0 |
| 机台链 | Line/Craft/Equipment 全活动 |

#### pull/push 操作矩阵（本轮覆盖）

| 操作 | 合法基线 | Map/Frame 软删 | Ambiguous/未知 STATE | Bind | TOCTOU |
|---|---|---|---|---|---|
| pull FRAME→EMPTY | PASS | PASS（拒） | PASS | 业务 Pre PASS；Final Bind **RED** | Map/Frame 实体 PASS；Bind **RED** |
| push AREA→FRAME | PASS | PASS（拒） | — | 同左 | 禁 Map 后 push 拒 PASS |
| 通用 Transit FRAME→AREA（无机台上下文） | PASS（不要求 Bind） | — | — | 不得误杀 | — |

#### 测试结果

| 过滤 | 结果 |
|---|---|
| `FullyQualifiedName~ChangeFrameRoutingGate` | **失败 4 / 通过 31 / 总计 35** |
| Grab/Identify/Inventory/PalletReturn/TypedEndpoint | **0 失败 / 58 通过** |
| Manual/Replay/Dispatch/DirectHandoff/Historical | **0 失败 / 54 通过** |
| 既有路由回归（含 Typed+Manual+…+PalletReturn，无 ChangeFrame） | **0 失败 / 86 通过** |
| 全量 Core.Tests | **失败 4 / 通过 353 / 总计 357**（原 322 全部继续通过） |
| `dotnet build CncLoader.sln` | **0 error / 0 warning** |
| `git diff --check -- src tests` | **无 whitespace error**（Issue 历史段落既有 trailing whitespace 未动） |

测试文件：`tests/CncLoader.Core.Tests/Routing/ChangeFrameRoutingGateTests.cs`
接缝：`TypedEndpointSeedShapes.SeedChangeFrame*`、`MutableEquipmentRoutingStore` Bind 计数/TOCTOU、`FakeFrameRoutingStore.DisableAfterFindCount`、`FakeLocationMapForRouting.MutateLocName*`、`IdlePositionScheduler`。

#### PASS（31）— 摘要

- 链文档化；pull Upload/Unload 与 push 全活动基线（Create/Transit=1；Resolver/Validator=2；AREA/FRAME 无 EqId）
- Map/Frame 实体禁用/缺失/未知 STATE/Ambiguous/AREA 角色篡改后 Final 拒；业务 Bind 缺失/禁用/错 Eq
- Pre 后禁 FRAME/AREA Map、禁 Frame 实体 → Final 拒；同实例二次禁用；取消/Resolver|Validator 异常 fail-closed
- 通用 Transit 无 Bind 要求仍成功

#### RED（4）— Actual

| 契约 | Actual |
|---|---|
| Pre 后禁 FrameBind → Final 拒 | **仍 RUNNING；Create=1 Transit=1；BindQueries=1；顺序 Resolve→Validate×2→Create→RcsTransit；SrcEq=0** |
| Final Context 携带换架 Equipment → Bind Required | **Contexts 均 SourceEquipmentId=0；仍发送** |
| 错误 LocType（FRAME 码改 AREA）须拒 | **业务 ResolveFrame 不看 LocType；Service 当 AREA→AREA 放行；仍发送** |
| D8 路由拒发不 Alarm | **RaiseCount=1**（`RaiseRcsTaskNotFound`） |

#### GREEN 所需最小生产改动（本轮不实现）

1. **Context：** `DispatchTransitAsync` / `DispatchRouteContextFactory` 在 `Kind=ChangeFrame`（或显式 Operation）且 `TransitDispatchArgs.EquipmentId` 有值时，把机台写入 FRAME 端点的 Bind 校验上下文（`SourceEquipmentId` / dest bind Eq），使 Validator 既有 FrameBind 分支生效。
2. **Final Bind：** 换架+机台上下文 → FrameBind Required（不存在/禁用/错指向拒发）；**不得**让通用无 Eq 的 FRAME↔AREA Transit 无条件要求 Bind。
3. **换架角色（可选同轮）：** Service 角色策略限制 pull From=FRAME / To=AREA∈{EMPTY_BUFFER,FULL_BUFFER}（及 push 逆），避免 LocType 被篡改后落入普通 AREA→AREA。
4. **通知（可另票）：** 路由 `RouteUnavailable`/`ConfigurationUnavailable` 对齐 D8：Warning、不 Raise 工位 Alarm；已下发失败路径保持既有告警。

#### 残余风险（未验证）

- 真 MySQL / 真 RCS / 真实换架 UI / WaterMonitor 自动换架
- Final→HTTP 窗口；push 火忘回调与真并发
- Tracker RedoCount；已下发换架 callback/Confirm 收口（本轮未接新门禁，符合 D2）

#### 下一步

ChangeFrame FRAME↔AREA **GREEN**（见下节；本轮已完成）。Tracker RED 仍阻塞最终回归。

---

### 审查补充第三组 GREEN 纪要（ChangeFrame Final Bind + D8，2026-08-05）

> 此内容由 AI 在 GREEN 期间生成。D1–D15 正文未改；Status 保持 `ready-for-agent`。

**状态：** 保持 `ready-for-agent`（D13 Grab/Identify/Inventory **GREEN**；ChangeFrame Final Bind+D8 **GREEN**；Tracker 仍未进入）

#### 生产改动

| 项 | 做法 |
|---|---|
| `DispatchOperationKind.ChangeFrame` | 新增；Orchestrator pull/push 设置 `Operation=ChangeFrame` + `EquipmentId` |
| `DispatchRouteContext` | 新增 `Operation`、`OperationEquipmentId`（非 Map.EquipmentId） |
| `RcsTaskService.DispatchTransitAsync` | ChangeFrame 缺 Eq≤0 fail-closed；Pre/Final 注入 Operation 上下文；角色 `FRAME↔AREA(EMPTY\|FULL_BUFFER)` |
| `RoutingAvailabilityValidator` | `Operation=ChangeFrame` 时：机台链活动 + FRAME 端点 Frame 实体 + `RequireActiveFrameBind(OpEq, FrameId)` |
| `ChangeFrameOrchestrator` D8 | 路由缺失 / `RouteUnavailable`/`ConfigurationUnavailable` → Warning，不 `RaiseRcsTaskNotFound/Canceled`；非路由失败保持 Raise |

#### Final Bind 条件

- Bind 存在且 `STATE=="0"`（null/空/未知 fail-closed via `ConfigActivity`）
- `EquipmentId` / `FrameId` 精确匹配 `OperationEquipmentId` + 路径 FRAME
- 关联 Equipment（Craft/WorkLine）活动；Frame 实体活动
- 普通 `Transit` / `PalletReturn` 不注入 `OperationEquipmentId`，不强制 Bind

#### pull/push 操作矩阵（GREEN 后）

| 操作 | From→To | Bind Final | 角色 |
|---|---|---|---|
| pull | FRAME→EMPTY/FULL_BUFFER | Required(OpEq) | Service 强制 |
| push | EMPTY/FULL_BUFFER→FRAME | Required(OpEq) | Service 强制 |
| 通用 Transit FRAME↔AREA | 任意受管 | **不**要求 Bind | Transit |

#### 4 RED → GREEN 证据

| RED | Actual（GREEN） |
|---|---|
| Context 无 EquipmentId | `OperationEquipmentId=EqId`；`Operation=ChangeFrame` |
| Final 不校验 Bind | Pre 后禁 Bind → Create=0 Transit=0；BindQueries≥2 |
| Pre 后禁 Bind 仍发送 | 同上拒发 |
| 路由拒发 Raise=1 | RaiseCount=0；非路由 RCS 失败 Raise≥1 保持 |

合法路径顺序：`Resolve→Validate→Resolve→Validate→CreateTask→RcsTransit`；Create=1 Transit=1；Raise=0。

#### 测试结果

| 过滤 | 结果 |
|---|---|
| RED 基线（改前） | ChangeFrame **失败 4 / 通过 31 / 总计 35** |
| `FullyQualifiedName~ChangeFrameRoutingGate` | **失败 0 / 通过 38** |
| Grab\|Inventory\|PalletReturn\|TypedEndpoint | **0 失败 / 58 通过** |
| Manual\|Replay\|Dispatch\|Handoff\|Historical | **0 失败 / 54 通过** |
| 全量 Core.Tests | **失败 0 / 通过 360 / 总计 360** |
| `dotnet build CncLoader.sln` | **0 error / 0 warning** |
| `git diff --check -- src tests` | **无 whitespace error** |

#### 仍阻塞（本段撰写时）

- D14：Tracker 先门禁后计数（RedoCount 顺序）— **已于下方 GREEN 解除**
- 最终回归 / 关 Issue

#### 下一步

Tracker RedoCount 顺序 **RED**（见下节；随后 GREEN 已完成）。

---

### 审查补充第四组 RED 纪要（Tracker 先门禁后 RedoCount，2026-08-05）

> 此内容由 AI 在 RED 期间生成。**生产行为零修改**（仅增 `ProbeAutoRedoOnceAsync` 探测接缝）；未进 GREEN；Status 保持 `ready-for-agent`。

**状态：** 保持 `ready-for-agent`（ChangeFrame **GREEN**；Tracker AutoRedo 顺序 **RED 锁定**）

#### 真实 Tracker 调用链（源码核对）

```
RcsCallbackNotifier.TaskStatusReceived / 轮询 Raise(poll)
  → HandleStatusEventAsync
       state==FAILED → AutoRedoAsync(taskId, source)
            1. TryIncrementRedoIfUnderAsync(taskId, MaxAutoRedo)   // 原子：RedoCount<max → +1, STATE=DISPATCHED, Error=null
            2a. success → RedispatchAsync(taskId)
                    ResolvePre→ValidatePre→ResolveFinal→ValidateFinal→BuildAndSend(redo)  // 不再 Increment
            2b. fail    → RaiseRcsRedoLimitAsync + NotifyTaskAbandoned(REDO_LIMIT)
```

**当前错误顺序：** `TryIncrement → Resolve/Validate → Send`
**D14 目标顺序：** `Resolve → ValidatePre → ResolveFinal → ValidateFinal → TryIncrement → Send`

#### 自动 / 手动计数责任表

| 入口 | 谁增 RedoCount | 门禁位置 | 备注 |
|---|---|---|---|
| Tracker `AutoRedoAsync` | `TryIncrementRedoIfUnderAsync`（先于门禁） | `RedispatchAsync` 内 | **RED：拒发仍耗次数 + 副作用改 Dispatched/清 Error** |
| 手动 `RedoAsync` | `IncrementRedoAsync`（门禁后） | Service 内 Pre+Final 后 | 已绿：拒发不 Increment |
| `RedispatchAsync` | **不**增 | Service 内 Pre+Final | 契约保持；由 Tracker 负责原子抢占 |

- `TryIncrement` 条件：`RcsTaskId` 匹配且 `RedoCount < max`；affected>0 → true；副作用含 STATE/Error。
- 双增风险：Tracker 用 TryIncrement，Redispatch 不再 Increment；手动 Redo 用另一方法 — 无双增 API，但 Tracker 顺序错误。
- ChangeFrame 历史重放：落库无 `Operation=ChangeFrame`；Redispatch 按 From/To Transit 门禁，**不伪造**换架 Context（本期记录为限制）。
- callback/对账：本轮未改；不接新执行门禁。

#### 测试结果

| 过滤 | 结果 |
|---|---|
| `FullyQualifiedName~RcsTaskTrackerRoutingGate` | **失败 13 / 通过 7 / 总计 20** |
| Replay\|Historical | **0 失败 / 16 通过** |
| ChangeFrame\|Grab\|Inventory\|PalletReturn | **0 失败 / 80 通过** |
| 全量 Core.Tests | **失败 13 / 通过 367 / 总计 380**（原 360 全部继续通过） |
| `dotnet build CncLoader.sln` | **0 error / 0 warning** |
| `git diff --check -- src tests` | **无 whitespace error** |

测试：`tests/CncLoader.Core.Tests/Routing/RcsTaskTrackerRoutingGateTests.cs`
接缝：`RcsTaskTracker.ProbeAutoRedoOnceAsync`；`MutableRcsTaskStore` TryIncrement 计数/OrderSink/TCS 门闩。

#### PASS（7）

- 链文档化；合法基线 Send=1 / IncSuccess=1 / Redispatch 不双增
- MaxRedo 达上限：IncSuccess=0 Send=0 + 上限告警
- Increment 抛异常：Send=0；Send 失败保持已耗次数（D14 边界）
- Redispatch 本身不 Increment；手动 Redo 拒发不 Increment

#### RED（13）— Actual 摘要

| 契约 | Actual |
|---|---|
| 顺序 Gate→Increment→Send | **`TryIncrement→Resolve→Validate→…→RcsTransit`** |
| Source/Target/Eq/Line/未知/歧义禁用 | **IncCalls=1；Redo+1；STATE→DISPATCHED；Error=null；Send=0** |
| Pre 后禁 Source/Target Final | **仍先 Increment；Redo+1** |
| 同实例二次禁用 | **仍 Increment（耗次数）** |
| 并发双 Probe（Max=3） | **IncSuccess=2；Transit=2** |
| Resolver 异常 | **仍先 Increment** |
| 路由拒发不耗次数 | **耗次数 + 任务被副作用改写** |

#### GREEN 所需最小职责调整（本轮不实现）

1. **Tracker `AutoRedoAsync`：** 删除先行 `TryIncrement`；改为调用统一 Service 入口（新建或扩展，例如 `RedispatchWithBudgetAsync` / `AutoRedoAsync` on Service）。
2. **Service：** `Resolve→ValidatePre→ValidateFinal → TryIncrementRedoIfUnderAsync → Send`；门禁失败 Increment=0、不改任务态/Error；仅抢占成功者 Send。
3. **保持：** 手动 `RedoAsync` 用 `IncrementRedoAsync`；`RedispatchAsync` 本身不增计数（若保留给已抢占调用方）；Send 失败不回滚次数；达上限告警语义。
4. **禁止：** 先增后减补偿；Tracker 内复制 Resolver/Validator。

#### 残余风险

- fake 锁 vs DB `ExecuteUpdate` affected-row 语义差异
- Final→HTTP 窗口；真并发 Tracker 轮询
- 历史 ChangeFrame 无 Operation Context 重放限制

#### 下一步

Tracker 先门禁后原子 RedoCount **GREEN**。

---

### 审查补充第四组 GREEN 纪要（Tracker AutoRedo 门禁后原子 Claim，2026-08-05）

> 此内容由 AI 在 GREEN 期间生成。未关闭 Issue；未进最终回归 / DI 绕过审计。Status 保持 `ready-for-agent`。

**状态：** 保持 `ready-for-agent`（Tracker AutoRedo 顺序 **GREEN**）

#### 13 RED → GREEN 证据

| RED 契约 | GREEN Actual |
|---|---|
| 顺序 Gate→Claim→Send | `Resolve→Validate→Resolve→Validate→TryClaim→RcsTransit` |
| Source/Target/Eq/Line/未知/歧义禁用 | ClaimCalls=0；Redo 不变；STATE/Error 不变；Send=0 |
| Pre 后禁 Source/Target Final | ClaimCalls=0；Send=0 |
| 同实例二次禁用 | ClaimCalls=0；不耗次数 |
| 并发双 Probe（Max=3） | ClaimSuccess=1；Transit=1；RedoCount=1；两者均可完成 Pre+Final（Resolver=4） |
| Resolver 异常 | ClaimCalls=0；Send=0 |
| 路由拒发不耗次数 | 字段全不变；无 Alarm |

#### Tracker / Service / Store 新职责

| 组件 | 职责 |
|---|---|
| `RcsTaskTracker.AutoRedoAsync` | 仅发现候选并调用 `AutoRedispatchAsync`；不直接 Claim/Increment |
| `IRcsTaskService.AutoRedispatchAsync` | Load→Resolve/Validate Pre+Final→`TryClaimAutoRedo`→Send |
| `IRcsTaskStore.TryClaimAutoRedoAsync` | 单条条件原子 Claim；替换旧 `TryIncrementRedoIfUnderAsync` |
| 手动 `RedoAsync` | 门禁后 `IncrementRedoAsync`（不变） |
| 手动 `RedispatchAsync` | 门禁后发送、不增 RedoCount（不变） |

#### 原子 Claim WHERE / SET

- WHERE：`RcsTaskId` 精确匹配 AND `TaskState == FAILED` AND `RedoCount < max`
- SET：`RedoCount+1`；`TaskState=DISPATCHED`；`TaskStatus="1"`；`ErrorMsg=null`
- 实现：`ExecuteUpdateAsync`；affected=0 时再读行区分 `LimitReached` / `NotClaimable` / `NotFound`
- 候选态：仅 `FAILED`（与 Tracker 触发对齐）；未知态 fail-closed

#### 自动 / 手动责任矩阵

| 入口 | 谁增 RedoCount | 门禁 | 备注 |
|---|---|---|---|
| Tracker `AutoRedispatchAsync` | `TryClaimAutoRedoAsync`（门禁后） | Service Pre+Final | 消费一次；并发单 Claim |
| 手动 `RedoAsync` | `IncrementRedoAsync`（门禁后） | Service Pre+Final | 拒发不增 |
| `RedispatchAsync` | 不增 | Service Pre+Final | 入口隔离 |

#### 并发与路由拒发证据

- 并发：TCS 门闩在 Claim 前对齐；ClaimSuccess=1；Transit=1；RedoCount 最多 +1
- 路由拒发：ClaimCalls=0；STATE/Error/RedoCount 全不变；仅 Warning，不 Growl/Alarm

#### 测试结果

| 过滤 | 结果 |
|---|---|
| RED 基线（改前） | Tracker **失败 13 / 通过 7 / 总计 20** |
| `FullyQualifiedName~RcsTaskTrackerRoutingGate` | **失败 0 / 通过 22** |
| Replay\|Historical | **0 失败 / 16 通过** |
| ChangeFrame\|Grab\|Inventory\|PalletReturn | **0 失败 / 80 通过** |
| 全量 Core.Tests | **失败 0 / 通过 382 / 总计 382** |
| `dotnet build CncLoader.sln` | **0 error / 0 warning** |
| `git diff --check -- src tests` | **无 whitespace error**（Issue 历史段既有 trailing 未动） |

#### 仍阻塞 / 下一步（本段撰写时）

- 生产 DI / 所有 RCS 发送入口的最终绕过审计（D15）— **已于下方审计通过**
- 最终回归 / 关 Issue
- 历史 ChangeFrame 重放：落库无 Operation Context，仍按 From/To Transit 门禁 fail-closed（本期不扩表）

---

### 最终绕过审计纪要（生产 DI + RCS 发送入口，2026-08-05）

> 此内容由 AI 在只读审计期间生成。**生产代码零修改**；仅新增审计测试与本 Comments。未关闭 Issue。Status 保持 `ready-for-agent`。

**审查结论：允许进入最终回归**

#### P0 / P1

- **P0：** 无（未发现可真实发 RCS 且绕过 Resolver/Validator Final 的生产入口）
- **P1（非阻塞）：**
  - `PositionScheduler` 构造可选 `IPlcWriteHook?` / `RcsCallbackNotifier?`（非门禁依赖；Validator/TaskService 必填）
  - ReservationFirst 的 Final 复用 Pre 的 `routeCtx`，但随后必定进入 `RcsTaskService.DispatchTransitAsync` 再做权威 Resolve+Validate Final（双保险）
  - 公开 `RedispatchAsync` 当前无 UI/Tracker 调用方（Tracker 已改 `AutoRedispatchAsync`）；方法本身仍有门禁

#### IRcsClient 发送面（完整）

| Client 方法 | 生产调用方 | 新外部执行？ | 门禁 | 结论 |
|---|---|---|---|---|
| `TransitTaskAsync` | 仅 `RcsTaskService`（DispatchTransit / BuildAndSend） | 经 Service 是 | Service Pre+Final / Claim 后 | 受保护 |
| `ExcuteTaskAsync` | 仅 `RcsTaskService`（Grab/Identify / BuildAndSend） | 经 Service 是 | Service Final+角色 | 受保护 |
| `CancelTaskAsync` | 仅 `RcsTaskService.CancelAsync` | 否（收口） | 不适用路由门禁 | 允许 |
| `QueryTaskAsync` | 仅 `RcsTaskService.QueryAsync` | 否（查询/TestConnection/对账） | 不适用 | 允许 |

生产程序集中 **唯一** 注入 `IRcsClient` 的类型：`RcsTaskService`。

#### 新外部执行入口矩阵（摘要）

| 操作 | 调用方 | Service 方法 | Final / 角色 | DI |
|---|---|---|---|---|
| 自动上/下料/交接 | `PositionScheduler` + ReservationFirst | `DispatchTransitAsync` | 调度 Pre + RF Final + Service Pre+Final | 真 |
| 手动 Transit | `RcsViewModel` | `DispatchTransitAsync` | Service Pre+Final | 真 |
| PalletReturn | `RcsViewModel` | `DispatchPalletReturnAsync`→Transit | Operation=PalletReturn | 真 |
| Grab / Identify | `RcsViewModel` | `DispatchGrab/IdentifyAsync` | Final+角色 | 真 |
| Inventory | `InventoryService` | `DispatchIdentifyAsync` | Final+角色 | 真 |
| ChangeFrame | `ChangeFrameOrchestrator` / WaterMonitor | `DispatchTransitAsync` | Operation=ChangeFrame + Bind | 真 |
| 手动 Redo | `RcsViewModel` | `RedoAsync` | Pre+Final→Increment | 真 |
| Redispatch | 公开 API（无当前生产调用方） | `RedispatchAsync` | Pre+Final 不增计数 | 真 |
| Tracker AutoRedo | `RcsTaskTracker` | `AutoRedispatchAsync` | Pre+Final→Claim→Send | 真 |

历史收口：callback / ConfirmCancel / queryTask 对账 / Cancel — **不接**新执行门禁（符合 D2）。

#### DI 结论

注册序：`AddCncCore → AddCncData → AddCncCommunication → AddCncUi`

| 接口 | 实现 | 生命周期 |
|---|---|---|
| `IRcsTaskService` | `RcsTaskService` | Singleton ×1 |
| `IManagedDispatchRouteResolver` | `ManagedDispatchRouteResolver` | Singleton ×1 |
| `IRoutingAvailabilityValidator` | `RoutingAvailabilityValidator` | Singleton ×1 |
| Stores | Equipment/LocationMap/Frame/RcsTask 真 Store | Singleton + `IDbContextFactory` |
| `IRcsClient` | factory→`RcsClient` | Singleton ×1 |

无 AlwaysAvailable / Tracing / Fake 生产注册；门禁构造无可选 null；Singleton 不直接注入 `CncDbContext`。

#### 重点 15 项（均通过）

1. RcsViewModel：无 IRcsClient，只经 TaskService
2. PositionScheduler：下发经 RF + DispatchTransit
3. Inventory：只 DispatchIdentify
4. ChangeFrame：只 DispatchTransit(Operation=ChangeFrame)
5. Tracker：AutoRedispatch，无旧 Increment→Redispatch
6. Service：Create/RCS 均在 Final 后
7. Redo/Redispatch/Auto 分入口无 bool 混责
8. PalletReturn：无 Skip
9. Grab/Identify：无旁路 Client
10. 模拟器收任务；出站仍经同一 RcsClient+Service
11. 对账只 Query/收口，不新建执行
12–15. 事件/UI/AREA N/A：既有专项 + 本矩阵覆盖

#### 测试

| 项 | 结果 |
|---|---|
| `ProductionRoutingGateWiring\|RcsSendBoundaryInventory` | **0 失败 / 15 通过** |
| P0-5 路由集合（含审计） | **0 失败 / 187 通过** |
| 全量 Core.Tests | **0 失败 / 397 通过**（原 382 + 审计 15） |
| build | **0 error / 0 warning** |
| `git diff --check -- src tests` | **无 whitespace error** |

文件：
- `tests/.../ProductionRoutingGateWiringTests.cs`
- `tests/.../RcsSendBoundaryInventoryTests.cs`

#### 下一步

最终全量回归与关闭审查（见下节；已完成并关闭）。

---

### 最终回归与验收关闭（2026-08-05）

> 此内容由 AI 在最终回归期间生成。**本轮仅修改 Issue**（勾选验收、Status/Labels、本纪要；清理 Issue trailing whitespace）。未改业务代码/测试/D1–D15。**未 commit/push。**

**Status：** `closed`
**Labels：** `dispatch, routing, soft-delete, equipment, safety, closed`

#### 1. 最终静态核对（12 项）

| # | 项 | 结果 |
|---|---|---|
| 1 | `STATE=="0"` 活动；null/空/未知 fail-closed（`ConfigActivity.IsActive`） | 通过 |
| 2 | 无 `(1,"LINE")` 路由回退（`GetWorkLineByEquipmentAsync` 失败返 null） | 通过 |
| 3 | 生产无 `SkipManagedRouteGate` | 通过 |
| 4 | 生产无 Fake/Tracing/AlwaysAvailable 门禁 | 通过 |
| 5 | `IRcsTaskService`/Resolver/Validator 真实且必填 DI | 通过 |
| 6 | 新外部执行全部经 `RcsTaskService` | 通过 |
| 7 | Final 位于 Create/RCS 前 | 通过 |
| 8 | ChangeFrame Final 校验 Equipment/Frame/FrameBind | 通过 |
| 9 | AutoRedo：Gate→原子 Claim→Send | 通过 |
| 10 | callback/Cancel/Confirm/Query/对账/TestConnection 未误接新执行门禁 | 通过 |
| 11 | 无直接绕过 Service 的 Client 新执行入口 | 通过 |
| 12 | Probe 接缝 internal；不在生产调用链 | 通过 |

#### 2. 最终回归命令

```text
P0-5 路由专项（用户给定 filter）
→ 失败 0，通过 235，跳过 0，总计 235

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 397，跳过 0，总计 397

dotnet test CncLoader.sln
→ 仅发现/执行 CncLoader.Core.Tests；失败 0，通过 397，总计 397
  （无额外测试项目，未形成额外覆盖）

dotnet build CncLoader.sln
→ 0 警告，0 错误

git diff --check -- src tests .scratch/p0-5-soft-deleted-routing-protection
→ 无 whitespace error
```

#### 3. 生产发送入口审计结论

- `IRcsClient` 四方法唯一生产调用方：`RcsTaskService`
- 新外部执行均经 Service 门禁；历史收口/TestConnection 不接新执行门禁
- 最终绕过审计：允许进入最终回归；无 P0 阻塞

#### 4. 改动范围核对

- 本关闭轮次：仅 Issue
- 工作区另有 P0-5 实现/测试改动（非本轮新增）
- `appsettings.json` 含 `ReconcileRetryIntervalMs`（非本轮引入）
- 未跟踪：`tests/CncLoader.Core.Tests/TestResults/`（勿提交）
- 无密钥/bin/obj 纳入关闭操作

#### 5. 未验证项

- 真 MySQL affected-row / 双连接 Claim
- 真 RCS / 真 PLC / 真实 UI
- 长时间生产并发
- Final→HTTP 跨系统窗口

#### 6. 已知残余风险（不阻塞关闭）

- Final→HTTP 极小窗口
- AutoRedo 发送失败已耗次数（D14）
- 历史 ChangeFrame 缺 Operation Context（fail-closed / 受管理 Transit）
- 真 MySQL Claim 原子性尚无双连接集成验证
- PlcPoint 为本期 N/A

#### 7. 无 ADR / PRD

维持 D9。

**结论：** 验收标准已满足，Issue 关闭。真实 UI/MySQL/PLC/RCS 不阻塞关闭。**未 commit/push。** 可进入下一优先级事项。
