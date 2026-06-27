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

### 备注
- 已是 git 仓库（远程 origin: github.com/rui-xiaomi/CNC）；commit/push 前先给用户看信息并确认。
- Phase 1/2 完成后暂停演示，Phase 3（配置管理）待用户确认。
- 后续 UI 开发以 `docs/prototype/index.html` 为唯一权威基准。
