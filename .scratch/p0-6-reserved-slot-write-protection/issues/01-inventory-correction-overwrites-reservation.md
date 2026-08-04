# P0-6｜盘点/人工校正可能覆盖预记槽

Type: bug  
Priority: P0  
Status: closed  
Labels: inventory, slot, reservation, concurrency, safety, closed  
Feature: p0-6-reserved-slot-write-protection

> 此内容由 AI 在分拣期间生成。

## 1. 问题陈述

P0-2 / ADR-0002 已将自动派工改为「原子预记 → 再 RCS 下发」，预记用 `SLOT_STATE='3'` + `REMARK=taskId` 锁定槽位。但盘点回写与人工校正仍对 `MAS_AUTO_FRAME_SLOT` 做**无条件**实体更新（先查询再 `SaveChanges`），**不检查预记态、不使用条件 UPDATE、无行版本**。

后果：`SLOT_STATE=3` 可被覆盖为空/占用；`taskId`（REMARK）可被清空或与态不一致；RCS 在途任务与本地槽位账脱节。P0-2 的原子预记只保护「派工选槽互抢」，**可被盘点/校正/清槽路径绕过**。

## 2. 槽位状态与预记不变量（源码确认）

### 2.1 表 / 实体 / 主键

| 项 | 真实值 |
|----|--------|
| 表 | `MAS_AUTO_FRAME_SLOT` |
| 实体 | `FrameSlot`（`src/CncLoader.Data/Entities/SignalAndFrameEntities.cs`） |
| 主键 | `ID1` → `FrameSlot.Id`（`BIGINT` 自增） |
| 业务定位 | `(FRAME_ID, SLOT_NO)` / `(FRAME_ID, LAYER_NO, POS_IN_LAYER)` 唯一 |
| 物料字段 | `ELECTRODE_ID` → `MaterialId` |
| taskId 载体 | `REMARK`（应用约定，非独立列） |
| 方向标记 | `BIND_SOURCE`：`RSV_PUT` / `RSV_TAKE`（预记）；落账后 `CONFIRMED`；盘点写 `RCS_QR` |

Schema 注释仍写 `0=空 1=占用 2=锁定`（`docs/sql/cnc_schema.sql`）；**`3=预记` 为应用层扩展**，见 `SlotStates`。

### 2.2 `SLOT_STATE` 语义

| 值 | 常量 | 含义 |
|----|------|------|
| `0` | `SlotStates.Empty` | 空 |
| `1` | `SlotStates.Occupied` | 占用（已落账） |
| `2` | `SlotStates.Locked` | 锁定/不可用 |
| `3` | `SlotStates.Reserved` | **预记**（占用意向，绑 taskId） |

**`SLOT_STATE=3` 确为预记态。** 上料取料预记与下料入库预记**都用 3**；方向靠 `BIND_SOURCE`：

- `RSV_TAKE`：取料预记（源槽，原占用 → 预记，保留物料）
- `RSV_PUT`：入库预记（目标空槽 → 预记，写入物料意向）

### 2.3 预记字段与外部写保护条件（已锁定 · D1）

| 字段 | 用途 |
|------|------|
| `SLOT_STATE='3'` | **外部写保护的唯一必要条件** |
| `REMARK` | taskId；诊断 + Confirm/Rollback 所有权校验 |
| `BIND_SOURCE` | `RSV_PUT` / `RSV_TAKE`；诊断 + 方向校验 |
| `ELECTRODE_ID` | 物料（TAKE 保留；PUT 写意向） |
| `FRAME_ID` + `SLOT_NO` / 层坐标 | 槽位定位 |
| `BIND_TIME` | 预记时间 |
| `UPDATETIME` | 行更新时间 |

**外部写保护条件（已锁定）：**  
`SLOT_STATE == SlotStates.Reserved`（`'3'`）即受保护。

- 不要求 `REMARK` 非空才保护。  
- `REMARK`/taskId 为空或 `BIND_SOURCE` 非 `RSV_TAKE`/`RSV_PUT`：仍禁止盘点/校正/清槽覆盖；额外 Warning；**禁止**外部校正偷偷「修复」异常预记。  
- `REMARK` / `BIND_SOURCE` 用于诊断与合法状态机所有权校验，**不作为外部写放行条件**。

### 2.4 正常状态转换（状态机路径）

| 操作 | 入口 | 条件/实现 | 结果 |
|------|------|-----------|------|
| Reserve PUT | `ReserveAsync` | `ExecuteUpdate` WHERE `Id`+`SLOT_STATE='0'`，affected=1 | → `3`，`REMARK=taskId`，`BIND_SOURCE=RSV_PUT` |
| Reserve TAKE | `ReserveTakeAsync` | 同上 WHERE `'1'` | → `3`，保留物料，`RSV_TAKE` |
| Confirm PUT | `ConfirmAsync` | `STATE=Reserved` + REMARK 所有权；必要时方向 | → `1`，`BIND_SOURCE=CONFIRMED`，保留 REMARK |
| Confirm TAKE | `ConfirmTakeAsync` | 同上 | → `0`，清物料/REMARK |
| Rollback PUT | `RollbackAsync` | `REMARK=taskId AND STATE='3'` | → `0`，清物料/REMARK |
| Rollback TAKE | `RollbackTakeAsync` | 同上 | → `1`，清 REMARK（物料仍在） |
| 陈旧回滚 | `RollbackStaleReservationsAsync` | 非 active；COMPLETED 跳过 | 按方向回滚 |
| Alarm / Reset | `PositionScheduler` | Alarm 粘滞；`ResetAlarmAsync` 先按方向 Rollback 再 WaitLoad | 预记收口属调度器，非盘点 |

源码：`src/CncLoader.Data/Repositories/SlotAccountService.cs`；常量 `src/CncLoader.Core/Rcs/ISlotAccountService.cs`（`SlotStates`）；契约 ADR-0002 / P0-2。

### 2.5 不变量（已锁定）

1. `SLOT_STATE=3` 的槽不得被盘点/人工校正/清槽等**外部写路径**改写（含异常预记）。  
2. 外部写不得清空或改写 Reserved 槽的 `REMARK` / `BIND_SOURCE` / 物料等预记相关字段。  
3. 合法 Confirm/Rollback/Reserve（专用接口）不得被「预记保护」误拦。  
4. 外部写必须用原子条件更新（`SLOT_STATE <> Reserved`）+ affected 分类（D8/D10）；禁止 check-then-update。

## 3. 自动盘点调用链（修复前现状）

```
[手动] FrameViewModel.StartInventoryAsync
    或
[自动] InventorySchedulerService（HostedService，InventoryAutoEnabled=true）
         └─ InventoryOneAsync → StartInventoryAsync
              │
              ▼
InventoryService.StartInventoryAsync
  ├─ 互斥：queue.Count>0 或 换架在途 → 拒发 FAILED 事件
  ├─ LOCATION_MAP ResolveFrame(station|shelf) — 缺则拒发+告警
  ├─ IRcsTaskService.DispatchIdentifyAsync（identifyQR）
  └─ _active[taskId] = InventoryTaskInfo
              │
              │  （约 3~4 分钟；期间 PositionScheduler 仍可 Reserve）
              ▼
RcsCallbackHost → RcsCallbackProcessor.HandleScanTaskStatusAsync
  → RcsCallbackNotifier.ScanResultReceived
  → InventoryService.OnScanResultReceived → HandleScanAsync
       └─ ISlotAccountService.CorrectFromInventoryAsync(frameId, posStart, products)
            │  AsTracking 加载整架槽位
            │  按孔位顺序写 MaterialId / SLOT_STATE=1 或 0
            │  **不检查 SLOT_STATE=='3'，不清 REMARK（Empty 路径也不清）**
            │  整架刷新 LAST_VERIFY_TIME
            └─ 单次 SaveChangesAsync
       → InventoryCompleted(COMPLETED, correctedCount)
            → FrameViewModel Growl.Success「校正 N 个」
            → InventorySchedulerService 日志
```

