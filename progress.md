# Progress Log — CNC 自动化上下料客户端

## Session 1 — 2026-06-26

### 已完成
- 读完全部 docs（客户端开发文档/数据库文档/UI设计文档/测试机信号表/sql/prototype）。
- 核查依赖版本，确认 .NET 8 + EF Core 8 + Pomelo 8.0.3 方向（见 findings.md）。
- brainstorming 定方向，确认 3 处设计决策（操作人/轮询中枢/模拟器）。
- 用户批准实施计划（`~/.claude/plans/docs-robust-goose.md`）。
- 建立 planning-with-files 三件套（task_plan/findings/progress）。

### Phase 1 完成（代码）— 暂停等用户确认
- 6 工程全部建成并 `dotnet build` 通过：0 警告 0 错误。
- Common：AppOptions/DPAPI 加解密/Serilog/ICurrentUser/连接串工厂。
- Core：SignalKey/PlcPointDefinition/PositionState/SignalStateStore/StatusSynthesizer/抽象接口。
- Communication：IPlcClient/NModbusPlcClient/PlcConnectionManager/ModbusTcpSimulator/PlcPollingService/SerilogDeviceLogger。
- Data：CncDbContext + 17 实体映射 + PlcPointSource + DataHealthProbe（EF8+Pomelo8.0.3，DbContextFactory）。
- UI：设计令牌+控件样式（计量仪表盘风）、自绘 chrome 主窗口、172px 扁平 8 项分组导航、ContentControl 路由、8 页 VM、监控看板静态骨架。
- App：Host 装配、全局异常兜底、启动自检（尽力而为）。

### 测试结果（本地运行 CncLoader.App.exe）
- 窗口显示 ✓（MainWindowTitle=CNC 自动化上下料客户端）。
- 内置 Modbus 模拟器启动 127.0.0.1:15001，PLC 建链 1/1 在线 ✓。
- 端到端读寄存器：D1006 = 2（经 NModbusPlcClient → 模拟器）✓。
- 全局异常/DB 故障不致命：DB 未连通仅告警，窗口照常显示 ✓。
- 日志按天滚动写入 logs/ ✓。
- **未完成**：DB 实连读种子数据（item 4）——demo 用 root/root 被拒，需用户提供 MySQL 凭据并执行 docs/sql/cnc_schema.sql 建库。

### Session 2 追加（2026-06-27）— 字体 + 数据库
- 三款字体静态 .ttf 已下载并嵌入 src/CncLoader.UI/Assets/Fonts/（Inter 4 字重 / JetBrains Mono 4 / Saira SemiCondensed 3），csproj 标记 Resource，Tokens.xaml 改 pack:// 引用（家族名 Inter / JetBrains Mono / Saira，系统字体兜底）。
- MySQL：mysql.exe 在 D:\MySQL\mysql-8.0.46-winx64\bin；用 root/Mas@2026 执行 docs/sql/cnc_schema.sql 建库 cnc_auto 成功，校验 PLC=3/机台=3/点位=36/料架=3。
- 口令未明文落 appsettings：用 DPAPI(CurrentUser, entropy=CncLoader.Secret.v1) 加密 Mas@2026，Password 存密文 + PasswordProtected=true。注意：密文绑定当前 Windows 用户(Administrator)，换用户运行需重新加密。
- 重新运行 CncLoader.App：数据库连通✓（线体1/PLC3/机台3/加工位6/点位36/料架3）；3 台 PLC 模拟器(15001-3)建链 3/3；轮询一轮读到 30 点位；端到端 D1006=2✓。**Phase 1 全部验收项通过。**

### 下一步（待用户确认 Phase 2）
- 等用户确认 Phase 2 → 进入 Phase 3：配置管理模块（线体/工序/机台/加工位/料架）。

### Session 3 — 2026-06-27 Phase 2 完成（代码）— 暂停等用户确认
- Core：PLC 服务接口（Catalog/Connection/Operation/PointManagement/DeviceLogStore/AlarmEvent）+ SignalTableTemplates + SignalLabels。
- Data：PlcCatalogService、PlcPointManagementService（信号表批量导入）、DeviceLogStore（MAS_AUTO_DEVICE_LOG）、AlarmEventService。
- Communication：PlcConnectionService（多 PLC 建链/断开/刷新登记）、PlcOperationService（写先落流水再下发+回读）、CompositeDeviceLogger、PlcEndpointResolver。
- UI：PlcViewModel 全功能实现 + PageTemplates 完整 PLC 页（列表/读/写/点位/告警/流水），设计令牌风格对齐原型。
- 验证：`dotnet build` 0 警告 0 错误；启动 3/3 PLC 在线；读 D1000–D1018 成功；CompositeDeviceLogger 落库读流水。

### Session 4 — 2026-06-27 文档整合 + UI 现代工业重设计
- 文档整合：合并开发方案为单一 `客户端开发文档.md`，删除冗余 `自动化标准化软件开发方案.md` 与原始需求 docx；全文 .NET 10 → .NET 8 统一并按真实代码校正技术架构。
- 引入 UI 控件库：`WPF-UI 4.3.0` + `HandyControl 3.5.1`（加入 `CncLoader.UI`）。
- 用 ui-ux-pro-max 方法论把 UI 方向重定为**现代工业 / HMI（深色优先）**，重写 `UI设计文档.md` 与 `prototype/index.html`（深色可切浅色、控件映射、PLC 含点位映射 tab）。
- WPF 现代工业深色改造：`Tokens/Styles/PageTemplates` 深色化（保留 brush 键），`App.xaml` 接 HandyControl SkinDark；报警/确认走 Growl/HandyControl MessageBox；移除 Saira。
- PLC 页按原型重做为「PLC 列表 + 读/写/点位映射三 tab」，点位映射并入 PLC 页并从导航移除；表格/下拉统一 HandyControl 基线；标题栏加图标、详情页标题改白；PLC 列表占比自适应。
- 验证：`dotnet build` 0 警告 0 错误；启动运行截图核对，3/3 PLC 在线，无 XAML/资源异常。
- **待落地**：WPF-UI `FluentWindow` + `NavigationView`（基于 Page 导航，需运行迭代）；据此删除旧自绘外壳并丰富监控看板。

