# P0-1：HasMat fresh 未知被当成无料，误过 Loaded/Unloaded 闸口

Type: bug
Priority: P0
Status: closed
Labels: bug, P0, closed
Feature: p0-1-hasmat-recheck

## 问题

HasMat fresh 读取失败/未知被错误当成明确无料，可能错误进入 Loaded/Unloaded、
写 POS_TEST_START 或落正常账。

### 源码锚点（2026-08-04 重读）

**缺陷链（运行期闸口，非对账①b）：**

1. `PlcOperationService.ReadRegisterAsync`
   - 文件：`src/CncLoader.Communication/Plc/PlcOperationService.cs`
   - 行：80–96
   - 行为：`catch` 返回 `PlcReadResult`，`RawValue = 0`，`Error = ex.Message`（**不抛**）

2. `PositionScheduler.ReadHasMatFreshAsync`
   - 文件：`src/CncLoader.Communication/State/PositionScheduler.cs`
   - 行：1175–1189
   - 行为：调用 `ReadRegisterAsync` 后**不检查** `r.Error`，直接 `return r.RawValue == hp.OnValue`
   - 后果：`Error != null` 且 `RawValue = 0`（默认 Off）→ 得到明确 `false`（无料）；
     `Error != null` 且 `RawValue = 1`（偶发脏值）→ 得到明确 `true`（有料）

3. `PositionScheduler` · `ExecuteActions` · `case PositionState.Transporting`
   - 文件：同上 `PositionScheduler.cs`
   - 行：880–891
   - 行为：
     - 上料：`next = fresh == true ? Loaded : Alarm`（未知/false → Alarm；但若 Error 却 RawValue=OnValue → **误进 Loaded**）
     - 下料：`next = fresh == false ? Unloaded : Alarm`（Error+RawValue=0 → **误进 Unloaded**）

4. 误推进后的副作用（同文件）：
   - `case PositionState.Loaded`（893–908）：`WriteTestStartAsync(ctx, 1)` → 写 `POS_TEST_START=1`；
     `ConfirmTakeAsync` 落取料账（`SlotAccountService.ConfirmTakeAsync`，`src/CncLoader.Data/Repositories/SlotAccountService.cs` ≈95）
   - `case PositionState.Unloaded`（910–922）：`WriteTestStartAsync(ctx, 2)` → 写 `POS_TEST_START=2`；
     `ConfirmAsync` 落入库账（同文件 ≈222）

5. 相关类型/接缝：
   - `PlcReadResult`：`src/CncLoader.Core/Plc/PlcModels.cs` 62–71（含 `RawValue`、`Error`）
   - `IPlcOperationService.ReadRegisterAsync`：`src/CncLoader.Core/Abstractions/IPlcOperationService.cs` 9
   - Alarm 粘滞：`ComputeNextState` `case Alarm` 782–784；人工恢复 `ResetAlarmAsync` 223–253
   - 看板态展示：`PositionStatus`（`Snapshots.cs` 18–26）→ `ISignalStateStore` →
     `DashboardViewModel.PositionCardVm.StateDisplay`（`DashboardViewModel.cs` 674）/
     `EquipmentViewModel` 行内 `StateDisplay`（286）；
     XAML：`PageTemplates.xaml` ≈2490（`Text="{Binding StateDisplay}"`）；
     文案源：`PositionStateNames.ToDisplay`（`PositionState.cs` 47–60，`Transporting` →「搬运中」）
   - 失败计数落点候选：`PositionContext`（`PositionScheduler.cs` 1504–1518，按工位实例；含 `CurrentTaskId`/`Phase`/`AlarmRaised`）

**配置框架现状（供「<=0 启动校验」落地）：**

- `AppOptions` / `RcsOptions` / `PlcOptions`：`src/CncLoader.Common/Configuration/AppOptions.cs`
- 注册：`CommonServiceCollectionExtensions.AddCncCommon`（L16–18）已 `AddOptions<AppOptions>().Bind(...).ValidateOnStart()`
- **缺口**：未注册 `.Validate(...)` / `.ValidateDataAnnotations()` / `IValidateOptions<AppOptions>`，
  故当前 `ValidateOnStart` **不会**因数值非法而失败
