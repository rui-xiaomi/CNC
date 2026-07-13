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