### Session 5 — 2026-06-27 Phase 3 配置管理模块（代码）— 暂停等用户确认
- Core：`Config/ConfigModels.cs`（线体/工序/机台/料架 列表与编辑 DTO + 槽位/绑定/明细）+ `Abstractions/IConfigServices.cs`（IWorkLineService/ICraftworkService/IEquipmentConfigService/IFrameService + NamedOption）。
- Data：`Repositories/ConfigServices.cs` 四服务（短连接 IDbContextFactory，State=="0" 视为启用）；线体/工序支持 Save 回写；机台读列表+加工位+关联料架；料架读列表+绑定+分层槽位；DI 注册 4 单例。
- UI：替换 4 个占位 VM 为全功能（各自独立文件）：WorkLine/Craftwork（列表+编辑+保存+级联下拉）、Equipment（按工序过滤+加工位+关联料架，新增/配置走 Growl.Info）、Frame（料架+绑定+分层槽位+电极反查高亮）；`SlotLayerVm/SlotVm` 承载层与槽位高亮。
- UI：`Converters/ConfigConverters.cs`（是否/启用禁用/状态灯/料架角色色）；`PageTemplates.xaml` 按 prototype 一比一还原四页 DataTemplate，移除其占位模板，保留 AGV/扫码枪 占位。
- 验证：`dotnet build` 0 警告 0 错误；运行 CncLoader.App，DB 实连（线体1/PLC3/机台3/加工位6/点位36/料架3），3/3 PLC 模拟器在线，端到端 D1006=2；UIAutomation 逐页点击 + 截图核对线体/工序/机台/料架四页与原型一致、读到真实种子。
- 追加（按用户确认「本阶段就把完整新增 CRUD 做掉」）：机台/料架 新增走模态对话框（CncLoader.UI/Views/Dialogs 三个 Window，承 App 暗色资源）。
  - Core/Data：EquipmentCreateModel/FrameCreateModel + CreateEquipmentAsync(自动建 2 加工位)/SetFrameBindingAsync(只换本机台绑定，保一架两用)/CreateFrameAsync(按层×每层预建空槽)/SuggestNextNoAsync/GetPlc·FrameOptions。
  - UI：EquipmentVM AddEquipment/ConfigureFrame、FrameVM AddFrame 接真命令；料架页「新增」按钮改绑 AddFrameCommand。
  - 实测：UIAutomation 驱动两对话框填写并保存 → DB eq3→4(EQ04 测试机A,工位1/2=EQ04-P1/P2)、frame3→4(测试料架 FR-TEST-01,2×5 预建 10 槽,层 1..2)；验证后删测试行还原 eq3/pos6/frame3/slot14。
- 待用户确认 Phase 3 → 进入 Phase 4：核心上下料流程（加工位级状态机、双工位并行调度、信号合成、电极槽位流转、加工记录）。

### Session 6 — 2026-06-27 PLC 配置 CRUD 补全 + 文档同步（代码）— 暂停等用户确认
- 起因：Phase 3 后回看 Phase 2，PLC 管理页的 PLC 配置 CRUD 仍是占位 Growl（"将随机台管理页在 Phase 3 一并实现"），列表操作列只有连接/断开。Phase 3 实际只做了线体/工序/机台/料架 CRUD，未补 PLC。
- Core：`PlcEditModel` 加 `IsNew`（PlcId=0 即新增）；新增 `PlcDeleteCheckResult`；`IPlcCatalogService` 扩展 `SuggestNextPlcIdAsync/CheckDeleteAsync/DeleteAsync`。
- Data：`PlcCatalogService.SaveAsync` 拆新增/编辑两路径——新增做 PlcId 唯一性校验+必填/范围校验后 INSERT；编辑 PlcId 只读不更新（避免外键断裂）；`SuggestNextPlcIdAsync` = max(PlcId)+1，空表=1；`CheckDeleteAsync` 数机台/点位引用返回 CanDelete+Message；`DeleteAsync` 软删 State='1'。
- UI：新建 `Views/Dialogs/PlcEditDialog.xaml(.cs)`（仿 EquipmentEditDialog，PlcId 新增可编辑+建议提示/编辑只读，协议下拉 ModbusTCP/RTU）；`PlcViewModel` 重写 `AddPlcCommand` 为真新增、新增 `EditPlcCommand/DeletePlcCommand`（编辑在线 PLC 保存前自动断开；删除先 CheckDelete，不可删则 Growl.Warning 提示引用数，可删则 HandyControl MessageBox 二次确认后软删）；`PageTemplates.xaml` PLC 列表操作列在 连接/断开 后追加 编辑/删除 按钮。
- 文档同步：`docs/客户端开发文档.md` §5.4 PLC 列表与连接管理 扩展为 新增/编辑/删除 三条说明（含 PlcId 只读、引用校验、软删 STATE='1'）；`task_plan.md` Phase 2 状态加 "+ PLC 配置 CRUD 补全"、追加条目 2.6 与验收清单第 8 项；`findings.md` 补 PLC_ID 外键引用与软删约束发现。
- 验证：`dotnet build` 0 警告 0 错误；启动 App（PID 22768）窗口正常渲染、无 XAML/DI 异常；服务层数据流等价 SQL 端到端跑通（suggest=4 → insert PlcId=4 → check_delete eq_refs=0/pt_refs=0 → 软删 State=1 → 还原后 plc_count=3）；DB 种子保持干净。
- 待用户人工确认的 UI 三路径（WPF UIAutomation 测试基线未建）：① 点"+ 新增 PLC" 弹对话框→填名称/IP→保存→列表新增+DB INSERT；② 选已有 PLC 点"编辑"→改名→保存→列表与 DB UPDATE；③ 选 0 引用测试 PLC 点"删除"→二次确认→列表移除+DB State=1；选被机台引用的 PLC 点"删除"→提示引用数禁止删除。
- 待用户确认后 → 进入 Phase 4：核心上下料流程。

