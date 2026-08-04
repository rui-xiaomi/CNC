# 领域文档

工程 skills 探索代码库时应如何消费本仓库领域文档。

## CNC 权威文档与读取顺序

动手改领域/UI/架构相关内容前，按下列顺序阅读（后者不可替代前者）：

1. [`AGENTS.md`](../../AGENTS.md) — 项目规范、架构红线、评审 checklist
2. [`CONTEXT.md`](../../CONTEXT.md) — **领域术语与历史约定 SSOT**
3. [`docs/adr/*.md`](../adr/) — **当前有效架构决策**
4. [`docs/UI设计文档.md`](../UI设计文档.md) — **视觉与交互目标态 SSOT**
5. **实际源码**（`src/`）— 当前实现真相

### SSOT 边界

| 文档 | 角色 |
|---|---|
| `CONTEXT.md` | 领域术语与历史约定 SSOT |
| `docs/adr/*.md` | 当前有效架构决策 |
| `docs/UI设计文档.md` | 视觉与交互**目标态** SSOT |

**UI 设计文档与当前实现不一致时**：必须读取相关 ADR 与实际源码，再决定改动范围；**不得**仅凭目标态文档直接重构现有 Shell。

## 探索前先读这些

- 仓库根目录的 **`CONTEXT.md`**
- `docs/adr/**` — 读领域相关的 ADR
- **有 UI 时必读** `docs/UI设计文档.md`（视觉与交互目标态 SSOT）
- `docs/design/DESIGN.md` — 仅为兼容工作流索引；读到后必须按其要求通读 `docs/UI设计文档.md` 与相关 ADR，**禁止**把索引文件当作色板/控件/页面规范正文

UI 模式三选一：`headless` | `spec-driven` | `mockup-driven`。判定顺序：PRD 末尾摘要「本计划 UI 模式」→ `docs/design/DESIGN.md` 文首 `> UI 模式：…`（与 `/to-prd`、`/to-issues`、`/triage` 一致）。

- **headless**：无可视化 UI — **跳过** `docs/design/` 门禁；不适用 Design Issue / UI issue 模板
- **spec-driven / mockup-driven**：若仓库包含 `docs/design/`，设计输入约定：
  - **视觉/交互目标态 SSOT**：`docs/UI设计文档.md`（必读）
  - **DESIGN.md**：兼容工作流索引指针，有 UI 时打开后跟读 SSOT 与相关 ADR；禁止在 DESIGN 中复制规范正文
  - **references/** — mockup-driven 时必读默认态 PNG（本仓当前以 `docs/prototype/index.html` 为验收参考，非 PNG references 门禁）
  - **platforms.md** — 多端时必读（本仓为单端 Windows 桌面，通常不适用）
  - 业务 Surface 布局规格 SSOT 在 **PRD**；issue 用 **PRD 绑定** + **States 矩阵** 路由 Agent
  - **spec-driven**：`UI设计文档.md`（经 DESIGN 索引）+ 相关 ADR + 源码现状 + 父 PRD Surface 清单该条 + issue PRD 绑定 → 可 `ready-for-agent`；**不检查** `references/`
  - **mockup-driven**：上列 + 默认态 references 就绪 → 可 `ready-for-agent`

如果 `CONTEXT.md` / `docs/adr/**` 不存在，**静默继续**。不要指出它们缺失，也不要建议提前创建它们。生产者 skill（`/grill-with-docs`）会在术语或决策实际确定时，再按需创建它们。

`docs/UI设计文档.md` 已存在且为 UI 目标态 SSOT；缺失时不得静默跳过 UI 相关任务的设计阅读。目标态与实现冲突时见上文「SSOT 边界」。

## 本仓库平台与依赖（真实值）

| 项 | 值 |
|---|---|
| 目标平台 | Windows 桌面 / 车间工控机 |
| 框架 | .NET 8（`net8.0-windows`）+ WPF |
| MVVM | CommunityToolkit.Mvvm **8.4.2** |
| 业务控件 | HandyControl **3.5.1**（已接入） |
| 外壳/导航库 | WPF-UI **4.3.0**（目前仅 PackageReference；FluentWindow / NavigationView 外壳未接入） |
| 根 Surface | **`app-shell`** |
| 测试 | 当前无正式单元测试工程；联调以模拟器 + `docs/演示实操手册.md` 为准 |
| UI Automation | 待确认 |
| 安装 / 发布 | 待确认 |

契约约束：遵循 `.cursor/skills/wpf-desktop-workflow/references/WPF-PROFILE.md`。**禁止**生成 Web 页面、路由、DOM/CSS/ARIA 或 Playwright 默认契约。

## 文件结构

```
/
├── AGENTS.md
├── CONTEXT.md
├── docs/
│   ├── UI设计文档.md              ← UI 视觉/交互目标态 SSOT
│   ├── prototype/index.html       ← 高保真原型（验收参考）
│   ├── adr/
│   │   └── *.md                   ← 当前有效架构决策
│   ├── agents/                    ← 本目录：skills 配置
│   ├── design/
│   │   └── DESIGN.md              ← 兼容索引 → UI设计文档 + ADR
│   └── repo-wiki/                 ← 人类阅读；见 wiki.md
└── src/                           ← 当前实现真相
```

## 使用词汇表词汇

当你的输出中提及某个领域概念时（无论是在 issue 标题、重构提案、假设还是测试名称中），请使用 `CONTEXT.md` 中定义的术语。不要偏离到术语表中明确避免使用的同义词上。

如果你需要的概念尚未出现在术语表中，这本身就是一个信号 —— 要么你正在创造项目不使用的语言（请重新考虑），要么确实存在一个缺口（请记录下来，供 `/grill-with-docs` 使用）。

## 标记 ADR 冲突

如果你的输出与现有 ADR 存在冲突，请明确指出来，而不是静默地覆盖：

> *与 ADR-0007（event-sourced orders）矛盾——但值得重开因为…*
