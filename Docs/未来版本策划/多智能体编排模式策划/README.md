# 多智能体编排模式策划方案（总览 / 入口）

> **文档定位**：本文档是多智能体编排方案的**入口与导航页**。详细技术内容已拆分到 7 篇子文档，按顺序阅读即可掌握全部方案。  
> **当前版本**：v2.4 阶段 6 全部 4 个 Phase 收口（Token 聚合 + 工具权限任务级收窄 + UI 体验统一/搜索/Banner 频控 + 长期记忆 owner 控制；用户决策 1-A / 2-B / 3-A / 4-A / 5-B / 6-A / 7-A / 8-A / 9-A / 10-A / 11-A / 12-A + 6-1-A/B/C / 6-2-A/B/C / 6-3-A/B/C/D / 6-4-A/B/C 已落地）  
> **历史快照**：原"多智能体编排模式策划方案.md"归档为 `_archive_v1快照.md`。**新工作请只参考本入口和拆分文档**，不再修改快照文件。

---

## 0. 阅读指南

| 顺序 | 文档 | 篇幅 | 读者关注点 |
| --- | --- | --- | --- |
| 1 | [01-SDK源码研究笔记](./01-SDK源码研究笔记.md) | 中 | 项目侧外的事实基础：`agent-framework` 1.5.0 的真实 API、默认值、Experimental 状态 |
| 2 | [02-当前代码现状](./02-当前代码现状.md) | 中 | 项目侧的底座：`AIAgentFactory`、`ChatHistoryDataProvider`、StateBag 键、token 追踪、**事件总线与 PluginBus 现状** |
| 3 | [03-编排模式与边界约束](./03-编排模式与边界约束.md) | 长 | **双层模式分类**、模式定位、运行边界、历史保存策略、事件分流原则 |
| 4 | [04-实施阶段](./04-实施阶段.md) | 长 | **Track A（Chat）+ Track B（Workflow）双轨制**，阶段 0-6 落地指南，Memory 同步升级要求 |
| 5 | [05-风险与规避](./05-风险与规避.md) | 中 | 18 项已知风险（含 Memory 兼容性 / AOT / UI 渲染新风险），分双轨阶段对照表 |
| 6 | [06-工作模式独立模块设计](./06-工作模式独立模块设计.md) | 长 | Workflow 数据模型、UI Tab 设计、与 Chat 的双向联动协议、任务恢复策略 |
| 7 | [07-事件分流与插件兼容设计](./07-事件分流与插件兼容设计.md) | 长 | PluginBus `workflow` topic、事件族 schema、Memory 插件接入、版本兼容矩阵 |

**最短阅读路径**（仅了解整体方案）：本入口 → [03](./03-编排模式与边界约束.md) §1 双层模式 → [04](./04-实施阶段.md) 总览。

**完整阅读路径**（参与实施）：按 1 → 2 → 3 → 4 → 5 → 6 → 7 顺序读完。

---

## 1. 核心设计原则

### 1.1 顶层范式：双层模式分离

```text
Madorin 顶层入口
  ├── 对话模式（Chat）          ← 现状，UI 一问一答
  └── 工作模式（Workflow）      ← 新增，UI 任务卡片（独立 Tab）
        ├── Discussion (GroupChat)
        └── Execution (Magentic / ParallelAnalysis)
```

理由：对话与工作流是**两种交互范式**，不是 UI 层面的小区别。强行合并会让程序逻辑、UI 渲染、执行流程三重混乱（详见 [03](./03-编排模式与边界约束.md) §1.2 的范式差异表）。

### 1.2 资源彻底隔离

