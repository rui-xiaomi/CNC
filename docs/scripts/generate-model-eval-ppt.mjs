/**
 * Generate CNC AI model evaluation PPT (Opus 4.8 vs GLM-2)
 * Output: docs/模型测评汇报.pptx
 */
import pptxgen from "pptxgenjs";
import { fileURLToPath } from "url";
import { dirname, join } from "path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const OUT = join(__dirname, "..", "模型测评汇报.pptx");

const C = {
  bg: "191D22",
  bg2: "232830",
  fg: "E8EBEE",
  muted: "9CA3AF",
  accent: "135C96",
  accentLight: "1F6FEB",
  ok: "16A34A",
  warn: "C2710C",
  alarm: "D62828",
  white: "FFFFFF",
};

const pptx = new pptxgen();
pptx.layout = "LAYOUT_16x9";
pptx.author = "瑞小米";
pptx.title = "CNC客户端 AI编程模型开发效能测评汇报";
pptx.subject = "Claude Code Opus 4.8 vs GLM-2";

pptx.defineSlideMaster({
  title: "DARK_MASTER",
  background: { color: C.bg },
  objects: [
    {
      rect: {
        x: 0,
        y: 0,
        w: "100%",
        h: 0.08,
        fill: { color: C.accent },
      },
    },
    {
      text: {
        text: "CNC 自动化上下料客户端 · AI 模型测评",
        options: {
          x: 0.4,
          y: 5.25,
          w: 9,
          h: 0.3,
          fontSize: 9,
          color: C.muted,
          align: "left",
        },
      },
    },
  ],
  slideNumber: { x: 9.2, y: 5.25, color: C.muted, fontSize: 9 },
});

function slide() {
  return pptx.addSlide({ masterName: "DARK_MASTER" });
}

function title(sl, text, opts = {}) {
  sl.addText(text, {
    x: 0.5,
    y: 0.35,
    w: 9,
    h: 0.7,
    fontSize: opts.size || 28,
    bold: true,
    color: C.white,
    fontFace: "Microsoft YaHei",
    ...opts,
  });
}

function subtitle(sl, text) {
  sl.addText(text, {
    x: 0.5,
    y: 1.05,
    w: 9,
    h: 0.4,
    fontSize: 14,
    color: C.muted,
    fontFace: "Microsoft YaHei",
  });
}

function bullets(sl, items, y = 1.6, opts = {}) {
  const rows = items.map((t) => ({
    text: t,
    options: { bullet: true, breakLine: true },
  }));
  sl.addText(rows, {
    x: opts.x ?? 0.55,
    y,
    w: opts.w ?? 8.8,
    h: opts.h ?? 3.5,
    fontSize: opts.fontSize ?? 14,
    color: C.fg,
    fontFace: "Microsoft YaHei",
    paraSpaceAfter: 8,
    valign: "top",
  });
}

function table(sl, headers, rows, y, opts = {}) {
  const head = headers.map((h) => ({
    text: h,
    options: {
      fill: { color: C.accent },
      color: C.white,
      bold: true,
      align: "center",
      fontSize: 11,
      fontFace: "Microsoft YaHei",
    },
  }));
  const body = rows.map((row) =>
    row.map((cell, i) => {
      const isObj = typeof cell === "object" && cell !== null;
      const text = isObj ? cell.text : String(cell);
      const extra = isObj ? cell : {};
      return {
        text,
        options: {
          fill: { color: C.bg2 },
          color: extra.color || C.fg,
          align: extra.align || (i === 0 ? "left" : "center"),
          fontSize: extra.fontSize || 11,
          fontFace: "Microsoft YaHei",
          bold: extra.bold || false,
        },
      };
    })
  );
  sl.addTable([head, ...body], {
    x: opts.x ?? 0.5,
    y,
    w: opts.w ?? 9,
    h: opts.h,
    colW: opts.colW,
    border: { type: "solid", color: "3D4450", pt: 0.5 },
    autoPage: false,
  });
}

