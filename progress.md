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

### Session 25 — 2026-07-07 第四阶段步骤⑤：状态机改造（代码）— 暂停等用户确认
- 范围（用户确认）：核心状态机+复核+队列+对账+PLC 行为模拟；§6.2 工序间流转/中转架/NG分流/水位/换架/空托盘/盘点留步骤⑥。
- Core（新增/改 `State/`）：`PositionState` 加 `Dispatching`/`Transporting`（上下料共用，context 区分阶段）+ `PositionStateNames` 同步；`DispatchQueue.cs`（`DispatchItem`/`PositionPhase`/`IDispatchQueue`/`IRouteResolver`）；`IPositionScheduler`（IsReconciled/Reconciled 事件/ResetAlarmAsync）；`IPlcWriteHook`（PLC 写钩子，模拟用）；`RcsModels.RcsResult` 加 `TaskId`；`IRcsTaskStore.TryIncrementRedoIfUnderAsync`；`IRcsTaskService.RedispatchAsync`+`ConfirmCancelHandledAsync`；`RcsTaskRow` 加 `TaskType`（对账区分阶段）。
- Communication（新增 `State/`）：`PriorityDispatchQueue`（priority 降序+同优先级 FIFO，线性扫描小规模）；`RouteResolver`（`LOAD_AREA`/`UNLOAD_AREA` 命名点 + 加工位 cell，LOCATION_MAP 解析，失败返回 null 告警）；`PositionScheduler` HostedService——
  - 启动：装载加工位+POS_TEST_START/HasMat 点位缓存 → §6.3 对账（`GetUnfinishedTaskIdsAsync`→按 TaskType 绑定回上下文）→ IsReconciled=true → 主循环(500ms)+派工循环。
  - 主循环：每加工位读信号(GetReadings+GetMachine)+读 RCS 态(GetByTaskIdAsync)→ComputeNextState→ExecuteActions（入队/复核/写启动/告警，可改写 next）→SetState（唯一 PositionStatus 权威）。
  - 状态机：Offline/Alarm→WaitLoad；WaitLoad+允许上料+无料+无任务→入上料队(prio5)→Dispatching；Dispatching+rcs DISPATCHED→Transporting；Transporting+rcs COMPLETED→**fresh PLC 读 HasMat 复核**（不依赖信号仓，避免轮询滞后误判）→上料 Loaded/下料 Unloaded/否则 Alarm；Loaded→写 POS_TEST_START=1→Processing；Processing+Ok→DoneOk/+Ng→DoneNg；Done→入下料队(prio8)→Dispatching；Unloaded→写 POS_TEST_START=2→WaitLoad。复核不过→ALARM 不写启动（安全底线）。
  - 派工循环：优先级出队→`DispatchTransitAsync`→绑定 taskId 回上下文；失败→Alarm。
- Communication（新增 `Simulation/CncMachineSimulator.cs`，`IHostedService,IPlcWriteHook`）：RCS 上料 COMPLETED（订阅 notifier）→置 HasMat=ON/AllowLoad=OFF；`OnTestStartWritten(1)`→延时 2s 置 PosOk=ON；RCS 下料 COMPLETED→置 HasMat=OFF/AllowLoad=ON/Ok=Ng=OFF；`OnTestStartWritten(2)`→复位 TestStart=0。`SkipMaterialArrival` 注入"RCS 报完成但工件未到位"。
- 修改：`PlcPollingService` 不再合成 PositionStatus（调度器为唯一权威，只负责信号采集+MachineStatus）；`ModbusTcpSimulator` 加 `ReadRegister`/`WriteRegister` 内部直读写；`RcsTaskService` 拆 `RedoAsync`→`BuildAndSendAsync`+`RedispatchAsync`，dispatch 方法返 `result with { TaskId }`。
- Common：`RcsOptions` 加 `SchedulerEnabled=true`/`SchedulerIntervalMs=500`。
- DI：`IDispatchQueue`/`IRouteResolver`/`PositionScheduler`(singleton+hosted+`IPositionScheduler`)/`CncMachineSimulator`(singleton+hosted+`IPlcWriteHook`)。
- 验证：`dotnet build` 0 警告 0 错误；harness 真 scheduler+Modbus sim+CncMachineSimulator+RcsSimulator+回调宿主+处理器+notifier+服务 + 假 store/log/alarms/points/location——**阶段1 ≥10 完整节拍**（WAIT_LOAD→DISPATCHING→TRANSPORTING→LOADED→PROCESSING→DONE_OK→DISPATCHING→TRANSPORTING→UNLOADED→WAIT_LOAD），**阶段2 注入 SkipMaterialArrival → 复核不过 ALARM 触发**；harness 已清理。
- 修复历程：① SetState 覆盖动作设置的 ctx.State 致双重入队（ExecuteActions 改为返回有效 next，SetState 用之）；② 复核读信号仓滞后致误判（改 fresh PLC 读 `ReadHasMatFreshAsync`）；③ NModbus master-write 与 sim 内部 ReadPoints 索引不一致致 CncMachineSimulator 读不到 TestStart（改用 `IPlcWriteHook` 回调，不依赖寄存器轮询）；④ ComputeNextState(Loaded) 直接返 Processing 致跳过 WriteTestStart（改为返 Loaded，由 ExecuteActions 写启动后转 Processing）。
- 待用户人工确认（需先连库运行 App + 录入 LOCATION_MAP 的 LOAD_AREA/UNLOAD_AREA/加工位 cell）：监控看板加工位状态自动跑节拍；断电重启后对账恢复；复核不过自动停下告警。
- 下一步（待确认）：步骤⑥ RCS 管理页五块补全 + 槽位账目 + identifyQR 盘点 + 水位监视+换架任务对 + NG 处理页 + 空托盘回收 + 料架页人工校正。

### Session 26 — 2026-07-07 第四阶段步骤⑥a：槽位账目（代码）— 暂停等用户确认
- 步骤⑥ 拆三个子步骤：⑥a 槽位账目（基础）/⑥b 水位+换架+空托盘/⑥c 盘点+NG+RCS页补全。本 session 完成 ⑥a。
- Core（新增 `Rcs/ISlotAccountService.cs`）：`ISlotAccountService`（ReserveAsync 选空槽预记/ConfirmAsync 落账/RollbackAsync 回滚/GetOccupancyAsync 占用统计/SetSlotAsync 人工校正/LocateElectrodeAsync 反查）+ DTO（`ReservedSlot`/`FrameOccupancy`/`SlotLocation`）+ `SlotStates` 常量（0空/1占用/2锁定/3预记）。
- Data（新增 `Repositories/SlotAccountService.cs`）：预记用 `SLOT_STATE='3'` + `REMARK=taskId` 跟踪（避免 DB schema 变更）；同架并发用 `ConcurrentDictionary<long, SemaphoreSlim>` 互斥，多加工位同时取料不会撞同一槽；落账后 `REMARK` 保留 taskId + `BindSource='RCS_CONFIRMED'` 供 redo 幂等判定（重复 Confirm 返回 true 不重复记账）；回滚仅对预记态生效，已落账不回滚。
- Core：`ConfigModels.SlotItem` 加 `SlotNo`/`SlotState`（槽位状态完整暴露，不再只有 Occupied bool）；`ConfigServices.GetDetailAsync` 投影同步。
- UI：`FrameViewModel` 注入 `ISlotAccountService` + `SelectSlotCommand`/`CorrectSlotCommand` + `SelectedSlot`/`CorrectElectrode`/`CorrectSlotState` 属性 + `SlotStateOptions`；`SlotVm` 加 `SlotNo`/`SlotState`/`Reserved`/`StateBadge`/`ElectrodeText`（占用/预记显示电极码、锁定显示锁）；`PageTemplates.xaml` 料架页槽位卡改 Button（可点击选中）+ 按 Occupied/Reserved 着色（占用 accent/预记 warn）+ 新增「人工校正」面板（选中槽→电极码+状态下拉→校正保存）。
- DI：`AddCncData` 注册 `ISlotAccountService → SlotAccountService` 单例。
- 验证：`dotnet build` 0 警告 0 错误；harness（EF Core InMemory + 真 SlotAccountService + 种子料架 5 槽）11 项全 PASS：Reserve 选首个空槽(槽3)/预记后 reserved=1/Confirm 落账/落账后 occupied=3/重复 Confirm 幂等/Reserve T2 选槽4/Rollback T2 回滚/回滚后 empty=2/并发 2 个 Reserve 各选不同槽/反查 EL-001 命中槽3/人工校正后反查 EL-MANUAL 命中；harness 已清理。
- 待用户人工确认 UI（需先连库）：料架页点槽位→人工校正电极码/状态保存；槽位卡按状态着色（占用蓝/预记橙）。
- 下一步（待确认）：步骤⑥b 水位监视器 + 换架任务对（先拉后送/回滚）+ 空托盘回收按钮。