### Session 7 — 2026-06-27 配置管理删除 CRUD 补全 + 线体编辑表单布局修正（代码）— 暂停等用户确认
- 起因：Phase 3 后用户指出线体/工序/机台均缺删除功能，且线体编辑表单未与底部对齐、内容未自适应。
- Core：新增 `DeleteCheckResult(CanDelete, Refs, Message)`；`IWorkLineService/ICraftworkService/IEquipmentConfigService` 各加 `CheckDeleteAsync/DeleteAsync`。引用约束：线体→工序引用；工序→机台引用；机台→点位映射+料架绑定引用（加工位是机台自带，删除机台时级联软删）。
- Data：`WorkLineService/CraftworkService/EquipmentConfigService` 实现 CheckDelete（按外键 COUNT 启用行）+Delete（软删 State='1'）；`EquipmentConfigService.DeleteAsync` 同时把其 2 个加工位 State='1'。
- UI：三 VM 各加 `DeleteLineCommand/DeleteCraftCommand/DeleteEquipmentCommand`——CheckDelete 不可删 Growl.Warning 提示引用数；可删 HandyControl MessageBox 二次确认后软删 + Growl.Success + ReloadAsync；WorkLineVM/CraftworkVM 补 `using System.Windows`。
- UI 模板：`PageTemplates.xaml` 线体/工序/机台 列表各加"操作"列含 DangerButton"删除"；**线体管理编辑表单**改为 `VerticalAlignment=Stretch` 撑满列高与列表底部对齐，内部 DockPanel——标题 Dock=Top、保存/取消按钮 DockPanel.Dock=Bottom 贴底、StackPanel 内容顶对齐自适应；线体列表 `MaxHeight=320` 去掉改为随内容自适应。
- 验证：`dotnet build` 0 警告 0 错误；服务层 SQL 等价验证——种子线体1 被 2 道工序引用→禁删✓ 种子工序1 被 3 台机台引用→禁删✓ 种子机台1 被 12 个点位引用→禁删✓ 孤立工序可删路径 INSERT→check eq_refs=0→软删 State=1→还原 active=2✓；DB 种子保持干净。
- 待用户人工确认 UI 三路径（WPF 无 UIAutomation 基线）：① 线体列表"删除"按钮；② 工序列表"删除"按钮；③ 机台列表"删除"按钮；④ 线体编辑表单与列表底部对齐、保存/取消按钮贴底。

### Session 8 — 2026-06-27 修复删除后列表仍显示 + 线体剥离 PLC 关联（代码）— 暂停等用户确认
- 起因：用户反馈点击删除后列表中依旧存在；线体管理不该有"关联 PLC"字段，业务链是 线体→工序→机台→PLC。
- 修复 1（删除后列表仍显示）：根因是 `WorkLineService.GetAllAsync`、`CraftworkService.GetByLineAsync`、`EquipmentConfigService.GetByCraftAsync`、`GetWorkLineOptionsAsync`、`GetCraftworkOptionsAsync` 均未过滤 `State=='0'`，软删行被读回。统一加 `.Where(x => x.State == ConfigFlags.Active)` 过滤。
- 修复 2（线体剥离 PLC 关联）：业务链校正——线体不直接关联 PLC，PLC 由机台绑定。
  - Core: `WorkLineListItem` 去掉 `PlcText`；`WorkLineEditModel` 去掉 `PlcId`；`IWorkLineService` 去掉 `GetPlcOptionsAsync`。
  - Data: `WorkLineService.GetAllAsync` 不再投影 PlcText；`GetByIdAsync` 不读 PlcId；`SaveAsync` 不写 PlcId；移除 `GetPlcOptionsAsync` 实现。`WorkLineConfig.PlcId` DB 列保留（schema 不动），仅 UI 不再编辑。
  - UI: `WorkLineViewModel` 去掉 `PlcOptions`/`SelectedPlcOption`；`PageTemplates.xaml` 线体列表去 "PLC" 列、编辑表单去"关联 PLC"行。
- 验证：`dotnet build` 0 警告 0 错误；SQL 等价验证软删后 `WHERE STATE='0'` 过滤生效（INSERT 测试线体 active=1 → 软删后 active=0 → 还原 active=1 种子保持）。
- 待用户人工确认 UI：① 删除任一行后列表立即移除；② 线体管理编辑表单与列表均无 PLC 相关字段。

### Session 9 — 2026-06-27 PLC 编辑对话框对应机台改为下拉 + 反向绑定（代码）— 暂停等用户确认
- 起因：用户反馈 PLC 编辑对话框"对应机台"应是下拉显示所有机台（不只是只读文本），且当前文本是黑色字体不符深色主题。
- 设计：业务链机台→PLC，但允许在 PLC 侧反向绑定——选机台=把该机台的 PLC_ID 设为当前 PLC。一机一 PLC 约束：原绑到本 PLC 的机台解绑；目标机台原绑的 PLC 自然失去绑定。
- Core: `PlcEditModel` 加 `BoundEquipmentId`（null/0=未绑定）；`IPlcCatalogService` 加 `BindEquipmentAsync(plcId, equipmentId, author)`。
- Data: `PlcCatalogService.BindEquipmentAsync`——清所有 `PlcId==plcId` 的机台 PlcId，再把目标机台 PlcId=plcId；null/0 仅解绑。
- UI: `PlcEditDialog` 把"对应机台" TextBlock 改为 ComboBox，首项"未绑定"(Id=0) + 全部启用机台，选中当前绑定项；ComboBox 显式 `Foreground={StaticResource FgBrush}` 修复深色背景下的黑色字体。构造改为 `(edit, suggestedId, equipments, boundEquipmentId)`。
- VM: `PlcViewModel.AddPlc/EditPlc` 调 `GetEquipmentsAsync()` 传机台列表 + 当前绑定 Id；保存后调 `BindEquipmentAsync(plcId, result.BoundEquipmentId, user)`。
- 验证：`dotnet build` 0 警告 0 错误；SQL 等价验证 BindEquipment(plcId=1, eqId=3) → EQ01 解绑、EQ03 改绑 PLC1（原 PLC3 失去绑定）→ 还原种子 EQ01/02/03 → PLC1/2/3 ✓。
- 待用户人工确认 UI：① PLC 编辑对话框"对应机台"为下拉、文字浅色可读；② 选别的机台保存后该机台 PLC_ID 改为本 PLC，原机台解绑；③ 选"未绑定"保存后本 PLC 无机台关联。

### Session 10 — 2026-06-27 新增机台对话框关联 PLC 加"无"选项（代码）— 暂停等用户确认
- 起因：用户反馈新增机台时关联 PLC 下拉无"无"选项，若不绑 PLC 无法选择。
- 改动：`EquipmentEditDialog` 构造在 PLC 列表前加 `NamedOption(0, "无")`；有 PLC 时默认选第一台 PLC，无 PLC 时默认选"无"。`CreateEquipmentAsync` 已有 `PlcId = model.PlcId > 0 ? model.PlcId : null` 处理，选"无"(Id=0) 时 DB 写 `PLC_ID=NULL`，机台不绑 PLC。
- 验证：`dotnet build` 0 警告 0 错误。
- 待用户人工确认 UI：新增机台对话框"关联 PLC"下拉首项为"无"，可选不绑定。