function kpiCard(sl, x, y, w, h, label, value, sub) {
  sl.addShape(pptx.shapes.RECTANGLE, {
    x,
    y,
    w,
    h,
    fill: { color: C.bg2 },
    line: { color: C.accent, width: 1 },
    rectRadius: 0.05,
  });
  sl.addText(label, {
    x: x + 0.15,
    y: y + 0.12,
    w: w - 0.3,
    h: 0.35,
    fontSize: 11,
    color: C.muted,
    fontFace: "Microsoft YaHei",
  });
  sl.addText(value, {
    x: x + 0.15,
    y: y + 0.45,
    w: w - 0.3,
    h: 0.55,
    fontSize: 26,
    bold: true,
    color: C.accentLight,
    fontFace: "Microsoft YaHei",
  });
  if (sub) {
    sl.addText(sub, {
      x: x + 0.15,
      y: y + h - 0.35,
      w: w - 0.3,
      h: 0.25,
      fontSize: 9,
      color: C.muted,
      fontFace: "Microsoft YaHei",
    });
  }
}

function diffBadge(text, color) {
  return { text, color };
}

// ── Slide 1: Cover ──
{
  const s = slide();
  s.addShape(pptx.shapes.RECTANGLE, {
    x: 0,
    y: 0,
    w: "100%",
    h: "100%",
    fill: { color: C.bg },
  });
  s.addShape(pptx.shapes.RECTANGLE, {
    x: 0,
    y: 0,
    w: "100%",
    h: 0.12,
    fill: { color: C.accent },
  });
  s.addText("CNC 客户端", {
    x: 0.8,
    y: 1.6,
    w: 8,
    h: 0.5,
    fontSize: 18,
    color: C.muted,
    fontFace: "Microsoft YaHei",
  });
  s.addText("AI 编程模型开发效能测评汇报", {
    x: 0.8,
    y: 2.1,
    w: 8.5,
    h: 1,
    fontSize: 36,
    bold: true,
    color: C.white,
    fontFace: "Microsoft YaHei",
  });
  s.addText("Claude Code Opus 4.8  vs  GLM-2", {
    x: 0.8,
    y: 3.15,
    w: 8,
    h: 0.5,
    fontSize: 20,
    color: C.accentLight,
    fontFace: "Microsoft YaHei",
  });
  s.addText("真实工业项目对比 · 分场景选型建议", {
    x: 0.8,
    y: 3.75,
    w: 8,
    h: 0.4,
    fontSize: 14,
    color: C.muted,
    fontFace: "Microsoft YaHei",
  });
  s.addText("汇报人：瑞小米          日期：2026 年 7 月", {
    x: 0.8,
    y: 4.8,
    w: 8,
    h: 0.35,
    fontSize: 13,
    color: C.fg,
    fontFace: "Microsoft YaHei",
  });
}

// ── Slide 2: TOC ──
{
  const s = slide();
  title(s, "目录");
  bullets(s, [
    "测评背景与目标",
    "项目与测评范围",
    "测评方法与评分体系",
    "核心结论（领导重点）",
    "分场景测评结果",
    "量化对比与成本",
    "优劣势与风险",
    "落地建议与下一步",
  ], 1.5, { fontSize: 16 });
  s.addText("⏱ 建议时长 15～20 分钟 · 时间紧可直接看第 4 页结论", {
    x: 0.55,
    y: 4.85,
    w: 8,
    h: 0.35,
    fontSize: 11,
    color: C.warn,
    fontFace: "Microsoft YaHei",
  });
}

// ── Slide 3: Background ──
{
  const s = slide();
  title(s, "为什么做这次测评？");
  bullets(s, [
    "CNC 自动化上下料 WPF 客户端全程 AI 辅助开发（Opus 4.8 + GLM-2）",
    "领导关注：哪个模型更值？什么任务该用谁？",
    "非实验室 Benchmark —— 基于真实交付项目的对比",
  ], 1.35, { h: 1.5 });
  table(
    s,
    ["目标", "说明"],
    [
      ["可量化", "7 维评分 + 8 个真实用例（Phase 1～5 + 专项）"],
      ["可复现", "同一 docs/ 权威文档、同一验收标准"],
      ["可落地", "输出分任务选型策略，控制成本与质量"],
    ],
    3.0,
    { colW: [1.5, 7.5] }
  );
}

