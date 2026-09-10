---
name: wpf-desktop-workflow
description: 为 WPF/Windows 桌面项目建立或执行可审计的 AI 编程工作流，覆盖桌面 Surface 设计、PRD、垂直切片 Issue、MVVM/STA/UI Automation 测试、诊断与发布门禁。用户提到 WPF、XAML、桌面客户端、Window、UserControl、Dialog、托盘、向导、FlaUI、WinAppDriver，或要求把 Web 工作流改成桌面版、初始化 WPF 设计规范、审查 WPF UI 方案时使用。
---

# WPF 桌面工作流

先识别真实项目，再应用桌面契约。不得仅把“页面、路由、DOM”替换成 WPF 名词。

## 1. 识别项目

1. 读取仓库自己的 `AGENTS.md`、`CLAUDE.md`、`README*`。
2. 检查 `.sln`、`.csproj`、`Directory.Packages.props`、`App.xaml` 与现有测试项目。
3. 仅在满足下列任一条件时启用 WPF 契约：
   - `.csproj` 含 `<UseWPF>true</UseWPF>`；
   - 项目文档明确声明 WPF；
   - 用户明确要求新建 WPF 项目。
4. 从真实依赖确定 MVVM、UI 库、数据访问、测试框架和目标 Windows 版本。模板中的 WPF-UI、HandyControl、EF Core、MySQL 只是参考配置，不得覆盖现有选型。

启用后通读 [references/WPF-PROFILE.md](references/WPF-PROFILE.md)。拆 Issue 时再读 [references/WPF-ISSUE-TEMPLATE.md](references/WPF-ISSUE-TEMPLATE.md)。

## 2. 选择任务路径

### 初始化桌面规范

1. 预览项目现状与将新增的文件。
2. 若项目尚无规范，可运行 `scripts/Initialize-WpfWorkflow.ps1 -ProjectPath <path>` 预览；用户确认后加 `-Apply`。
3. 不覆盖已有 `CONTEXT.md`、ADR、DESIGN、PRD 或 XAML。发生冲突时生成差异清单并停止。
4. 将模板中的技术基线替换为项目实际依赖；保留未验证项为 `待确认`，不得编造版本和资源键。

### 设计或评审 UI

1. 使用 Surface，而不是 Web 页面作为基本单元。
2. 明确窗口所有权、模态性、生命周期、焦点、键盘、DPI、主题、多显示器和可访问性。
3. 为每个 Surface 写状态矩阵；只列适用状态，并为不适用的全局状态说明理由。
4. 通用视觉规则写入 `DESIGN.md`；业务 Surface 布局写入 PRD；可观察行为写入 Issue。

### 生成 PRD 和 Issue

1. PRD 先写 `状态策略`，再写 `Surface 清单`。
2. 每个 Surface 使用稳定的 `surface-id`；兼容旧流程时可同时填写 `page-id`，值与 `surface-id` 相同。
3. 先交付 `app-shell`，功能 Surface 依赖它。
4. Issue 必须包含 PRD 定位、States 矩阵、测试层级、完成证据与人工验证项。

### 实现和验证

1. 先证明 RED，再做最小 GREEN，最后重构。
2. 测试顺序：领域/ViewModel → STA WPF 集成 → 关键路径 UI Automation → 人工视觉与兼容性。
3. UI Automation 只覆盖高价值主路径；用条件等待，禁止固定睡眠。
4. 说明每项验证是否需要交互式 Windows Session。无会话时不得声称 UI Automation 通过。
5. 运行项目真实的 build/test/lint 命令；没有真实工程时只声明静态验证。

### 诊断

按 ViewModel/领域 → STA 集成 → harness/UI Automation → Binding/Validation/Dispatcher → 日志/dump/ETW → PowerShell HITL 的顺序建立反馈回路。修复后补回归测试并清理探针。

## 3. 与现有 Skills 协作

- `/setup-skills`：记录 WPF profile 与项目实际依赖。
- `/grill-with-docs`：按桌面 Surface、生命周期和状态进行拷问。
- `/to-prd`：使用 Surface 清单与桌面状态策略。
- `/to-issues`：使用 WPF Issue 模板，不使用 Web CSS/ARIA/路由模板。
- `/tdd`：按四层测试策略执行。
- `/diagnose`：优先 WPF 反馈回路。
- `/triage`：检查 app-shell、自动化名称、STA 与人工桌面验收门禁。

WPF 契约与通用 Skill 冲突时，以本 Skill 的桌面平台契约为准；领域、PRD 绑定、垂直切片和 Triage 状态机仍沿用通用规则。

## 4. 完成标准

- 项目技术选型来自真实文件，不来自模板猜测。
- DESIGN、PRD、Issue 的职责没有重复。
- Surface、状态、依赖和测试层级可以相互追溯。
- build/test/UI Automation/人工 QA 的实际状态分别报告。
- 未验证的依赖版本、资源键、签名、安装升级和 CI 均明确列出。