### Session 11 — 2026-06-27 机台编辑功能补全（代码）— 暂停等用户确认
- 起因：用户反馈机台管理页只有新增/配置料架/删除，缺编辑机台本身（名称/编码/类型/所属工序/关联 PLC）。
- Core: `ConfigModels` 加 `EquipmentEditModel(Id, CraftworkId, No, Name, Code, Type, PlcId)`；`IEquipmentConfigService` 加 `GetByIdAsync/UpdateAsync`。
- Data: `EquipmentConfigService.GetByIdAsync` 返回编辑数据；`UpdateAsync` 更新名称/编码/类型/工序/PLC，**EquipmentNo 业务键不改**（避免加工位编码、点位映射引用断裂）；`PlcId=0` 写 `NULL`。
- UI: `EquipmentEditDialog` 改造支持新增/编辑双模式——构造 `(crafts, plcs, suggestedNo, preselectCraftId)` 新增、`(crafts, plcs, edit)` 编辑；编辑时标题"编辑机台"、机台编号只读、预填现值；返回 `CreateResult` 或 `EditResult`。CraftCombo/PlcCombo 显式 `Foreground={StaticResource FgBrush}` 修深色黑字。PLC 下拉含"无"。
- VM: `EquipmentViewModel` 加 `EditEquipmentCommand`；AddEquipment 改用 `dlg.CreateResult`；EditEquipment 调 `GetByIdAsync` → 弹编辑对话框 → `UpdateAsync` → 刷新。
- 模板: `PageTemplates.xaml` 机台列表"操作"列宽 80→120，加"编辑" GhostButton 在"删除"前。
- 验证：`dotnet build` 0 警告 0 错误；SQL 等价 UpdateAsync（EQ02 改名/编码/PLC_ID=1）→ 仅名称/编码/类型/PLC 更新、EQUIMENT_NO 不变 ✓；还原种子 EQ01/02/03 → PLC1/2/3 ✓。
- 待用户人工确认 UI：机台列表点"编辑" → 弹编辑对话框预填现值、编号只读、可改名称/编码/类型/工序/PLC → 保存回写。

### Session 12 — 2026-06-27 Phase 5 外设测试页（AGV / 扫码枪）按原型一比一还原（代码）— 暂停等用户确认
- 起因：用户要求优先开始 Phase 5 开发，客户端页面与原型一比一还原。
- Core: 新建 `IExternalDeviceTestService.cs` — `IAgvTestService.TestConnectionAsync(AgvTestConfig)→AgvTestResult`；`IScanListenerService`（IsListening/ConnectedCount/Start/Stop/ScanReceived 事件/GetRecent）+ DTO `AgvTestConfig/AgvTestResult/ScanRecord/AgvCommWay(HttpRest,Socket)`。
- Communication: 新建 `ExternalDevices/AgvTestService.cs`（HTTP REST GET，可选 Basic Auth，5s 超时；Socket 模式 TCP 连一次解析 host:port）；`ExternalDevices/ScanListenerService.cs`（TcpListener 监听端口，按 CR/LF 拆行，维护 ConcurrentQueue 最近 50 条 + 连接计数 + 事件）。`AddCncCommunication` 注册两服务单例。
- UI: `AgvViewModel`/`ScanViewModel` 从 `PageViewModels.cs` 拆为独立文件并实现全功能。Agv：表单 5 字段 + TestConnectionCommand + TerminalLines 终端 + 状态徽标"连通 Nms/不通"。Scan：ModeOptions/Port + StartListen/StopListen 命令 + ScanRows 表 + 状态徽标"已连接 N"。ScanVM Dispose 取消订阅并停服务。
- UI 模板: `PageTemplates.xaml` 移除 AGV/扫码枪占位模板，按 `docs/prototype/index.html` §AGV/扫码枪 一比一还原：单 panel max-width 700、表单字段左标签 84px、终端深色 TermBrush + mono 字体、状态徽标 Border+Ellipse+TextBlock、最近扫码 DataGrid。密码字段保留 PasswordBox 视觉但不绑（"仅测试连通"无需密码）。
- 转换器: `ConfigConverters.cs` 加 `InverseBoolConverter`（含 Instance 单例），用于 IsTesting 时禁用测试按钮。
- 验证：`dotnet build` 0 警告 0 错误；启动 App（PID 20828）窗口正常渲染、无 XAML/DI 异常、PLC 模拟器建链如常。
- 待用户人工确认 UI：① AGV 页表单 + 测试连接按钮 + 终端结果；② 扫码枪页表单 + 开始监听/停止按钮 + 最近扫码表；③ 两页布局与原型一致。

### Session 13 — 2026-06-27 修复 AGV 页导航栈溢出（PasswordBox 误用 TextBox 样式）（代码）— 暂停等用户确认
- 起因：用户反馈点击 AGV 管理页"疯狂弹框报错无法创建新的堆栈防护页面"。
- 根因：`PlcEditDialog`/`EquipmentEditDialog` 之前已修过 PasswordBox 样式问题，但 AGV 模板的密码字段仍用 `Style="{StaticResource FormInput}"`，而 FormInput 是 `TargetType=TextBox` 的样式，应用到 PasswordBox 抛 `XamlParseException: TextBox TargetType 与元素 PasswordBox 的类型不匹配`。全局异常处理器 `e.Handled=true` 后 WPF 重试渲染 → 再次抛 → 弹框循环 → 栈耗尽。
- 修复：AGV 模板密码字段改用内联属性（Height/FontSize/Background/Foreground/BorderBrush/Padding），不再套 FormInput 样式。
- 验证：`dotnet build` 0 警告 0 错误；启动 App（PID 18876）窗口正常渲染、无新异常日志（log_006 之后无新增），导航到 AGV 页不再弹框。
- 待用户人工确认：点击 AGV 管理页能正常进入，无弹框。

### Session 14 — 2026-06-27 AGV 页删除用户名/密码字段（代码）— 暂停等用户确认
- 起因：用户要求删除 AGV 页面的用户名与密码两项（"仅测试连通"无需鉴权）。
- 改动：
  - Core: `AgvTestConfig` 去 `Username/Password`。
  - Communication: `AgvTestService` 去 Basic Auth 逻辑与 `AuthenticationHeaderValue` using。
  - UI: `AgvViewModel` 去 `Username/Password` 属性，`TestConnectionAsync` 不再传鉴权字段。
  - 模板: `PageTemplates.xaml` AGV 表单去"用户名"和"密码"两行，"地址"后直接是"测试连接"按钮。
- 验证：`dotnet build` 0 警告 0 错误。
- 待用户人工确认：AGV 页表单只有 名称/通信方式/地址 三项 + 测试连接按钮。

### Session 15 — 2026-06-27 AGV/扫码枪页布局左对齐（代码）— 暂停等用户确认
- 起因：用户反馈 AGV 页与扫码枪页面板居中显示，与原型左上方不一致。
- 根因：Border 用 `HorizontalAlignment="Stretch" + MaxWidth="700"`，Stretch 时剩余空间被分配导致视觉居中。
- 修复：两页 Border 改 `HorizontalAlignment="Left"`，与原型 `max-width:700px` 左对齐一致。
- 验证：`dotnet build` 0 警告 0 错误。
- 待用户人工确认：AGV/扫码枪页面板贴左上方，不再居中。