// ── Slide 4: Project scope ──
{
  const s = slide();
  title(s, "测评项目概况");
  s.addText("项目简介", {
    x: 0.5,
    y: 1.2,
    w: 4.2,
    h: 0.35,
    fontSize: 14,
    bold: true,
    color: C.accentLight,
    fontFace: "Microsoft YaHei",
  });
  bullets(
    s,
    [
      "WPF 工位机桌面程序（.NET 8）",
      "经 IP/TCP 与 3 台 PLC 通信（Modbus / FINS）",
      "不直连 CNC 机台，一切经 PLC 信号",
      "管理线体、工序、机台、料架配置",
      "后续：双加工位并行上下料调度",
    ],
    1.55,
    { x: 0.55, w: 4.3, h: 2.8, fontSize: 12 }
  );
  table(
    s,
    ["项", "数据"],
    [
      ["工程数", "6 个分层项目"],
      ["数据表", "17 张"],
      ["功能页", "8 页"],
      ["源文件", "~120 个"],
      ["已完成", "Phase 1～3、5 + FINS 协议"],
      ["待做", "Phase 4 状态机、Phase 6 联调"],
    ],
    1.55,
    { x: 5.0, w: 4.5, colW: [1.2, 3.3] }
  );
  s.addText("模型参与：请按实际填写各 Phase 主导模型（见配套 CSV 打分表）", {
    x: 0.5,
    y: 4.75,
    w: 9,
    h: 0.35,
    fontSize: 11,
    color: C.muted,
    fontFace: "Microsoft YaHei",
  });
}

// ── Slide 5: Methodology ──
{
  const s = slide();
  title(s, "测评方法与评分体系");
  table(
    s,
    ["维度", "权重", "说明"],
    [
      ["需求理解准确度", "20%", "仅与 PLC 通信、一机一 PLC、写先落流水等"],
      ["代码质量", "20%", "6 工程分层、命名一致、无过度设计"],
      ["首次可用率", "15%", "dotnet build 0 警告 0 错误"],
      ["调试与排错", "15%", "XAML 样式错配、软删过滤、UDP 假在线等"],
      ["跨文件一致性", "10%", "Core / Data / UI / Communication 同步"],
      ["文档与方案", "10%", "与权威 docs/ 一致、可执行"],
      ["交互效率", "10%", "轮次少、返工少、自主完成度高"],
    ],
    1.25,
    { colW: [2.2, 0.8, 6.0] }
  );
  bullets(
    s,
    [
      "同一用例、同一验收标准 · 记录轮次与 build 结果",
      "配合 Skills：brainstorming / planning-with-files / ui-ux-pro-max",
    ],
    4.85,
    { h: 0.8, fontSize: 11 }
  );
}

// ── Slide 6: Core conclusion ──
{
  const s = slide();
  title(s, "核心结论", { size: 26 });
  s.addShape(pptx.shapes.RECTANGLE, {
    x: 0.5,
    y: 1.15,
    w: 9,
    h: 1.05,
    fill: { color: C.bg2 },
    line: { color: C.accent, width: 1.5 },
    rectRadius: 0.06,
  });
  s.addText(
    "建议按任务类型分模型使用：L4 高难（架构、工业协议、复杂排障）优先 Opus 4.8；\nL2 常规（CRUD、文档、简单 UI）可优先 GLM-2 控制成本。【请据实测微调】",
    {
      x: 0.7,
      y: 1.28,
      w: 8.6,
      h: 0.9,
      fontSize: 14,
      color: C.white,
      fontFace: "Microsoft YaHei",
      valign: "middle",
    }
  );
  kpiCard(s, 0.5, 2.45, 2.8, 1.35, "综合评分 /5", "待填", "Opus · GLM 填分后更新");
  kpiCard(s, 3.5, 2.45, 2.8, 1.35, "首次可运行率", "待填", "build 即通过比例");
  kpiCard(s, 6.5, 2.45, 2.8, 1.35, "平均轮次/任务", "待填", "提需求到验收");
  table(
    s,
    ["场景", "推荐模型", "场景", "推荐模型"],
    [
      ["架构 / 多工程分层", "Opus 4.8", "CRUD + 仓储", "GLM-2"],
      ["工业协议 Modbus/FINS", "Opus 4.8", "Bug 排障", "Opus 4.8"],
      ["WPF UI + MVVM", "待填", "文档方案", "GLM-2"],
    ],
    4.05,
    { colW: [2.0, 1.3, 2.0, 1.3] }
  );
}

