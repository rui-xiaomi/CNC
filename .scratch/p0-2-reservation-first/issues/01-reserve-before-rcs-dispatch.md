# P0-2：RCS 下发早于槽位预记，存在可执行 orphan 任务窗口

Type: bug
Priority: P0
Status: closed
Labels: bug, P0, closed
Feature: p0-2-reservation-first

## 问题

`PositionScheduler` 的自动上料和下料路径当前先调用 RCS，再执行槽位预记。
并发盘点、人工校正或其他写入者抢占槽位时，预记可能失败，但实物搬运已经开始，
只能依赖取消和本地 FAILED 补偿，存在账实脱节与 orphan 任务窗口。

## 已确认策略

- 自动派工预生成全局唯一 taskId。
- 有槽位账的路径必须先用该 taskId 原子预记，再使用同一 taskId 下发 RCS。
- 预记失败不得调用 RCS：上料无料保持等待；下料无空槽进入 Alarm。
- RCS 明确下发失败或抛出异常时回滚本次预记并进入 Alarm。
- RCS 下发成功后保留预记，完成后继续沿用现有 Confirm；失败/取消继续沿用现有 Rollback。
- 并发争抢同一槽位时，只有预记成功的一方可以调用 RCS。
- 直接交接到下一机台时，先登记 Pending `_expectedInbound` 再下发；目标工位只消费下发成功后转为 Dispatched 的登记；失败只删除 taskId 匹配的登记。
- 无料架槽位账的命名区路径保持现状，不伪造槽位预记。
- 手工 RCS 测试接口保持兼容：未传 taskId 时仍由 `RcsTaskService` 生成。

## RED 测试

1. 预记返回 null 时不调用 RCS。
2. 预记成功后才调用 RCS，且两者使用同一 taskId。
3. RCS 明确失败时回滚预记。
4. RCS 抛异常时回滚预记。
5. 两个并发请求抢同一资源时只有一方调用 RCS。
6. RCS 返回的 taskId 与预生成值不一致时按失败回滚。

## 验收标准

- [x] 上料/下料有槽位账路径均为“预生成 taskId → 预记 → 下发”。
- [x] 预记失败路径没有 RCS 调用。
- [x] 下发失败的预记已回滚。
- [x] `RcsTaskService` 支持并尊重调用方预生成 taskId，其他调用方保持兼容。
- [x] NUnit 测试通过。
- [x] `dotnet build CncLoader.sln` 0 警告、0 错误。

## 范围外

- 不引入消息队列、Outbox、分布式事务或新数据库表。
- 不修改 RCS 协议、PLC 点位和手工 RCS 测试流程。
- 不处理 P0-3 及其他分级问题。

## Comments

> 此内容由 AI 在分拣期间生成。

## 验收关闭（2026-08-04）

**状态：** `verification` → `closed`

**验收结果：**

- ADR：`docs/adr/0002-槽位预记先于RCS下发.md`（Accepted）
- NUnit：`ReservationFirst*` 8/8 通过
- `dotnet build CncLoader.sln`：0 警告、0 错误
- 本地模拟器启动后自动派工日志可见「先预记再下发 RCS」节拍

**结论：** 本 Issue 验收标准已满足，关闭。
