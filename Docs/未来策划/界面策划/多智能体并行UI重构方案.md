# Madorin Workbench — 多智能体并行 UI 重构方案

> 状态：设计稿 v1.0（供产品决策）  
> 日期：2026-07-20  
> 作者：基于 `Src/Netor.Cortana.UI` 代码实际阅读后撰写  
> 关联文档：`存储优化/对话记录文件化存储方案.md`

---

## 一、现状诊断

### 1.1 代码事实

| 文件 | 行数 | 核心问题 |
|------|------|---------|
| `App.axaml.cs` | ~1000+ | 承担组合根：数据库/AI/插件/网络/UI 服务全部在此注册 |
| `MainWindow.axaml.cs` | ~860 | 持有三模式视图引用，手动管理 IsVisible 切换 |
| `ChatView.axaml.cs` | ~807 | 专家模式所有逻辑集中在控件 code-behind |
| `WorkModeView.axaml.cs` | ~1211 | 工作模式消息渲染/状态机/EventHub 订阅全在此 |
| 共享输入区 | ~1400 | 三模式复用但通过 InputMode 枚举分支处理 |
| `WorkModeViewController.cs` | ~1126 | `_currentTimeline`/`_currentMainStep` 等单值字段，只维护当前一个任务 |

### 1.2 已有能力（不从零开始）

现有工作模式已能处理：任务创建/计划/步骤/并行块/嵌套工作流/子智能体任务/工具调用/风险授权/暂停恢复。

现有会议模式已能处理：多参与者/主持人/流式发言/思考过程/工具调用/等待用户/历史回放。

现有专家模式已能处理：子智能体编排诊断/附件/流式响应/Token 统计/会话恢复。

**核心缺口：现有 UI 缺跨任务的控制面和隔离的状态模型，不是缺多智能体能力本身。**

### 1.3 不可修补的根本问题

1. `MainWindowVm.CurrentMode` 是全局单值 → 无法同时管理多个运行中的任务
2. `WorkModeViewController._currentTimeline` 是单值字段 → 多任务并发事件会串入同一时间线
3. `App.axaml.cs` 是混合体（组合根 + 基础设施 + UI）→ 新功能每次都要在这里挖洞

---

## 二、重构目标

### 2.1 一句话定位

**从「带三个 Tab 的聊天窗口」变成「多 Agent 并行工作站」。**

### 2.2 六条核心原则

1. **任务是一等公民**：专家/工作/会议是任务的类型标签，不再是全局互斥状态。同一时刻可有多个任务并行运行。

2. **焦点与运行分离**：切换标签只改变「当前看哪个任务」，不停止任何后台任务。关闭标签 ≠ 停止任务。

3. **Agent 实例化**：同一个 Agent 定义可以在不同任务中产生多个独立实例，各自有独立状态和上下文。

4. **事件按 ID 路由**：所有 EventHub 事件必须携带 `TaskId + RunId + AgentInstanceId`，UI 层按 ID 路由，禁止依赖"当前任务"全局变量。

5. **存储分层**：沿用文件化存储方案——SQLite 存元数据与运行态，`.cortana/conversations/` 文件存大正文。UI 不直接扫目录。

6. **保留三模式行为契约**：专家/工作/会议的完整交互逻辑全部保留，重写呈现层，不复制旧 code-behind。

---

## 三、信息架构

### 3.1 核心对象模型

```
Workspace
└── WorkItem（Expert / Work / Meeting）
    └── Run（可暂停/恢复/取消/重试）
        └── AgentInstance（独立状态/上下文/Token 计数）
            └── ToolCall / Approval / Artifact
```

`WorkItem` 的焦点状态（打开/关闭标签）与 `Run` 的运行状态完全正交。

### 3.2 一级导航（7 个入口）