// ── Slide 7: Use case map ──
{
  const s = slide();
  title(s, "8 个真实测评用例");
  table(
    s,
    ["编号", "场景", "难度", "Opus 4.8", "GLM-2"],
    [
      ["A", "Phase 1 六工程架构 + 模拟器", diffBadge("L4", C.alarm), "待填", "待填"],
      ["B", "Phase 2 PLC 读写/流水/CRUD", diffBadge("L3", C.warn), "待填", "待填"],
      ["C", "Phase 3 配置四页 CRUD", diffBadge("L3", C.warn), "待填", "待填"],
      ["D", "欧姆龙 FINS/UDP 协议接入", diffBadge("L4", C.alarm), "待填", "待填"],
      ["E", "Phase 5 AGV/扫码枪页", diffBadge("L2", C.ok), "待填", "待填"],
      ["F", "XAML 样式错配栈溢出修复", diffBadge("L2", C.ok), "待填", "待填"],
      ["G", "UI 现代工业风重设计", diffBadge("L3", C.warn), "待填", "待填"],
      ["H", "文档整合与方案撰写", diffBadge("L2", C.ok), "待填", "待填"],
    ],
    1.2,
    { colW: [0.6, 4.5, 0.7, 1.5, 1.5] }
  );
  s.addText("L2 简单 · L3 复杂 · L4 高难 —— 覆盖从零架构到现场联调全链路", {
    x: 0.5,
    y: 4.9,
    w: 9,
    h: 0.3,
    fontSize: 11,
    color: C.muted,
    fontFace: "Microsoft YaHei",
  });
}

// ── Slide 8: L4 deep dive ──
{
  const s = slide();
  title(s, "高难任务深度对比（L4）");
  s.addText("用例 A · Phase 1 基础架构", {
    x: 0.5,
    y: 1.15,
    w: 4.3,
    h: 0.35,
    fontSize: 13,
    bold: true,
    color: C.accentLight,
    fontFace: "Microsoft YaHei",
  });
  bullets(
    s,
    [
      "6 工程分层 + EF Core 17 表",
      "Modbus 模拟器 + 中央轮询中枢",
      "DPAPI 加密 + WPF 主壳 8 页导航",
      "验收：3 PLC 建链、轮询 30 点位",
      "Opus / GLM 得分：待填",
    ],
    1.5,
    { x: 0.55, w: 4.2, fontSize: 11, h: 2.5 }
  );
  s.addText("用例 D · FINS/UDP 协议", {
    x: 5.0,
    y: 1.15,
    w: 4.5,
    h: 0.35,
    fontSize: 13,
    bold: true,
    color: C.accentLight,
    fontFace: "Microsoft YaHei",
  });
  bullets(
    s,
    [
      "手写 OmronFinsPlcClient（UDP 9600）",
      "工厂按 PLC_READ_WAY 分流",
      "修复：先显示窗口再后台自检",
      "UDP 探活判离线，避免假在线",
      "现场：节点号默认 IP 末段（风险点）",
    ],
    1.5,
    { x: 5.05, w: 4.4, fontSize: 11, h: 2.5 }
  );
  s.addShape(pptx.shapes.RECTANGLE, {
    x: 0.5,
    y: 4.15,
    w: 9,
    h: 0.85,
    fill: { color: C.bg2 },
    line: { color: C.warn, width: 1 },
    rectRadius: 0.04,
  });
  s.addText(
    "关键差异（待填）：架构一次性分层是否正确？FINS 能否少轮次完成且边界情况处理到位？",
    {
      x: 0.65,
      y: 4.28,
      w: 8.7,
      h: 0.6,
      fontSize: 12,
      color: C.fg,
      fontFace: "Microsoft YaHei",
    }
  );
}