### Session 27 — 2026-07-07 第四阶段步骤⑥b：换架任务对 + 空托盘回收（代码）— 暂停等用户确认
- Core（新增 `Rcs/IChangeFrameOrchestrator.cs`）：`IChangeFrameOrchestrator`（ChangeFrameAsync 发起换架/ProgressChanged 事件/GetActiveTransactions）+ DTO（`ChangeFrameProgressEvent`/`ChangeFrameStep`/`FrameRole` 枚举 0/1/2/3=Upload/Unload/Transit/NgFrame）；`IRcsTaskService` 加 `DispatchPalletReturnAsync`（transitTask + Kind=PalletReturn, priority=8，不建托盘账）。
- Communication（新增 `State/ChangeFrameOrchestrator.cs`）：先拉后送——取该角色绑定料架(`IEquipmentConfigService.GetFrameBindingIdsAsync`)→解析料架站点 cell + 缓存区 cell(`ILocationMapService.ResolveFrameAsync`/`ResolveAreaAsync` FULL_BUFFER/EMPTY_BUFFER)→生成 TXN_ID(`CF-yyyyMMddHHmmss-seq`)→下发第一发拉旧架(站点→缓存, priority=9, Kind=ChangeFrame, TxnId 绑定)→订阅 `notifier.TaskStatusReceived`：第一发 completed→下发第二发送新架(缓存→站点)；第一发 CANCELED 或 `IsRedoExhausted`(RedoCount>=MaxAutoRedo)→告警+工单(绑定不解除、原状保持)；第二发 CANCELED/redo 耗尽→告警+工单(锁定工序，人工送架后界面点"新架到位"再绑定，绑定不在本编排)；完成/Done 移除活动事务。活动事务内存 ConcurrentDictionary 跟踪。
- Common：`RcsOptions` 加 `WaterFullThreshold=2`/`FullBufferArea="FULL_BUFFER"`/`EmptyBufferArea="EMPTY_BUFFER"`/`PalletReturnArea="PALLET_RETURN"`。
- UI：`RcsViewModel` 注入 `IChangeFrameOrchestrator` + `ChangeFrameCommand`/`PalletReturnCommand` + `ChangeFrameEquipmentId`/`ChangeFrameRole`/`PalletReturnFromCode` 属性 + `ChangeFrameRoleOptions`；订阅 `ProgressChanged` 终端追加 `↻ 换架 {txn} {step} {state}` + Growl 完成/异常提示；`PageTemplates.xaml` RCS 页「连接&下发」加「换架/空托盘回收」面板（机台ID+角色下拉+换架按钮；点位+回收按钮）。
- DI：`AddCncCommunication` 注册 `IChangeFrameOrchestrator → ChangeFrameOrchestrator` 单例。
- 验证：`dotnet build` 0 警告 0 错误；harness 真 orchestrator+RcsTaskService + 假 store/equipment/location/alarms/client 7 项全 PASS：先拉后送 happy path（第一发下发→模拟 completed→第二发下发→completed→事务移除+无告警+两次 dispatch）；第二发失败（模拟 completed 第一发→第二发失败+RedoCount=3→IsRedoExhausted→工单告警+事务移除）；harness 已清理。
- 待用户人工确认 UI（需先连库 + 录入 LOCATION_MAP 的 FULL_BUFFER/EMPTY_BUFFER/PALLET_RETURN 区 + 料架站点 cell）：RCS 页填机台ID+角色点「换架」→任务列表出现两发 ChangeFrame 任务（TXN_ID 关联）→终端 `↻ 换架` 进度；空托盘回收填点位点「回收」→PalletReturn 任务下发。
- 下一步（待确认）：步骤⑥c identifyQR 盘点后台任务 + NG 处理页 + RCS 管理页五块补全 + 水位监视器自动触发。

### Session 28 — 2026-07-07 第四阶段步骤⑥c：盘点后台任务 + 水位监视器 + RCS 页换架事务展示（代码）— 暂停等用户确认
- Core（新增 `Rcs/IInventoryService.cs`+`IWaterMonitorService.cs`）：`IInventoryService`（StartInventoryAsync/InventoryCompleted 事件/GetActiveInventories）+ `InventoryResultEvent`/`InventoryTaskInfo`；`IWaterMonitorService`（CheckAsync/WaterLevelChanged 事件）+ `WaterLevelEvent`；`ISlotAccountService` 加 `CorrectFromInventoryAsync`（按 SlotNo 顺序从 posStart 起的 products 全量校正电极码 + 范围内空码清槽 + 所有槽位 LAST_VERIFY_TIME=now）+ `GetSlotsAsync` + `SlotRecord`。
- Communication（新增 `State/InventoryService.cs`+`WaterMonitorService.cs`）：`InventoryService`——`StartInventoryAsync` 解析料架 station→`DispatchIdentifyAsync`→活动表跟踪→订阅 `notifier.ScanResultReceived`：error_code=0 → `CorrectFromInventoryAsync` 全量校正→`InventoryCompleted(COMPLETED, 校正数)`；error_code!=0 → FAILED；`notifier.TaskStatusReceived` 兜底 FAILED/CANCELED。`WaterMonitorService`（HostedService，周期 `SchedulerIntervalMs*4`）：遍历料架 `GetOccupancyAsync`，下料架接近满（剩余空槽≤WaterFullThreshold）/上料架空（empty=total）→ 调 `IChangeFrameOrchestrator.ChangeFrameAsync` 自动换架；已有该角色换架进行中（GetActiveTransactions 或 _inFlight 去重）跳过。
- UI：`FrameViewModel` 注入 `IInventoryService` + `StartInventoryCommand` + `InventoryPosStart`/`InventoryCount` + 订阅 `InventoryCompleted`（Dispatcher Invoke Growl + 刷新料架详情）；料架页模板加「发起盘点」面板。`RcsViewModel` 加 `ChangeFrameTransactions` ObservableCollection + `RefreshChangeFrameTransactionsAsync`（ProgressChanged 时刷新）；RCS 页「换架/回收」面板下加「进行中的换架事务」DataGrid。
- DI：`AddCncCommunication` 注册 `IInventoryService → InventoryService` 单例 + `WaterMonitorService`（HostedService + `IWaterMonitorService`）。
- 验证：`dotnet build` 0 警告 0 错误；harness 真 InventoryService+notifier + 假 taskSvc/slots/locMap/alarms 7 项全 PASS：发起盘点返回 taskId+DispatchIdentify 被调(Station=FRAME-1,Count=3)+活动列入+回调后 CorrectFromInventory 被调(frameId=1,products=3)+InventoryCompleted COMPLETED 校正数 3+活动移除+失败回调(error_code=1)→FAILED；harness 已清理。
- **简化/待办**：① 水位监视器 `FindBindingAsync` 料架→机台角色反查为占位（返回固定 (1,Unload)），需 `IEquipmentConfigService` 加 `GetBindingByFrameAsync(frameId)→(equipmentId,role)`，步骤⑦或现场补；② NG 处理页未单独建——料架页人工校正「置空(0)」即释放 NG 架槽位，覆盖 §6.2 NG 闭环的"释放槽位"动作，工件记录归档依赖步骤⑦ WORK_RECORD；③ 盘点互斥（发起前检查队列无待处理搬运）未做，依赖调度器队列查询接口（步骤⑦/现场）。
- 待用户人工确认 UI（需先连库 + 录入料架 station 点位）：料架页选料架→填起始孔位+数量→「发起盘点」→任务列表出 identifyQR 任务→模拟器回推 scanTaskStatus 后槽位自动校正 + Growl 提示；RCS 页换架事务列表实时更新。
- 下一步（待确认）：步骤⑦ 加工记录写 `MAS_AUTO_WORK_RECORD`（关联任务/工件/加工位/耗时）+ 监控看板联动。