| 资源 | Chat | Workflow |
| --- | --- | --- |
| 数据库表 | `ChatSessions` / `ChatMessages` | `OrchestrationTask` / `OrchestrationStep` / `OrchestrationParticipant` / `OrchestrationMessage` |
| EventHub 事件 | `conversation.*` | `workflow.*` |
| PluginBus topic | `conversation` | `workflow` |
| 历史回放 dispatcher | `PluginBusConversationHistoryDispatcher` | `PluginBusWorkflowHistoryDispatcher` |
| Memory 入库 EventHandler | `MemoryConversationEventHandler` | `MemoryWorkflowEventHandler` |
| UI 入口 | "对话" Tab | "工作台" Tab |
| 取消语义 | 取消当前一轮 | 取消整个任务 |
| 流式 token 上 PluginBus | ✅ 是 | ❌ 否（决策 3-A） |

### 1.3 设计原则

1. **复用 SDK 能力**：`Microsoft.Agents.AI.Workflows` 已实现 Magentic / GroupChat / Handoff / Concurrent，项目侧不重复造轮子。
2. **不改 Chat 主链路**：利用 `Workflow.AsAIAgent()` 把 Workflow 包成 `AIAgent`，`AiChatHostedService._agent.RunStreamingAsync(...)` 保持不变（详见 [01](./01-SDK源码研究笔记.md) §6）。
3. **双层数据彻底隔离**：Chat 与 Workflow 的事件、表、协议完全分离，互不污染。
4. **第一阶段不动**：`AgentEntity` 字段、`IAiChatEngine` 契约、Chat 数据库表、`conversation.*` 事件字段（详见 [02](./02-当前代码现状.md) §6）。
5. **历史完整优先**：Chat 模式保留工具调用链协议；Workflow 模式中间消息走独立表（详见 [03](./03-编排模式与边界约束.md) §5）。
6. **Memory 同步升级**：Workflow 上线（阶段 4B）前 Memory 插件必须同步发版（详见 [07](./07-事件分流与插件兼容设计.md) §6）。
7. **任务列表与恢复机制对齐 Chat 风格**：Workflow 任务列表与 ChatHistoryPanel 视觉 / 字段 / 排序对齐（决策 7-A），但启动时**不自动选中任何任务**（决策 8-A，与 Chat 的范式差异）。详见 [02](./02-当前代码现状.md) §10 与 [06](./06-工作模式独立模块设计.md) §3 / §7。
8. **标题异步生成对齐 Chat**：Workflow 任务标题在用户未填时占位 = `InitialInput.Truncate(32)`，任务完成后异步触发 LLM 生成（决策 6-A）。复用 `IChatCompactionClientResolver` + `SuppressUsage`，事件独立（`OnWorkflowTaskTitleUpdated`）。详见 [06](./06-工作模式独立模块设计.md) §4.6。
9. **AOT 风控前置**：项目 Release 已启用 `PublishAot=true`，引入 SDK Workflow 能力时必须扩展 `TrimmerRootAssembly` + `rd.xml`，每个阶段都跑 AOT 烟雾测试（决策 9 之延伸）。详见 [05](./05-风险与规避.md) §风险 17、[04](./04-实施阶段.md) §0.5。
10. **Workflow UI 走 MVVM 三层**：Workspace Tab 用 View / VM / Model 分离，VirtualizingStackPanel + ItemsRepeater 虚拟化，与 Chat Tab 命令式现状解耦（决策 11-A / 12-A）。详见 [05](./05-风险与规避.md) §风险 18、[06](./06-工作模式独立模块设计.md) §5.8。

---

## 2. 模式选择决策表

详见 [03](./03-编排模式与边界约束.md) §11。