// ── Slide 9: L2-L3 routine ──
{
  const s = slide();
  title(s, "常规任务对比（L2～L3）");
  table(
    s,
    ["用例", "任务", "Opus 4.8", "GLM-2"],
    [
      ["C", "线体/工序/机台/料架 CRUD + 引用校验", "待填", "待填"],
      ["E", "AGV/扫码枪外设测试页 UI 还原", "待填", "待填"],
      ["G", "WPF-UI + HandyControl 工业风改造", "待填", "待填"],
      ["H", "权威文档合并 + 三件套维护", "待填", "待填"],
    ],
    1.2,
    { colW: [0.6, 5.5, 1.3, 1.3] }
  );
  s.addShape(pptx.shapes.RECTANGLE, {
    x: 0.5,
    y: 3.35,
    w: 9,
    h: 1.55,
    fill: { color: C.bg2 },
    line: { color: "3D4450", width: 0.5 },
    rectRadius: 0.04,
  });
  s.addText("Bug 修复案例 · 用例 F", {
    x: 0.65,
    y: 3.45,
    w: 4,
    h: 0.3,
    fontSize: 12,
    bold: true,
    color: C.alarm,
    fontFace: "Microsoft YaHei",
  });
  bullets(
    s,
    [
      "现象：点击 AGV 页 → 疯狂弹框「无法创建堆栈防护页面」",
      "根因：PasswordBox 误用 TargetType=TextBox 的 FormInput 样式",
      "修复：内联样式替代，最小 diff，1 轮定位",
      "Opus / GLM 定位轮次：待填对比",
    ],
    3.75,
    { x: 0.65, w: 8.5, fontSize: 11, h: 1.1 }
  );
}

// ── Slide 10: Quantitative ──
{
  const s = slide();
  title(s, "量化对比");
  table(
    s,
    ["难度层级", "包含用例", "Opus 均分", "GLM 均分"],
    [
      ["L2 简单", "E / F / H", "待填", "待填"],
      ["L3 复杂", "B / C / G", "待填", "待填"],
      ["L4 高难", "A / D", "待填", "待填"],
    ],
    1.2,
    { colW: [1.2, 3.5, 1.5, 1.5] }
  );
  s.addText("七维雷达图（填分后插入）", {
    x: 0.5,
    y: 2.85,
    w: 4.2,
    h: 2.2,
    fontSize: 12,
    color: C.muted,
    align: "center",
    valign: "middle",
    fill: { color: C.bg2 },
    line: { color: "3D4450", width: 1 },
    fontFace: "Microsoft YaHei",
  });
  table(
    s,
    ["效率指标", "Opus 4.8", "GLM-2"],
    [
      ["总交互轮次", "待填", "待填"],
      ["返工次数", "待填", "待填"],
      ["人工修正工作量", "待填", "待填"],
      ["单功能点成本（可选）", "待填", "待填"],
    ],
    2.85,
    { x: 5.0, w: 4.5, colW: [2.0, 1.1, 1.1] }
  );
}

