# WPF 平台契约

## 目录

1. 平台边界
2. 文档分工
3. Surface 模型
4. 状态模型
5. UI 与线程规则
6. 测试契约
7. 诊断契约
8. 发布门禁

## 1. 平台边界

保留通用流程：领域澄清、CONTEXT/ADR、PRD 绑定、垂直切片、Triage、TDD、诊断和架构维护。

替换 Web 契约：页面/路由/app-shell、DOM/CSS/ARIA、浏览器 fixture、Playwright、PNG 单一验收、Web 部署与回滚。

WPF 契约必须覆盖：Window/Page/UserControl/Dialog/Popup/托盘/向导/浮动工具窗、Owner、模态性、Dispatcher/STA、Binding、DPI、主题、多显示器、UI Automation、安装升级和回滚。

## 2. 文档分工

| 内容 | 唯一来源 |
|---|---|
| 领域术语、历史约定 | `CONTEXT.md` |
| 难以逆转的技术选择 | `docs/adr/*.md` |
| 全局视觉、交互、窗口和可访问性规则 | `docs/design/DESIGN.md` |
| 业务 Surface 布局、字段、状态适用性 | PRD `Surface 清单` |
| 可观察行为、测试与完成证据 | Issue |

不得把业务 Surface 布局写进 DESIGN，也不得把色板和字号全文复制到 PRD/Issue。

## 3. Surface 模型

使用稳定的 `surface-id`：

| 前缀 | 类型 | 示例 |
|---|---|---|
| `app-shell` | 保留的根外壳标识 | `app-shell` |
| `shell-` | 主窗口与全局外壳 | `shell-main` |
| `view-` | 主窗口内容视图 | `view-orders` |
| `dialog-` | 模态或非模态对话框 | `dialog-order-edit` |
| `panel-` | 停靠/抽屉/浮动面板 | `panel-inspector` |
| `tray-` | 托盘菜单和通知 | `tray-main` |
| `wizard-` | 多步骤向导 | `wizard-import` |
| `tool-` | 独立工具窗口 | `tool-log-viewer` |

每个 Surface 至少定义：Owner、模态性、打开/关闭条件、焦点入口、键盘路径、最小尺寸、DPI/主题要求、状态适用性、自动化名称和测试接缝。

根外壳优先使用保留标识 `app-shell`，以兼容既有依赖门禁。兼容旧版 `page-id` 时，保留字段但令其值等于 `surface-id`；新文档以 `surface-id` 为主。

## 4. 状态模型

候选状态：`default`、`loading`、`empty`、`error`、`disabled`、`offline`、`busy`、`dirty`、`readonly`、`validating`。

不是所有 Surface 都必须实现十态。PRD 应说明触发条件、可观察呈现、允许与禁止的操作、恢复路径、是否持久化，以及不适用理由。

`busy` 不等于 `loading`：前者表示操作进行中，后者表示数据尚未可用。`readonly` 不等于 `disabled`：前者允许查看与复制，后者通常不可交互。

## 5. UI 与线程规则

- 不在 UI 线程执行阻塞 IO；禁止 `.Result`、`.Wait()`、`Thread.Sleep()`。
- 后台线程更新 UI 必须经 Dispatcher 或框架提供的调度抽象。
- 长操作定义进度、取消、失败和窗口关闭策略。
- Binding 错误、Validation 错误和未观察到的 Task 异常必须进入可检查日志。
- 每个可交互元素设置稳定的 `AutomationProperties.Name` 或项目统一 AutomationId。
- 对话框设置 Owner；定义模态/非模态行为与关闭返回值。
- 验收 100%、125%、150%、200% DPI，以及浅色/深色主题和多显示器回落。
- 避免用固定像素绝对定位；数据量大的列表/表格验证虚拟化。

UI 库、资源键、控件名必须来自项目真实依赖。模板中的 WPF-UI/HandyControl 映射只在项目采用相同库且版本验证通过时使用。

## 6. 测试契约

| 层级 | 目标 | 运行环境 |
|---|---|---|
| 领域/ViewModel | 行为、状态转换、命令可用性、校验 | 普通测试进程 |
| STA WPF 集成 | XAML 加载、Binding、资源、Dispatcher、控件属性 | Windows + STA |
| UI Automation | 启动应用并走关键用户路径 | 交互式 Windows Session |
| 人工 QA | DPI、主题、多显示器、输入法、视觉、安装升级 | 目标 Windows 环境 |

- 优先断言可观察行为，不测试私有实现。
- STA 测试必须显式声明 ApartmentState.STA 或使用项目测试框架的 STA 支持。
- UI Automation 使用条件等待，禁止用固定 `Sleep` 代替状态同步。
- 失败时保存日志、截图和自动化树；不得通过降低断言让测试变绿。
- 无交互式会话时跳过应可见地报告，不能静默当作通过。

## 7. 诊断契约

按下列顺序选择最小反馈回路：

1. ViewModel/领域测试；
2. STA WPF 集成测试；
3. 最小 Window/UserControl harness；
4. FlaUI、WinAppDriver 或项目既有 UI Automation；
5. WPF Binding Trace、Validation、Dispatcher 异常；
6. 应用日志、dump、ETW/PerfView；
7. PowerShell HITL 循环。

性能问题同时观察 UI 线程阻塞、布局/渲染、虚拟化、资源查找、GC 与 IO。

## 8. 发布门禁

分别记录编译、自动测试、UI Automation、人工 QA、签名、安装、升级、卸载、配置迁移和回滚。任一项未执行就标记“未验证”。

不得用“Actions 已启用”“能编译”“静态检查通过”替代真实 Windows 运行、安装升级或业务回归。
