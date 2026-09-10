# DESIGN.md — 视觉与交互规范（WPF 桌面端）

> **性质**：本文件是**品牌视觉与通用交互的唯一权威来源（SSOT）**。
> **边界**：这里只写「全局通用」的规则。**具体功能页面的布局规格不写在这里**——那些写在 PRD 的「页面清单 › UI 设计描述」里。
> **修改规则**：本文件只能通过 Design Issue（`#D-global`）修改。`/tdd` 实现过程中**禁止**直接改本文件。

---

## §0 UI 模式与技术基线

| 项 | 值 |
|---|---|
| **UI 模式** | `spec-driven`（布局 SSOT 在 PRD 页面清单，不提供 PNG 设计稿） |
| **平台** | Windows 10 1809+ / Windows 11 |
| **框架** | .NET 8 + WPF |
| **架构** | MVVM（CommunityToolkit.Mvvm） |
| **UI 库** | 主库 **WPF-UI**（外壳/主题/基础控件） + 补充库 **HandyControl**（业务控件）。分工见 ADR-0001 |
| **WPF-UI 包版本** | 待从目标项目 `.csproj` / `Directory.Packages.props` 填写并验证 |
| **HandyControl 包版本** | 待从目标项目 `.csproj` / `Directory.Packages.props` 填写并验证 |
| **数据访问** | EF Core + MySQL（Pomelo） |
| **DPI 感知** | `PerMonitorV2`（须在 app.manifest 声明） |

> 本模板是 WPF-UI + HandyControl 参考 profile。若目标项目未使用这两个库，不复制 §2.2、§2.3、§5 和桥接 XAML；应按真实依赖重写。

**创意北极星**（一句话，写完删掉本行说明）：
> 例：「工业现场可用——戴手套能点中，一米外能看清，出错时永远说人话。」

---

## §1 布局与窗口规则

> Web 版没有这一节，桌面端必须有。

### 1.1 窗口尺寸

| 项 | 值 | 说明 |
|---|---|---|
| 默认尺寸 | 1280 × 800 | 首次启动。1366×768 笔记本上仍能完整显示 |
| 最小尺寸 | 1024 × 640 | 低于此值内容会被裁切 |
| 可最大化 | 是 | |
| 记忆窗口位置 | 是 | 须处理"上次的显示器已拔掉"的情况，见 §1.4 |

> **默认值取舍**：1280×800 是业务客户端的安全基准——比 1366×768 的常见笔记本分辨率留了余量，又不至于在 1920×1080 上显得局促。若产品明确面向工业一体机（常见 1920×1080 固定全屏），把默认尺寸改成全屏并把最小尺寸提到 1280×720。

### 1.2 响应式断点（按窗口宽度）

| 断点 | 阈值 | 布局变化 |
|---|---|---|
| 紧凑 | < 1100 px | 侧栏折叠为图标（仅图标 + ToolTip） |
| 标准 | 1100 – 1600 px | 默认布局，侧栏展开 |
| 宽松 | > 1600 px | 内容区分栏（列表 + 详情并排） |

### 1.3 DPI 缩放

**必须在 100% / 125% / 150% / 200% 下验收**，四档都要过。

- 禁止硬编码像素做绝对定位——用 `Grid` / `DockPanel` 布局
- 图标须提供矢量（Path / DrawingImage）或多倍图
- 字号不写死，引用 §3 的字体阶梯

### 1.4 多显示器

- 对话框在**父窗口所在的显示器**居中，不是主显示器
- 记忆的窗口位置若落在已不存在的显示器上 → 回落到主显示器居中

---

## §2 颜色与主题

**双库分工**：WPF-UI 是主题权威，HandyControl 的色板**桥接到** WPF-UI，不独立取值。

> ⚠️ 下表是候选映射。填入并锁定两个包版本后，必须通过真实 WPF 工程编译和浅/深主题运行验证；验证前不得标记 DESIGN 完成。

### 2.1 主题模式

| 模式 | 支持 | 切换方式 |
|---|---|---|
| 浅色 | 是 | `ApplicationThemeManager.Apply(ApplicationTheme.Light)` |
| 深色 | 是 | `ApplicationThemeManager.Apply(ApplicationTheme.Dark)` |
| 跟随系统 | 是 | `SystemThemeWatcher.Watch(window)` |