| 环节 | 锚点 |
|------|------|
| UI | `FrameViewModel.StartInventoryAsync` |
| 调度 | `InventorySchedulerService` |
| 服务 | `InventoryService` |
| 写库 | `SlotAccountService.CorrectFromInventoryAsync` |
| 配置 | `RcsOptions.InventoryAutoEnabled`（默认 false）、`InventoryIntervalMinutes` |

## 4. 人工校正调用链（修复前现状）

```
FrameViewModel.CorrectSlotAsync / ClearSelectedSlotAsync
  ├─ 校验已选料架+槽位；状态码从下拉解析
  └─ ISlotAccountService.SetSlotAsync(frameId, slotNo, materialId, slotState, author)
       ├─ AsTracking FirstOrDefault (FRAME_ID, SLOT_NO)
       ├─ 不存在 → throw
       ├─ 直接赋 SlotState / MaterialId
       ├─ 仅当 slotState=='0' 时 Remark=null、BindTime=null
       └─ SaveChangesAsync → Information 日志「人工校正…」
  → Growl.Success「已校正/已置空」+ LoadDetailAsync
  → catch → Growl.Error
```

| 环节 | 锚点 |
|------|------|
| 页面/命令 | 料架页 `CorrectSlotCommand` / `ClearSelectedSlotCommand` |
| 服务 | `SetSlotAsync` |

## 5. 派工预记调用链（P0-2 现状 · 合法状态机）

```
PositionScheduler 上料/下料消费者
  → 预生成 taskId
  → ReservationFirstDispatcher.ExecuteAsync
       ├─ reserve: ReserveTakeAsync / ReserveAsync   # 条件 ExecuteUpdate
       ├─ 失败 → 不调 RCS
       ├─ dispatch: RcsTaskService（同一 taskId）
       └─ 失败 → RollbackTakeAsync / RollbackAsync
  → 完成路径：ConfirmTakeAsync / ConfirmAsync（PLC 门后）
```

Confirm/Rollback/Reserve **不走**外部校正入口（D9）。

## 6. 其他写入路径

### 6.1 生产路径

| 路径 | 是否写槽位 | 修复要求 |
|------|------------|----------|
| 换架 `ChangeFrameOrchestrator` | 否 | 不新增无关保护（D4） |
| 清槽 `ClearSelectedSlotAsync` | 是 → `SetSlot` | 与人工校正**完全相同**的 Reserved 保护（D3/D4） |
| 料架改层重建 | 非空（含 3）拒绝 | 保留；RED 回归确认 Reserved 仍拒绝（D4） |
| 删料架 `CheckDeleteFrame` | 非空拒删 | 同上 |
| 对账 Confirm/Rollback | 合法状态机 | 不得误拦（D9） |
| 扫码盘点校正 | 是 | 按 D2/D5/D8 跳过 Reserved |

### 6.2 工具 / 脚本（非生产 · 已知风险）

| 工具 | 说明 |
|------|------|
| `tools/FrameSeedReset` | **本期不改**；可清空含预记槽位；**禁止对运行库/生产库执行** |
| `docs/sql/refill_upload_frame_electrodes.sql` | 演示 SQL；禁止对运行库执行 |
| 演示种子 SQL | 同上 |

## 7. 当前错误时序（修复前）

### 7.1 盘点丢失更新（TOCTOU）

```
盘点读取槽位非预记
→ 派工原子 Reserve，写 SLOT_STATE=3 / taskId
→ 盘点用旧实体 SaveChanges
→ SLOT_STATE/taskId 被覆盖或清空/不一致
```

### 7.2 盘点直接覆盖已有预记

```
Reserve 已完成（槽为 3）
→ identifyQR 回调 CorrectFromInventory
→ 范围内槽无条件改 0/1
→ 预记消失；Growl「盘点完成：校正 N 个」
```

### 7.3 人工校正覆盖

```
Reserve 已完成
→ 人工校正不检查预记
→ SetSlot 直接改状态（Empty 时清 taskId）
→ 预记与 RCS 任务脱节；Growl Success
```

## 8. 目标安全行为（已锁定）

### 8.1 单槽外部写

- `SetSlot` / 清槽：遇 `STATE=3` → **拒绝**；STATE/REMARK/BIND_SOURCE/物料全不变；UI 不得 Success。  
- 文案：「槽位已被任务预记，不能人工校正」。  
- 无管理员强制覆盖、无隐藏 bypass。

### 8.2 批量盘点 / 批量清槽

- 安全槽（非 Reserved）可更新；Reserved **完全不变**。  
- 同一事务提交安全槽更新；Cancellation / DB 异常 → **整体回滚**；禁止逐槽 `SaveChanges` 半批提交。  
- 预记冲突 = 可预期跳过，不是系统异常。  
- 结果至少含：`RequestedCount`、`UpdatedCount`、`UnchangedCount`、`ReservationConflictCount`、`NotFoundCount`、`ConflictSlots`（可限展示数量）。  
- `ReservationConflictCount > 0` → 不得返回纯 Success。  
- DB 异常 → 整次失败（非跳过）。

### 8.3 原子并发（D8）

外部写：

```
WHERE slot identity matches
  AND SLOT_STATE <> Reserved
```

+ 检查 affected。Reserve 先成功 → 校正 affected=0；校正先成功 → Reserve 按现有 0/1 条件重判。不得清空 Reserved 的 REMARK/BIND_SOURCE。不新增迁移/行版本/锁表；优先 `ExecuteUpdateAsync`。

### 8.4 affected=0 分类（D10）

只读重查分类（**禁止**分类后再无条件更新）：

1. 行不存在 → `NotFound`  
2. 当前 Reserved → `ReservationConflict`  
3. 已等于目标且不涉及清除有效预记 → `Unchanged`（幂等成功统计）  
4. 存在、非 Reserved、值不同 → `ConcurrencyConflict`（fail-closed，不报成功；不无限重试）  
5. 查询/更新异常 → `DatabaseError`  
6. Cancellation → 传播/`Cancelled`，非业务冲突；事务回滚  

结果类型须让 Service/ViewModel 区分以上状态，不能只返回 `bool`。

## 9. UI / 日志 / Alarm（已锁定 · D6/D7）

| 场景 | UI | 日志 | Alarm |
|------|-----|------|-------|
| 单槽人工冲突 | Warning/Error Growl：「槽位已被任务预记，不能人工校正」；**禁止 Success** | Warning | 不置工位 Alarm |
| 用户批量盘点全成功 | Success | Information | 否 |
| 用户批量有预记跳过 | **Warning**（更新数/跳过数） | 汇总 Warning | 否 |
| 用户批量全部冲突 | **Warning**，不显示成功 | 汇总 Warning | 否 |
| 用户批量 DB 失败 | Error | Error | 否 |
| 后台自动盘点 | **不弹 Growl** | 汇总 Warning | 否 |
| 异常预记（缺 REMARK/非法 BIND_SOURCE） | （随冲突反馈） | **单独 Warning** | 否 |

不显示完整敏感 taskId；定位仅槽位 + 脱敏任务标识。不把安全拒绝记成系统异常。不新增 Surface。

## 10. 可测试接缝

| 接缝 | 约束 |
|------|------|
| `ISlotAccountService` / `SlotAccountService` | 先打真实 Service；原子 SQL 经仓储接缝 + affected 行数 |
| `FrameViewModel` | 真实 VM：单槽冲突不 Success；用户盘点 Warning |
| `InventoryService` / 完成事件 | 结果携带计数；后台不 Growl |
| 包 | NUnit + 手写 fake；**不引入 mock 包**；**不新增 EF InMemory** |
| 真 MySQL 双连接 | **后续集成验证**，不作为 RED 唯一证据 |

测试工程：`tests/CncLoader.Core.Tests`。

## 11. RED 顺序（已锁定 · 按组实施）

### 第一组——单槽保护

