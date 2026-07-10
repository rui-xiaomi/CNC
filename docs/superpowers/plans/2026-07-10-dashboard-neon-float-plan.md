# 实现计划 — 监控看板深色悬浮卡片视觉升级

> Spec：`docs/superpowers/specs/2026-07-10-dashboard-neon-float-design.md`  
> 日期：2026-07-10

## 目标

仅升级监控看板视觉：轻悬浮卡、克制光晕、矢量状态图标、装饰波形、看板青/红提亮。布局与 ViewModel 逻辑不变。

## 阶段拆分

### Phase A — Token + DashboardStyles 骨架
1. `Tokens.xaml` 增加 `DashAccentColor`/`DashAlarmColor` 及对应 Brush
2. 新建 `Theme/DashboardStyles.xaml`：
   - `DashWaveBrush`（DrawingBrush，低透明度正弦/折线 Path）
   - `DashPanel`：BasedOn `Panel` + 轻 `DropShadowEffect`（BlurRadius≈8, Opacity≈0.35, ShadowDepth≈2）
   - `DashKpiCard`：同抬升 + 可选波形 Overlay 约定（或文档说明在模板内叠一层）
   - `DashGlowSelected` / `DashGlowAlarm`：用 Border 触发器或附加 Effect 资源（选中青、告警红；默认无 Effect）
3. `App.xaml` 在 `Styles.xaml` 之后、`PageTemplates.xaml` 之前合并 `DashboardStyles.xaml`

**验证**：`dotnet build`；其它页仍用 `Panel`，观感不变。

### Phase B — 状态图标 Converter
1. `ConfigConverters.cs` 增加 `StateBadgeToIconConverter`：`string` → `Geometry`（或 `StreamGeometry`）
2. Geometry 映射：`ok` 勾、`run` 三角、`warn` 三角感叹、`alarm` 感叹三角、`ng` 方块、`idle`/`offline` 空心圆
3. 在 `PageTemplates.xaml` 资源区注册 Converter
4. 可选：KPI 固定图标用静态 Geometry 资源（在线/OK/NG/告警），不经 StateBadge

**验证**：单元级可在 XAML 预览或运行后目视；build 通过。

### Phase C — Dashboard DataTemplate 换皮
仅改 `DataTemplate DataType=DashboardViewModel`：

1. **KPI×4**：`Panel` → `DashKpiCard`；标题旁加 Path 图标；数字 Foreground 可用 `DashAccent`/`Ok`/`DashAlarm`/`Warn`
2. **产线流外层**：`DashPanel`；站点卡选中触发器改用 `DashAccent` 描边 + 淡青 Effect；状态 `Ellipse` → `Path` + IconConverter
3. **机台卡**：外层 `DashPanel`；`IsHighlighted` → DashAccent 光晕；门/安全 Ellipse → Path；工位卡 `IsAlarm` → DashAlarm 边+光；「恢复」按钮前景/边框用 `DashAlarmBrush`
4. **右栏**：记录/告警容器 `DashPanel` + 波形；告警行级别前加警告 Path

波形落地建议：卡片内容用 `Grid`，底层 `Rectangle Fill=DashWaveBrush Opacity=0.1`，上层内容。

**验证**：启动 App → 监控看板目视对照验收清单；切 PLC/料架页确认未变。

### Phase D — 设计文档例外条款
1. 修订 `docs/UI设计文档.md`：全局仍禁霓虹；监控看板允许本规格克制光晕/轻阴影/装饰波形
2. 在 §6.1 监控看板补一句指向本 spec

**验证**：文档与实现一致。

## 验收清单（对照 spec §8）

- [ ] 仅看板视觉变化
- [ ] 默认几乎无光晕；选中淡青；告警淡红
- [ ] 状态圆点 → 矢量图标，标签色正确
- [ ] 波形不挡字
- [ ] `dotnet build` 0 错误（尽量 0 警告）

## 风险与注意

| 风险 | 缓解 |
| --- | --- |
| `DropShadowEffect` 软件渲染卡顿 | Blur 小、仅看板卡片；必要时改 Border 模拟抬升 |
| Effect 与 Clip/圆角冲突 | 外层无 Clip；阴影挂在外层 Border |
| Converter 返回 Geometry 绑定 Path.Data | 确认 `Path.Data` 绑定类型正确 |
| 改动文件数可能 >5 | 已在 spec 批准；实现时严格只动清单内文件 |

## 文件清单（最终）

1. `src/CncLoader.UI/Theme/Tokens.xaml`
2. `src/CncLoader.UI/Theme/DashboardStyles.xaml`（新）
3. `src/CncLoader.App/App.xaml`
4. `src/CncLoader.UI/Converters/ConfigConverters.cs`
5. `src/CncLoader.UI/Theme/PageTemplates.xaml`
6. `docs/UI设计文档.md`