**规则**：所有颜色引用**必须用 `DynamicResource`**，不得用 `StaticResource`——否则运行时切换主题不生效。

### 2.2 语义色键（权威来源：WPF-UI）

| 语义 | WPF-UI 键 | HandyControl 桥接键 | 用途 |
|---|---|---|---|
| 主色 | `AccentFillColorDefaultBrush` | `PrimaryBrush` | 主按钮、选中态 |
| 成功 | `SystemFillColorSuccessBrush` | `SuccessBrush` | 完成、正常 |
| 警告 | `SystemFillColorCautionBrush` | `WarningBrush` | 需注意但不阻断 |
| 危险 | `SystemFillColorCriticalBrush` | `DangerBrush` | 错误、破坏性操作 |
| 信息 | `SystemFillColorAttentionBrush` | `InfoBrush` | 提示 |
| 正文文字 | `TextFillColorPrimaryBrush` | `PrimaryTextBrush` | 默认文字 |
| 辅助文字 | `TextFillColorSecondaryBrush` | `SecondaryTextBrush` | 说明、单位 |
| 弱化文字 | `TextFillColorTertiaryBrush` | `ThirdlyTextBrush` | 占位、时间戳 |
| 禁用 | `TextFillColorDisabledBrush` | — | 不可用控件文字 |
| 控件背景 | `ControlFillColorDefaultBrush` | `RegionBrush` | 输入框、按钮底 |
| 卡片背景 | `CardBackgroundFillColorDefaultBrush` | `SecondaryRegionBrush` | 卡片、面板 |
| 窗口背景 | `ApplicationBackgroundBrush` | — | 主窗口 |
| 边框 | `ControlStrokeColorDefaultBrush` | `BorderBrush` | 分隔线、描边 |

### 2.3 双库主题桥接（必做）

两个库各有一套色板，深色模式下**不会自动一致**。解决办法是在 App 级资源字典里把 HandyControl 的语义键**重定向**到 WPF-UI 的值：

```xml
<!-- App.xaml — 合并顺序很重要 -->
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <!-- 1. HandyControl 先进（它的隐式样式会被后面覆盖） -->
      <ResourceDictionary Source="pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml"/>
      <ResourceDictionary Source="pack://application:,,,/HandyControl;component/Themes/Theme.xaml"/>

      <!-- 2. WPF-UI 后进，赢得原生控件的隐式样式 -->
      <ui:ThemesDictionary Theme="Light"/>
      <ui:ControlsDictionary/>

      <!-- 3. 桥接层：把 HandyControl 的语义键指向 WPF-UI 的画刷 -->
      <ResourceDictionary Source="/Themes/HandyControlBridge.xaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

`HandyControlBridge.xaml` 内容形如：

```xml
<ResourceDictionary xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
  <SolidColorBrush x:Key="PrimaryBrush"       Color="{DynamicResource SystemAccentColorPrimary}"/>
  <SolidColorBrush x:Key="DangerBrush"        Color="{DynamicResource SystemFillColorCritical}"/>
  <SolidColorBrush x:Key="SuccessBrush"       Color="{DynamicResource SystemFillColorSuccess}"/>
  <SolidColorBrush x:Key="WarningBrush"       Color="{DynamicResource SystemFillColorCaution}"/>
  <SolidColorBrush x:Key="PrimaryTextBrush"   Color="{DynamicResource TextFillColorPrimary}"/>
  <SolidColorBrush x:Key="SecondaryTextBrush" Color="{DynamicResource TextFillColorSecondary}"/>
  <!-- 其余按 §2.2 表格逐条对应 -->
</ResourceDictionary>
```

**切换主题时必须两边一起切**，封装成一个方法，禁止在业务代码里单独调其中一个：

```csharp
public static void ApplyTheme(ApplicationTheme theme)
{
    ApplicationThemeManager.Apply(theme);
    HandyControl.Themes.ThemeManager.Current.ApplicationTheme =
        theme == ApplicationTheme.Dark
            ? HandyControl.Themes.ApplicationTheme.Dark
            : HandyControl.Themes.ApplicationTheme.Light;
    // 桥接字典须在此之后重新合并一次，否则 HandyControl 会用回自己的深色值
}
```

### 2.4 禁忌

- 禁止在 XAML 里写字面量颜色（`#FF0000`、`Red`）
- 禁止用颜色作为唯一的信息载体（色盲不可读）——须同时有图标或文字
- **禁止直接引用 HandyControl 的原始色板键**（如 `DarkPrimaryBrush`）——只能用 §2.2 表格中列出的、已桥接的键
- 禁止在业务代码里单独切换任一库的主题