// ── Slide 11: Pros/cons ──
{
  const s = slide();
  title(s, "优劣势总结");
  s.addText("Claude Code Opus 4.8", {
    x: 0.5,
    y: 1.1,
    w: 4.3,
    h: 0.35,
    fontSize: 14,
    bold: true,
    color: C.accentLight,
    fontFace: "Microsoft YaHei",
  });
  bullets(
    s,
    [
      "✅ 复杂架构与多工程分层理解深（Phase 1）",
      "✅ 工业协议实现与现场排障（FINS/启动流程）",
      "✅ 跨层改动同步性好",
      "❌ Token 成本相对较高（待填具体数据）",
      "❌ 劣势 2：待填",
      "最适合：架构、协议、复杂 Bug、联调排障",
    ],
    1.45,
    { x: 0.55, w: 4.2, fontSize: 11, h: 3.2 }
  );
  s.addText("GLM-2", {
    x: 5.0,
    y: 1.1,
    w: 4.5,
    h: 0.35,
    fontSize: 14,
    bold: true,
    color: C.ok,
    fontFace: "Microsoft YaHei",
  });
  bullets(
    s,
    [
      "✅ 常规 CRUD / 页面开发响应快（待填实测）",
      "✅ 文档整理与方案撰写性价比较好",
      "✅ 简单修改成本低",
      "❌ L4 高难任务可能需要更多轮次（待填）",
      "❌ 劣势 2：待填",
      "最适合：CRUD、文档、简单 UI 调整",
    ],
    1.45,
    { x: 5.05, w: 4.4, fontSize: 11, h: 3.2 }
  );
}

// ── Slide 12: Risks ──
{
  const s = slide();
  title(s, "风险与局限");
  bullets(s, [
    "样本量：1 个项目、8 个用例 —— 不代表所有技术栈与团队",
    "工程师因素：Prompt 质量、docs/ 准备度会显著影响结果",
    "模型版本迭代：Opus 4.8 / GLM-2 会更新，结论有时效性",
    "未覆盖：Phase 4 状态机、Phase 6 现场联调尚未纳入测评",
    "成本数据：若无精确 Token 账单，成本对比为估算",
  ], 1.4, { fontSize: 14 });
  s.addText("主动说明边界 → 增强汇报可信度", {
    x: 0.55,
    y: 4.85,
    w: 8,
    h: 0.35,
    fontSize: 12,
    color: C.warn,
    fontFace: "Microsoft YaHei",
  });
}

// ── Slide 13: Recommendations ──
{
  const s = slide();
  title(s, "落地建议");
  table(
    s,
    ["任务类型", "推荐模型"],
    [
      ["架构设计 / 多工程分层", "Opus 4.8"],
      ["工业协议（Modbus / FINS）", "Opus 4.8"],
      ["复杂 Bug / 现场联调排障", "Opus 4.8"],
      ["常规 CRUD + UI 页面", "GLM-2"],
      ["文档整理 / 方案撰写", "GLM-2"],
      ["简单布局 / 文案修改", "GLM-2"],
    ],
    1.15,
    { colW: [5.5, 3.5] }
  );
  bullets(
    s,
    [
      "维护权威 docs/ + prototype/ 降低 AI 理解偏差",
      "长任务用 planning-with-files（task_plan / findings / progress）",
      "Phase 验收清单制度化，客观衡量首次可用率",
      "补测：Phase 4 状态机、Phase 6 现场联调",
    ],
    3.85,
    { fontSize: 12, h: 1.5 }
  );
}

// ── Slide 14: Q&A ──
{
  const s = slide();
  title(s, "感谢聆听 · Q&A", { size: 30 });
  bullets(
    s,
    [
      "Q：为什么不用统一一个模型？\n   A：L4 质量与 L2 成本需求不同，分场景更优",
      "Q：AI 能替代多少开发人力？\n   A：架构决策与验收仍靠人，AI 负责大量编码与文档",
      "Q：安全/泄密风险？\n   A：代码本地、密钥 DPAPI 加密、不上传 .env",
      "Q：后续还测什么？\n   A：Phase 4 双工位状态机是高难补测项",
    ],
    1.55,
    { fontSize: 13, h: 3.5 }
  );
  s.addText("配套材料：docs/模型测评汇报模板.md · 打分表 CSV · 本 PPT", {
    x: 0.55,
    y: 4.9,
    w: 9,
    h: 0.3,
    fontSize: 10,
    color: C.muted,
    fontFace: "Microsoft YaHei",
  });
}

await pptx.writeFile({ fileName: OUT });
console.log("Generated:", OUT);