| # | 场景 | 归属 |
|---|------|------|
| R1 | Reserved + 人工校正 → STATE/REMARK/BIND_SOURCE 全不变 | Service |
| R2 | Reserved + 清槽 → 全不变 | Service |
| R3 | Reserved 且 REMARK 为空 → 仍拒绝 + 数据异常 Warning | Service |
| R4 | 非 Reserved → 正常校正 | Service |
| R5 | 行不存在 → NotFound | Service |
| R6 | 已是目标值 → Unchanged | Service |
| R7 | ViewModel 单槽冲突不显示 Success | ViewModel |

### 第二组——盘点批量

| # | 场景 | 归属 |
|---|------|------|
| R8 | 含安全槽 + Reserved → 安全槽更新、Reserved 跳过 | Service |
| R9 | `UpdatedCount` / `ReservationConflictCount` 正确 | Service |
| R10 | 有冲突时 UI 为 Warning，不是 Success | ViewModel |
| R11 | 全部 Reserved 时不写任何槽 | Service |
| R12 | 后台自动盘点只记汇总 Warning，不弹 Growl | Service/调度 |
| R13 | Cancellation/DB 异常时安全槽更新整体回滚 | Service |

### 第三组——原子并发与状态机

| # | 场景 | 归属 |
|---|------|------|
| R14 | 校正后并发 Reserve 先成功 → 校正 affected=0，Reserved 保持 | Service/仓储 |
| R15 | 校正先成功 → Reserve 按现有条件成功或明确拒绝，互不覆盖 | Service/仓储 |
| R16 | 人工校正与清槽并发，不得覆盖后来形成的 Reserved | Service/仓储 |
| R17 | Confirm/Rollback 合法路径不被误拦 | Service |
| R18 | taskId 不匹配的 Confirm/Rollback 继续拒绝 | Service |
| R19 | 改层/删架对 Reserved 的现有拒绝保持 | Service（回归） |
| R20 | affected=0 区分 Reserved / NotFound / Unchanged / ConcurrencyConflict | Service |

**第一个 RED 起点：** `ISlotAccountService.SetSlotAsync`（真实 `SlotAccountService`）—— Reserved 槽人工校正不得改写任何预记字段（R1）。

## 12. 已锁定决策 D1–D10

### D1｜活动预记与保护条件 — **已锁定**

外部写保护以 `SLOT_STATE == SlotStates.Reserved`（`3`）为**唯一必要条件**。不要求 REMARK 非空。异常预记（缺 REMARK / 非法 BIND_SOURCE）仍禁止覆盖，并 Warning；禁止外部写「修复」。REMARK/BIND_SOURCE 仅诊断与状态机所有权，不放行。

### D2｜自动/手动盘点 — **已锁定**

遇 Reserved：跳过；不改 STATE/REMARK/BIND_SOURCE/material 等；继续其他安全槽；汇总 Warning（可附脱敏槽位列表）；不自动释放预记；不把盘点识别当 Confirm/Rollback。

### D3｜人工校正 — **已锁定**

Reserved 一律拒绝；无管理员强制覆盖；无隐藏 bypass；UI 明确「槽位已被任务预记，不能人工校正」；结果不得 Success。强制覆盖未来另建 ADR/授权，本期不做。

### D4｜清槽及其他外部写 — **已锁定**

清槽与人工校正同保护；批量清槽跳过冲突并报告；换架不写槽、不增无关保护；改层/删架非空拒绝保留并回归 Reserved；`FrameSeedReset` 本期不改，已知风险：不可对运行库执行；Confirm/Rollback/Reserve 不走外部入口。

### D5｜批量策略与事务 — **已锁定**

安全槽继续、冲突槽跳过。返回至少：`RequestedCount`、`UpdatedCount`、`UnchangedCount`、`ReservationConflictCount`、`NotFoundCount`、`ConflictSlots`。有 ReservationConflict 不得纯 Success。DB 异常整次失败。安全槽更新同一事务；Cancellation/DB 异常未提交部分整体回滚；禁止逐槽 SaveChanges 半批提交。

### D6｜UI/调用方反馈 — **已锁定**

不新增 Surface。单槽冲突 Warning/Error；用户盘点：全成功 Success / 有跳过 Warning / 全冲突 Warning / DB 失败 Error。后台自动盘点不重复 Growl，只汇总 Warning。不显示完整敏感 taskId。

### D7｜日志与 Alarm — **已锁定**

预记冲突 Warning；批量优先一条汇总；异常预记单独 Warning；不置工位 Alarm、不新增粘滞 Alarm；安全拒绝不是系统异常；DB 异常 Error + 失败返回。

### D8｜数据库原子防护 — **已锁定**

禁止「先查非 Reserved 再跟踪实体 SaveChanges」。外部写须 `WHERE identity AND SLOT_STATE <> Reserved` + affected。与 Reserve 由 DB 条件决定顺序。不新增迁移/行版本/锁表；优先 `ExecuteUpdateAsync`。

### D9｜合法状态机 — **已锁定**

保护仅针对 Inventory correction / Manual correction / Clear slot。Reserve/Confirm/Rollback 继续专用接口，并验证 Reserved + REMARK 所有权 + 必要方向。禁止用 SetSlot/Correct 模拟 Confirm/Rollback；禁止 same-task bypass。

### D10｜affected=0 / 并发 / 取消 — **已锁定**

只读重查分类为 NotFound / ReservationConflict / Unchanged / ConcurrencyConflict / DatabaseError / Cancelled。分类后不得无条件更新。ConcurrencyConflict fail-closed、不无限重试。Cancellation/DatabaseError 事务回滚。结果类型不可只返回 bool。

**ADR / PRD：** 本期**不建**。修复既有预记不变量；强制覆盖若未来需要再开短 ADR。

## 13. 验收标准

### 功能

- [x] `SLOT_STATE=3` 不被 `CorrectFromInventoryAsync` / `SetSlotAsync`（含清槽）改写任何预记相关字段  
- [x] 异常预记（缺 REMARK/非法 BIND_SOURCE）同样拒绝/跳过，并 Warning  
- [x] 非 Reserved 槽校正/盘点仍可用；Unchanged/NotFound 分类正确  
- [x] 批量返回 D5 计数字段；有冲突非纯 Success；DB 异常整次失败且事务回滚  
- [x] 外部写使用条件 UPDATE（`STATE <> Reserved`）；与 Reserve 并发时校正不得盖掉已预记  
- [x] Confirm/Rollback/Reserve 合法路径不被误拦；taskId 不匹配继续拒绝  
- [x] 改层/删架对 Reserved 的拒绝保持  
- [x] RED R1–R20 全部绿  

### UI 行为

- [x] 单槽冲突：Growl 提示「槽位已被任务预记，不能人工校正」，不显示 Success  
- [x] 用户盘点：有跳过 → Warning（更新数/跳过数）；全冲突 → Warning；全成功 → Success；DB 失败 → Error  
- [x] 后台自动盘点不弹 Growl  
- [x] 不展示完整敏感 taskId  

### 构建

- [x] `dotnet test tests/CncLoader.Core.Tests` 通过  
- [x] `dotnet build CncLoader.sln` 0 警告 0 错误  

## 14. 非目标

- 不改 RCS/PLC 协议与信号表  
- 不引入分布式锁/Outbox/新表/行版本迁移  
- 不修改 `FrameSeedReset` / 演示 SQL（仅文档化风险）  
- 不实现管理员强制覆盖  
- 不处理其他已关闭 P0  
- 不新增 Surface / PRD / ADR  
- 不引入 mock 包或 EF InMemory  

## 15. 未验证 / 残余限制

- 真 MySQL 双连接并发 Reserve vs Correct：后续集成验证，非 RED 唯一证据  
- 开启 `InventoryAutoEnabled` 的现场/模拟器端到端盘点  
- 真 PLC/RCS identifyQR 孔位对齐  
- 料架页人工操作录屏  
- **无强制覆盖**（D3）：异常预记只能走合法 Confirm/Rollback/运维工具，不能靠校正「修好」  
- **工具路径** `FrameSeedReset` 仍可抹掉预记——禁止对运行库执行  

## 16. 风险验证结论（修复前 · 源码级）

