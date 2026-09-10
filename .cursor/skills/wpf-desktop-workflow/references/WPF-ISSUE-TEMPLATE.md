# WPF UI Issue 模板

```markdown
## 目标

一句话描述用户可观察的结果。

## PRD 绑定

- 父 PRD：#___
- surface-id：`___`
- 兼容 page-id：`___`（与 surface-id 相同；不需要兼容时删除）
- Surface 类型：Window / Page / UserControl / Dialog / Panel / Tray / Wizard / ToolWindow
- Owner / 模态性：___
- 覆盖用户故事：US-___
- 实现前必读：
  1. PRD `状态策略`
  2. PRD `Surface 清单 › <surface-id>`
  3. `docs/design/DESIGN.md` 对应章节
  4. 相关 ADR

## 依赖

- app-shell / 前置 Issue：#___

## States 矩阵

| 状态 | PRD 来源 | 触发方式 | 可观察预期 | 恢复路径 |
|---|---|---|---|---|
| default | 状态策略 › default | ___ | ___ | — |
| loading | 状态策略 › loading | ___ | ___ | ___ |

不适用状态及理由：___

## 验收标准

### 功能

- [ ] ___

### UI 行为

- [ ] 键盘、焦点、Owner、关闭与 AutomationProperties 行为符合 PRD

### 人工桌面 QA

- [ ] 100% / 125% / 150% / 200% DPI
- [ ] 浅色 / 深色主题
- [ ] 多显示器与断开显示器回落

## 测试计划

- 领域/ViewModel：___
- STA WPF 集成：___
- UI Automation：___（是否需要交互式 Windows Session：是/否）
- 人工 QA：___

## 完成证据

- build：命令 + 结果
- test：命令 + 结果
- UI Automation：命令 + 结果或未运行原因
- 人工 QA：结果或未验证项
```

功能/headless Issue 可删除 Surface、DPI、主题和 UI Automation 字段，但仍保留 PRD 绑定、测试计划与完成证据。