### Session 29 — 2026-07-07 第四阶段步骤⑦：加工记录 + 监控看板联动（代码）— Phase 4 完成
- Core（新增 `Rcs/IWorkRecordService.cs`）：`IWorkRecordService`（RecordStartAsync/RecordResultAsync/FindOpenByPositionAsync/GetRecentAsync/GetShiftStatsAsync）+ DTO（`WorkRecordStartArgs`/`WorkRecordRow`/`WorkShiftStats`）。
- Data（新增 `Repositories/WorkRecordService.cs`）：写 `MAS_AUTO_WORK_RECORD`——RecordStart 时同加工位若有未结束记录（WORK_RESULT 空）→ 自动补结为异常(2)防重启残留；RecordResult 写结果(0=OK/1=NG)+WORK_END_TIME+耗时由 start/end 算；REMARK 存 `rcsTask={taskId}` 溯源；当班统计按今天 WORK_START_DATE 计 OK/NG/总数。
- Communication：`PositionScheduler` 注入 `IWorkRecordService`——ExecuteActions `Loaded` 案（写 POS_TEST_START=1 后）调 `RecordStartAsync`（关联 ctx.CurrentTaskId 上料 taskId，存 ctx.WorkRecordId）；`DoneOk`/`DoneNg` 案调 `RecordResultAsync("0"/"1")`。`PositionContext` 加 `WorkRecordId`。
- UI：`DashboardViewModel`（独立文件，替换 PageViewModels 占位）注入 `ISignalStateStore`+`IWorkRecordService`+`IAlarmEventService`，订阅 `PositionChanged`/`MachineChanged`——`Positions` ObservableCollection<PositionCardVm>（EquipmentText/PositionText/StateDisplay/StateBadge）+ `RecentRecords` ObservableCollection<WorkRecordRow> + `OkCount`/`NgCount`/`TotalCount`/`AlarmCount`/`OnlineMachines`；`RefreshCommand`。`PageTemplates` 监控看板模板从静态骨架改为实时绑定：4 统计卡（在线机台/当班OK/当班NG/未处理告警）+ 加工位 ItemsControl（状态徽标 Ellipse + 中文态）+ 最近加工记录 DataGrid（机台/工位/电极/结果/耗时/开始）。新增 `StateBadgeToBrushConverter`（offline/alarm/run/ok/ng/idle → 对应 Brush）。
- DI：`AddCncData` 注册 `IWorkRecordService → WorkRecordService` 单例。
- 验证：`dotnet build` 0 警告 0 错误；harness 真 WorkRecordService + EF InMemory 5 项全 PASS：RecordStart 返回主键+FindOpenByPosition 命中进行中+RecordResult 写 OK+耗时+当班统计 OK=2 NG=1 总数=3+重启残留自动补结异常(2)；harness 已清理。
- 待用户人工确认 UI（需先连库 + 跑节拍）：监控看板加工位卡片状态随节拍变化（WAIT_LOAD→DISPATCHING→...→PROCESSING→DONE_OK），当班 OK/NG 计数递增，最近加工记录列表刷新。
- **Phase 4 全部 7 步完成**（①通信层 ②回调宿主 ③模拟器 ④任务跟踪器 ⑤状态机改造 ⑥槽位账目/换架/盘点 ⑦加工记录/看板）。下一步 Phase 5（外设测试页，已完成）→ Phase 6（现场联调）。

### Session 30 — 2026-07-07 第四阶段代码复审与安全语义修复（代码）
- 起因：用户要求复审 Phase 4 实际代码（非文档）找出偏差与 bug。通读核心代码 + 构建验证后，修 5 处（安全相关优先）：
- **bug#1 Alarm 不粘滞（安全底线失效）**：`PositionScheduler.ComputeNextState` 的 `case Alarm` 原 `return WaitLoad`（一 tick 就自动离开 Alarm），改为 `return Alarm` 粘滞——只能经 `ResetAlarmAsync` 人工恢复退出。`ExecuteActionsAsync` 结尾报警标记重置由 `next==WaitLoad||Alarm` 收窄为仅 `next==WaitLoad`（Alarm 期间保持标记，不每 tick 重复告警）。`ResetAlarmAsync` 补 `SetState(WaitLoad)` 刷看板 + 清 `AlarmRaised`。
- **bug#2 派工失败重试风暴**：`DispatchOneAsync` 失败分支原 `ctx.State = Alarm`（不刷看板 + 下一 tick 因 bug#1 回 WaitLoad 重新入队 → 500ms 节拍疯狂重发/告警），改为 `SetState(ctx, Alarm)` + `AlarmRaised=true`，配合 bug#1 粘滞收敛。
- **bug#3 水位监视器误触发（生产危险）**：`WaterMonitorService.FindBindingAsync` 原写死返回 `(1, FrameRole.Unload)`——种子空料架启动即误触发对机台1下料架换架（接真机会真指挥 AGV）。改为拿不到真实绑定 `return null`（跳过）。`RcsOptions` 加 `WaterMonitorEnabled`（默认 false）+ `StartAsync` 守卫；`appsettings.json` Rcs 节显式加 `"WaterMonitorEnabled": false`。绑定精确反查（IEquipmentConfigService.GetBindingByFrameAsync）仍留现场。
- **bug#4 CncMachineSimulator 无守卫**（与 findings 声称"生产不注册"不符，实为无条件注册 HostedService+IPlcWriteHook）：注入 `IOptions<AppOptions>`，`StartAsync` 加 `Plc.UseSimulator=false` 直接返回（不装载/不订阅/不起循环；behaviors 空则 IPlcWriteHook 回调自然 no-op）。
- **bug#5 加工记录串位**：`WorkRecordService.FindOpenByPositionAsync` 原只按 EquipmentId 过滤（双工位机台串位），加 `PositionCode == "POS-{positionId}"` 精确匹配。注：PositionCode 目前是调度器合成串，真实 POSITION_CODE 载入留现场。
- 验证：`dotnet build` 0 警告 0 错误。运行态节拍（Alarm 粘滞/失败停下/模拟器不启动）需人工跑 App 确认。
- **未动（待用户明确）**：① appsettings 明文库口令（命中密钥自主边界）；② bug#6 线体硬编码 `WorkLineId=1/"LINE"`；③ bug#7 调度器 ctx 跨线程竞态（需加锁/plumbing）。

### Session 31 — 2026-07-07 第四阶段 bug#6/#7 修复（代码）
- **bug#6 线体硬编码**：Core `IEquipmentConfigService` 加 `GetWorkLineByEquipmentAsync(equipmentId)→WorkLineRef?`（机台→工序→线体反查）+ `WorkLineRef(WorkLineId, LineCode)` 记录；Data `EquipmentConfigService` 实装（Equipment.CraftworkId→Craftwork.WorkLineId→WorkLineConfig.WorkLineCode）。`PositionScheduler` 注入 `IEquipmentConfigService` + `_lineCache`（机台→线体缓存），`EnqueueUpload/UnloadAsync` 用 `ResolveLineAsync` 替换写死 `WorkLineId=1/"LINE"`（反查失败回退 (1,"LINE") 防 NPE）。`ChangeFrameOrchestrator` 同步改（ChangeFrameAsync 起始解析线体存入 ctx，两发共用）。InventoryService 仅有 frameId 无 equipmentId，暂留（同料架→机台反查缺口）。
- **bug#7 ctx 跨线程竞态**：`PositionScheduler` 加 `_posGates`（每加工位一把 SemaphoreSlim）。主循环 `DrivePositionAsync` 拆出 `DrivePositionCoreAsync` 在 gate 内执行；派工回填 `DispatchOneAsync` 的 ctx 写入块（网络下发在锁外、仅结果写入在锁内）与 `ResetAlarmAsync`（UI 线程）均取同一 gate，串行化对单个 ctx 的读写；不同加工位仍并行。
- 验证：`dotnet build` 0 警告 0 错误；ReadLints 无错。运行态需人工跑 App 确认（taskId 前缀为真实线体 code、双工位并发无错乱）。
- **仍未动**：appsettings 明文库口令（命中密钥自主边界，待用户明确处理方式）。

### Session 32 — 2026-07-07 新增日志/告警页 + 看板性能优化 + 告警角标修复（代码）
- **新增「日志/告警」页**（导航 key=`log`，分组"运行"，原型未含、缺口补齐）：一页两 tab。
  - 告警明细：`IAlarmEventService` 加 `GetAlarmsAsync(unhandledOnly,limit)`/`MarkHandledAsync(id,author)`/`DeleteAllAsync()`（物理删除 ExecuteDeleteAsync）/`GetUnhandledCountAsync()`；`AlarmRow` 加 `AlarmType`（4 处构造 + ToRow 统一）。UI：`LogViewModel` 列 时间/级别/类型/消息/状态 + "只看未处理"筛选 + 条数下拉(默认50) + 刷新 + "标记已处理" + "全部删除"(二次确认物理删) + 订阅 `AlarmRaised` 实时追加。
  - 应用日志：新增 `ILogFileReader`/`LogFileReader`（Common，读最新 `logs/cncloader-*.log` 尾部、级别筛选、异常续行并入上一条、FileShare.ReadWrite 不抢 Serilog 文件锁）；条数下拉默认 50。Common DI 注册。
  - 页面接入：UI DI 注册 `LogViewModel`；`ShellViewModel` 导航加项；`PageTemplates.xaml` 加 TabControl DataTemplate。
- **监控看板性能优化**（用户反馈卡顿）：`DashboardViewModel` 原每次状态变化全量 `Positions.Clear()`+重建 + 同步 `Dispatcher.Invoke` + 后台常驻刷新 → 改为 200ms `DispatcherTimer` 节流（UI 线程合并刷新，无跨线程同步 Invoke）+ 卡片**原地增量更新**（`PositionCardVm` 的 State/PlcOnline/Safe/DoorOpen 改为可观察属性，StateDisplay/StateBadge 联动）+ 最近记录改 `BeginInvoke`。
- **顶部告警角标修复**：`ShellViewModel.UnhandledAlarms` 原声明未赋值恒为 0 → 注入 `IAlarmEventService`，启动加载一次 + `OnTick` 每 5s 刷新 `GetUnhandledCountAsync`（覆盖告警产生/标记已处理/清空各来源，UI 线程统一刷新）。
- 验证：`dotnet build` 0 警告 0 错误；ReadLints 无错。运行态（页面渲染/卡顿缓解/角标计数/全部删除二次确认）需人工跑 App 确认。
- 备注：应用日志仍是文件只读展示，不入库；「全部删除」为物理硬删不可恢复。