| 用户意图 | 推荐顶层模式 | 推荐子模式 | 关键信号 |
| --- | --- | --- | --- |
| 普通问答 | Chat | `None` | 没 @ 任何智能体 |
| 咨询单专家 | Chat | `None`（直接对话） | @ 1 个智能体 |
| 咨询多专家 | Chat | `ToolDelegation` | @ 2-3 个智能体 |
| 客服分流 / 主智能体不擅长 | Chat | `HandoffChat` | 当前 Agent 明确不擅长某个领域 |
| 复杂任务拆解 | **Workflow / Execution** | `Magentic` | "帮我做"+多步骤 |
| 方案讨论 | **Workflow / Discussion** | `GroupChat` | "讨论一下"+多角色 |
| 多专家独立分析 | **Workflow / Execution** | `ParallelAnalysis` | "并行分析"/"多视角对比" |
| 长时执行专家接管 | Workflow / Execution | `HandoffExecution`（阶段 5B 待评估） | 主 Agent 转专家 + 多步执行 |

---

## 3. 实施路线图（双轨制）

详见 [04-实施阶段](./04-实施阶段.md)，本节仅给路线图。

```text
阶段 0：清理（前置）
    │
阶段 1：Chat 内增强 Coordinator（仍属 Chat 模式）
    │
    ├──── Track A（Chat 模式收口）
    │       阶段 2A：IAgentOrchestrator 抽象
    │       阶段 3A：HandoffChat（客服分流）
    │
    └──── Track B（Workflow 模式新建）  ↑ 与 Track A 并行
            阶段 2B：Workflow 数据模型 + IWorkflowExecutor
            阶段 3B：Workflow UI Tab + GroupChat
            阶段 4B：Magentic + ParallelAnalysis  ← Memory 插件同步升级
            阶段 5B：HITL + 任务恢复 + Chat→Workflow 推荐
              │
阶段 6：跨模式联动收口
```

### 3.1 改动量与周期

| 阶段 | 目标 | 是否架构级 | 改动量 | 周期 |
| --- | --- | --- | --- | --- |
| 0 | csproj NoWarn + Agent 元数据三处统一 | 否 | S | 0.5 周 |
| 1 | Chat 内 Coordinator instructions 增强 | 否 | S | 1 周 |
| 2A | `IAgentOrchestrator` 抽象 | 否 | S-M | 1.5 周 |
| 3A | Chat 内 HandoffChat | 否 | M | 1 周 |
| 2B | Workflow 数据模型 + 服务壳子 + DB 迁移 | 是 | M-L | 2 周 |
| 3B | Workflow UI Tab + GroupChat | 是 | L | 2-3 周 |
| 4B | Magentic + ParallelAnalysis + Memory 同步升级 | 是 | L | 2-3 周 |
| 5B | HITL + 任务恢复 + Chat→Workflow 推荐 | 是 | L | 3-5 周 |
| 6 | 跨模式联动收口 | 否 | M | 1 周 |

**Track A**（阶段 0/1/2A/3A）：约 5 周  
**Track B**（阶段 0/2B/3B/4B/5B）：约 9-12 周  
两条 Track 可在阶段 1 完成后**并行推进**。

---

## 4. 关键决策回顾