- **结论**：框架**支持**启动校验（补 `Validate`/`IValidateOptions` 即可），不是「不支持」；
  实现时须补上，**禁止**静默把 `<=0` 降级成默认 6
- 命名风格（现有）：`RcsOptions` 下 `SchedulerIntervalMs`、`MaxAutoRedo`、`WaterFullThreshold` 等 PascalCase；
  建议挂 `App:Rcs:`，名称候选 `HasMatRecheckFailThreshold`（与调度节拍同节；最终名以实现时核对为准）

## 已确认决策

### P0-1「HasMat 读取失败被当成无料」— 决策纪要

**底线（已确认）**  
Error/未知 ≠ 无料；不得进 Loaded/Unloaded；不得写 `POS_TEST_START`；不得落正常账；须能用测试复现（无需真 PLC）。

| # | 决策 |
|---|---|
| 1 | 单次失败 → **保持 `Transporting`**（阶段/taskId/预记不动），不进 Alarm |
| 2 | **调度主循环下一 tick** 再 fresh 读；不另开重试器、闸口内不连读 |
| 3 | **可配置连续失败阈值，默认 6**；读到明确 true/false 清零；明确不符仍立刻 Alarm |
| 4 | Alarm 前：态仍 `Transporting`，UI 副文案「复核中（n/阈值）」，Warning 日志，**不弹 Growl** |
| 5 | 阈值内读成功 → **按结果自动推进**；已 Alarm → **必须人工 `ResetAlarm`** |
| 6 | **上料/下料同一策略与同一阈值**；仅成功判据相反（有料→Loaded / 无料→Unloaded） |

## 实现约束

- 失败计数按当前运输任务/工位隔离，不得使用全局单计数器。
- 只在 COMPLETED + Transporting + fresh 未知时累加。
- taskId 变化、离开 Transporting、读到明确 true/false 时清零。
- 达到阈值只触发一次 Alarm。
- Alarm 后必须人工 ResetAlarm。
- UI 使用当前任务状态的既有展示位置显示“复核中（n/阈值）”。
- 不新增 Surface，不弹 Growl。
- 配置缺失默认 6。
- 配置 <= 0 使用 Options 启动校验失败；若现有配置框架不支持，先报告，不自行降级。
  （源码结论见上：框架支持，须补 Validate；禁止降级。）
- 配置项名称先检查现有 Options 命名风格，HasMatRecheckFailThreshold 只是建议名。

## Tracer Bullet

本 Issue 同时建立第一个最小测试工程，但只服务本安全修复：

- **测试框架（已锁定）：NUnit**
  - 包：`Microsoft.NET.Test.Sdk`、`NUnit`、`NUnit3TestAdapter`
  - **本 Issue 不引入**：FlaUI、coverlet、STA 扩展、CI 配置
  - 后续 STA WPF 测试复用 NUnit 的 `ApartmentState.STA`（本期不建 STA 体系）
- 不搭建 CI
- 不搭建 FlaUI
- 不搭建完整 STA 测试体系
- 不创建多个空测试工程
- 不引入 mock 框架（手写可注入假实现即可）

建议：解决方案内新增单一测试工程 `tests/CncLoader.Core.Tests`（`net8.0`），加入 `CncLoader.sln`；以可注入 `IPlcOperationService` / 抽取可测的 HasMat 三态结果为准，避免整机 Host。

## RED 测试

至少覆盖：

1. 下料：RawValue=0 且 Error!=null，不得进入 Unloaded。
2. 上料：RawValue=1 且 Error!=null，不得进入 Loaded。
3. 未知读数 1～5 次：保持 Transporting，不 Alarm。
4. 第 6 次连续未知：进入 Alarm，且只触发一次。
5. 阈值内读到明确成功结果：计数清零并按正常结果自动推进。
6. 明确结果与预期不符：立即 Alarm，不等待 6 次。
7. 未知读数：不得写 POS_TEST_START。
8. 未知读数：不得落正常入库/出库账。
9. taskId 变化或离开 Transporting：旧失败计数不影响新任务。
10. 上料和下料采用同一阈值，但成功判据相反。
11. Alarm 后即使读数恢复，也不得自动推进，必须先人工 ResetAlarm。
12. 配置缺失为 6，配置 <= 0 启动校验失败。