| # | 命题 | 结论 |
|---|------|------|
| 1 | 自动盘点能改 Reserved | **成立** |
| 2 | 人工校正能覆盖 Reserved | **成立** |
| 3 | 清槽能覆盖；换架直接覆盖 | 清槽**成立**；换架**不成立** |
| 4 | TOCTOU | **成立** |
| 5 | P0-2 原子预记被绕过 | **成立** |

## 17. Agent 简报

**类别：** bug  
**摘要：** 外部写（盘点/人工校正/清槽）不得覆盖 `SLOT_STATE=3`；改为条件更新 + 可区分结果；UI/日志按锁定策略反馈。

**当前行为：**  
`CorrectFromInventoryAsync` / `SetSlotAsync` 无条件改写 `FRAME_SLOT`，可破坏 Reserved；UI 报 Success；与 Reserve 存在丢失更新。

**期望行为：**  
- 保护条件唯一：`SLOT_STATE == Reserved`（含异常预记）。  
- 单槽校正/清槽：拒绝，字段全不变，Growl 非 Success。  
- 批量盘点/清槽：安全槽同事务更新，Reserved 跳过；有冲突非纯 Success；取消/DB 错整体回滚。  
- 原子：`WHERE identity AND SLOT_STATE <> Reserved` + affected 分类（D10）。  
- Confirm/Rollback/Reserve 专用接口不受影响。  
- 后台自动盘点不 Growl，只汇总 Warning。

**关键接口：**  
- `ISlotAccountService.SetSlotAsync` — 不得再只返回成功/抛通用异常糊弄；须可区分 ReservationConflict / NotFound / Unchanged / ConcurrencyConflict / DatabaseError  
- `ISlotAccountService.CorrectFromInventoryAsync` — 返回 D5 计数字段；Reserved 跳过  
- 清槽 — 与 SetSlot 同保护（可共用实现）  
- `InventoryResultEvent` / 完成通知 — 携带更新/冲突计数供 VM  
- `FrameViewModel.CorrectSlotAsync` / `ClearSelectedSlotAsync` / 盘点完成处理 — Growl 对齐 D6  
- 不新增强制覆盖参数  

**验收标准：** 见 §13  

**范围外：** 见 §14  

**实现前必读：**  
1. `CONTEXT.md` 预记 / 落账 / 派工术语  
2. `docs/adr/0002-槽位预记先于RCS下发.md`  
3. 本 Issue §12 已锁定 D1–D10 与 §11 RED 顺序  

**PRD 绑定（功能 / headless）：**  
- **Seam：** 槽位账外部写保护（`ISlotAccountService` 盘点/校正/清槽）  
- **父 PRD Issue：** 无（无新 Surface）  
- **覆盖的用户故事：** 无独立 PRD；对齐 ADR-0002 预记不变量  
- **接口 / 行为 SSOT：** 本 Issue §8、§12  

**第一个 RED：** `SlotAccountService.SetSlotAsync` — Reserved 槽人工校正后 STATE/REMARK/BIND_SOURCE 全不变（R1）。

---

## Comments

> 此内容由 AI 在分拣期间生成。

### 分拣笔记（2026-08-04 · 源码核查）

- 风险真实：盘点与人工校正（含清槽）可覆盖 `SLOT_STATE=3`；换架不写槽；TOCTOU 成立；P0-2 可被绕过。

### 决策纪要（2026-08-04 · D1–D10 已锁定）

**状态：** `needs-triage` → `ready-for-agent`

| # | 锁定摘要 |
|---|----------|
| D1 | 保护唯一条件：`STATE=3`；异常预记仍禁覆盖 + Warning |
| D2 | 盘点跳过 Reserved，继续安全槽，汇总 Warning |
| D3 | 人工校正一律拒绝；无强制覆盖；文案固定 |
| D4 | 清槽同保护；换架无关；改层/删架回归；FrameSeedReset 不改但禁运行库 |
| D5 | 安全继续/冲突跳过；D5 计数字段；同事务；取消/DB 错回滚 |
| D6 | Growl 规则；后台不弹；脱敏 taskId |
| D7 | Warning 汇总；不置工位 Alarm |
| D8 | 条件 UPDATE `STATE <> Reserved`；无迁移/行版本 |
| D9 | 只挡外部写；Confirm/Rollback/Reserve 专用 |
| D10 | affected=0 六类结果；ConcurrencyConflict fail-closed |

**ADR/PRD：** 不建。  
**RED：** R1–R20 三组顺序已锁定；首测 `SetSlotAsync`（R1）。

### RED R1–R4（2026-08-04 · 第一组单槽保护）

**真实 `SetSlotAsync` 调用链：**

```
FrameViewModel.CorrectSlotAsync / ClearSelectedSlotAsync
  → ISlotAccountService.SetSlotAsync(frameId, slotNo, materialId, slotState, author)
       → ISlotAccountStore.OpenAsync()          # 行为保持接缝（本轮抽取）
       → session.FindByFrameSlotAsync           # 生产=AsTracking FirstOrDefault
       → 直接赋 SlotState / MaterialId
       → 仅 Empty 时 Remark=null、BindTime=null（BindSource 当前不清）
       → session.SaveChangesAsync               # 生产=ApplyToEntity + SaveChanges
       → Information「人工校正…」
```

参数：`(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)`；返回 `Task`（无结果类型）。

**测试接缝：** 已抽取 `ISlotAccountStore` / `ISlotAccountSession` / `SlotRow`（Core）+ `SlotAccountStore`（Data EF）。  
`SetSlotAsync` 生产路径真实走该接缝；DI 注册 `ISlotAccountStore → SlotAccountStore`。  
**未**加入 Reserved 判断 / 条件 UPDATE / 结果分类。Reserve/Confirm/Rollback 仍直连 `IDbContextFactory`。

**R1–R4 测试名称**（`tests/CncLoader.Core.Tests/State/SlotAccountReservedProtectionTests.cs`）：

| # | 方法名 | 结果 |
|---|--------|------|
| R1 | `R1_Reserved人工校正为有料_不得覆盖状态与预记字段` | **FAIL**（RED） |
| R2 | `R2_Reserved清槽_不得清空状态与预记字段` | **FAIL**（RED） |
| R3 | `R3_Reserved且REMARK为空_仍拒绝且记数据不一致Warning` | **FAIL**（RED） |
| R4 | `R4_非Reserved正常校正_应按现有语义更新并保存` | **PASS** |

**RED 命令与计数：**

```
dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj --filter "FullyQualifiedName~SlotAccountReservedProtection"
→ 失败 3，通过 1（R4），总计 4

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 3，通过 95，总计 98（原有 94 全绿 + R4；仅新增 R1–R3 预期红）

dotnet build CncLoader.sln → 0 警告 0 错误
```

**Reserved 被覆盖的字段（实测）：**

| 场景 | 被覆盖 |
|------|--------|
| R1 人工→Occupied | `SLOT_STATE` 3→1；`MaterialId`；`UpdateTime`；SaveCount=1。REMARK/BIND_SOURCE/BindTime 本次未改 |
| R2 清槽→Empty | `SLOT_STATE` 3→0；`Remark`→null；`MaterialId`→null；`BindTime`→null；SaveCount=1。**BIND_SOURCE 当前未被清** |
| R3 REMARK 空 | 同清槽覆盖；无数据不一致 Warning |

**R4：** 非 Reserved（Empty→Occupied）按现有语义更新，SaveCount=1，保持通过。

**尚未开始：** R5 NotFound、R6 Unchanged、R7 ViewModel Growl；第二/三组盘点批量与原子并发；GREEN 修复。

**状态：** 保持 `ready-for-agent`（未关闭）。

### GREEN R1–R6（2026-08-04 · 单槽保护 + 结果契约 + 原子写）

**R5/R6 RED 基线（改造前源码行为，上一轮 + 本轮契约对照）：**
- R5：槽不存在时旧路径 `throw InvalidOperationException`，无 `NotFound` 状态。
- R6：已等于目标仍无条件 Save + 刷新 `UpdateTime`，无 `Unchanged`。
- 本轮在落地 `SlotMutationResult` 后接入原子写并同批修绿（未单独保留破损路径再跑一遍 R5/R6 红灯）。