---

## §3 字体阶梯

| 级别 | 字号 | 字重 | 用途 |
|---|---|---|---|
| 标题 1 | 20 | SemiBold | 窗口/视图标题 |
| 标题 2 | 16 | SemiBold | 分组标题 |
| 正文 | 14 | Regular | 默认 |
| 辅助 | 12 | Regular | 说明文字、单位 |
| 数据 | 14 | Regular（等宽族） | 编号、数值、日志 |

> 单位为 WPF 的设备无关像素（`FontSize`），系统缩放由 DPI 感知自动处理，**不要**再乘缩放系数。
> 字体族：中文 `Microsoft YaHei UI`，西文回落 `Segoe UI`，等宽用 `Cascadia Mono` 回落 `Consolas`。

**规则**：
- 字体族只用系统字体（`Microsoft YaHei UI` / `Segoe UI`），不内嵌字体
- 数值、编号、时间戳一律用**等宽**，防止跳动
- 全局阶梯只此五级，页面不得自定义字号

---

## §4 层级与视觉深度

WPF 没有 CSS 的 `z-index` 和 `box-shadow` 体系，改用**窗口层级 + 阴影资源**。

### 4.1 窗口层级

| 层级 | 类型 | 模态 | 规则 |
|---|---|---|---|
| L0 | 主窗口 | — | 有且仅有一个 |
| L1 | 停靠面板 | 否 | 随主窗口移动，可关闭 |
| L2 | 非模态工具窗 | 否 | 独立窗口，`Owner` 指向主窗口 |
| L3 | 模态对话框 | 是 | 必须有确定/取消语义 |
| L4 | 弹出层 | — | Popup / ToolTip / Flyout，不抢焦点 |
| L5 | 全局通知 | 否 | 托盘气泡 / 顶部横幅，自动消失 |

**规则**：
- 模态对话框**不得再弹模态对话框**（超过一层就是设计缺陷）
- 所有子窗口必须设 `Owner`，否则会跑到主窗口后面
- L4 弹出层**不得**包含需要键盘输入的表单

### 4.2 阴影 / 描边

**本项目不使用投影做层级区分**——Fluent 的做法是靠背景层次 + 描边，性能也更好。

| 用途 | 实现方式 |
|---|---|
| 卡片 | 背景 `CardBackgroundFillColorDefaultBrush` + 描边 `ControlStrokeColorDefaultBrush`，圆角 4 |
| 弹出层 | `ui:` 控件自带的 Flyout 样式，不额外加效果 |
| 对话框 | 由 `ui:ContentDialog` 的遮罩层区分，窗口投影由系统绘制 |

**性能警告**：`DropShadowEffect` 在大量元素上会显著掉帧。列表项、表格行**禁用**阴影，只用背景色区分。

---

## §5 通用控件原语

**仲裁规则（唯一一条，记住它）**：
> **同一需求两个库都有 → 一律用 WPF-UI。HandyControl 只用于 WPF-UI 没有的控件。**

这条规则防止的是最坏情况：同一种交互在 A 页面用了 `ui:`、B 页面用了 `hc:`，视觉不一致且自动化测试要写两套选择器。

命名空间：
```xml
xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
xmlns:hc="https://handyorg.github.io/handycontrol"
```

### 5.1 外壳与导航（全部 WPF-UI）

| 需求 | 用什么 | 禁止 |
|---|---|---|
| 主窗口 | `ui:FluentWindow` | 禁止用原生 `Window` |
| 标题栏 | `ui:TitleBar` | |
| 主导航 | `ui:NavigationView` | |
| 图标 | `ui:SymbolIcon` | 禁止用图片做图标（DPI 会糊） |
| 卡片容器 | `ui:Card` / `ui:CardExpander` | |

### 5.2 输入类