| 图标 | 入口 | 职责 |
|------|------|------|
| ⊞ | 总览 | 全局控制台：所有任务 + Agent + 审批 + 活动流 |
| ☑ | 任务 | 统一任务列表（Expert/Work/Meeting 统一管理）|
| 🤖 | 智能体 | Agent 定义管理 + 运行实例监控 |
| ⚠ | 审批 | 跨任务聚合所有等待处理的授权与询问 |
| 📚 | 历史 | 文件化存储浏览 + 归档/恢复/健康检查 |
| 📁 | 工作区 | 文件树 + 产物 + 插件/技能/MCP |
| ⚙ | 设置 | Provider/Model/Agent/Plugin/MCP/系统/存储 |

专家/工作/会议**只出现**在：「新建」下拉菜单 + 任务列表的模式标签。不再作为窗口顶部的全局切换器。

---

## 四、Shell 布局

### 4.1 整体结构

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  TITLE BAR 36px   ── 拖动区 ──   [工作区: Madorin]  ─── [─][□][✕]          │
├─────────────────────────────────────────────────────────────────────────────┤
│  HEADER BAR 44px  [＋新建▼] [⌘命令面板]  ────────  [⚡3运行] [⚠2审批] [⚙]  │
├────┬───────────────┬──────────────────────────────────────────┬─────────────┤
│    │               │  TASK TAB BAR 36px                       │             │
│    │               │  [⊞总览] [💬重构讨论×] [⚙数据分析●×] [＋]│             │
│ A  │  SIDE         ├──────────────────────────────────────────┤  INSPECTOR  │
│ C  │  PANEL        │                                          │  右侧检查器  │
│ T  │  220px        │  MAIN CONTENT                            │  320px      │
│ I  │  可折叠        │                                          │  可折叠     │
│ V  │               │  ● 总览仪表板                            │             │
│ I  │  任务列表      │  ● 专家模式：聊天气泡 + 输入框           │  当前选中对象│
│ T  │  Agent 列表   │  ● 工作模式：时间线 + 授权浮窗           │  的详细状态  │
│ Y  │  文件树        │  ● 会议模式：多角色气泡 + 成员栏         │             │
│    │  历史索引      │                                          │             │
│ B  │               │                                          │             │
│ A  │               ├──────────────────────────────────────────┤             │
│ R  │               │  AGENT STRIP 28px折叠 / 160px展开        │             │
│    │               │  ●Claude:生成中  ●GPT4o:工具调用  ●等待  │             │
│ 48px              ├──────────────────────────────────────────┴─────────────┤
│    │               │  STATUS BAR 24px  ●3运行  ⚠2审批  42k  14:23:05      │
└────┴───────────────┴─────────────────────────────────────────────────────────┘
```

### 4.2 尺寸规格

| 区域 | 尺寸 | 备注 |
|------|------|------|
| Activity Bar | 48px 宽 | 始终可见 |
| Side Panel | 220~320px 宽 | 可拖拽/折叠 |
| Header Bar | 44px 高 | 固定 |
| Task Tab Bar | 36px 高 | 固定 |
| Agent Strip | 28px折叠 / 160px展开 | 展开向上推主内容 |
| Status Bar | 24px 高 | 固定 |
| Inspector | 320~380px 宽 | Phase 2 实现 |
| 默认窗口 | 1440 × 900 | 最小 1024 × 720 |

---

## 五、各区域详细设计

### 5.1 总览仪表板（默认首页）

**运行摘要带**
```
[● 3 任务运行中]  [🤖 5 Agent 实例]  [⚠ 2 待审批]  [✕ 0 失败]  [今日 124k tokens]
```

**需要关注队列**（按严重级别 + 等待时长排序）

**活跃任务表**

| 标题 | 模式 | 状态 | 进度 | Agent | 耗时 | 最后活动 |
|------|------|------|------|-------|------|---------|
| 登录模块重写 | ⚙工作 | ●运行中 | 3/8步 | Claude | 12m | 刚才 |
| 架构评审会 | 👥会议 | ●运行中 | 发言中 | 3人 | 28m | 2s前 |
| 代码优化讨论 | 💬专家 | ●运行中 | — | GPT-4o | 5m | 1s前 |

**Agent 实例矩阵** + **实时活动流**

### 5.2 任务列表（Side Panel）

```
┌──────────────────────┐
│ 🔍 搜索任务...         │
│ [全部][运行中][已完成]  │
├──────────────────────┤
│ 运行中 (3)            │
│  ● 登录模块重写  ⚙   │
│    Claude · 步骤3/8   │
│  ● 架构评审会  👥     │
│    3 Agent · 发言中   │
│  ● 代码优化讨论  💬   │
│    GPT-4o · 生成中    │
├──────────────────────┤
│ 今天完成 (2)          │
│  ✓ 需求分析  ⚙       │
└──────────────────────┘
```

模式色标：💬 蓝绿（专家）/ ⚙ 橙（工作）/ 👥 紫（会议）

### 5.3 Agent Monitor Strip

**折叠（28px）**：单行摘要，实时步骤文字滚动

**展开（160px）**：每个 Agent 一张卡片

```
┌─────────────────────┐
│ 🤖 Claude-3.7       │
│ ● 运行中             │
│ 登录模块重写         │
│ ▶ 生成 auth.cs...   │
│ 步骤 3/8            │
│ 8.2k ████░ 上限64k  │
│ [聚焦][暂停][停止]  │
└─────────────────────┘
```

支持「分离」为独立浮动窗口（双屏场景）。

### 5.4 右侧检查器（Inspector）

根据选中对象动态切换内容：任务详情 / Agent 详情 / 审批详情 / 产物详情。

---

## 六、三模式视图

### 6.1 专家模式

**保留**：消息气泡/Markdown/工具过程卡片/流式响应/停止/附件/会话恢复/诊断面板

**新增**：顶部任务标题栏（会话名/关联 Agent/Token）；「转为工作任务」入口

### 6.2 工作模式

**保留**：计划步骤/并行块/嵌套工作流/工具过程/询问/授权浮窗/暂停恢复

**新增 Tab**：`[计划与进度] [Agent 泳道] [活动流] [对话] [产物] [运行记录]`

- **Agent 泳道**：每个 AgentInstance 一行，展示步骤/工具/等待关系
- **产物**：新增/修改/删除文件，支持 diff 检查

### 6.3 会议模式

**保留**：参与者/主持人/流式发言/思考/工具/用户插话/历史回放

**新增**：议程进度指示器；行动项（可批量转为工作任务）；会议结束自动写 `.cortana/meetings/` Markdown 摘要

---

## 七、状态模型

### 7.1 任务状态机

```
Draft → Queued → Planning → Running
                             ├→ WaitingApproval → Running
                             ├→ WaitingUser      → Running
                             ├→ Paused           → Running
                             └→ Recovering       → Running