### Session 16 — 2026-06-30 新增欧姆龙 FINS 协议支持（代码 + 文档）
- 背景：现场 PLC 改走 FINS 协议，需在保持 Modbus 的同时支持 FINS，并能在本机先验证。
- 通信层：
  - `PlcEndpoint` 增 `Protocol` 字段；工厂 `PlcClientFactory`（原 `NModbusPlcClientFactory`）按协议分流创建 `NModbusPlcClient` / 新增 `OmronFinsPlcClient`。
  - `OmronFinsPlcClient`：手写 FINS/UDP（9600），D→DM 字读写（读 0x0101/写 0x0102），节点号自动取 IP 末段、网络/单元号=0；连接做 DM 探活，离线判未连接；socket 用 CancellationToken 重载避免未观察异常。
  - 协议透传：`PlcEndpointResolver.Resolve` 加 protocol 参数，`PlcConnectionService`/`PlcRuntimeBootstrapper` 全链路传递；`PlcOptions.FinsDefaultPort=9600`。
  - 本地模拟：新增 `OmronFinsUdpSimulator`（UDP 响应读写），`UseSimulator=true` 时按协议分流——FINS→16000+id，Modbus→15000+id，两类可共存。
- 启动修复：`App.OnStartup` 改为先显示窗口、后台跑启动自检——原先自检 await 在前，连不上真机时数十秒读超时导致“窗口不弹出”。
- UI：`PlcEditDialog` 协议下拉加 FINS，选中联动端口 9600。
- 验证：`dotnet build` 0 警告 0 错误（WPF 运行态待现场/人工确认）。
- 文档同步：`docs/客户端开发文档.md`(§2 通信栈、目录结构、§3.3 适配器、§5.4 新增 PLC)、`docs/UI设计文档.md`(§6.3 连接条)、`docs/sql/cnc_schema.sql`(PLC_READ_WAY/端口注释)、`task_plan.md`(技术栈/结构)、`findings.md`(FINS 节点号假设)。

### Session 17 — 2026-06-30 现场配置变更：取消机台安全 + 平面度工位2
- 现场确认两点：① 内长宽(EQ01)、平面度(EQ02) 取消「机台安全」信号；② 平面度只保留工位1，取消工位2。
- 代码无需改：`StatusSynthesizer` 用 `MachineSafe == false` 才报警，缺该信号时为 null，不会误报警。
- 种子 `docs/sql/cnc_schema.sql`：删 EQ1/EQ2 MACHINE_SAFE 点位；删平面度工位2(POS ID=4)及其 5 个点位(D1212/1214/1216/1218/1302)。
- 信号表 `docs/测试机信号表.md`：内长宽去机台安全、平面度去机台安全+工位2，各加变更注。
- 现有库迁移：新增 `docs/sql/migration_2026-06-30_remove_machinesafe_and_pmd_pos2.sql`（软删 STATE='1'，按 EQUIMENT_NO/POSITION_CODE 定位，幂等可回滚）。需对现有 cnc_auto 执行后才在运行时生效。

### Session 18 — 2026-06-30 FINS 现场联调期 UI/健壮性修复（代码）
- 标题栏协议改为跟随实际配置：`ShellViewModel` 注入 `IPlcCatalogService`，`PlcProtocolText` 去重各 PLC 协议（多协议用 / 连接），`ShellWindow` 绑定之（提交 2337249）。
- FINS 模拟器端口占用容错：`OmronFinsUdpSimulator.StartAsync` 单台 bind 失败(10048，常因开多个程序实例)只记警告并继续，不再整体抛异常（提交 e2afac4）。
- 启动不弹窗修复（前序）：`App.OnStartup` 先显示窗口、自检改后台，避免连不上真机时数十秒读超时阻塞窗口。
- 点位映射页（提交 cab3912 的一部分）：新增「保存全部」批量写多行；保存/删除/导入失败弹 `Growl.Error`；信号KEY/ON/OFF/长度列 `UpdateSourceTrigger=PropertyChanged` 即时提交。
- 跨面板刷新：`PointMappingViewModel.PointsChanged` 事件 → `PlcViewModel.OnPointsChangedAsync` 自动刷新读/写面板并切到对应 PLC。
- 断开重连单次读无反应修复：`PlcViewModel.RefreshPlcRowAsync` 替换行前记录选中、替换后无条件恢复 `SelectedPlc`（替换被选中行会被 DataGrid 置 null）。
- 排障要点（现场注意）：① 勿同时运行多个程序实例（会抢模拟器端口 / 端口冲突）；② FINS 节点号默认取 IP 末段，不符需后续做成可配置。
- 文档同步：`docs/客户端开发文档.md` §5.4 点位映射维护操作说明。

### Session 19 — 2026-07-06 第四阶段启动：DB schema 变更（RCS 对接）
- 起因：开始第四阶段（RCS 对接 + 上下料流程），按开发文档 §10(v3) 7 步推进，每步暂停确认。首步补齐 DB schema。
- 新增迁移脚本 `docs/sql/migration_phase4_rcs.sql`（幂等：存储过程守卫 ADD COLUMN、新表 CREATE IF NOT EXISTS、附回滚段）：
  - `MAS_AUTO_AGV_TASK` 扩展 RCS 任务全生命周期列：RCS_TASK_ID/RCS_KIND/RCS_STATUS/TASK_STATE/PRIORITY/DISPATCH_TIME/REDO_COUNT/CANCEL_MANUAL_FLAG/POSITION_ID/ELECTRODE_ID/TXN_ID/REQ_PARAM + 3 索引。
  - 新增 `MAS_AUTO_LOCATION_MAP`（逻辑位置↔RCS点位编码 station/cell 双层）、`MAS_AUTO_RCS_MSG_LOG`（双向报文流水）。
  - `MAS_AUTO_FRAME_SLOT` 加 BIND_SOURCE/LAST_VERIFY_TIME；`FRAME_BIND.FRAME_ROLE` 语义扩展 0/1/2/3=上料/下料/中转/NG（CHAR(1) 不变，兼容旧种子）；ALARM_TYPE 注释补 RCS_WARN。