**结果契约（Core）：**
- `SlotMutationStatus`：Updated / Unchanged / ReservationConflict / NotFound / ConcurrencyConflict / DatabaseError / Cancelled
- `SlotMutationResult`：Status、FrameId/SlotNo/SlotId、Message、Snapshot；`Succeeded` = Updated|Unchanged

**`SetSlotAsync` 新签名：** `Task<SlotMutationResult> SetSlotAsync(...)`

**原子 SQL 条件（`SlotAccountStore.TrySetExternalSlotAsync`）：**
```
WHERE FRAME_ID=? AND SLOT_NO=? AND SLOT_STATE <> '3'(Reserved)
SET SlotState, MaterialId, UpdateTime
  + Empty 时另清 Remark/BindTime（不碰 BIND_SOURCE）
→ ExecuteUpdateAsync；affected 后 AsNoTracking 重查分类
```

**affected=0 分类：** NotFound / ReservationConflict（含异常预记 Warning）/ Unchanged / ConcurrencyConflict；异常→DatabaseError；取消→Cancelled。分类后禁止无条件更新。

**调用方适配：**
| 调用方 | 适配 | 结果消费 |
|--------|------|----------|
| `FrameViewModel.CorrectSlotAsync` / `ClearSelectedSlotAsync` | 编译通过（`_ = await`） | **暂忽略**，仍固定 Success Growl → **R7** |
| `PositionSchedulerStartupTests.FakeSlots` | 返回 Updated | 测试 stub |
| Reserve/Confirm/Rollback | 未改，仍专用路径 | — |

**旧 session 接缝：** `OpenAsync` / `ISlotAccountSession` **暂时保留**（本轮 SetSlot 已不用）；供后续批量评估，未大范围删除。

**GREEN 验证：**

```
dotnet test ... --filter "~SlotAccountReservedProtection"
→ 失败 0，通过 6（R1–R6）

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 100（原 94 + R1–R6）

dotnet build CncLoader.sln → 0 警告 0 错误
git diff --check → 无冲突标记错误（仅 CRLF warning）
```

**尚未完成：** R7 ViewModel Growl；第二组盘点批量/事务；第三组原子并发与 Confirm/Rollback 回归；真 MySQL 双连接。

**状态：** 保持 `ready-for-agent`（未关闭）。

### RED R7（2026-08-04 · ViewModel 反馈）

**真实 FrameViewModel 调用链：**

```
CorrectSlotCommand → CorrectSlotAsync
  ├─ 未选中 → Warning「请先选中料架与槽位。」
  ├─ 解析 CorrectSlotState / CorrectMaterial
  ├─ ISlotAccountService.SetSlotAsync(...)   # 结果暂忽略（_ = await）
  ├─ Success「槽位 {Label} 已校正。」         # 固定，不看 Status
  └─ LoadDetailAsync(frameId)               # 始终刷新

ClearSelectedSlotCommand → ClearSelectedSlotAsync
  ├─ 未选中 → Warning
  ├─ SetSlotAsync(..., null, Empty, ...)
  ├─ Success「槽位 {Label} 已置空释放。」     # 固定
  ├─ CorrectMaterial/State 复位空
  └─ LoadDetailAsync(frameId)               # 始终刷新
```

- DI：`FrameViewModel` Singleton；依赖 `IFrameService` / `ISlotAccountService` / `IInventoryService` / `IPositionScheduler` / `ICurrentUser` / **`IUserNotificationService`**
- 无独立 `IsBusy`；命令结束看 `IAsyncRelayCommand.IsRunning`
- 异常仍走 catch → Error（本轮结果路径不抛）

**通知接缝：** 已抽取 `IUserNotificationService`（Core）+ `HandyControlUserNotificationService`（UI→Growl）。  
仅接线 `CorrectSlotAsync` / `ClearSelectedSlotAsync`（含未选中 Warning / catch Error）；**未**改结果映射，生产仍固定 Success。未全项目迁移 Growl。

**R7 测试**（`tests/CncLoader.Core.Tests/UI/FrameViewModelSlotMutationTests.cs`）：

| 测试 | 结果 |
|------|------|
| `R7_人工校正ReservationConflict_不得Success且须Warning预记文案` | **FAIL** |
| `R7_清槽ReservationConflict_不得Success且须Warning` | **FAIL** |
| `R7_Updated_保持现有成功文案` | **PASS** |
| `R7_Unchanged_不得Success且须中性Info` | **FAIL** |
| `R7_NotFound_不得Success且须Error槽位不存在` | **FAIL** |
| `R7_ConcurrencyConflict_不得Success且须Warning刷新重试` | **FAIL** |
| `R7_DatabaseError_不得Success且不得泄露内部细节` | **FAIL** |
| `R7_Cancelled_不提示失败且命令不残留运行中` | **FAIL** |
| `R7_Updated后刷新明细_清槽冲突当前也刷新_特征记录` | **PASS**（特征） |

**当前仍错误显示 Success 的状态：**  
ReservationConflict（校正+清槽）、Unchanged、NotFound、ConcurrencyConflict、DatabaseError、Cancelled。

**刷新特征：** Updated 后 `LoadDetailAsync`；ReservationConflict **当前也刷新**（忽略结果后仍走 Success 分支）。本轮不改策略。

**RED 命令：**

```
dotnet test ... --filter "~FrameViewModelSlotMutation"
→ 失败 7，通过 2，总计 9

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 7，通过 102，总计 109（原 100 全绿 + R7 中 2 绿）

dotnet build CncLoader.sln → 0 警告 0 错误
```

**GREEN 尚未开始**（禁止本轮 switch Status 映射）。

**状态：** 保持 `ready-for-agent`（未关闭）。

### GREEN R7（2026-08-04 · ViewModel 状态映射）

**实现：**
- `FrameViewModel.NotifySlotMutationResult`：集中映射 `SlotMutationStatus` → 单次通知
- `CorrectSlotAsync` / `ClearSelectedSlotAsync`：消费 `SetSlotAsync` 返回值，删除 `_ = await` 与固定 Success
- 映射放在 ViewModel；`IUserNotificationService` / `HandyControlUserNotificationService` 仍只转发文案

**校正/清槽文案：**

| Status | 级别 | 文案 |
|--------|------|------|
| Updated | Success | 校正：`槽位 {Label} 已校正。` / 清槽：`槽位 {Label} 已置空释放。` |
| Unchanged | Info | `槽位状态无需修改` |
| ReservationConflict | Warning | 校正：`槽位已被任务预记，不能人工校正` / 清槽：`槽位已被任务预记，不能清空` |
| NotFound | Error | `槽位不存在或已被删除` |
| ConcurrencyConflict | Warning | `槽位状态已变化，请刷新后重试` |
| DatabaseError | Error | `槽位操作失败，请查看日志`（不展示 Result.Message） |
| Cancelled | （静默） | 无 Success/Info/Warning/Error；命令 IsRunning 自然收尾 |
| 未知 enum | Error | `槽位操作未完成，请刷新后重试`（fail-closed） |

**Cancellation / 未知状态：** Cancelled 静默、不记用户可见失败；未知 Status 安全 Error、不抛、不 Success。

**刷新策略：** 未改；冲突后仍 `LoadDetailAsync`（特征测试保留）。

**测试：**
```
dotnet test ... --filter "~FrameViewModelSlotMutation"
→ 失败 0，通过 11（原 9 绿 + 未知 Status + HandyControl 无映射断言）

dotnet test ... --filter "~SlotAccountReservedProtection"
→ 失败 0，通过 6（R1–R6）

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 111（≥109）

dotnet build CncLoader.sln → 0 警告 0 错误
git diff --check → 无 whitespace error（仅 CRLF warning）
```

**未验证：** 真实 HandyControl Growl 视觉；批量盘点 R8–R13；真 MySQL 并发；DPI。

**下一阶段：** 批量盘点 R8–R13 RED。