Running / Waiting / Paused → Completed | Failed | Cancelled
```

三个正交维度（不塞进同一个枚举）：
- **状态**：任务在做什么
- **健康**：Healthy / Delayed / Disconnected / Degraded
- **关注**：None / Info / Action / Critical

### 7.2 Agent 实例状态机

```
Starting → Running → Completed
            ├→ WaitingTool / WaitingAgent / WaitingUser → Running
            ├→ Paused → Running
            └→ Failed / Cancelled
```

同一 Agent 定义同时参与两个任务时，UI 展示两个独立实例，**不合并**。

---

## 八、事件路由与状态仓库

```
EventHub 领域事件
    └→ WorkbenchEventNormalizer（补齐 TaskId/RunId/AgentInstanceId/Sequence）
        └→ ExecutionProjectionService（按 ID 路由，批次合并高频事件）
            └→ WorkbenchStateStore（持有多个 TaskVm / AgentInstanceVm）
                └→ 页面级只读快照 → ViewModel → UI
```

- 高频流式事件以 50~100ms 批次合并，避免每个 token 触发布局
- 页面销毁不清除运行态
- 重启后用数据库运行态和文件记录重新对账

---

## 九、新项目结构

```
Src/Netor.Madorin.Workbench/
├── Entities/     # UI 只读快照、状态值对象
├── Core/         # 导航/命令/查询契约，禁止依赖 Avalonia
├── Services/     # EventHub 订阅、状态仓库、文件存储适配
└── Desktop/      # Avalonia Shell + Views + ViewModels
    ├── Shell/    (AppShell / ActivityBar / SidePanel / TaskTabBar / StatusBar)
    ├── Header/   (HeaderBar / GlobalStatusChip)
    ├── Overview/ (OverviewPage / TaskTable / AgentMatrix)
    ├── Panels/   (TaskListPanel / AgentRegistryPanel / WorkspacePanel / HistoryPanel)
    ├── AgentMonitor/ (AgentMonitorStrip / AgentCard / AgentFloatWindow)
    ├── Inspector/
    ├── Modes/    (ExpertMode / WorkMode / MeetingMode)
    └── Common/   (ChatBubble / MarkdownRenderer / InputAreaView)