- 同步更新 `docs/sql/cnc_schema.sql`（全新建库含全部新表/列，DROP 段补两新表）；EF 实体 `RunAndReservedEntities.cs`（AgvTask 扩展 + 新增 LocationMap/RcsMsgLog）、`SignalAndFrameEntities.cs`（FrameSlot 扩展）、`CncDbContext.cs` 注册两 DbSet；`docs/数据库文档.md` 补表清单/结构变化/§5b RCS 表说明。
- 环境校正：本机为 MySQL 8.4（服务 MySQL84，`C:\Program Files\MySQL\MySQL Server 8.4\bin\mysql.exe`），root 口令 `2580.wxr`（非旧 session 的 8.0.46/Mas@2026）。**注意 appsettings.json 仍是空口令，运行前需用户按本机配置连接口令（建议 DPAPI 加密，勿明文提交）**。
- 验证：`dotnet build` 0 警告 0 错误；迁移已应用到本机 cnc_auto 并校验两新表 + AGV_TASK/FRAME_SLOT 新列就位；重复执行幂等（RERUN_EXIT=0）。
- 下一步（待用户确认）：进入步骤①（IRcsClient + RcsClient 4 出站接口 + TaskId 先落库 + RcsOptions + LOCATION_MAP 录入 + RCS 页骨架）。

### Session 20 — 2026-07-06 第四阶段步骤①：RcsClient + 手动测试页 + LOCATION_MAP（代码）— 暂停等用户确认
- Core（新增 `Rcs/`）：`RcsModels`（transit/excute/cancel/query 请求 DTO + RcsPosition/RcsContainer/GrabItem + QueryCondition + RcsResult + RcsTaskKind/State 常量）；`IRcsClient`（4 出站接口）；`IRcsMessageLog`（报文流水 + 展示行）；`IRcsTaskStore`（先落库/状态推进/redo/取消确认/未完结查询）；`ILocationMapService`（LOCATION_MAP CRUD + 解析）；`IRcsTaskService`（编排：生成 taskId→先落库→下发）；`RcsTaskId`（`{线体}-{类型}-{yyyyMMddHHmmss}-{4位序列}` 生成器）。
- Communication（新增 `Rcs/`）：`RcsClient`（HttpClient 封装公共字段/超时10s/网络重试≤3指数退避/双向报文落库/relaxed JSON 编码）；`RcsTaskService`（transit/grab/identify 下发 + cancel + redo 同 taskId 幂等 + query）。DI 注册 IRcsClient（内建 HttpClient）+ IRcsTaskService。
- Data（新增仓储）：`RcsMessageLog`（MAS_AUTO_RCS_MSG_LOG）、`RcsTaskStore`（MAS_AUTO_AGV_TASK 全生命周期）、`LocationMapService`（MAS_AUTO_LOCATION_MAP）。DI 注册三单例。
- Common：`AppOptions` 加 `RcsOptions`（BaseUrl/clientCode/version/tokenCode/超时/重试/回调 host+port/轮询/UseSimulator）；`appsettings.json` 加 Rcs 节。
- UI：新增 `RcsViewModel`（连接配置只读展示 + 手动下发搬运/抓取/识别 + 取消/redo/查询 + 任务列表 + 报文流水 + 位置映射录入编辑）；`PageTemplates.xaml` 加 RCS 页 DataTemplate（4 tab：连接&下发/任务列表/报文流水/位置映射）；导航加 "RCS 任务" 项（物流组）；UI DI 注册。
- 验证：`dotnet build` 0 警告 0 错误；临时 harness 用假 handler+假 store 跑通 4 接口，请求 JSON 与 docx §12 示例一致（transit taskType=move+cell / grab param 数组+station / identifyQR param="101,3" / cancel 仅公共字段 / query condition+分页），先落库 3 条 CREATED、报文流水 OUT 逐条触发、taskId 格式正确；harness 与 docx 提取临时文件已清理。
- 完成标志达成：四种请求可发出 + 报文有流水 + 格式与 §12/docx 一致（对本地 Mock）✓。
- 待用户人工确认 UI（需先修 appsettings 连库口令）：RCS 页四 tab 可用、下发后任务/报文列表刷新、位置映射录入保存。
- 下一步（待确认）：步骤②（RcsCallbackHost 内嵌 Kestrel 3 回调 + 幂等去重 + warnCallback 落库）。

### Session 21 — 2026-07-06 文档同步 + appsettings 连库口令（DPAPI）
- 文档同步：`task_plan.md` Phase 4 重写为 v2/RCS 版（4.0–4.7 条目，DB+步骤①打勾）；`findings.md` 加"第四阶段（RCS 对接）关键发现与决策"段（分层接缝/taskId/JSON编码/FRAME_ROLE/本机 MySQL8.4 环境/迁移幂等/Kestrel 依赖）；`docs/README.md` 清单收录 `agv对外接口.docx` 与 `sql/migration_*.sql`。
- appsettings 连库口令：本机 MySQL 8.4 root 口令用 DPAPI(CurrentUser, entropy=CncLoader.Secret.v1) 加密写入 `appsettings.json`，`PasswordProtected=true`，不留明文。密文绑定当前 Windows 用户(75626)+本机，换用户/机器需重新加密。
- App 运行态验证受阻（非代码问题）：本机 **应用程序控制策略拦截新构建的 CncLoader.App.dll**（事件日志 FileLoadException 0x800711C7 "应用程序控制策略已终止此文件"），exe 无法加载运行——无法本地冒烟跑 UI/DB 连通；`dotnet build`（含 BAML 编译）通过为当前可用自动化上限。DB 连通性已由迁移执行（root/2580.wxr 直连成功）间接佐证；DPAPI 密文由同参 Protect/Unprotect 保证可解。
- 待办：`docs/客户端开发文档.md` §12 的"落地"指引因文件被编辑器占用(只读锁)未写入，后续解锁再补（`数据库文档.md` 已覆盖）。