**状态：** 保持 `ready-for-agent`（未关闭）。

### RED R8–R13（2026-08-04 · 批量盘点）

**真实盘点回写链（源码确认）：**

```
[手动] FrameViewModel.StartInventoryAsync → IInventoryService.StartInventoryAsync（仅 taskId / Info「已发起」）
[自动] InventorySchedulerService.InventoryOneAsync → 同上（author=inv-auto）
         │
         ▼ identifyQR 回调
InventoryService.HandleScanAsync
  → ISlotAccountService.CorrectFromInventoryAsync(frameId, posStart, products)
       → ISlotAccountStore.OpenAsync / FindByFrameOrderedAsync（本轮行为保持接缝）
       → 按层→位顺序无条件写 MaterialId/STATE/BIND_SOURCE（仍覆盖 Reserved）
       → 一次 SaveChangesAsync
       → 返回 InventoryCorrectionResult（骨架；Conflict=0）
  → InventoryCompleted(COMPLETED, CorrectedCount=UpdatedCount, Correction=…)
       → FrameViewModel：仍固定 Success（R10 RED）
       → Scheduler：仅 TCS/日志，无 Growl（R12 契约 PASS）
```

| # | 项 | 结论 |
|---|----|------|
| 1 | `CorrectFromInventoryAsync` | 现返回 `InventoryCorrectionResult`；原 `int` 语义迁到 `UpdatedCount` |
| 2 | 盘点识别结果 | `RcsScanResultEvent.Products` 字符串列表（孔位顺序） |
| 3 | Frame/Slot 匹配 | `posStart`→DecodeIdentifyHole；整架 `LayerNo/PosInLayer` 有序对齐 |
| 4 | DbContext/Session | 生产经 `ISlotAccountStore` Session；DI=`SlotAccountStore` |
| 5 | Save 边界 | 单次 `SaveChangesAsync`（整架 LastVerifyTime 一并刷） |
| 6 | 用户命令 | `StartInventoryCommand` → 发起 Info；完成靠事件 |
| 7 | 后台 | `InventorySchedulerService` 等 `InventoryCompleted` TCS |
| 8 | 回 VM | `InventoryCompleted` → `OnInventoryCompleted`（`_notify`） |
| 9 | Updated/Skipped 通道 | **已挂** `InventoryResultEvent.Correction`；VM **未消费冲突** |
| 10 | Store Session 接缝 | **正好**：`OpenAsync` + `FindByFrameOrderedAsync` + 一次 Save |

**行为保持接缝：** 复用 `ISlotAccountStore`/`ISlotAccountSession`；新增 `FindByFrameOrderedAsync`；`CorrectFromInventory` 改走 Session；**仍覆盖 Reserved**；未加条件 UPDATE / skip。

**新增结果契约：** `InventoryCorrectionResult` / `InventoryCorrectionStatus`（Core）。  
机械接入：Service 返回 + Event.Correction 透传；当前凡写入一律 `UpdatedCount`，`ReservationConflictCount=0`，`ConflictSlots=[]`。

**R10 路径：A**（Correction 能回到事件/VM；VM 仍固定 Success → RED）。

**测试结果：**

| # | 测试 | 结果 |
|---|------|------|
| R8 | `R8_混合批次_Reserved槽完整快照不得变且须计冲突` | **FAIL**（B 被覆盖；Conflict=0；Updated=2） |
| R9 | `R9_结果计数_四类互斥可核对` | **FAIL**（Updated=3；Unchanged/Conflict/NotFound=0） |
| R10 | `R10_混合盘点完成_不得Success且须Warning…` | **FAIL**（Success=1；Warning=0） |
| R10 | `R10_发起盘点成功不得当作回写全部成功` | **PASS**（契约） |
| R11 | `R11_全部Reserved_不得更新且不得纯成功计数` | **FAIL**（全覆盖；Succeeded=true） |
| R12 | 后台无 IUserNotification/Growl + 完成不抛 | **PASS**（契约） |
| R13 | 取消 / DB 异常回滚 / 成功一次提交 | **PASS**（整批回滚特征已有） |

```
dotnet test ... --filter "~InventoryReservedSlotProtection|~InventorySchedulerReservedConflict|~FrameViewModelInventory"
→ 失败 4，通过 6，总计 10

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 4，通过 117，总计 121（原 111 全绿 + 6 契约绿；仅 R8/R9/R10混合/R11 预期红）
```

**GREEN 尚未开始**（禁止 skip Reserved / 条件 SQL / UI Warning 映射）。

**下一阶段：** R8–R13 GREEN（跳过预记 + 真计数 + VM Warning）。

**状态：** 保持 `ready-for-agent`（未关闭）。

### GREEN R8–R13（2026-08-04 · 批量盘点保护）

**批量事务实现：**
- `ISlotAccountSession`：`TrySetExternalSlotAsync`（同 DbContext）+ `CommitAsync` / `RollbackAsync`
- `OpenAsync` 显式 `BeginTransaction`；Dispose 未 Commit 安全回滚
- 单槽 `ISlotAccountStore.TrySetExternalSlotAsync` 未改语义

**条件 UPDATE（会话内）：**
```
WHERE FRAME_ID=? AND SLOT_NO=? AND SLOT_STATE <> '3'
→ ExecuteUpdateAsync（占用附带 RCS_QR/BindTime/LastVerify；空码不碰 Remark/BIND_SOURCE）
affected=0 只读分类，禁止无条件 SaveChanges
```

**计数与状态：**
- Updated / Unchanged / ReservationConflict / NotFound / ConcurrencyConflict 互斥
- `Completed` / `CompletedWithWarnings` / `Cancelled` / `DatabaseError`
- ConflictSlots 上限 10，REMARK 脱敏；总冲突数不截断
- 有 Updated → Commit 一次；全冲突/无变化 → Rollback、CommitCount=0

**UI 最终通知：** `FrameViewModel.NotifyInventoryFinalResult` 消费 `Correction`  
- 无警告 Success；有预记冲突 Warning（含更新/跳过数）；全预记「未更新，全部为预记槽」；DatabaseError 固定文案；Cancelled 静默  
- 发起 Info ≠ 回写成功；每最终结果只通知一次

**后台日志边界：** Scheduler 仍无 Growl / 无 `IUserNotificationService`；汇总 Warning 只在 `SlotAccountService` 记一条。

**验证：**
```
dotnet test ... --filter "~InventoryReservedSlotProtection|~InventorySchedulerReservedConflict|~FrameViewModelInventory"
→ 失败 0，通过 10

dotnet test ... --filter "~SlotAccountReservedProtection|~FrameViewModelSlotMutation"
→ 失败 0，通过 17（R1–R7）

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 121

dotnet build CncLoader.sln → 0 警告 0 错误
git diff --check → 无 whitespace error（仅 CRLF warning）
```

**尚未完成：** R14–R20 原子并发 / Confirm/Rollback 状态机回归；真 MySQL 事务与隔离级别未验证。

**状态：** 保持 `ready-for-agent`（未关闭）。

### RED R14–R20（2026-08-04 · 原子并发与状态机契约）

**本轮范围：** 只写测试 + 最小行为保持接缝；**不修改**外部写保护逻辑、不进 GREEN、不 commit。

#### 最小生产接缝（行为保持，非保护修复）

| 接缝 | 说明 |
|------|------|
| `ISlotAccountStore` 扩展 | `ReservePut/Take`、`ConfirmPut/Take`、`RollbackPut/Take`；旧 fake 用接口默认 `NotSupported` |
| `SlotAccountStore` | 承接原 `SlotAccountService` 内 EF 条件更新/跟踪落账（SQL 语义不变） |
| `SlotAccountService` | Reserve/Confirm/Rollback 改为委托 Store + 日志 |
| `IFrameStructureStore` + `FrameStructureStore` | 改层/删架非空计数与 Apply/SoftDelete；`FrameService` 编排不变（非空含 Reserved 仍拒绝） |

**未改：** 外部写 `STATE <> Reserved` 条件、盘点 skip、UI 映射；Confirm/Rollback **仍不校验** BIND_SOURCE 方向（见 R18 RED）。