| 需求 | 用什么 | 来源 | 禁止 |
|---|---|---|---|
| 单行输入 | `ui:TextBox` | WPF-UI | |
| 多行输入 | `ui:TextBox AcceptsReturn="True"` | WPF-UI | |
| 密码 | `ui:PasswordBox` | WPF-UI | |
| 搜索 | `ui:AutoSuggestBox` | WPF-UI | |
| 数值输入 | `ui:NumberBox` | WPF-UI | **禁止用普通 TextBox 收数值** |
| 数值 + 单位/步进 | `hc:NumericUpDown` | HandyControl | 仅当 `ui:NumberBox` 不够用时 |
| 下拉选择 | 原生 `ComboBox` | WPF-UI 隐式样式 | |
| 多选下拉 | `hc:CheckComboBox` | HandyControl | WPF-UI 无此控件 |
| 日期 | `hc:DatePicker` | HandyControl | WPF-UI 无此控件 |
| 日期时间 | `hc:DateTimePicker` | HandyControl | 禁止用两个控件拼 |
| 时间段 | `hc:DateTimeRange` | HandyControl | |
| 开关 | `ui:ToggleSwitch` | WPF-UI | |
| 表单字段装饰 | `hc:InfoElement.Title` / `.Necessary` / `.Placeholder` 附加属性 | HandyControl | |

### 5.3 数据展示

| 需求 | 用什么 | 来源 | 关键约束 |
|---|---|---|---|
| 数据表格 | 原生 `DataGrid` | WPF-UI 隐式样式 | **必须** `EnableRowVirtualization="True"` + `VirtualizingPanel.IsVirtualizing="True"`；禁止行内阴影 |
| 列表 | 原生 `ListView` | WPF-UI 隐式样式 | 同上，必须开虚拟化 |
| 树形 | 原生 `TreeView` | WPF-UI 隐式样式 | 大树须用虚拟化 `VirtualizingStackPanel` |
| 分页 | `hc:Pagination` | HandyControl | WPF-UI 无此控件；与后端分页参数绑定，禁止前端切片 |
| 步骤条 | `hc:StepBar` | HandyControl | |
| 徽标/标签 | `hc:Badge` / `hc:Tag` | HandyControl | |
| 属性编辑 | `hc:PropertyGrid` | HandyControl | 仅用于调试/配置页 |
| 分隔 | `hc:Divider` | HandyControl | |

### 5.4 反馈类（这一组最容易乱，逐条对齐）

| 需求 | 用什么 | 来源 | 禁止 |
|---|---|---|---|
| 加载指示（局部） | `ui:ProgressRing` | WPF-UI | 见 §7 |
| 进度条（确定量） | `ui:ProgressBar` | WPF-UI | 见 §6.2 |
| 空状态 | `hc:Empty` | HandyControl | WPF-UI 无；见 §7 |
| 行内校验错误 | `INotifyDataErrorInfo` + `hc:InfoElement` | HandyControl | **禁止弹窗报校验错误** |
| 轻提示（不阻断） | `hc:Growl` | HandyControl | WPF-UI 的 `ui:Snackbar` 一次只显一条，业务场景不够用 |
| 确认框 | `ui:MessageBox` | WPF-UI | **禁止 `System.Windows.MessageBox`；禁止 `hc:MessageBox`**（避免双风格） |
| 复杂对话框 | `ui:ContentDialog` | WPF-UI | 禁止套第二层模态 |
| 抽屉面板 | `hc:Drawer` | HandyControl | WPF-UI 无 |
| 顶部横幅（离线等） | `ui:InfoBar` | WPF-UI | 见 §7 `offline` |

### 5.5 已知冲突点

| 控件 | 冲突 | 处理 |
|---|---|---|
| `MessageBox` | 三方都有（系统 / WPF-UI / HandyControl） | 只用 `ui:MessageBox`，封一个静态帮助类，代码审查禁掉另外两个 |
| `TextBox` / `ComboBox` / `Button` | 两库都注册隐式样式 | 按 §2.3 的合并顺序，WPF-UI 胜出；不要显式写 `hc:TextBox` |
| 弹出提示 | `ui:Snackbar` vs `hc:Growl` | 统一用 `hc:Growl`（支持队列与分类） |
| 图标字体 | 两库各带一套 | 只用 `ui:SymbolIcon`，不引 HandyControl 的图标资源 |

---

## §6 长任务与 UI 响应性

> Web 版没有这一节。这是桌面端最高频的翻车点，必须写成规范而不是留给实现时决定。

