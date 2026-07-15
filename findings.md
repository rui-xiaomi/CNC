# Findings — 监控看板悬浮卡片视觉升级

## Spec / Plan
- Spec：`docs/superpowers/specs/2026-07-10-dashboard-neon-float-design.md`
- Plan：`docs/superpowers/plans/2026-07-10-dashboard-neon-float-plan.md`

## 用户确认决策
1. 范围：仅监控看板（A）
2. 光晕：克制，默认几乎无光，选中/告警才亮（A）
3. 波形：纯装饰，不绑数据（A）
4. 状态图标：圆点 → 矢量，保留彩色标签（A）
5. 落地：看板专用 Style 包，不改全局 `Panel`（方案 1）
6. Spec 全文已批准（2026-07-10）

## 与现有设计基线的关系
- `docs/UI设计文档.md` 原禁止霓虹/装饰悬浮 → 收窄为「全局禁止，监控看板允许克制例外」
- 签名元素（机台卡 `CalibrationTicks`）保留
- 全局 `AccentColor`/`AlarmColor` 不动；看板另增 `DashAccent`/`DashAlarm`

## 技术锚点
- Dashboard 模板：`PageTemplates.xaml` → `DataTemplate` for `DashboardViewModel`
- 全局 Panel：`Styles.xaml` `x:Key="Panel"`（无阴影）
- 资源合并：`App.xaml` Tokens → Styles → **DashboardStyles（新）** → PageTemplates
- 状态键：`StateBadge` 字符串 `ok|run|warn|alarm|ng|idle|offline`

## 已知技术债（暂不处理）

### RCS 回调去重：`ForgetTask` 与 `_seenOrder` 僵尸项
- **位置**：`src/CncLoader.Communication/Rcs/RcsCallbackProcessor.cs` — `ForgetTask` / `MarkSeen`
- **现象**：redo 成功后 `ForgetTask` 只从 `_seen`（HashSet）删除 `push:{taskId}:*` / `scan:{taskId}:*`，`_seenOrder`（Queue）仍保留对应字符串
- **影响**：不影响正确性。容量顶满（4000）时 FIFO 淘汰会对已不在 `_seen` 的键多做几次无效 `Remove`
- **决策**：已知技术债，**暂不处理**（对抗评审 [可选] 项，2026-07-13）

## 本轮结论（Session 74 · 2026-07-13 `/continue`）
- Phase 4 代码步骤已全部勾完，计划状态改为「已完成（待用户确认）」；下一主线为 **Phase 6 现场联调**（清单已写入 `task_plan.md`）。
- 审查高危修复、PLC Dispose 占闸、设备流水收敛、`AGENTS.md`/`reviewer` 已落地；`ForgetTask`/`_seenOrder` 僵尸项维持技术债。
- Phase 6 中凡涉及真 PLC/FINS/现场网络的项一律标 **待真机验证**，不得在无真机证据下声称完成。
- 本地 `main` 相对 `origin/main` 可能有未 push 提交；push 须用户确认。

## Session 75 · 联调前配置核对（选项 A）
- 新增 `docs/现场联调配置清单.md`：演示默认 vs 现场必改对照表 + 环境/DB/日志自检勾选。
- 新增 `src/CncLoader.App/appsettings.Field.example.json`（双模拟器关、Scheduler 默认关、无真实口令）。
- 更新 `docs/演示实操手册.md` §7、`docs/README.md` 索引；`task_plan` Phase 6「文档已备、现场改值待勾」。
- **未**把仓库默认 `UseSimulator` 改成 false（避免破坏本机演示）；现场用示例文件改配。

## Session 76 · RCS 点到点联调页（2026-07-15）
- 模式取自 `Rcs.UseSimulator`，展示生效 `IRcsRuntimeConfig.BaseUrl`。
- 暂停自动派工 = 进程内 `volatile` 标志，闸在 UploadRequested / Done 入队 / DispatchLoop / TryDispatchUpload / DispatchOne；**不**停 PLC 轮询、回调、Tracker、已下发任务收口；换架/水位/盘点自动未纳入本开关。
- 真实 RCS：须本次运行「测试连接」成功且 BaseUrl+ClientCode 未改；下发前二次确认；取消不落库不 HTTP。
- 手工点到点不查 LOCATION_MAP、不写 POS_TEST_START、不改槽位账（沿用原 `DispatchTransitAsync` 手工路径）。