## GREEN 范围

- 最小修改 fresh 读取结果语义、状态转换、计数和可观察 UI 状态。
- 不重构整个 PositionScheduler。
- 不实现 MaxReconnectAttempts。
- 不修改 PLC 地址或协议。
- 不改变 Alarm 粘滞规则。
- 不处理其他 P0/P1/P2。

## 验收标准

- [x] RED 1–12 全部绿
- [x] `ReadHasMatFreshAsync`（或等价接缝）在 `Error!=null` / 异常时返回未知，不再把 `RawValue` 当成明确有/无料
- [x] 运行期 `Transporting`+`COMPLETED`：未知保持 Transporting 并累加；达阈值一次 Alarm；明确不符立刻 Alarm；明确符合推进 Loaded/Unloaded
- [x] 未知路径不调用写 `POS_TEST_START`、不调用 `ConfirmTakeAsync`/`ConfirmAsync` 正常落账
- [x] 看板/机台加工位既有 `StateDisplay` 位置可观察「复核中（n/阈值）」；无新 Surface、无 Growl
- [x] 配置缺失默认 6；`<=0` 启动失败（Validate）
- [x] `dotnet test` 新工程通过；`dotnet build CncLoader.sln` 0 警告 0 错误

## 验证

- `dotnet test <新增测试工程>`
- `dotnet build CncLoader.sln`
- 使用模拟器或可注入 PLC 读结果验证连续失败和恢复
- 真 PLC 回归列为后续非阻塞验证

## 依赖

- 无 — 可立即开始（决策与测试框架已锁定；无父 PRD）
- 允许新增测试工程依赖：仅 `Microsoft.NET.Test.Sdk`、`NUnit`、`NUnit3TestAdapter`

## 阻塞项

- 无 — 可立即开始

## Comments

> 此内容由 AI 在分拣期间生成。

## 验收关闭（2026-08-04）

**状态：** `ready-for-agent` → `closed`

**验收结果：**

- NUnit：14/14 通过
- `dotnet build CncLoader.sln`：0 警告、0 错误
- 本地 App 启动及正常上下料节拍验证通过
- 连续 HasMat 读取未知策略由自动化测试覆盖
- 真 PLC 故障注入尚未验证，列为后续现场回归项（非本 Issue 阻塞）

**结论：** 本 Issue 验收标准已满足，关闭。真 PLC 故障注入不阻塞关闭。

## 分拣笔记（2026-08-04）

**类别 / 状态：** bug → `ready-for-agent`

**到目前为止，我们已经确定：**

- 六条决策完整保留；GREEN / 模拟器 vs 真 PLC 边界清晰
- 源码锚点整体准确；生产主路径为下料 `Error!=null` + `RawValue=0` → 误进 Unloaded；上料误进 Loaded 为契约防护（需 `RawValue==OnValue`）
- RED 1–12 覆盖计数隔离、清零、阈值、Alarm 幂等、人工 Reset
- 测试框架已确认：NUnit + `Microsoft.NET.Test.Sdk` / `NUnit` / `NUnit3TestAdapter`
- 本 Issue 不引入 FlaUI、coverlet、STA 扩展、CI；后续 STA 复用 `ApartmentState.STA`

**已关闭的提问：**

- 测试框架与允许的测试包清单（见上）

---

## Agent 简报

**类别：** bug  
**摘要：** HasMat fresh 读失败/未知不得当成明确有/无料；Transporting+COMPLETED 闸口按连续失败阈值处理，达阈值一次 Alarm，未知路径禁写 `POS_TEST_START`、禁落正常账。