### 6.1 硬规则

| 规则 | 说明 |
|---|---|
| **UI 线程禁止阻塞** | 禁止 `.Result` / `.Wait()` / `Thread.Sleep` |
| **超过 200ms 必须异步** | 数据库查询、文件 IO、网络请求一律 `async` |
| **超过 1s 必须有进度反馈** | 见下表 |
| **超过 5s 必须可取消** | 传 `CancellationToken` |

### 6.2 进度反馈的选择

| 耗时 | 呈现方式 | 是否锁 UI |
|---|---|---|
| < 200ms | 无 | 否 |
| 200ms – 1s | 光标变忙 | 否 |
| 1s – 5s | 局部加载指示（只盖住相关区域） | 局部 |
| > 5s | 进度条 + 取消按钮 | 局部，其余可操作 |
| 阻断性操作（如数据迁移） | 模态进度对话框 | 全局 |

**默认取局部锁，不取全局锁。** 全局遮罩要在 PRD 里逐个说明理由。

### 6.3 数据库相关

- 列表查询**必须分页**，禁止一次拉全表
- 大结果集绑定前必须确认表格已开虚拟化
- 长事务禁止在 UI 线程发起
- 连接失败按 §7 的 `offline` 态处理，不弹异常堆栈

---

## §7 全局状态呈现

> 各状态的**业务判定条件**写在 PRD「状态策略」；这里只定**长什么样**。

| 状态 | 视觉呈现 | 位置 |
|---|---|---|
| `loading` | `ui:ProgressRing` 居中 + 半透明遮罩（不透明度 0.6，用 `ApplicationBackgroundBrush`） | 覆盖数据区域，不覆盖导航 |
| `empty` | 插图 + 一句说明 + 一个主操作 | 数据区域居中 |
| `error` | 图标 + 人话说明 + 重试按钮 | 数据区域居中 |
| `disabled` | 控件置灰 + ToolTip 说明原因 | 控件本身 |
| `offline` | 状态栏常驻标识 + 顶部横幅 | 全局 |
| `busy` | 见 §6.2 | 按耗时决定 |
| `dirty` | 标题栏加 `*` + 保存按钮高亮 | 窗口标题 + 工具栏 |
| `readonly` | 输入控件置灰 + 顶部说明条 | 全局 |
| `validating` | 行内提示，不弹窗 | 字段下方 |

**错误文案规则**：
- 说人话，不暴露异常类型和堆栈
- 必须告诉用户**下一步能做什么**（重试 / 联系管理员 / 检查网络）
- 技术细节放"详情"折叠区，供排障用

---

## §8 宜忌

### 宜

1. 所有异步操作用 `IProgress<T>` 回报进度，不直接操作 UI 元素
2. 快捷键遵循 Windows 惯例（Ctrl+S 保存 / Esc 取消 / F5 刷新 / Ctrl+F 查找）
3. 破坏性操作二次确认，确认框里写清**具体删什么**（"删除工单 WO-20260804-001"而非"确定删除吗"）
4. 每个可点击控件有 ToolTip 或可访问性名称（UI 自动化测试依赖它）
5. 表格列宽、排序、筛选条件持久化到用户配置

### 忌

1. **忌用系统 `MessageBox`** —— 视觉不统一，且无法自动化测试
2. **忌在 View 的 code-behind 写业务逻辑** —— 测试接缝在 ViewModel，写进 View 就测不到
3. **忌用颜色作为唯一信息载体**
4. **忌模态套模态**
5. **忌无反馈的长任务** —— 用户会以为程序卡死然后强杀
6. **忌把异常直接抛给用户看**
7. **忌在 XAML 里写死尺寸和颜色**

---

## 附：与 PRD 的分工

| 内容 | 写在哪 |
|---|---|
| 色彩 / 字体 / 层级 / 控件映射 / 宜忌 | **本文件** |
| 窗口规则 / 长任务规则 / 状态视觉 | **本文件** |
| 各状态的业务判定条件 | PRD 「状态策略」 |
| 具体页面的布局与控件排布 | PRD 「页面清单 › UI 设计描述」 |
| 某个页面某个状态的可观察预期 | Issue 「States 矩阵」 |

**引用规则**：PRD 和 Issue 中**只引用本文件的键名与控件名**，禁止把色值表、字号表抄进去。