| # | 决策 | 选项 | 影响范围 |
| --- | --- | --- | --- |
| 1 | Workflow 事件 topic | **A：新 topic `workflow`** | PluginBus 协议扩展，详见 [07](./07-事件分流与插件兼容设计.md) §3 |
| 2 | Memory 入库策略 | **B：自动入库 `workflow.task.completed` 的 FinalReport** | Memory 插件需新增 `MemoryWorkflowEventHandler` |
| 3 | Workflow 流式 token | **A：不上 PluginBus，仅给 UI** | 节省带宽，避免 Memory 流式 buffer 污染 |
| 4 | Workflow 历史回放 | **A：独立 dispatcher** | Task 级别粒度，不回放 Step / Message |
| 5 | Memory 插件版本协调 | **B：Workflow 上线前同步更新** | 引入版本兼容性风险（[05](./05-风险与规避.md) §风险 16），需 Release 流程门控 |
| 6 | 任务标题生成策略 | **A：用户填可选 + LLM 兜底** | 任务完成后异步触发，复用 Chat 标题生成机制，新增 `OnWorkflowTaskTitleUpdated` 事件（[06](./06-工作模式独立模块设计.md) §4.6） |
| 7 | 任务列表是否对齐 Chat 风格 | **A：完全对齐** | OrchestrationTask 补 `Summary` / `IsPinned` / `IsArchived` / `LastActiveTimestamp` / `TotalTokenCount` / `IsTitleAutoGenerated`；UI 复用 ChatHistoryPanel 视觉与代码模式（[06](./06-工作模式独立模块设计.md) §3 / §5） |
| 8 | 启动时是否自动选中任务 | **A：不自动选中** | 与 Chat `LoadOrCreateSessionAsync` 行为差异化，避免打扰运行中或完成的任务（[06](./06-工作模式独立模块设计.md) §7.1） |
| 9 | 宿主重启后的 running 任务处理 | **A：标记 failed + SystemNotice** | 启动扫描清理孤儿任务，错误信息明示重启导致；阶段 5B+ 接入 Checkpoint 后改为先尝试恢复（[06](./06-工作模式独立模块设计.md) §4.8） |
| 10 | 任务复制为新模板 | **A：3B 起支持** | 新增 `BuildRequestFromTemplateAsync` API 与 `SourceTaskId` 字段，UI 右键菜单"复制为新任务"（[06](./06-工作模式独立模块设计.md) §4.7） |
| 11 | Workflow UI 是否走数据驱动 | **A：MVVM 三层架构（仅 Workflow Tab）** | Workspace Tab 用 View / VM / Model 分离，VirtualizingStackPanel + ItemsRepeater 虚拟化；Chat Tab 现状不变。详见 [05](./05-风险与规避.md) §风险 18 / [06](./06-工作模式独立模块设计.md) §5.8 |
| 12 | 是否同时打开多个任务详情 | **A：不支持，单详情区共享** | 避免渲染压力，符合"一次只看一个"的工作模式心智；任务列表可滚动浏览多条但详情区只显示当前选中（[06](./06-工作模式独立模块设计.md) §5.8.3） |

---

## 5. 核心结论汇总

### 5.1 SDK 事实层（来自 [01](./01-SDK源码研究笔记.md)）

| 结论 | 出处 | 对实施的影响 |
| --- | --- | --- |
| Workflow 可直接 `.AsAIAgent()` | §6 | 阶段 3A / 3B / 4B 改动量降为 S-M 级 |
| Magentic 默认要 HITL | §3.1 | 阶段 4B 必须 `.RequirePlanSignoff(false)` |
| GroupChat 默认 40 轮 | §4 | 必须显式设置 `MaximumIterationCount = 3` |
| Magentic 单任务 ≥13 次 LLM | §3.2 | 阶段 4B 提交前给用户成本提示 |
| `AsAIFunction` 默认单参数 | §7 | 附件传递走自定义委托 |
| `AgentSkillsProvider` sealed | §9 | 按 Agent 隔离技能走自定义 `AgentSkillsSource` |
| `AgentSession` 跨 Agent 不可复用 | §13 | 子 Agent 必须独立 session |
| `StateKeys` 唯一性强约束 | §8 | 同类型多实例必须重写 StateKeys |
| Experimental 诊断 MAAIW001/MAAI001 | §15 | csproj NoWarn 需要配置 |

### 5.2 项目事实层（来自 [02](./02-当前代码现状.md)）

| 结论 | 出处 | 对实施的影响 |
| --- | --- | --- |
| Memory 插件已订阅 `conversation` topic 在生产运行 | §9.6 | 不能动 `conversation.*` 字段，必须新增 `workflow` topic |
| Memory 入库依赖单 Agent 模型 | §9.6 | Workflow 多 Agent 中间消息绝不进 ObservationRecord |
| Memory 按 `assistantMessageId` 维护 `_assistantStreams` 字典 | §9.6 | Workflow 不发流式 delta（决策 3-A） |
| 历史回放只查 `ChatMessages` | §9.4 | 需要新增 `PluginBusWorkflowHistoryDispatcher` |
| `ChatHistoryDataProvider` 时间戳归一化依赖单线程 | §4.2 | Workflow 中间消息走独立表，归避并发风险 |