```

依赖方向：`Desktop → Core → Entities`，`Services → Core + Entities + 现有后端`

Desktop ViewModel **不得直接引用** `CortanaDbContext` / `WorkTaskService` / `WorkflowExecutor`。

---

## 十、技术约束（不变）

| 项目 | 保持 |
|------|------|
| UI 框架 | Avalonia 12 + .NET 10 |
| 发布 | Native AOT，win-x64 |
| 后端引用 | Entitys / AI / Voice / Plugin / Networks / Store 全部不变 |
| 编排 SDK | Microsoft.Agents.AI.Workflows |
| 事件总线 | Netor.EventHub |
| 设计 Token | 迁移 DesignTokens / ThemeResources / SharedStyles |

---

## 十一、决策清单

| # | 问题 | 推荐 |
|---|------|------|
| D1 | 项目命名 | `Netor.Madorin.Workbench` |
| D2 | 右侧检查器首版实现 | 是（Phase 2） |
| D3 | 首版并行任务上限 | 不强制，超 5 个时提示 |
| D4 | 双任务分屏 | Phase 4 可选，首版不做 |
| D5 | 旧 UI 处理 | 存档，双版本并行一个周期后下线 |

---

## 十二、实施阶段

| 阶段 | 内容 | 估时 |
|------|------|------|
| Phase 0 | 验证执行引擎承载 ≥3 并行 Run；补齐事件 ID 字段 | 1~2 周 |
| Phase 1 | 新项目骨架；Shell 布局；导航；TabBar；设计 Token 迁移 | 2~3 周 |
| Phase 2 | 总览仪表板；任务列表；AgentMonitorStrip；事件投影层 | 3~5 周 |
| Phase 3 | 三模式重建（专家→工作→会议），保留行为契约，重写呈现层 | 5~8 周 |
| Phase 4 | 历史文件化对接；存储健康页；审批中心；可选分屏 | 3~5 周 |
| Phase 5 | 双 UI 并行测试；功能矩阵对齐；切换默认入口；旧 UI 归档 | 2~4 周 |

**总周期：15~25 周。**

---

## 十三、首版验收标准

1. 同时运行 3 个工作任务 + 1 个专家会话 + 1 个会议，切换焦点不丢事件、不串消息、不停止后台运行。
2. 总览 1 秒内定位所有运行中 Agent 的所属任务、当前步骤和等待原因。
3. 关闭运行中标签后任务继续运行，可从总览重新打开。
4. 应用重启后能恢复活跃对象或明确标记为可恢复/失败/断联。
5. 历史列表只读元数据，打开时按需读 JSONL，文件缺失有降级结果。
6. 20 个任务/50 个 Agent 实例/每秒 100 条事件的压力下 UI 线程保持可交互。
7. Native AOT Release 发布成功，三模式核心链路通过烟雾测试。