**当前行为：**
`IPlcOperationService.ReadRegisterAsync` 失败时返回带 `Error` 的 `PlcReadResult`（通常 `RawValue=0`）且不抛。`ReadHasMatFreshAsync`（或等价接缝）不检查 `Error`，把 `RawValue` 与 OnValue 比较当成明确布尔。下料在 Error+默认 0 时误进 Unloaded 并可能写测试启动与落账；上料在 Error 且脏值为 OnValue 时可能误进 Loaded。单次真正未知（null）现行亦直接 Alarm，与「阈值内保持 Transporting」不符。

**期望行为：**
- Error/未知 ≠ 有料/无料；不得进 Loaded/Unloaded；不得写 `POS_TEST_START`；不得落正常账。
- 单次未知：保持 Transporting（阶段/taskId/预记不动），调度下一 tick 再 fresh 读；不另开重试器、闸口内不连读。
- 可配置连续失败阈值，默认 6；明确 true/false 清零；明确不符立刻 Alarm；达阈值只 Alarm 一次。
- Alarm 前 UI 既有状态展示位显示「复核中（n/阈值）」，Warning 日志，不弹 Growl；不新增 Surface。
- 阈值内读成功按结果自动推进；已 Alarm 必须人工 `ResetAlarm`（粘滞规则不变）。
- 上料/下料同一策略与同一阈值，仅成功判据相反。
- 失败计数按工位/当前运输任务隔离；taskId 变化、离开 Transporting、读到明确结果时清零。
- 配置缺失默认 6；`<=0` 须 Options 启动校验失败，禁止静默降级。

**关键接口：**
- `PlcReadResult` / `IPlcOperationService.ReadRegisterAsync` —— 失败语义（`Error` + 默认 RawValue）保持；消费方必须识别未知
- HasMat fresh 读接缝 —— `Error!=null`/异常 → 未知（非明确 bool）
- `PositionScheduler` Transporting + RCS COMPLETED 闸口 —— 计数、保持、Alarm、推进
- 工位上下文 —— 存放按任务隔离的失败计数（非全局）
- `RcsOptions`（或同节 Options）—— 阈值项（候选名 `HasMatRecheckFailThreshold`）；补 `Validate`/`IValidateOptions`，挂现有 `ValidateOnStart`
- 看板/机台 `StateDisplay`（或等价可观察字段）—— 复核中文案，无新 Surface、无 Growl
- `ResetAlarm` —— Alarm 后唯一恢复路径（行为不放宽）

**测试工程（Tracer Bullet）：**
- 新增单一工程建议：`tests/CncLoader.Core.Tests`（`net8.0`），加入解决方案
- 框架：NUnit；包仅限 `Microsoft.NET.Test.Sdk`、`NUnit`、`NUnit3TestAdapter`
- 手写假 `IPlcOperationService`（或不引入 mock 包的等价注入）；避免整机 Host
- 不引入 FlaUI / coverlet / STA 扩展 / CI

**验收标准：**
- [x] RED 1–12 全部绿（含计数隔离、清零、阈值、Alarm 一次、人工 Reset、配置校验）
- [x] fresh 接缝在 Error/异常时返回未知
- [x] Transporting+COMPLETED：未知保持并累加；达阈值一次 Alarm；明确不符立刻 Alarm；明确符合推进
- [x] 未知路径不写 `POS_TEST_START`、不正常落账
- [x] 既有 `StateDisplay` 可观察「复核中（n/阈值）」；无新 Surface、无 Growl
- [x] 配置缺失=6；`<=0` 启动失败
- [x] `dotnet test` 新工程通过；`dotnet build CncLoader.sln` 0 警告 0 错误
- [x] 模拟器或可注入读结果验证连续失败/恢复；真 PLC 回归非本 Issue 阻塞

**范围外：**
- 其他 P0/P1/P2
- `Plc.MaxReconnectAttempts` 自动重连
- 大范围重构 `PositionScheduler`
- 改 PLC 地址/协议、放宽 Alarm 粘滞
- FlaUI、coverlet、STA 测试体系、CI

**实现前必读 / PRD 绑定：** 无父 PRD；本 Issue 为 headless 安全闸口修复（仅改既有状态展示文案）。决策纪要 1–6 与 RED/GREEN 为本契约 SSOT。