### Session 33 — 2026-07-07 演示实操手册 + 端到端跑通节拍时发现并修复 3 处真 bug（代码 + 文档）
- 起因：用户要演示实操手册并首次真机跑闭环。写好 `docs/演示实操手册.md`（零真机、纯模拟器）+ 一键种子脚本 `docs/sql/demo_seed_location_map.sql`（幂等，全 5 加工位 cell + 上下料区 + 缓存/回收区 + 料架 shelf），已执行入库校验 13 条。跑 App 时节拍停在 WaitLoad 不动，排查出 3 处「从没端到端跑过节拍」才暴露的 bug：
- **bug#8 CncMachineSimulator 只驱动 Modbus 模拟器**：现场 PLC 走 FINS（DB 里 3 台 PLC_READ_WAY=FINS），App 起的是 FINS 模拟器；但机台行为模拟器只写 `ModbusTcpSimulator` 的寄存器存储，两个模拟器各自独立 → 调度器经 FINS 客户端读不到「允许上料/工件到位」。修：抽 `ISimulatorRegisterStore`（WriteRegister）接口，`ModbusTcpSimulator`/`OmronFinsUdpSimulator` 均实现（FINS 补 WriteRegister 写 DM 字存储）；`CncMachineSimulator` 依赖 `IEnumerable<ISimulatorRegisterStore>`，写入向所有模拟器广播（只对登记了该 PLC 的模拟器生效）；DI 把两模拟器注册为 `ISimulatorRegisterStore`。
- **bug#9 初始 AllowLoad=ON 写入被启动时序丢弃**：`CncMachineSimulator`（HostedService）在 `Host.StartAsync` 阶段写初始 AllowLoad=ON，但 `PlcRuntimeBootstrapper.AddPlc` 在窗口显示后才登记 PLC，早于此的写入全 no-op 丢失，随后种子把 AllowLoad 置 OFF → 永远等待上料。修：`CncMachineSimulator` 内部维护 `PositionBehavior.HasMaterial`（上料完成置 true / 下料完成置 false），循环里对「空闲加工位(无料且非检测中)」每拍幂等重置 AllowLoad=ON，不受时序影响；有料/检测中不触碰，交回调驱动。
- **bug#10 持续轮询从未启动**：`PlcPollingService.StartAsync`（持续循环）无人调用，App 仅在自检调 `PollOnceAsync` 一次 → 信号仓只填一次即陈旧，调度器读不到实时信号。修：`PlcPollingService` 实现 `IHostedService`（其 Start/Stop 签名天然符合，内部 Task.Run 立即返回不阻塞启动），DI 注册 `AddHostedService`。
- 验证：`dotnet build` 0 警告 0 错误；启动 App 后端到端闭环跑通——EQ1-POS2 与 EQ2-POS3 并行，多轮完整节拍（WaitLoad→Dispatching→Transporting→Loaded→Processing→PosOk→Dispatching→Transporting→Unloaded→WaitLoad），taskId 前缀真实线体码 LINE01（bug#6 修复生效），全程无 ALARM。FINS 与 Modbus 两协议节拍均通。
- 文档：`docs/演示实操手册.md` 修正 §1.2 PLC 表名（MAS_AUTO_PLC→MAS_AUTO_WORKLINE_PLC）+ 端口/协议说明（FINS 也可跑）。
- 待办：新增文件（手册、种子脚本、ISimulatorRegisterStore）与既有未提交文件均待 commit（需用户确认）；水位监视器料架→机台绑定反查仍留现场（bug#3 已安全化）。

### Session 34 — 2026-07-07 手动调试开关 + RCS 轮询事件显示码修正（代码）
- **新增 `Plc.PollingEnabled` 开关**（`AppOptions`+`PlcPollingService` 托管启动守卫+DI 传参）：false 时不启动持续轮询循环（信号仓不自动刷新），供手动单步调试；自检单轮读、PLC 页手动读不受影响。`appsettings.json` 演示态设 false。改配置无需重编、重启即生效。
- **修 RCS 轮询事件假错误码**：`RcsTaskTracker.ErrorCodeFrom` 原对非完成/非取消的所有态（含进行中 EXECUTING）返回 Error(1)，致 RCS 页终端显示 `↩ poll ... error_code=1 → EXECUTING` 误导为失败。改为仅 FAILED→1、Canceled→9、其余→0（Success）。纯展示修正，不影响自动 redo（redo 只看 TaskState==FAILED）。
- 排障提醒（记入现场文档）：`SimulatorFailureRate`/`SimulatorCancelRate`/`SimulatorMinDelayMs` 等模拟器项在 RcsSimulator 构造时读取、**不热加载**，改后必须重启 App；启动日志「失败率 N % 取消率 N %」为准。
- 验证：`dotnet build` 0 警告 0 错误。运行态（失败率 100% → FAILED→自动 redo→RCS_REDO_LIMIT 告警）待用户重启后确认。

### Session 35 — 2026-07-07 修 RcsSimulator queryTask 把失败任务误报 completed（代码）
- 起因：用户 SimulatorFailureRate=1.0 下发搬运，任务先 FAILED（首次回调）触发自动 redo，但随后 poll 把它推成 EXECUTING→COMPLETED，redo/告警链被打断。查日志坐实：redo 重发的二次失败回调因 `push:{taskId}:{error_code}` 幂等去重被吞，且 `queryTask` 把该任务报成 completed → poll 以 queryTask 为准覆盖 FAILED。
- 根因：`RcsSimulator.ScheduleCallback` 回推后 `_tasks.TryRemove(taskId)` 删除任务；`queryTask` 对"找不到的任务"一律返回 `completed`（"已回推完成移除→视为completed"）。于是**任何**回推过的任务（含失败）被删后都被 queryTask 报 completed。
- 修：`SimTask` 加 `FinalErrorCode`（-1=未回推）；回推 finally 不再删除、改记 `FinalErrorCode=errorCode`（redo 重发同 taskId 由新 SimTask 覆盖重置）；`queryTask` 按真实终态返回——Canceled→canceled / FinalErrorCode<0→underway / 0→completed / 9→canceled / 其它(1)→failed；未知任务仍兜底 completed。这样 poll 会看到 failed→FAILED→（poll 源事件驱动）继续自动 redo→REDO_COUNT 到 MaxAutoRedo(3)→RaiseRcsRedoLimit 告警（RCS_REDO_LIMIT）。回调侧 (taskId,error_code) 去重仍在，但设计上"poll 以 queryTask 为准"，poll 修正后链路正常，故未改去重（真机 queryTask 报真实态，同样成立）。
- 顺带修显示：`RcsTaskTracker.ErrorCodeFrom` 非终态不再假返回 Error(1)（Session 34）。
- 验证：`dotnet build` 0 警告 0 错误；启动 App（失败率 100%、轮询关）就绪，待 UI 手动下发搬运确认 FAILED→redo×3→RCS_REDO_LIMIT 告警全链（UI 点击项，代码无法代触发）。

### Session 36 — 2026-07-07 修 RCS 页调用终端撑高页面（UI）
- 起因：自动闭环下 RCS 页「调用终端」越跑越长、整页底部被拉长。
- 根因：`PageTemplates.xaml` RCS 页调用终端 Border 无高度约束（对比 AGV 页终端有 `Height=160`），TerminalLines 虽代码限 200 行但无高度上限，ScrollViewer 不生效，200 行全渲染撑高页面。
- 修：调用终端 Border 加 `VerticalAlignment=Top` + `MaxHeight=440`，ScrollViewer 内部滚动（AutoScroll.ToEnd 自动滚底）。
- **全局根因**：`ShellWindow.xaml` 页面宿主是 `ScrollViewer`>`ContentControl`，给页面无限高，导致所有 `Height="*"`/DockPanel 填充的列表拿到无限高、把所有行渲染出来撑高整页（内部滚动失效）。开发时已对多数列表加 `MaxHeight`（扫码220/换架120/机台220/工序260/料架200/PLC列表240/点位360/PLC操作流水200/AGV终端160）兜底，但漏了 7 处会持续增长的。
- 全量补齐 `MaxHeight`+`VerticalAlignment=Top`：RCS 任务(360)/报文(360)/位置映射(360)、日志-告警(460)/应用日志(460)、PLC 读结果(320)、看板最近记录(320)。保留页面级 ScrollViewer 作整体兜底（与既有做法一致，不动宿主避免影响表单页）。
- 验证：`dotnet build` 0 警告 0 错误。运行态待重启确认。