---

## 6. 改动量评估表（最终结论）

| 能力 | 当前基础 | 修改量 | 是否架构级 | 归属 Track |
| --- | --- | --- | --- | --- |
| @ 子智能体工具调用 | 已有 `BuildWithSubAgents` | S | 否 | A |
| Coordinator 策划/分派/总结 | 可基于 instructions 增强 | S | 否 | A |
| Chat 内 Handoff（客服分流） | 利用 SDK `HandoffWorkflowBuilder` | M | 否 | A |
| Workflow 数据模型 + 服务壳子 | 新建 | M-L | **是** | B |
| GroupChat 讨论模式 | 利用 SDK `GroupChatWorkflowBuilder` | M | **是**（含 UI） | B |
| Magentic 完整流程 | 利用 SDK `MagenticWorkflowBuilder` | L | **是** | B |
| ParallelAnalysis | 利用 SDK `BuildConcurrent` + 自定义 aggregator | M | **是** | B |
| Memory 插件订阅 workflow topic | Memory 插件新增 handler | S-M | 否 | **B 配套** |
| HITL 计划批准 | 当前不支持 | M-L | 是（UI / WebSocket） | B |
| 任务恢复 | 当前不支持 | M | 是 | B |
| Chat ↔ Workflow 联动 | 当前不存在 | M | 否 | 阶段 6 |

> **关键利好**：得益于双层模式分离 + `Workflow.AsAIAgent()`，Chat 模式（Track A）改动量保持极小，所有架构级变更集中在 Workflow 独立模块（Track B），互不干扰。

---

## 7. 推荐实施顺序

```text
1. 阶段 0：清理（元数据统一 + Experimental NoWarn）
2. 阶段 1：Coordinator 模式（Chat 内增强）
3. 阶段 2A & 阶段 2B 并行启动：
   - Track A 继续 Chat 模式收口
   - Track B 启动 Workflow 数据模型与服务壳子
4. 阶段 3A（HandoffChat）+ 阶段 3B（Workflow UI + GroupChat）
5. 阶段 4B（Magentic + ParallelAnalysis）—— 同步升级 Memory 插件
6. 阶段 5B（HITL + 任务恢复 + Chat→Workflow 推荐）
7. 阶段 6：跨模式联动收口
```

**关键节点**：

- **阶段 1 是分叉点**：之后两条 Track 可并行
- **阶段 4B 是版本协调点**：宿主与 Memory 插件必须同步发版（决策 5-B）
- **阶段 5B 是 HITL 启用点**：之前 Magentic 强制 `RequirePlanSignoff(false)`

**禁止操作**：

- 不要一开始就做完整 Magentic + UI 可视化（违反渐进原则）
- 不要把 Workflow 中间消息塞进 `ChatMessages` 表（违反数据隔离）
- 不要在 `conversation` topic 上加 Workflow 事件（违反协议兼容）
- 不要在 Memory 插件未升级时上线阶段 4B（违反版本协调）

---

## 8. 评审检查清单（合并 PR 前必查）

完整列表见 [05-风险与规避](./05-风险与规避.md) §评审检查清单。核心项：

### 通用项

- [ ] csproj 包含 `<NoWarn>$(NoWarn);MAAIW001;MAAI001</NoWarn>`
- [ ] Workflow.AsAIAgent 包装的 Agent 有非空 Id/Name/Description
- [ ] 没有 `subAgent.RunAsync(... session: someSharedSession ...)` 模式
- [ ] 多实例 Provider 重写了 `StateKeys`

### Track A（Chat 模式）