#### 共享并发 fake

`tests/.../State/ConcurrentSlotLedger.cs`：
- 同时实现外部条件写 + Reserve/Confirm/Rollback 原子状态；
- 锁 + TCS（`RunContinuationsAsynchronously`）；会话 WHERE 按**已提交** `_slots` 评估（模拟他连接 Reserve 已提交）；
- 记录线性化；`finally`/测试末尾释放 gate；无 Sleep。

断言一律经真实 `SlotAccountService` / `FrameService`。

#### 测试清单与结果

| # | 测试 | 结果 |
|---|------|------|
| R14 | `SlotReservationConcurrencyTests.R14_盘点条件写前Reserve先成功_affected0且Reserved完整保持` | **PASS** |
| R15A | `...R15A_外部校正先成功使不满足Reserve前置_Reserve须拒绝` | **PASS** |
| R15B | `...R15B_外部校正先成功且仍满足Reserve前置_Reserve写入完整所有权不得混字段` | **PASS** |
| R16 | `...R16_人工校正条件写前Reserve先成功_...` | **PASS** |
| R16 | `...R16_清槽条件写前Reserve先成功_...`（真实 `SetSlotAsync`，非盘点代替） | **PASS** |
| R17 | `SlotStateTransitionProtectionTests.R17_*` Confirm/Rollback 合法路径 ×3 | **PASS** |
| R18 | taskId 不匹配 / 非 Reserved Rollback | **PASS** |
| R18 | `R18_BIND_SOURCE方向不匹配_ConfirmPut不得落账RSV_TAKE` | **FAIL（RED）** |
| R18 | `R18_BIND_SOURCE方向不匹配_ConfirmTake不得清空RSV_PUT` | **FAIL（RED）** |
| R19 | `FrameReservedSlotStructuralProtectionTests.R19_*` 改层/删架 ×3 | **PASS** |
| R20 | 单槽六类分类 + 批量互斥/ConcurrencyConflict | **PASS** |

**P0-2 复用结论：** `ReservationFirstDispatcherTests` 只测协调器 lambda，**无**真实 `SlotAccountService` Confirm/Rollback 接线 → R17/R18 **新写**真实 Service 测试；不重复 Dispatcher 用例。

#### 确定性交错方法（R14/R16）

1. 设 `PauseBeforeExternalWrite = TCS(RunContinuationsAsynchronously)`  
2. `Task.Run` 启动真实 `CorrectFromInventoryAsync` / `SetSlotAsync`  
3. `WaitUntil` 见到 `ExternalConditionalUpdateEnter`（Yield 轮询，无 Sleep）  
4. 主线程跑真实 `ReserveAsync` / `ReserveTakeAsync` 至成功  
5. `pause.TrySetResult()`；`finally` 再释放防死锁  
6. 断言完整槽位快照 + 线性化子序列  

#### R14 线性化记录（断言强制）

```
ReadIdentity
→ ReserveCommit
→ ExternalConditionalUpdate(affected=0)
→ ClassifyReserved
```

（完整 trace 另含 `ExternalConditionalUpdateEnter` / `ReserveEnter` / `Rollback` 等；子序列顺序由 `HasOrder` 锁定。）

#### Confirm/Rollback 所有权证据

- 合法 PUT/TAKE Confirm、Rollback：**PASS**；`ExternalWriteCount=0`（不经 `TrySetExternalSlot`）。  
- taskId 不匹配 / Rollback 非 Reserved：**拒绝且快照不变**。  
- **方向不匹配 RED：** `ConfirmPut` 可把 `RSV_TAKE` 落成 Occupied+CONFIRMED；`ConfirmTake` 可清空 `RSV_PUT`（生产 Store 未校验 BIND_SOURCE）。属真实状态机缺口，本轮只记录不修。

#### 改层/删架（R19）

- `CountNonEmptySlots`（`SLOT_STATE != Empty`，含 Reserved）→ 改层抛「占用/预记/锁定」；`ApplyUpdateCount=0`。  
- `CheckDeleteFrame`/`DeleteFrame`：`CanDelete=false`，不软删。  
- 空架改层仍放行（回归非空语义未收紧错）。

#### 生产静态核对（源码）

- 单槽/批量外部写 SQL：`SlotState != SlotStates.Reserved`（`SlotAccountStore.ExecuteConditionalUpdateAsync`）。  
- Reserve PUT/TAKE 条件仍为 Empty/Occupied，未放宽。  
- Confirm/Rollback 仍校验 Remark 所有权 + Reserved；**缺**方向校验（R18 RED）。  
- 改层/删架非空计数仍 `!= "0"`（Reserved 计入）。

#### 验证命令

```
dotnet test ... --filter "~SlotReservationConcurrency|~SlotStateTransitionProtection|~FrameReservedSlotStructuralProtection"
→ 失败 2，通过 15，总计 17（仅 R18 方向×2 预期红）

dotnet test ... --filter "~SlotAccountReservedProtection|~InventoryReservedSlotProtection|~FrameViewModelSlotMutation|~FrameViewModelInventory"
→ 失败 0，通过 25（R1–R13 回归）

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 2，通过 136，总计 138（原 121 全绿 + 15 新绿；仅 R18×2 红）

dotnet build CncLoader.sln → 0 警告 0 错误
```

**真 MySQL 双连接并发：** 仍未验证（fake 线性化 ≠ InnoDB 隔离级别）。

**GREEN 未开始**（R18 方向校验为候选修复；外部写保护 R14–R16/R20 已直接 PASS）。

**状态：** 保持 `ready-for-agent`（未关闭）。

### GREEN R18（2026-08-04 · Confirm/Rollback 方向所有权）

#### 源码确认的方向/字段语义（以 Reserve 写入常量为准）

| 操作 | BIND_SOURCE | SLOT_STATE | MaterialId | REMARK | BindTime |
|------|-------------|------------|------------|--------|----------|
| ReservePut | `RSV_PUT` | Reserved | 写入意向物料 | =taskId | now |
| ReserveTake | `RSV_TAKE` | Reserved | **保留**原物料 | =taskId | now |
| ConfirmPut | → `CONFIRMED` | → Occupied | 保留 | **保留** taskId | now |
| ConfirmTake | （不改） | → Empty | → null | → null | → null |
| RollbackPut | （不改） | → Empty | → null | → null | → null |
| RollbackTake | （不改） | → Occupied | **保留** | → null | → null |

幂等：ConfirmPut 遇 Occupied+同 REMARK → true；ConfirmTake 遇 Empty+同 REMARK → true。

#### 实现（Data 层原子条件，非先读后写）

`SlotAccountStore` Confirm/Rollback 改为 `ExecuteUpdateAsync`，WHERE 精确比较常量：

- ConfirmPut：`REMARK==taskId AND STATE==Reserved AND BIND_SOURCE==RSV_PUT`
- ConfirmTake：`… AND BIND_SOURCE==RSV_TAKE`
- RollbackPut：`… AND BIND_SOURCE==RSV_PUT`
- RollbackTake：`… AND BIND_SOURCE==RSV_TAKE`

affected=0 → 返回 false；**禁止**第二次无条件 UPDATE。  
`ConcurrentSlotLedger` 同步同一原子条件语义。

未改：外部校正/盘点保护、UI、Reserve 前置条件。

#### 测试

| 测试 | 结果 |
|------|------|
| `R18_BIND_SOURCE方向不匹配_ConfirmPut不得落账RSV_TAKE` | **PASS**（原 RED→绿） |
| `R18_BIND_SOURCE方向不匹配_ConfirmTake不得清空RSV_PUT` | **PASS**（原 RED→绿） |
| `R18_BIND_SOURCE方向不匹配_RollbackPut不得回滚RSV_TAKE` | **PASS**（新增） |
| `R18_BIND_SOURCE方向不匹配_RollbackTake不得回滚RSV_PUT` | **PASS**（新增） |
| `R18_ConfirmPut正确方向_RSV_PUT完整字段转换` | **PASS**（新增） |
| `R18_ConfirmTake正确方向_RSV_TAKE完整字段转换` | **PASS**（新增） |
| R17 合法 Confirm/Rollback | **PASS**（回归） |