### Session 37 — 2026-07-07 RCS 列表填满贴底 + 页面按视口限高（UI）
- 起因：用户反馈 RCS 页任务列表/报文流水/位置映射「有点矮、要跟底部对齐」（Session 36 加的 MaxHeight=360 太矮且顶对齐）。
- 关键认识：宿主 `ScrollViewer` 以无限高测量内容，去掉 MaxHeight 列表又会撑长页面；「填满视口+贴底+内部滚动」必须把页面**限定为视口高度**。
- 修：`ShellWindow.xaml` 页面宿主 `ContentControl` 由无约束改为 `Height={Binding ViewportHeight, ElementName=PageHost}`（精确等于视口）——页内 `Grid *` 行/DockPanel 填充列表得以填满并内部滚动、贴底，整页不再被长列表撑高（全屏 HMI，表单页内容均短于视口）。
- 去掉 RCS 任务/报文/位置映射 三处 Session 36 加的 MaxHeight（改回填充）。其余页（日志/PLC读/看板）保持 Session 36 的 MaxHeight（用户未要求改，功能正常，无回归；如需一致填满可后续同法处理）。
- 验证：`dotnet build` 0 警告 0 错误；启动 App 渲染正常、无 XamlParse/未处理异常、双工位节拍照常。

### Session 38 — 2026-07-07 全页面布局统一：填满贴底自适应（UI）
- 需求：用户要求所有页面统一「跟底部对齐、自适应填满」。
- 基础：Session 37 已把页面宿主限定为视口高度（`ContentControl.Height=ViewportHeight`），页内 `Grid *`/DockPanel 填充列表得以填满并内部滚动。
- 逐页处理（`PageTemplates.xaml`）：
  - AGV/扫码枪：主面板去 `VerticalAlignment=Top`（撑满贴底）；终端/最近扫码表改为 DockPanel 填充子（去 `Dock=Top`/固定 `Height`/`MaxHeight`），终端加 AutoScroll。
  - 日志/告警（告警明细/应用日志）、PLC 读结果、看板最近记录：去掉 Session 36 加的 `MaxHeight` → DataGrid 填充+内部滚动。
  - 工序页：行定义由 `[Auto,Auto,*(空),Auto]` 改为 `[*(列表),Auto(表单),Auto(状态)]`，列表去 `MaxHeight` 填满，状态条移到 Row2。
  - 机台页：加工位/关联料架详情区 Grid 及两 Border 去 `VerticalAlignment=Top` → 详情区填充贴底。
  - 料架页：槽位区 Border 去 `VerticalAlignment=Top` 填满；槽位内容（层+人工校正+发起盘点）包 `ScrollViewer` 防裁切。
  - 点位映射页：行定义中间行由 `Auto` 改 `*`、主表去 `MaxHeight` → 主表填满内部滚动。
  - RCS 页（Session 37 已改）：三列表填充、左栏表单包 ScrollViewer。
  - PLC 页：主区 Row1(*) TabControl 本就填充；读结果表已填充；写操作 tab 为表单（保持）。
- 保留的既有 `MaxHeight`：换架事务(120)、PLC 列表(240)、机台/料架/工序等主列表按 master 列表定位处（视觉需要）——未强改。
- 验证：`dotnet build` 0 警告 0 错误；启动渲染正常、无 XamlParse/未处理异常、3/3 在线、节拍照常。逐页观感待用户确认（无法代看）。

## 上线缺口补齐（7 项，本次）

按批准计划 `补齐上位机上线缺口` 完成 7 个上线前缺口，全部 `dotnet build` 0 警告 0 错误。

- **Step1 绑定反查**：`IEquipmentConfigService` 加 `GetBindingByFrameAsync`/`GetFrameBindingByRoleAsync`/`GetNextProcessEquipmentsAsync`（Data 实现，FRAME_ROLE "0/1/2/3"↔`FrameRole`）；`WaterMonitorService.FindBindingAsync` 改真实反查（空架偏上料角色、满架偏非上料），删占位 TODO。
- **Step2 上料前校验**：`EnqueueUploadAsync` 返回三态（Queued/WaitMaterial/Failed），上料架账面 `occupied=0` → 保持 WaitLoad 等料（水位/人工补），不误告警。
- **Step3 槽位账双向**：`ISlotAccountService` 加 `ReserveTake/ConfirmTake/RollbackTake`（占用→预记→空/回占用）+ `RollbackStaleReservationsAsync`；`ReservedSlot` 带 electrodeId；`BIND_SOURCE` 记 `RESERVE_PUT/RESERVE_TAKE`。调度器：上料 LOADED→ConfirmTake、下料 UNLOADED→Confirm、Alarm 按 phase 回滚；下发成功即按方向预记（`ApplySlotReservationAsync`）。
- **Step4 OK/NG 全量分流**：`DispatchItem` 加 `UnloadTarget`+目标料架/机台工位/电极；`IRouteResolver` 加 `ResolvePositionCellAsync`/`ResolveFrameCellAsync`；`ResolveUnloadTargetAsync`——NG→NG架(role3)，OK→下一工序空闲工位直接交接（`_expectedInbound` 登记，下游见料走 Loaded；上料触发已排除待交接工位避免抢占）/下一工序全忙→中转架(role2)/末道→下料架(role1，回退 UNLOAD_AREA)。上料源优先中转架回流(`FindIdlePositionAmong` 判空闲)。
- **Step5 定期盘点**：`InventorySchedulerService`（HostedService，RCS 空闲=队列空+无在盘点+无换架事务时逐架 identifyQR，串行等完成/超时8min）；`RcsOptions.InventoryAutoEnabled`(默认关)/`InventoryIntervalMinutes`(60)；DI 注册。
- **Step6 重启三方对账**：`ReconcileAsync` ①RCS 未完结绑定 ②`RollbackStaleReservationsAsync(unfinished)` 按方向回滚陈旧预记 ③逐工位 fresh 读 HasMat，有料无任务无待交接→ALARM+`RaiseRcsTaskNotFound`（三方对完账才开闸）。
- **Step7 模拟器/验证**：`CncMachineSimulator` 下料完成时若 `ToCode` 经 `ILocationMapService.ResolveByRcsCodeAsync` 映射到某工位→目标 HasMat=ON（演示工序间交接闭环）。临时 harness（EF InMemory，Data+Config 服务真实实例）18 项全 PASS（绑定反查/下一工序/取放双向/陈旧预记按方向回滚/active 不回滚），跑通已删。
- **未做运行态端到端**：调度器路由/交接/盘点定时/PLC 对账需真实 `LOCATION_MAP`（加工位/料架 cell）+ `FRAME_BIND`（中转 role2/NG role3）种子才能跑通，当前种子仅 role0/1，属 Phase 6 现场；`WaterMonitorEnabled`/`InventoryAutoEnabled` 默认关，由运维开启。

## 业务流程测试（本次）

- **自动化端到端 harness（EF InMemory + 真 PositionScheduler/队列/路由/槽位账/配置/加工记录/告警 + 可控假 RCS/PLC 模拟机台节拍）**：13 项断言全 PASS，跑通已删。覆盖：
  - S1 上料架空 → 不下发、保持 WaitLoad；S2 OK → 下一工序空闲工位直接交接 + 上料取料落账(F1 3→2) + 下游接收；S3 NG → NG架 cell + 入库落账；S4 下游忙 → 中转架 cell；S5 中转架有件 → 下游回流取件(from=中转架)；S6 重启三方对账（TAKE 陈旧预记→占用、PUT→空、PLC 有料无任务→ALARM+告警）。
- **测试中发现并修复的逻辑缺口**：`PositionScheduler.EnqueueUploadAsync` 原对「无上料架(role0)绑定」的机台会回退从 LOAD_AREA 自取原料——纯下游机台（只有中转/下料架）会误自取。改为：无 role0 绑定 → 返回 WaitMaterial（只接收上游交接/中转回流）。`dotnet build` 0/0。
- **手动集成测试支撑**：`docs/sql/demo_seed_location_map.sql`（基础闭环路由）+ 新增 `docs/sql/test_routing_seed.sql`（叠加：双工序 + NG架(role3)/中转架(role2) 绑定 + 料架 cell，含 CLEANUP 回滚段）。已对本机 cnc_auto 只读核对：表名/列名/ID(91,92空闲)/NOT NULL/自增/唯一键/RCS_CODE 无冲突全部匹配，可直接跑。跑 App（双模拟器全开）后在监控看板/日志/`MAS_AUTO_AGV_TASK`/`FRAME_SLOT` 观察各流程。
- **模拟器 NG 率**：`CncMachineSimulator` 加 `RcsOptions.SimulatorNgRate`（默认 0=全 OK），检测按概率出 `POS_NG=ON` → 走 NG→NG架 分流，使 App 里可稳定演示 NG 流程；appsettings 加 `SimulatorNgRate: 0.0`。焦点 harness 4 项 PASS（NgRate=1.0→PosNg、=0.0→PosOk），跑通已删。`dotnet build` 0/0。
- **真机联跑修复的代码 bug（重要）**：`SlotAccountService` 的 `BIND_SOURCE` 值 `RESERVE_PUT`(11)/`RESERVE_TAKE`(12)/`RCS_CONFIRMED`(13) 超过列宽 `BIND_SOURCE VARCHAR(10)`，真 MySQL 写槽位账时抛 `DbUpdateException`（EF InMemory 不校验列宽，故 harness 未暴露）。改短为 `RSV_PUT`/`RSV_TAKE`/`CONFIRMED`（均 ≤10）。
- **App 真机联跑观察排障（本机）**：观察不到流程的叠加原因 = ①多僵尸实例占 RCS 端口 8090/9080；②bin 陈旧 appsettings 取消率 30% 致频繁 ALARM；③上述 BIND_SOURCE 超长；④数据：`POS_HAS_MAT`/EQ1-POS1 点位被误设 `RW=1`（应为读 0）致 hasMat 不轮询、POS1 不触发（已 UPDATE 修回）；⑤上料架 F1 被前一实例吃空、空架保护挡上料（已补料）。修完实测 45s：Dispatching/Transporting/COMPLETED 持续、PosOk/PosNg 均出、F1 取料落账、NG架/中转架入库、当班 OK/NG 记录齐全。运行约束：只跑单实例（端口独占）；F1 ~10 轮耗尽需补料或启水位换架。