- [ ] `AgentOrchestrator` 仅服务 Chat 模式（None / ToolDelegation / HandoffChat）
- [ ] 改动不影响 `ChatHistoryDataProvider` 时间戳归一化策略
- [ ] 不引入新的 PluginBus topic
- [ ] Memory 插件协议保持向后兼容（`conversation.*` 字段不变）

### Track B（Workflow 模式）

- [ ] `IWorkflowExecutor` 中间消息只写 `OrchestrationMessage` 表，**不写** `ChatMessages`
- [ ] Magentic 显式 `.RequirePlanSignoff(false)`（直到阶段 5B）
- [ ] GroupChat 显式覆盖 `MaximumIterationCount`
- [ ] Workflow 事件走 `workflow` topic，**不走** `conversation`
- [ ] PluginBus 不发 Workflow 流式 delta
- [ ] `PluginBusWorkflowHistoryDispatcher` 只查 `OrchestrationTask` 表
- [ ] OrchestrationTask 字段对齐 ChatSession（决策 7-A）
- [ ] 任务列表查询按 `IsPinned DESC, LastActiveTimestamp DESC` 排序，分页 30 条
- [ ] **工作台 Tab 启动时不自动选中任何任务**（决策 8-A）
- [ ] 启动时孤儿任务清理（running/paused/pending → failed + SystemNotice，决策 9-A）
- [ ] 任务标题用户填可选；留空时占位 = `InitialInput.Truncate(32)` 并标记 `IsTitleAutoGenerated=1`（决策 6-A）
- [ ] 任务完成后异步触发 LLM 标题生成，复用 `IChatCompactionClientResolver` + `SuppressUsage`（决策 6-A）
- [ ] `OnWorkflowTaskTitleUpdated` 事件已实现，UI 局部刷新对应列表项（决策 6-A）
- [ ] Memory 插件主动 swallow `workflow.task.title.updated` 不入库
- [ ] "复制为新任务"功能可用，`SourceTaskId` 正确写入（决策 10-A）
- [ ] **Workspace Tab 走 MVVM 三层架构**，命令式 UI 构造代码 ≤ 5 处（决策 11-A）
- [ ] 任务列表用 `VirtualizingStackPanel`，60+ 任务下滚动流畅（决策 11-A）
- [ ] 任务详情 Step 列表用 `ItemsRepeater`，20+ 步骤无卡顿（决策 11-A）
- [ ] 不支持同时打开多个任务详情，单详情区共享（决策 12-A）
- [ ] AOT Release 构建成功，Madorin.exe 启动并能完成一个 GroupChat 任务（关联风险 17）

### AOT 发布门控（每个阶段必查）

- [ ] `dotnet publish -c Release -r win-x64` 成功，无 IL2026/IL3050/IL3053 错误
- [ ] 新增 `Microsoft.Agents.AI.*` API 时同步更新 `TrimmerRootAssembly` 与 `rd.xml`
- [ ] 新增 `JsonSerializer.Serialize/Deserialize` 必须用 source-gen 或 `[DynamicallyAccessedMembers]`
- [ ] AOT Release 产物启动不崩溃（无 PlatformNotSupportedException / MissingMetadataException）

### 阶段 4B 特有

- [ ] 宿主与 Memory 插件版本号同步对齐
- [ ] Memory 插件 `MemoryWorkflowEventHandler` 已实现并测试
- [ ] 协议版本握手字段 `capabilities.workflow.topic.minVersion` 已加入
- [ ] 旧 Memory 插件连接新宿主时正确降级

---

## 9. 文档维护约定