错误方向：完整快照（STATE/REMARK/BIND_SOURCE/MaterialId/BindTime）不变；trace 含 `*Rejected`、无 `*Commit`。

#### 验证

```
dotnet test ... --filter "~SlotStateTransitionProtection|~SlotReservationConcurrency"
→ 失败 0，通过 18

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 142

dotnet build CncLoader.sln → 0 警告 0 错误
```

**真 MySQL 双连接：** 仍未验证。

**状态：** 保持 `ready-for-agent`（未关闭）。

### GREEN｜禁止人工创建 Reserved（2026-08-04 · 审查 Medium 修复）

**背景：** 最终审查发现违反 D9——UI「预记(3)」+ `SetSlotAsync` 可无 REMARK/BIND_SOURCE 写出 `STATE=3`，绕过专用 Reserve。

#### 三层防护

| 层 | 行为 |
|----|------|
| UI | `SlotStateOptions` 仅 `空(0)/占用(1)/锁定(2)`；选中已预记槽时编辑默认「空(0)」；`SlotVm` 展示「预记」不变 |
| Service | `NormalizeExternalSlotState`：仅 0/1/2；`Reserved`/未知 → `InvalidTargetState`，**不调 Store**，Warning 日志 |
| Store | `TrySetExternalSlot` / `ExecuteConditionalUpdate`：`target==Reserved` → `InvalidTargetState=true`，不 `ExecuteUpdate`（不冒充 ReservationConflict） |

新增 `SlotMutationStatus.InvalidTargetState`；VM Warning 固定：`预记状态只能由派工流程创建，不能人工设置`。

#### 测试（RED 契约 → 已绿）

- Service：非 Reserved / 已 Reserved 目标写 3 → InvalidTargetState、StoreCall=0、快照不变  
- Store 直调 target=Reserved → AtomicUpdateCount=0  
- ReservePut 合法创建仍完整所有权  
- VM：选项不含预记；展示 StateBadge=reserved；InvalidTargetState → Success=0 Warning=1  

#### 静态核对（src）

- 合法 Reserve 路径：保留  
- UI 展示 Reserved：保留（`SlotVm`）  
- 外部人工写 target=Reserved：Service+Store 拒绝  
- `FrameSeedReset`/工具：未改  

#### 验证

```
~SlotAccountReservedProtection|~FrameViewModelSlotMutation → 0 失败 / 23 通过
~SlotReservationConcurrency|~SlotStateTransitionProtection → 0 失败 / 18 通过
全量 → 0 失败 / 148 通过
dotnet build CncLoader.sln → 0 警告 0 错误
git diff --check → 无 whitespace error（仅 CRLF warning）
```

**后续仍开放：** LAST_VERIFY_TIME 整架注释不一致、历史 BIND_SOURCE 残留、幂等 Confirm 不校验 CONFIRMED、真 MySQL 双连接。

**状态：** 保持 `ready-for-agent`（未关闭）。

### 验收关闭（2026-08-04）

> 此内容由 AI 在分拣期间生成。

**状态：** `ready-for-agent` → `closed`  
**Labels：** 追加 `closed`（移除 `ready-for-agent`）

#### 1. 根因

- 自动盘点 / 人工校正 / 清槽使用跟踪实体无条件 `SaveChanges` 写
- 可覆盖 P0-2 原子预记（`SLOT_STATE=3` + REMARK/taskId）
- 通用 `SetSlotAsync` 还能人工创建 Reserved（绕过专用 Reserve）
- Confirm/Rollback 原先缺 BIND_SOURCE 方向校验

#### 2. 最终实现

- 单槽条件 UPDATE：`WHERE (FRAME_ID, SLOT_NO) AND SLOT_STATE != Reserved`
- 批量同事务条件 UPDATE；成功只 Commit 一次；取消/DB 异常整批回滚
- affected=0 分类：ReservationConflict / NotFound / Unchanged / ConcurrencyConflict（分类后无无条件重试）
- 安全槽继续更新，Reserved 跳过且完整快照不变
- UI：冲突/mixed → Warning（非 Success）；后台 Scheduler 仅日志，不依赖 Growl/Dispatcher
- target=Reserved 三层拒绝：UI 下拉不含预记 → Service `InvalidTargetState`（StoreCall=0）→ Store 第二道防线
- Confirm/Rollback：`Reserved + REMARK=taskId + BIND_SOURCE 方向` 原子校验；错误方向/taskId 不改完整快照

#### 3. R1–R20 与额外防线

| 组 | 覆盖 | 结果 |
|----|------|------|
| R1–R7 | 单槽保护 + ViewModel 映射 | 全部 PASS |
| R8–R13 | 批量盘点 + 事务回滚 + 后台 Scheduler | 全部 PASS |
| R14–R16 | 并发交错（含 R15A/R15B） | 全部 PASS |
| R17/R18 | Confirm/Rollback 合法路径与方向/taskId 拒绝 | 全部 PASS |
| R19 | 改层/删架视 Reserved 为非空 | 全部 PASS |
| R20 | affected=0 分类 | 全部 PASS |
| 额外 | 人工 target=Reserved 三层拒绝；选项不含预记；展示仍为预记 | 全部 PASS |

测试无 Skip/Ignore、无弱化断言、无测试专用生产分支、无静态 Override 残留；真实 `SlotAccountService` / Store 路径覆盖核心安全逻辑。

#### 4. 最终回归命令

```text
dotnet test ... --filter "~SlotAccountReservedProtection|~FrameViewModelSlotMutation"
→ 失败 0，通过 23，跳过 0，总计 23

dotnet test ... --filter "~InventoryReservedSlotProtection|~InventorySchedulerReservedConflict|~FrameViewModelInventory"
→ 失败 0，通过 10，跳过 0，总计 10

dotnet test ... --filter "~SlotReservationConcurrency|~SlotStateTransitionProtection|~FrameReservedSlotStructuralProtection"
→ 失败 0，通过 21，跳过 0，总计 21

dotnet test tests/CncLoader.Core.Tests/CncLoader.Core.Tests.csproj
→ 失败 0，通过 148，跳过 0，总计 148（含原 P0-1～P0-4）

dotnet build CncLoader.sln
→ 0 警告，0 错误

git diff --check
→ 无 whitespace error（exit 0；仅有 CRLF 提示）
```

#### 5. 未做真实 UI / 真 MySQL 的理由

- 外部写保护、affected 分类、批量事务、Confirm/Rollback 方向校验均已由可复跑自动化测试覆盖
- 不为验收写入本地业务库、不制造预记任务、不启动 App、不跑 FrameSeedReset

#### 6. 未验证项

- 真 MySQL 双连接隔离级下 Reserve vs Correct 交错
- 真实 identifyQR 盘点端到端
- 真实 PLC/RCS Confirm 落账路径
- HandyControl Growl / DPI / 多显示器人工观感

#### 7. 保留风险（未修复，不阻塞关闭）

- `LAST_VERIFY_TIME` 注释与实现不一致（非整架统一刷新）
- 历史异常 `BIND_SOURCE` 残留仍受 STATE=3 保护，不能靠人工校正「修好」
- 幂等 Confirm 不校验 `CONFIRMED`（既有契约）
- `FrameSeedReset` 工具仍可清空含预记槽——禁止对运行库/生产库执行
- Commit 成功但客户端收到异常的不确定提交窗口本期不猜测解决

#### 8. 无 ADR / PRD

- 未提供管理员强制覆盖
- 未新增 Surface
- 未做数据库迁移 / 行版本列
- 修复既有 ADR-0002 预记不变量，无需新建 ADR

**静态核对（1–27）：** 全部成立（源码审查 + 上述测试）。

**结论：** 本 Issue 验收标准已满足，关闭。真实 UI/MySQL/PLC/RCS 不阻塞关闭。下一步候选：P0-5「调度路由未过滤软删配置」。