### Session 39 — 2026-07-08 演示手册同步 + 三道串行产线 + 料架页增强 + 一批真 bug 修复（代码 + SQL + 文档）
> 本 session 全程「带用户在本机模拟器上跑通并熟悉全部功能」，边跑边发现问题边修。所有改动 `dotnet build` 0 警告 0 错误；App 单实例本机实跑验证（FINS 3 PLC）。

**A. 演示手册同步到代码现状**（`docs/演示实操手册.md`，之前停在 Session 33）：
- §1.3 配置补全 `SimulatorNgRate`(0.3)/`PollingEnabled`/`SchedulerEnabled`/`InventoryAutoEnabled`；注明模拟器四项不热加载、改后必重启。
- §4.1 增 `test_routing_seed.sql`/`three_stage_line_seed.sql` 说明；§4.2 下料改为完整分流表（交接/中转/NG/末道下料）；§4.3 异常清单对齐第二层四项；§5 水位反查纠正（已实装）；§2.3 协议改为跟随 `PLC_READ_WAY`。

**B. 三道串行产线种子**（新增 `docs/sql/three_stage_line_seed.sql`，幂等可重复=重置演示态）：
- 起因：用户确认真实业务是「内长宽(EQ01)→平面度(EQ02)→A基准(EQ03) 依次串行」，每道 OK 看下游有空位则直接交接/无则进该台中转架等位，NG→NG架人工处理。原 test_routing 只串了 EQ01→EQ03 两道、EQ02 孤立。
- 内容：工序 node 1/2/3（craft1 内长宽 / 新建 craft92 平面度 / craft91 改 A基准 node3）；绑定 frame1→EQ1 上料、frame2→EQ2 中转、frame92→EQ3 中转、frame3→EQ3 下料、frame91→EQ1/EQ2/EQ3 共用 NG；补 frame2 cell(653002)；frame1 补满 10 电极；**下料/NG/两个中转架统一重建为 12 空槽**（避免一轮溢出，见 F）。
- 实跑验证：件流经三道全路径（直接交接 NextMachineCell / 中转架 TransitFrame / NG架 NgFrame / 末道下料 DownloadFrame），电极码全程可追。

**C. 机台安全信号（现场决策：保留信号、测试期置安全）**：
- 现象：EQ03 保留「机台安全」点位（Session 17 只删了 EQ01/EQ02），模拟器从不驱动机台级信号→读默认 OFF→EQ03 静默卡 Offline/Alarm，件堆在其中转架不被取。
- 修：`CncMachineSimulator` 捕获机台级 `MACHINE_SAFE/DOOR` 点位（PositionId=null），启动+每拍幂等置「安全=ON/门=关」（`MachineBehavior`/`WriteMachineSafety`）；避免 Session 33 时序坑。
- 修 `PositionScheduler`：机台在 **Offline（启动未就绪）** 态读到不安全→保持 Offline 等就绪，不 latch 粘滞告警；仅**运行中**安全掉线才 Alarm（安全底线不变）。
- 曾误删 EQ03 机台安全（`migration_2026-07-08_remove_eq3_machinesafe.sql`），后按用户「保留信号、测试期置安全」**已回滚**（该迁移脚本保留在库未采用，可删）。

**D. 料架页绑定机台 CRUD**（`FrameViewModel`/`ConfigModels.FrameBindRow`/`IConfigServices`/`ConfigServices`/`PageTemplates`）：
- `FrameBindRow` 扩 `BindId/EquipmentId/RoleCode`；`IFrameService` 加 `GetEquipmentOptionsAsync/BindEquipmentAsync(一机一角色一料架,替换旧)/UnbindAsync`；修 `GetDetailAsync` 角色文本原只映射上料/下料→补全中转/NG。
- 绑定面板加「机台+角色(四种)+绑定」表单 + 每行「解绑」（二次确认）。

**E. 料架页动态刷新**（修「显示不正确/不刷新」）：
- 根因：`FrameViewModel` 只在切料架/手动操作后加载一次，不订阅槽位变化→线体跑时列表占用数/槽位停在旧值。
- 修：1.5s `DispatcherTimer` **原地刷新**（`FrameRowVm.Occupied`/`SlotVm.ElectrodeId/SlotState` 改可观察），不丢选中槽/校正框焦点。

**F. 电极账目两个真 bug（用户「为什么少几个电极」查出）**：
- **bug①满架静默丢件**：`SlotAccountService.ReserveAsync` 目标料架无空槽 `return null`，`ApplySlotReservationAsync` 忽略返回值→件不入账、不告警、不日志。修：满架→`LogWarning` + `RaiseRcsWarnAsync`「料架N已满,请人工换架/清架」，不静默丢。
- **bug②电极码没随件流转**：下料/NG架 `ELECTRODE_ID=NULL`、加工记录电极 null。修：`PositionContext` 加 `ElectrodeId`；上料取料时从 `ReserveTakeAsync` 捕获→下料 `DispatchItem.ElectrodeId`/交接 `InboundHandoff`/下游接收/`RecordStartAsync` 全链路传递，下料完成清空。
- 演示数据：NgRate=0.3 三道累积 NG≈66%，原 NG架/中转架各 4 槽会溢出→统一扩到 12。修后一轮 10 件账目守恒（落料架 + 在制/告警件 = 10），电极码全程可追。

**G. 告警人工恢复入口**（`DashboardViewModel`/`PageTemplates` 看板）：
- 之前 `IPositionScheduler.ResetAlarmAsync` 未接 UI。加：看板注入 `IPositionScheduler` + `ResetAlarmCommand`；`PositionCardVm.IsAlarm`；告警工位卡片显示红色「恢复」按钮（仅告警态可见）→二次确认→`ResetAlarmAsync`→回 WaitLoad。

**H. 料架编辑/删除**（`ConfigModels.FrameEditModel`/`IFrameService`/`ConfigServices`/`FrameEditDialog`/`FrameViewModel`/`PageTemplates`）：
- `GetFrameForEditAsync/UpdateFrameAsync(改层数/每层槽数则重建空槽——有非空槽位拒绝)/CheckDeleteFrameAsync(被绑定或有占用则拒删)/DeleteFrameAsync(软删 STATE='1')`。
- `FrameEditDialog` 加编辑模式（预填+编辑标题）；料架列表头加 新增/编辑/删除 按钮。

**排障经验（记录）**：
- 构建曾因 `NUGET_PACKAGES` 指向残缺沙箱缓存报 NU5037/CS0006；改用 `$env:NUGET_PACKAGES=~/.nuget/packages` 后正常。
- 多次误起重复实例（进程查询偶发假空）导致抢端口——务必单实例，`COUNT=` 方式确认。
- 偶发「写 POS_TEST_START 失败」→工位告警（FINS 写寄存器瞬时超时，非代码引入）；如需可给 `WriteTestStart` 加一次重试（待定）。

**I. SQL 文件整合（本 session 末，用户要求）**：
- 提交代码后，把演示所需 SQL 全部并入单一权威建库脚本 `docs/sql/cnc_schema.sql`：三道工序 node1/2/3 + 机台挂序、5 料架（含 NG91/EQ2中转2/EQ3中转92，各 12 槽）+ 7 条 3-stage 绑定、上料架补满 EL-001..010、全套 LOCATION_MAP（区域/加工位 cell/料架 shelf+cell）。EQ03 机台安全点位保留。
- **删除**其余 SQL：`demo_seed_location_map.sql`、`test_routing_seed.sql`、`three_stage_line_seed.sql`、`migration_phase4_rcs.sql`、`migration_2026-06-30_*.sql`、`migration_2026-07-08_*.sql`（`docs/sql/` 现仅剩 cnc_schema.sql）。
- 验证：整库重建无报错，工序 node1/2/3、7 绑定、frame1=10 电极其余各 12 空槽、LOCATION_MAP 19 行、MACHINE_SAFE 保留 1 条 全部正确。`演示实操手册.md` §4.1 改为「建库即就绪，无需额外种子」。
- 注意：删迁移脚本后仅支持**全新建库**（cnc_schema.sql 已含全部效果），不再有对既有库的增量迁移路径。