- **事实优先级**：项目代码 > SDK 源码 > 本系列文档 > 原长快照
- 修改 SDK 默认值的事实时，先更新 [01](./01-SDK源码研究笔记.md)，再级联检查 [03](./03-编排模式与边界约束.md) / [04](./04-实施阶段.md) / [05](./05-风险与规避.md) / [06](./06-工作模式独立模块设计.md)
- 新增项目侧底层改动时，先更新 [02](./02-当前代码现状.md)，再级联检查后续文档
- 修改 PluginBus / 事件协议时，先更新 [07](./07-事件分流与插件兼容设计.md)，再级联检查 [02](./02-当前代码现状.md) §9 / [04](./04-实施阶段.md) 阶段 4B / [05](./05-风险与规避.md) §风险 16
- 阶段验收完成后，把对应 [04](./04-实施阶段.md) 阶段标记为 "✅ 完成"，并把实际改动数据回填到改动量表
- 一篇子文档超过 700 行时考虑进一步拆分

---

## 10. 历史变更

| 版本 | 日期 | 变更摘要 |
| --- | --- | --- |
| v1 | 早期 | 单文档 1289 行，未区分 Chat / Workflow 模式 |
| v1.1 | 拆分日 | 拆分为 6 文档（README + 01-05），统一模式枚举 |
| v2 | 拆分日 + 1 | 引入双层模式（Chat / Workflow）+ 事件分流（conversation / workflow topic）+ 双轨制实施（Track A / Track B）+ Memory 同步升级要求 |
| v2.1 | 拆分日 + 2 | 任务列表 / 标题生成 / 恢复机制对齐 Chat 风格（决策 6-A / 7-A / 8-A / 9-A / 10-A）：02 新增 §10 Chat session 标题生成事实层；06 补 OrchestrationTask 字段（Summary / IsPinned / IsArchived / LastActiveTimestamp / IsTitleAutoGenerated / SourceTaskId）+ §4.6 异步 LLM 标题生成 + §4.7 复制任务 + §4.8 启动孤儿清理 + §7 状态分流恢复流程；07 新增 `workflow.task.title.updated` 事件；04 各阶段补 DB 字段迁移 / 列表 API / 复制任务 / LLM 标题生成实施要点。 |
| v2.2 | 阶段 4B 完成 | 阶段 4A + 4B 已实施：Magentic / ParallelAnalysis 子模式接入（`MagenticWorkflowFactory` + `ParallelAnalysisWorkflowFactory`，复用 3B `Builders/` 模式）；`WorkflowExecutor` 升级为 SubMode 白名单分发（`IsRealWorkflowSubMode`），通用化为 `StartRealWorkflowBackground` / `RunWorkflowAsync`；`NewTaskDialog` 解锁两个 ComboBox 选项；Memory 插件同步升级（决策 5-B）：新增 `MemoryWorkflowEventHandler` + 订阅 `workflow` topic + dispatcher workflow 路由 + DI 注册；SDK `Microsoft.Agents.AI.*` 1.3.0 → 1.5.0 升级（含 `Microsoft.Extensions.AI` 10.5.0 → 10.5.1）。**推到 5B 的待办**：① Magentic token 成本警告 UI 弹窗（[04] §4B.6）—— 阶段 5B 接入 HITL UI 时一并实现；② PluginBus workflow 协议版本号握手（[04] §4B.1）—— 4B 通过单 commit 同步 host + plugin 实现"原子发版"，运行时版本号检查推到 5B。 |
| v2.2 | AOT + MVVM | AOT 风控 + Workflow UI MVVM 三层（决策 11-A / 12-A）：05 新增风险 17（Native AOT 发布兼容性）+ 风险 18（UI 渲染架构无法支撑 Workflow 模式）；04 阶段 0 新增 §0.5 AOT 基线校验（TrimmerRootAssembly / rd.xml 扩展 + 烟雾测试 + 降级路径）；06 §5 新增 §5.8 Workflow UI 三层架构（View / VM / Model 文件组织 + 控件选型 + 数据流示例 + 生命周期）；README 阅读指南风险数 16 → 18，决策回顾表新增决策 11/12，Track B 评审清单新增 AOT 与 MVVM 验收项。 |
| v2.3 | 阶段 6 Phase 1+2+3 完成 | **阶段 6 三项 polish 已全部落地**（commit 452097e / c636d2e / 4e646f8）：① **Token 聚合**（Phase 1，决策 6-1-A/B/C）—— `AIAgentFactory` 新增 `CreateSubAgentTrackingClient` 把 sub-agent ChatClient 包成 `TokenTrackingChatClient`；`WorkflowExecutor.PersistAndPublishStep` 从 tracker 反查 step 级 token 持久化到 `OrchestrationStep`，`HandleTaskCompleted` 累加写到 `OrchestrationTask.TotalTokenCount`；Magentic 任务完成时用真实数据移动平均（权重 7:1）校准 `EstimatedTokenMultiplier`；UI 详情面板新增 `TotalTokens` 显示。② **工具权限任务级临时收窄**（Phase 2，决策 6-2-A/B/C）—— `OrchestrationTaskEntity` + DB 新增 `ToolBlacklistJson` 列；`WorkflowTaskRequest` 加 `ToolBlacklist` 字段；`AssembleToolProviders` / `BuildSubAgent` / `BuildWorkflowParticipants` 全链路透传 taskBlacklist，按 "pluginId" 整体跳过或 "pluginId:toolName" 细粒度排除；`NewTaskDialog` 一期硬编码 4 项高风险工具（sys_csx_script / sys_app_launcher / sys_window_manager / sys_office）CheckBox 列表。③ **UI 体验统一 + 搜索 + Banner 频率限制**（Phase 3，决策 6-3-A/B/D）—— Chat StopButton 加 `titlebar-btn-warn` hover 警示色 + ToolTip "停止当前对话"；Workflow CancelButton hover 色减弱（#8a3838 → #7a3030 与 Chat 节奏对齐）；详情面板加 HITL 暂停徽章（"⏸ 等待批准"）；Chat 历史 + Workflow 任务列表统一加搜索框（200ms 防抖 + 子串 LIKE 匹配，不引入 FTS5）；Magentic 成本预警 Banner 加"知道了"按钮 + 30 分钟跨会话抑制（SystemSettings.Workflow.Magentic.CostWarningSnoozeUntilMs）。 |
| **v2.4** | **阶段 6 全部完成（当前）** | **阶段 6 Phase 4 长期记忆 owner 控制已落地**（commit 待补 C4，决策 6-4-A 修订 / 6-4-B / 6-4-C）：`AgentEntity` 新增 `AllowWorkflowMemory` bool 字段（default true 向后兼容）；`MadorinDbContext` 加幂等 `ALTER TABLE Agents ADD COLUMN AllowWorkflowMemory INTEGER NOT NULL DEFAULT 1`；`AgentService` Insert/Update/Read/Bind 四处加字段处理；`WorkflowTaskCompletedArgs` 加可选 `AllowMemoryIngest` 参数（default true）；`WorkflowExecutor.ResolveAllowMemoryIngest` 根据 `request.ManagerAgentId` 查 Manager 配置位填充事件参数（fallback 异常时返回 true 避免静默丢数据）；Memory 插件 `MemoryWorkflowEventHandler` 实时事件 + 历史回放两条路径都加 `IsMemoryIngestAllowed` helper 检查（字段缺失默认 true 兼容旧 export 记录）；`AgentSettingsPage.axaml(.cs)` 新增 `ChkAllowWorkflowMemory` CheckBox（重置/加载/Update/Add 四处都加字段处理）。**关键决策修订**（plan §4.3 ⚠️ 副作用复核）：原方案"host 端不发事件"会破坏 `WorkflowTaskListVm` / `TaskDetailVm` / `WebSocketWorkflowFeedRelayService` 等订阅者；改为事件正常发，仅在事件参数中携带 owner 配置位由 Memory 插件按需丢弃 ingest 副作用，避免破坏其他订阅者。 |
