# 产线流末端出口：实心色块对比度

日期：2026-07-10  
范围：监控看板产线流末端「NG出站 / 下料出站」仅视觉，不改调度逻辑。

## 问题

末端已是 soft 底 + 2px 描边 + 图标，暗底上对比不足，远看仍像小徽章。

## 决策

采用**实心色块 + 深色字**（方案 1）。机台卡 soft 体系不动。

## 视觉规格

| 元素 | NG出站 | 下料出站 |
|------|--------|----------|
| 背景 | `NgBrush`（`#F97316`） | `OkBrush`（`#22C55E`） |
| 文字 / 图标 | `AccentFgBrush`（深色，保证对比） | 同左 |
| 描边 | 无（或 1px 同色加深，避免糊边） | 同左 |
| 字号 / 字重 | 保持 13 / Bold | 同左 |
| Padding / MinWidth | 保持现有 `14,10` / `108` | 同左 |
| 分叉箭头 `↗↘` | 仍用 `NgBrush` / `OkBrush`；未激活 Opacity 0.45，激活 1 | — |

激活态（`NgArrowActive` / `UnloadArrowActive`）：对应出口卡 Opacity `1 ↔ 0.72`、周期约 1.2s（0.6s×2 AutoReverse）循环呼吸闪；退出激活即 StopStoryboard 恢复 Opacity=1。无 DropShadow。

## 非目标

- 不改机台站点卡、工位 soft tint
- 不加 DropShadow / 霓虹
- 不改成与机台卡同级的双出口大卡结构
- 不改 `ForkSecondaryTitle` / `AggregateDisplay` 文案

## 改动面

- `PageTemplates.xaml`：末端两个 `Border` 的 `Background` / `Foreground` / `BorderThickness`
- `docs/UI设计文档.md` §6.1：一句说明末端为实心出口块

## 验收

- 暗底上 NG 为实心橙块、下料为实心绿块，深色字清晰可读
- 机台卡外观无回归
- 构建 0 警告 0 错误；看板产线流末端肉眼确认