### Session 40 — 2026-07-09 上料料源竞态修复（单消费者决策 + 原子预占 + 抢不到料=等待）
> 起因：诊断「上料架仅剩 1 件、同机台双工位都空」场景。发现旧逻辑「入队只读 occupied、锁料在派工循环」两步间无预占，两工位同 tick 都读到 occupied=1 → 各发一条 RCS 任务，第二条空跑 → 到位复核无料转 Alarm（粘滞、卡人工），并伴电极码丢失/账实不符。按用户三层设计修复。改动集中 2 文件，`dotnet build` 0 警告 0 错误、ReadLints 无错。

- **Layer 1 单一调度循环（`PositionScheduler`）**：工位状态机不再自查料/选槽/下发——WAIT_LOAD 仅置 `ctx.UploadRequested=true`（`ExecuteActionsAsync`）。派工消费者 `DispatchLoopAsync` 统一决策：先清下料队（priority 高），队列空时 `AllocateUploadsAsync` 收集"请求上料"工位，按 `WaitLoadSince` 升序（空闲最久优先）→ `PositionId` 升序 确定性排序，逐个 `TryDispatchUploadAsync`（解析料源+路由 → 下发 RCS → 原子取料预记 → 绑定转 Dispatching）。单线程串行保证前一位预记落地后后一位再查料，"两位同时看到同一件料"结构上不可能。原 `EnqueueUploadAsync` 拆为纯解析的 `ResolveUploadPlanAsync`（不入队/不下发）。
- **Layer 2 原子预占（`SlotAccountService`）**：`ReserveAsync`/`ReserveTakeAsync` 由「SELECT+内存 SemaphoreSlim+SaveChanges」改为「候选选取 + 带条件 `ExecuteUpdateAsync`（WHERE SLOT_STATE=期望态）+ 影响行数校验，=0 则重选」。即使人工校正/盘点回写等第二写入者并发，DB 也不双占。移除 `_frameLocks` 内存锁（单消费者 + 原子 UPDATE 已足够）。复用现有 `SLOT_STATE='3'=预记`，不改 schema。
- **Layer 3 抢不到料=正常等待**：`ResolveUploadPlanAsync` 无料源占用 → WaitMaterial，工位保持 WAIT_LOAD 不告警不重试风暴，由下一轮轮询/新料到位自然唤醒。仅"已下发但源架被外部写入者在窗口内取空"（罕见账实异常）→ ALARM 告警人工。`SetState` 统一维护进/出 WAIT_LOAD 的 `WaitLoadSince` 计时与 `UploadRequested` 清标记（移除 `DrivePositionCoreAsync` 里对 `ctx.State` 的提前赋值，改由 SetState 单点写入以正确判转移）。
- 说明：上料不再走 `IDispatchQueue`（改走消费者扫描分配）；下料仍走队列 + `DispatchOneAsync`（不变）。`DispatchOneAsync`/`ApplySlotReservationAsync` 的 Upload 分支成为死路径（保留不删，防御性）。
- 运行态验证（本机 App + 三 PLC 模拟器 + RCS 模拟器）：上料架仅 1 件 EL-001、EQ1 双工位皆空 → 仅 EQ1 POS2 下发上料（取料预记 1 次），POS1 保持 WAIT_LOAD 等料、无告警；落账后 frame1 无残留预记（SLOT_STATE='3' 为 0），电极码流转到加工记录。确定性选位命中先进 WAIT_LOAD 者。

### Session 40（续）— 交接路径同构竞态修复（下料"选下游空工位"收敛进单消费者）
> 承接上一改：上料竞态修好后，识别出"上游工序间直接交接（NextMachineCell）"是另一条料源路径且存在同构竞态——"选下游空工位"在 drive loop（`ResolveUnloadTargetAsync`/`FindIdlePositionAmong`）、"登记 `_expectedInbound`"在 consumer，两步分离 → 上游两件同时 OK 可能都选中同一个下游空工位（如 EQ1 双工位→EQ2 单工位 POS3）。改动集中 `PositionScheduler.cs` + `DispatchQueue.cs`（加 `DispatchItem.IsOk`），`dotnet build` 0 警告 0 错误、ReadLints 无错。

- **下料终点决策整体移进单消费者**：`EnqueueUnloadAsync`（drive loop）只解析下料源 cell + 结果入队（新增 `DispatchItem.IsOk` 携带 OK/NG）；`DispatchOneAsync`（消费者）出队后才 `ResolveUnloadTargetAsync`（含选位）→ 下发 → `ApplyUnloadReservationAsync`（NextMachineCell 登记 `_expectedInbound` / 料架 `ReserveAsync`）。选位与登记同在单消费者串行完成，前一件登记落地后后一件才选位 → 两件下料不会抢同一下游工位。原 `ApplySlotReservationAsync`（含已死的 Upload 分支）删除，替换为只管下料的 `ApplyUnloadReservationAsync`。
- **选位加确定性排序**：`FindIdlePositionAmong` 从"遍历取首个"改为收集候选后按"空闲最久（`WaitLoadSince` 最早）→ 工位编号升序"排序取首（防饿死、均衡、可复现），对齐上料路径策略。
- 运行态验证（NG 率临时置 0、frame1 放 2 件、EQ1 双工位）：EL-101/EL-102 两件同拍 OK 下料，POS1→`602203`（NextMachineCell 直接交接 EQ2 POS3），POS2→`653002`（TransitFrame，POS3 已占改走 EQ2 中转架）；EL-101 随后从中转架回流上料 EQ2；两件全程流经三工序、电极码可追、无 ALARM。
- 说明：`DispatchItem` 的 `SourceFrameId/UnloadTarget/DestFrameId/DestEquipmentId/DestPositionId` 现为预留字段（上料不入队、下料终点由消费者用 `UnloadDecision` 决策），保留未删以控改动面。

### Session 41 — 2026-07-09 启动对账①b：终态任务立刻收口预记（防强杀重启残留）
> 起因：§4.3④ 强杀重启实测——对账①绑回未完结任务成功，但模拟器内存清空后 query 把未知任务兜底报 completed，调度器未走 ConfirmTake/Alarm→Rollback，预记挂在 `SLOT_STATE=3`。按方案 A 在对账阶段收口。

- **`PositionScheduler.ReconcileAsync` 加 ①b**：绑回后对 unfinished 批量 `queryTask`；终态立刻 `SettleSlotForTerminalAsync`（COMPLETED 先 fresh 读 HasMat，符合才 Confirm，否则 Rollback；CANCELED/FAILED 按方向 Rollback）并从 active 剔除供②兜底。
- **工位态**：Upload+有料→Loaded；Unload+无料→写 POS_TEST_START=2 回 WaitLoad；HasMat 未读到→WaitLoad（**不 latch Alarm**）；明确不符→Alarm；取消→WaitLoad+`RCS_CANCELED`。
- 文档：`客户端开发文档.md` §6.3 补 ①b；`演示实操手册.md` §4.3④ 验收点对齐。
- 验证：`dotnet build` 0/0；强杀重启——强杀前预记 taskId 重启后 `still_reserved=0`，`RECONCILE-*` 告警=0（HasMat=null 走 WaitLoad），日志「对账①b：收口终态任务 N 个」。演示态 appsettings + frame1 已还原。

### Session 41（续）— 复核不过 + 水位自动换架 实测
- **复核不过**：新增 `Rcs.SimulatorSkipMaterialArrival`（启动注入 `CncMachineSimulator.SkipMaterialArrival`）。`true` 下跑节拍 → EQ1/EQ2 上料 COMPLETED 后不置 HasMat → `Transporting→Alarm`（「RCS 报完成但 PLC 无料」），无 Processing/写启动；粘滞不自动离开；看板「恢复」→ `人工恢复 → WAIT_LOAD`。`dotnet build` 0/0。
- **水位自动换架**：`WaterMonitorEnabled=true` + `SchedulerEnabled=false`，frame1 抽空 → 启动即「水位触发：料架1 EMPTY → 机台1 Upload」；同拍还触发 frame3 FULL→Unload、frame92 EMPTY→Transit；各 TXN 两发 `change_frame` 均 COMPLETED（先拉后送）。
- 演示态已还原（SkipMaterial=false、WaterMonitor=false、Scheduler=true、NgRate=0.3）；frame1 补满；手册 §4.3① / §5 水位说明已同步。

### Session 42 — 2026-07-09 定期自动盘点实测 + AUTHOR 超长修复
- **触发机制**（`InventorySchedulerService`）：`InventoryAutoEnabled=true` 时 HostedService 启动；`_lastRun=MinValue` → **首检立刻到期**；之后每 30s 检查，距上次 ≥ `InventoryIntervalMinutes` 且 RCS 空闲（派工队列空 + 无在途盘点 + 无换架）→ 逐料架 `StartInventoryAsync`（identifyQR）串行，一架完成再下一架。
- **实测**：开 `InventoryAutoEnabled` + 间隔 1 分钟 + 关调度 → 启动即「定期盘点开始一轮：5 个料架」→ 5 条 `identify` 均 `COMPLETED`（AUTHOR=`inv-auto`），各架 `LAST_VERIFY_TIME` 刷新。
- **真 bug**：作者原写 `"inventory-scheduler"`（20 字符）超 `AUTHOR VARCHAR(15)` → `DbUpdateException`，整轮失败。改为 `"inv-auto"`。`dotnet build` 0/0。
- 演示态已还原（`InventoryAutoEnabled=false`、调度开、NgRate=0.3）；frame1 补满 EL-001..010，其余架清空。