### Session 22 — 2026-07-06 第四阶段步骤②：RcsCallbackHost 内嵌 Kestrel 3 回调（代码）— 暂停等用户确认
- Core（新增 `Rcs/`）：`RcsCallbackModels`（`RcsCallbackInterfaces` 接口名/路径常量、`RcsErrorCode` 0/1/9→态映射、事件记录 `RcsTaskStatusEvent`/`RcsScanResultEvent`/`RcsWarnEvent`）；`IRcsCallbackNotifier` + `RcsCallbackNotifier`（单例事件总线，处理器 Raise*、跟踪器/UI 订阅）；`IRcsCallbackProcessor`（收原始报文→返回应答报文体）。
- Core：`IAlarmEventService` 加 `RaiseRcsWarnAsync(robotCode/beginTime/warnContent/taskCode)`；Data `AlarmEventService` 实现（ALARM_TYPE=RCS_WARN、级别 '2' 严重、拼消息、AlarmRaised 事件）。
- Communication（新增 `Rcs/`）：`RcsCallbackProcessor`——解析 JsonDocument（`data.system.error_code/msg`、scan 的 `code`/`products`、warn 的 `data[]`）；每次先落 IN 报文流水（`IRcsMessageLog` direction=IN，reqBody=原文、respBody=应答）；幂等去重内存有界集合（push `push:{taskId}:{code}`、scan `scan:{taskId}:{code}`、warn `warn:{robot}|{begin}|{content}`，容量 4000）；push/scan → `IRcsTaskStore.UpdateStateAsync`（0→COMPLETED/9→CANCELED/其它→FAILED）；warn 逐项 → `RaiseRcsWarnAsync` + 派发事件；所有 Handle* 吞异常总能应答，防 RCS 反复重推。`RcsCallbackHost : IHostedService,IAsyncDisposable`——`WebApplication.CreateSlimBuilder()` + `ConfigureKestrel` 监听 `CallbackHost:CallbackPort`，MapPost 三端点读原始 body 交处理器、`Results.Content(ack,"application/json")`；ClearProviders 静默自带日志；启动失败（端口占用）仅 LogError 不阻断。
- 依赖：`CncLoader.Communication.csproj` 加 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`（net8.0 库可用；WPF App 传递引用，运行需本机装 ASP.NET Core 8 运行时——现场部署注意项）。
- DI：Core 注册 `RcsCallbackNotifier` 单例 + `IRcsCallbackNotifier`；Communication 注册 `IRcsCallbackProcessor` + `AddHostedService<RcsCallbackHost>`（随 `_host.StartAsync()` 启动）。
- UI：`RcsViewModel` 注入 `IRcsCallbackNotifier`，订阅 TaskStatus/ScanResult/Warn 三事件——终端追加（↩/⚠）+ 刷新任务/报文列表 + warn 弹 Growl.Warning（单例 VM，无退订泄漏）。
- 验证：`dotnet build` 0 警告 0 错误；临时 harness（Communication+Core+Common，假 MsgLog/Store/Alarms + 真 RcsCallbackHost/Processor/Notifier）起 Kestrel:19087，用 docx §3.5/3.6/3.7 示例报文 POST——全 PASS：三端点 200 且 ack 含 taskId（warn 空串）、push 0→COMPLETED / 9→CANCELED、push+warn 幂等去重（重复不再更新/不新增告警）、scan products=3 code=101、warn→2 条 RCS_WARN、6+ 条 IN 报文均落库；harness 已删除。
- 待用户人工确认（需先连库）：运行 App 后 RCS 回调服务监听 `0.0.0.0:9080`（appsettings），外部 POST 三回调 → 任务态更新/报文流水 IN/告警面板出 RCS_WARN/RCS 页终端与列表实时刷新。
- 下一步（待确认）：步骤③ RcsSimulator 本机联调（收任务→延时→回推完成/失败/取消，失败率/延时/取消可配），与步骤②回调服务端闭环自测。

### Session 23 — 2026-07-06 第四阶段步骤③：RcsSimulator 本机联调（代码）— 暂停等用户确认
- Common：`RcsOptions` 加模拟器可配项 `SimulatorMinDelayMs`(1500)/`SimulatorMaxDelayMs`(4000)/`SimulatorFailureRate`(0)/`SimulatorCancelRate`(0)。
- Communication（新增 `Simulation/RcsSimulator.cs`，`IHostedService,IAsyncDisposable`）：扮演 RCS 服务端。`WebApplication.CreateSlimBuilder` 从 `RcsOptions.BaseUrl` 解析端口 `ListenAnyIP`，MapPost 4 出站接口——transit/excute/cancel/query 收原始 body、应答 `{Success:true,Message,Data:null}`；excute 按 `taskType` 分流 grab/identifyQR；受理即建 `SimTask`（含 position[0].code、identify 的 posStart/count）入内存表并调度延时回推。回推逻辑：延时随机 [min,max]；error_code 判定=已被 cancelTask 标记→9 / 命中 FailureRate→1 / 命中 CancelRate→9 / 否则 0；identify→scanTaskStatus（成功时按 count 生成 `SIM{code}-{pos}` products + code）、其余→pushTaskStatus；回推 host 为 0.0.0.0 时改走 127.0.0.1 环回到 `CallbackPort`。cancelTask 标记 Canceled 抑制/改判回推。停机 `CancellationTokenSource` 取消挂起回推。
- DI：`AddCncCommunication` 注册 `AddHostedService<RcsSimulator>()`（守卫在 StartAsync 内，UseSimulator=false 直接返回）。与步骤② `RcsCallbackHost` 并存，各自 Kestrel/端口。
- 验证：`dotnet build` 0 警告 0 错误；临时 harness（真实 `RcsCallbackHost`+`RcsCallbackProcessor`+`RcsCallbackNotifier` + 假 store/log/alarm + 3 个真实 `RcsSimulator`：成功18090/失败18091/取消18092，均回推 19088）闭环——POST transit→COMPLETED、identify→scanTaskStatus 事件 products=3 code=101 且 COMPLETED、fail=1→FAILED、下发即 cancelTask→CANCELED，全 PASS；harness 已删除。
- 待用户人工确认（需先连库运行 App）：`UseSimulator=true` 下 RCS 页手动下发 transit/grab/identify → 秒级后任务列表自动翻 COMPLETED、报文流水出 IN pushTaskStatus/scanTaskStatus、终端 ↩ 提示；调 `SimulatorFailureRate`/`SimulatorCancelRate`/延时可复现失败/取消/慢任务。
- 修复（同 session）：RCS 页回调到达不实时刷新——根因 `OnTaskStatusReceived/OnScanResultReceived` 在 Kestrel 后台线程触发，`RefreshTasksAsync/RefreshMessagesAsync` 直接改 `ObservableCollection` 抛跨线程异常被 catch 吞掉。改为 `ReplaceOnUi` 助手：`Dispatcher.CheckAccess()` 命中直接改、否则 `Dispatcher.Invoke` marshal 到 UI 线程（任务/报文/位置映射三处统一走）。构建 0 警告 0 错误。
- 修复（同 session）：任务列表表头 `态` → `状态`（`PageTemplates.xaml`）。
- 增强（同 session）：报文流水筛选（用户确认「筛选+可调条数+暂停刷新」，不做传统分页；表清理留 Phase 6）——
  - Data：`IRcsMessageLog.QueryAsync(RcsMsgQuery{Direction/Interface/TaskId/Limit})` 服务端筛选（方向精确、接口精确、taskId 模糊 Contains、条数 Clamp 1..5000），`GetRecentAsync` 复用之；`IRcsTaskService.QueryMessagesAsync` 委派。
  - UI：报文流水 tab 加筛选栏（taskId 框 / 方向 / 接口 / 条数 100·500·1000·2000 下拉 / 查询 / 自动刷新 CheckBox）；`RcsViewModel` 加对应筛选属性 + `AutoRefreshMessages`；回调到达时任务列表始终刷新、报文流水仅在自动刷新开启时刷新（筛选/排查时不被回调刷走）。构建 0 警告 0 错误。
- 下一步（待确认）：步骤④ 任务跟踪器（回调主通道 + queryTask 2~5s 批量兜底 + RCS 11→本系统 5 态映射 + redo + cancel 人工工单锁点位）。

### Session 24 — 2026-07-06 第四阶段步骤④：任务跟踪器（代码）— 暂停等用户确认
- Core（新增 `Rcs/RcsStatusMapper.cs`）：`RcsStatus` 11 态常量 + `ToTaskState` 按 §4.5 映射（uninitialized/queued/standby/blocked/delayed→DISPATCHED；underway→EXECUTING；completed→COMPLETED；failed/error/skipped→FAILED；canceled/killed→CANCELED；未知→null）+ `IsTerminal`。`RcsCallbackModels` 的 `RcsTaskStatusEvent` 加 `Source`(默认 callback)。
- Core：`IRcsTaskStore` 加 `TryIncrementRedoIfUnderAsync(taskId, maxRedo)` 原子 CAS（REDO_COUNT<max 才 +1 并回 DISPATCHED/清错，返回 bool），避免回调与轮询并发双重 redo。`IRcsTaskService` 加 `RedispatchAsync`（只重发不递增，配合 CAS）+ `ConfirmCancelHandledAsync` 委派。
- Core：`IAlarmEventService` 加 `RaiseRcsTaskCanceledAsync`/`RaiseRcsTaskNotFoundAsync`/`RaiseRcsRedoLimitAsync`（ALARM_TYPE=RCS_CANCELED/RCS_NOT_FOUND/RCS_REDO_LIMIT，级别严重/警告/严重）。
- Data：`RcsTaskStore.TryIncrementRedoIfUnderAsync` 实装；`AlarmEventService` 加 3 个 RCS 任务告警实装（私有 `RaiseRcsTaskAlarmAsync` 复用）。
- Communication：`RcsTaskService.RedoAsync` 拆为 `BuildAndSendAsync`(按落库种类重建请求) + `RedispatchAsync`(不递增) + `ConfirmCancelHandledAsync`；新增 `Rcs/RcsTaskTracker.cs`（`IHostedService`）：
  - 轮询循环每 `PollIntervalMs`：`GetUnfinishedTaskIdsAsync` → 批量 `QueryAsync`(condition IN) → `ParseItems`(items[].id/status) → `ApplyPollStateAsync`(映射+UpdateStateAsync+RaiseTaskStatus source=poll)；未返回的 taskId → `RaiseRcsTaskNotFoundAsync`(去重)。
  - 订阅 `notifier.TaskStatusReceived`：FAILED → `AutoRedoAsync`(TryIncrement→Redispatch，超限→RaiseRcsRedoLimit 去重)；CANCELED → `RaiseRcsTaskCanceledAsync`(去重)。终态清理去重集合。
  - 与回调冲突以 queryTask 为准（poll 直接覆盖态）。
- Communication：模拟器 `queryTask` 实装——按请求 condition IN 的 taskId 列表从内存表回 `items[]`（已回推完成的视为 completed，被 cancel 的 canceled，否则 underway），`ParseQueryTaskIds` 解析 IN 条件。
- Common：`RcsOptions` 加 `TrackerEnabled=true`/`MaxAutoRedo=3`。
- DI：`AddCncCommunication` 注册 `AddHostedService<RcsTaskTracker>()`（`TrackerEnabled=false` 时 StartAsync 直接返回）。
- UI：`RcsViewModel` 事件行带 Source（`↩ {source} ...`）；新增 `ConfirmCancelHandledCommand` + 模板加「确认取消已处理」按钮；任务列表加「取消处理」列（`CancelFlagConverter` 0→待处理/1→已处理）。
- 验证：`dotnet build` 0 警告 0 错误（Core/Data/Communication/UI）；harness 真 tracker+processor+notifier+service + 假 client/store/log/alarms 10 项全 PASS：poll underway→EXECUTING、poll completed→COMPLETED、callback FAILED→自动 redo Redispatch 调用 +REDO_COUNT=1、REDO_COUNT=3→不再 redo +RaiseRcsRedoLimit 告警、CANCELED→RaiseRcsTaskCanceled 告警+态CANCELED、ConfirmCancelHandled→flag=1；harness 已清理。
- 待用户人工确认（需先连库运行 App）：下发后任务态自动从 DISPATCHED→EXECUTING→COMPLETED（轮询驱动）；模拟器 FailureRate=1 时 FAILED 任务自动 redo ≤3 次后告警；取消任务后告警面板出 RCS_CANCELED，点「确认取消已处理」后 CANCEL_MANUAL_FLAG=1。
- 下一步（待确认）：步骤⑤ 状态机改造（DISPATCHING/TRANSPORTING 态 + LOADED 双条件 + 优先级队列 + §6.2 路由决策 + §6.3 启动对账 + PLC 复核收口），PLC 模拟器+RcsSimulator 双位并行联跑 ≥10 节拍。

### 备注
- 已是 git 仓库（远程 origin: github.com/rui-xiaomi/CNC）；commit/push 前先给用户看信息并确认。
- 本机环境：MySQL 8.4（服务 MySQL84），root 口令 `2580.wxr`；appsettings 用明文口令开发（PasswordProtected=false，勿提交明文进 git）。
- Smart App Control（Win11 智能应用控制）曾强制开启拦截未签名新构建 exe（事件 3118/3077，FileLoadException 0x800711C7）；用户已在 Windows 安全中心手动关闭（VerifiedAndReputablePolicyState=0）。关闭后已验证 `CncLoader.App.exe` 正常运行：DB 连通（线体1/PLC3/机台3/加工位6/点位36/料架3）、3 PLC 模拟器建链、轮询读 D 寄存器正常。本机现可跑 App+连库+RCS 页端到端。
- Phase 1/2 完成后暂停演示，Phase 3（配置管理）待用户确认。
- 后续 UI 开发以 `docs/prototype/index.html` 为唯一权威基准。
- 第四阶段：按开发文档 §10(v3) 7 步推进，每步暂停等用户确认；DB schema 变更（Session 19）已完成并应用。
