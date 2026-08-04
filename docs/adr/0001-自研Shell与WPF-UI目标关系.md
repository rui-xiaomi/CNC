---
status: accepted
---

# 自研 Shell 为过渡基线，WPF-UI 外壳目标并存但不等于已批准迁移

UI 设计文档将 WPF-UI（FluentWindow + NavigationView + 主题）描述为外壳目标，且工程已引用 WPF-UI 4.3.0；但运行时主窗口仍是自研 `ShellWindow`（原生 Window + 自研侧栏 ListBox + ContentControl），主题与业务控件基线为 HandyControl 3.5.1 + 自研 `Theme/` 资源。因此决定：**文档目标态与当前实现过渡态并存**——以自研 Shell / 导航 / 主题为现行事实；WPF-UI 仅作依赖占位，未承担外壳、导航或主题；本 ADR **不批准也不否决** 迁移到 FluentWindow/NavigationView。

## 被否决的理解（易误判）

**把 PackageReference 当成已接入。** 全仓未使用 FluentWindow / NavigationView / WPF-UI 主题字典；有包不等于已切换外壳。

**把 UI 设计文档的目标段落当成开工令。** 设计文档可继续描述目标观感；改主窗口/导航实现须另有迁移 ADR（或显式修订本 ADR）后才能动工。本会话亦不修改 `docs/UI设计文档.md`。

**把模板「双库分工 ADR」当成现状。** 模板中的「WPF-UI 主 + HandyControl 补 + 桥接字典」是目标方向素材，**尚未**在产品代码落地；过渡期不得按「已桥接」编写或「优化」资源合并顺序。

**借接库之机改成 Frame/Page 导航。** 现行为 ViewModel-first（`INavigationService` → 内容区绑定页面 VM）。该模型是过渡期约束，也是未来若迁移 NavigationView 的前置条件；具体适配方案属迁移 ADR，不在此批准。

## 后果

- Agent / 评审以自研 Shell 与 HandyControl + 自研 Theme 为现行 UI 基线；不得以「设计文档写了 WPF-UI」为由直接改 `ShellWindow` 或拆除自研导航。
- 根 Surface 标识仍为 `app-shell`；视觉 SSOT 仍在 `docs/UI设计文档.md`（经 `docs/design/DESIGN.md` 索引），本 ADR 不复制色板或控件规范。
- 若批准迁移：须新开 ADR，写清窗口基类、NavigationView 与 ViewModel-first 的适配、资源字典顺序、主题切换入口，以及 HandyControl 残留控件的桥接策略；在那之前保持现状。