### Session 43 — 2026-07-09 现场工程缺口修复（不含 PLC 配置 / 口令）
- **禁 LOCATION 假码**：`RouteResolver.ResolveFrameCellAsync` 缺映射返回 null；`ChangeFrameOrchestrator` / `InventoryService` 缺 cell/shelf/station 或缓存区 → 拒发 + 告警，删 `FRAME-{id}` 兜底。
- **写启动重试**：`WriteTestStartAsync` 失败后 Delay(250ms) 再写一次，仍失败才 return false。
- **手动盘点互斥**：`InventoryService` 注入队列+换架编排，有在途搬运/换架则拒发。
- **位置映射编辑下拉**：机台/工位/料架改为 NamedOption 中文 ComboBox（工位随选中机台加载）。
- **NG 提示**：料架页人工校正区加「置空释放」说明。
- 文档：`演示实操手册` / `cnc_schema.sql` 注释同步。`dotnet build` 0/0。**未做**：FINS 节点可配、口令 DPAPI。

### Session 44 — 2026-07-09 identify 孔位 / 盘点线体 / NG UX
- **孔位映射**：`CorrectFromInventoryAsync` 按文档「三位数、百位=面/层」解码为 `LAYER_NO`/`POS_IN_LAYER`，products 沿物理序校正；起始孔无对应槽则跳过并打日志。定期盘点起始改为 `101`；模拟器扫码产品码按孔位递增（满 99 进层）。
- **盘点线体**：`InventoryService` 经料架绑定机台 → `GetWorkLineByEquipmentAsync`，无绑定回退 `LINE/1`。
- **NG UX**：料架列表「仅 NG 架」筛选（`FrameListItem.HasNgRole`）；人工校正区「置空释放」一键清槽。
- 验证：`dotnet build` 0/0。

### Session 45 — 2026-07-09 UI 布局优化（看板 / 料架 / RCS）
- **监控看板**：对齐原型——机台治具卡（门/安全指示 + accent 刻度 + 双工位行）+ 右侧实时告警流（确认/全部确认）；KPI 保留；「最近加工记录」让位给告警。
- **料架页**：左右分栏——左列表+绑定，右槽位网格 + 底栏校正/盘点，消除纵向挤压。
- **RCS 页**：Tab 顺序改为 任务列表 → 报文流水 → 连接&下发 → 位置映射（默认落在任务）。
- 验证：`dotnet build` 0/0。

### Session 46 — 2026-07-09 RCS 任务页小修
- 「取消处理」列：仅 CANCELED 显示待处理/已处理（COMPLETED 不再误显）。
- `ConfirmCancelHandled`：仅 CANCELED 允许，任务不存在/状态不符抛错；已处理幂等。
- 任务列表点选回填 `OperateTaskId`；状态列加宽至 110 避免 `COMPLE...`。
- 验证：`dotnet build` 0/0。

### Session 47 — 2026-07-09 料架页布局 P1
- `FindResult`（统计/反查）从盘点行挪到槽位标题下方独立行，盘点行只留起始/数量/发起。
- 槽位网格：先居中，后按反馈改为贴顶（去掉 ViewportHeight 居中包装）。
- 「占用」列宽 60、右内边距 14。
- 验证：`dotnet build` 0/0；已重启 App。

### Session 48 — 2026-07-09 RCS 任务列表中文化
- 列头：taskId→任务号，redo→重试次数，落库→创建时间。
- 种类/状态单元格：transit→搬运、COMPLETED→已完成 等（`RcsDisplayLabels` + 转换器）；落库仍英文码。
- 连接&下发：「redo」按钮→「重试」；报文筛选/操作区 taskId→任务号。
- 列宽/MinWidth 防表头被排序箭头挤花；任务号加宽+悬停完整号；整表 ToolTip 改标题旁提示。
- 验证：`dotnet build` 0/0；已重启 App。

### Session 49 — 2026-07-09 报文流水页优化
- 方向/接口筛选与列显示中文化（出站/入站、搬运下发/状态回调等）；查询条件映射回英文码。
- 新增「结果」「错误」列；耗时显示为 `N ms`；「请求」改「摘要」+悬停。
- 底部详情区：点选一行展示格式化请求体/响应体；刷新后尽量保持选中。
- 验证：`dotnet build` 0/0；已重启 App。

### Session 50 — 2026-07-09 连接&下发页优化
- 连接配置压成一行；任务类型改为「搬运/抓取/识别」，按类型显隐参数区。
- 抓取孔位补中文标签；识别时起点改「料架站」、隐藏终点。
- 任务操作 / 换架回收分区卡片化；终端撑满高度 + 清空按钮。
- 验证：`dotnet build` 0/0；已重启 App。

### Session 51 — 2026-07-09 RCS 连接配置正式落库
- 表 `MAS_AUTO_WORKLINE_AGV` 增 RCS_* 列（schema + `migrate_workline_agv_rcs.sql`）；实体同步。
- `IRcsConnectionConfigService` / `RcsRuntimeConfig` / 启动 bootstrap；`RcsClient`/`RcsTaskTracker`/`RcsCallbackHost`/`RcsSimulator` 读运行时配置。
- 连接&下发顶栏可编辑（地址/clientCode/回调/超时/重试/轮询）+ 保存落库；出站热更新，回调变更提示重启。
- 不做口令加密。验证：`dotnet build` 0/0。
- 补「测试连接」出站（queryTask）+ 状态灯。

### Session 55 — 2026-07-09 PLC 通信告警刷屏修复
- 根因：`ReadPointsAsync` 每个寄存器失败都 `RaisePlcAlarmAsync` → Shell Growl 堆叠。
- 修：未连接整批只告一次；有连接时失败合并为一条；同 PLC 30s 节流；PLC 页未连接不读；编辑保存后自动重连。

### Session 56 — 2026-07-09 线体/工序/机台列表体验
- 「启用」确认：只读徽标（非开关）；停用走删除软删；加 ToolTip。
- 机台「所属 PLC」完整显示 `名称 (IP)` + 列宽/ToolTip。
- 关联料架展示全部角色（上/下/中转/NG）；`FrameRoleText` 提到 `ConfigFlags` 共用。
- 线体列表去掉误导性 RCS/AGV 列（连接只在 RCS 页配）；所在电脑/电脑IP/计划数量标预留。

### Session 57 — 2026-07-09 监控看板布局 B
- 上半：机台治具卡 | 实时告警（默认只拉未处理）；「全部确认」按库内未处理全量。
- 底部通栏：最近加工记录（时间/机台/工位/电极/结果/耗时），2s 轮询 + 节拍结束刷新。
- 细节：时间列加宽防 `11:42...` 截断；告警/记录空态占位文案。
- KPI 文案改为「工位 OK/NG」标明非整件；去掉未处理告警下的刷新按钮。
- 验证：`dotnet build` 0/0；已重启 App。

### Session 58 — 2026-07-09 日志/告警页美化
- 告警：状态徽标、级别着色、消息 ToolTip、空态；新增「全部确认」；条数变更自动刷新。
- 应用日志：级别着色、默认 200 行、筛选联动刷新、空态。
- 底栏按当前 tab 显示摘要，不再残留「该告警已处理」。
- 验证：`dotnet build` 0/0；已重启 App。

### Session 59 — 2026-07-09 监控看板布局 A
- 从「上双栏+底通栏」改为：左机台通高 | 右上告警 | 右下加工记录。
- KPI/告警口径/加工记录列不变；右栏记录表列宽略收以适配窄栏。
- 验证：`dotnet build` 0/0；已重启 App。

### 备注
- 已是 git 仓库（远程 origin: github.com/rui-xiaomi/CNC）；commit/push 前先给用户看信息并确认。
- 本机环境：MySQL 8.4（服务 MySQL84），root 口令 `2580.wxr`；appsettings 用明文口令开发（PasswordProtected=false，勿提交明文进 git）。
- Smart App Control（Win11 智能应用控制）曾强制开启拦截未签名新构建 exe（事件 3118/3077，FileLoadException 0x800711C7）；用户已在 Windows 安全中心手动关闭（VerifiedAndReputablePolicyState=0）。关闭后已验证 `CncLoader.App.exe` 正常运行：DB 连通（线体1/PLC3/机台3/加工位6/点位36/料架3）、3 PLC 模拟器建链、轮询读 D 寄存器正常。本机现可跑 App+连库+RCS 页端到端。
- Phase 1/2 完成后暂停演示，Phase 3（配置管理）待用户确认。
- 后续 UI 开发以 `docs/prototype/index.html` 为唯一权威基准。
- 第四阶段：按开发文档 §10(v3) 7 步推进，每步暂停等用户确认；DB schema 变更（Session 19）已完成并应用。
