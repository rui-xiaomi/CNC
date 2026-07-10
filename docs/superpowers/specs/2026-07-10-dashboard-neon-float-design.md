# 监控看板 · 深色悬浮卡片视觉升级

> 日期：2026-07-10  
> 范围：**仅监控看板**（`DashboardViewModel` DataTemplate）  
> 实现路径：看板专用 Style 包，不改全局工业风基线

## 1. 目标

在保持原有深色主题与一屏信息架构的前提下，将监控看板从扁平色块提升为更有层次的悬浮卡片，并强化状态可读性：

- 轻悬浮卡片 + 克制霓虹光晕
- 矢量状态图标 + 彩色状态标签
- 卡片内装饰性波形底纹
- 看板内青/红强调色略提亮

## 2. 约束（已确认）

| 项 | 决策 |
| --- | --- |
| 范围 | 仅监控看板；其它页面观感不变 |
| 光晕强度 | 克制：默认几乎无光；选中/告警才亮 |
| 波形 | 纯装饰，低透明度，不绑定真实数据 |
| 状态图标 | 增强现有：圆点 → 矢量图标，保留彩色标签（色+形+文） |
| 落地方式 | 看板专用 Style 包（`DashPanel` / `DashKpiCard`），不改全局 `Panel` |

**明确不做**：玻璃拟态、全局霓虹、紫色渐变、emoji、强呼吸动画、历史趋势 API、改 ViewModel 业务逻辑、改其它页面。

## 3. 视觉语言

### 3.1 卡片解剖

适用于 KPI、产线流外层、机台卡、右栏记录/告警容器：

| 层 | 规格 |
| --- | --- |
| 底 | `Surface`，圆角 6–8 |
| 抬升 | `DropShadowEffect` 或等效：`0 4px 12px rgba(0,0,0,.35)`，Blur 小、Opacity 低 |
| 边框·默认 | 现有 `Border` |
| 边框·选中/高亮 | accent 描边 + 约 2px 淡青外发光 |
| 边框·告警 | alarm 描边 + 淡红外发光 |
| 波形底纹 | 右下角 Path / DrawingBrush，透明度约 8–12%，不挡文字 |
| 签名元素 | 机台卡顶栏保留校准刻度 hairline（`CalibrationTicks`） |

### 3.2 看板专用配色

全局 token（`AccentColor` / `AlarmColor`）不变。看板资源另增：

| Token | 建议值 | 用途 |
| --- | --- | --- |
| `DashAccent` | `#22D3D8` | 选中描边、看板主交互强调 |
| `DashAlarm` | `#F87171` | 告警光晕、「恢复」等危险按钮 |

## 4. 状态图标

`StateBadge` 字符串 → 14×14 矢量 Geometry（非 emoji）：

| Badge | 图标语义 | 标签色 |
| --- | --- | --- |
| `ok` | 勾选 ✓ | Ok / 绿 |
| `run` | 三角 ▶ | Run / 蓝 |
| `warn` | 三角感叹 ⚠ | Warn / 琥珀 |
| `alarm` | 警告感叹 | Alarm / 红 |
| `ng` | 实心方块 ■ | Ng / 橙 |
| `idle` | 空心圆 | Idle / 灰 |
| `offline` | 空心圆（更淡） | FgMuted |

实现：`StateBadgeToIconConverter`（或 Geometry 资源 + Converter），替换看板内状态 `Ellipse`。

### 应用位置

- 产线流站点：图标 + `AggregateDisplay`
- 机台卡门/安全：圆点 → 图标
- 工位卡：状态文字旁加图标；告警时边框红光
- 右栏告警行：级别前加警告图标
- KPI：标题旁对应语义图标（在线/OK/NG/告警）

彩色状态标签（soft tint 双槽 seg、工位 soft 背景）保留并略加强对比。

## 5. 各区块改动清单

| 区块 | 改动 |
| --- | --- |
| KPI×4 | `DashKpiCard`：轻阴影 + 装饰波形；标题旁图标；数字用现有/看板提亮色 |
| 产线流 | 外层 `DashPanel`；站点选中时 accent 光晕；状态点 → 图标 |
| 左机台卡 | `DashPanel` + 高亮光晕；门/安全图标；工位告警红光；「恢复」用 `DashAlarm` |
| 右栏记录/告警 | `DashPanel` + 波形；告警行图标 + 彩色级别标签 |

**布局骨架不变**：KPI → 产线流 → 左机台 / 右记录+告警；间距与信息密度保持。

## 6. WPF 落地

| 文件 | 动作 |
| --- | --- |
| `src/CncLoader.UI/Theme/DashboardStyles.xaml` | **新建**：`DashPanel`、`DashKpiCard`、阴影、选中/告警外发光、波形 DrawingBrush |
| `src/CncLoader.UI/Theme/Tokens.xaml` | 增加 `DashAccent` / `DashAlarm` Color + Brush |
| `src/CncLoader.UI/Theme/PageTemplates.xaml` | 仅 Dashboard `DataTemplate` 改用上述 Style；Ellipse → Path |
| `src/CncLoader.UI/Converters/ConfigConverters.cs` | `StateBadgeToIconConverter` |
| `src/CncLoader.App/App.xaml`（或 Styles 合并点） | 合并 `DashboardStyles.xaml` |
| `docs/UI设计文档.md` | 修订例外：全局禁止霓虹；**监控看板允许克制光晕与装饰波形** |

## 7. 与现有设计文档的关系

`docs/UI设计文档.md` 当前写明禁止霓虹光晕与装饰性悬浮阴影。本规格将其收窄为：

- **全局 / 其它页面**：仍禁止
- **监控看板**：允许本规格定义的克制光晕、轻阴影、装饰波形

签名元素（机台卡校准刻度）与色+形+文状态双/三通道要求继续有效。

## 8. 验收标准

1. 仅监控看板视觉变化；PLC / 料架等页观感不变  
2. 默认卡片几乎无光晕；选中机台或产线节点有淡青光；告警工位有淡红光  
3. 看板内状态圆点全部换成矢量图标，标签色正确  
4. 波形底纹不遮挡数字/文字，对比度仍可读  
5. `dotnet build` 通过；一屏布局与现有信息架构不变  

## 9. 非目标

- 真实数据驱动的波形/趋势图  
- 全局主题或其它页面的悬浮霓虹化  
- ViewModel / 调度 / PLC 逻辑变更  
- 浅色主题下的霓虹变体（若浅色看板需适配，另开任务）
