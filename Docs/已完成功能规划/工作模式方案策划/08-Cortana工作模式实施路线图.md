# 08 - Cortana 工作模式实施路线图

> 在 Cortana 项目中落地多智能体编排的分阶段计划

## 一、目标与原则

### 1.1 目标
- 短期：解决"主智能体被卡死"和"工具风险"两个紧迫问题
- 中期：建立"总经理-部门-员工"分层组织
- 长期：可扩展到多业务领域、跨进程协作、云原生部署

### 1.2 原则
1. **能用 AF 框架原生就不自造** — 减少维护成本
2. **逐步引入，可回退** — 每阶段都能独立工作
3. **Critic 优先** — 任何 LLM 输出在落地副作用前必须有验收
4. **持久化优先** — 早期就规划好状态/检查点存储
5. **审计可追溯** — 全链路日志 + Activity

---

## 二、阶段划分

### 阶段 0：调研验证（1-2 周）

**目标**：把 AF 框架在本地跑通，验证关键能力。

- [ ] 在 Cortana 仓库引入 `Microsoft.Agents.AI` / `Microsoft.Agents.AI.Workflows` 试点
- [ ] 跑通 `samples\01-get-started\01_hello_agent`（基本智能体）
- [ ] 跑通 `samples\03-workflows\_StartHere\03_AgentWorkflowPatterns`（4 种编排）
- [ ] 跑通 `samples\03-workflows\_StartHere\07_WriterCriticWorkflow`（验收循环）
- [ ] 跑通 `samples\03-workflows\Orchestration\Magentic`（Manager 模式）
- [ ] 跑通 `samples\03-workflows\HumanInTheLoop`（暂停恢复）
- [ ] 输出：技术验证报告（哪些好用、哪些坑、与项目现状的差距）

### 阶段 1：MVP 总经理（2-3 周）

**目标**：用 Magentic 替换/包装现有主智能体，**仅支持单部门、固定流程**。

#### 设计
```
老板 → 总经理(Magentic) → 唯一部门(Sequential: Worker → Critic)
```

#### 任务清单
- [ ] 定义 `Plan` / `Ledger` 数据模型（[07-业务模型映射设计.md](./07-业务模型映射设计.md) §7）
- [ ] 实现 `GeneralManagerAgent`（系统提示词 + 工具签名）
- [ ] 实现一个示例部门（如"代码生成部门"）
- [ ] 接入 `LoggingAgent` + `OpenTelemetryAgent`
- [ ] 实现 `FileSystemJsonCheckpointStore` 持久化
- [ ] 实现 HTTP 端点：`POST /api/tasks` → 返回 taskId
- [ ] 实现 SSE 端点：`GET /api/tasks/{id}/events`
- [ ] 前端：基础任务列表 + 实时进度展示

#### 验收
- 用户能提交任务，立即拿到 taskId
- 进度通过 SSE 流式展示
- 任务结果包含完整 Ledger（每步状态可见）
- 任意时刻杀进程，重启后任务能续上

---

### 阶段 2：多部门 + 验收循环（3-4 周）

**目标**：支持多个部门并存，引入完善的验收-重做机制。

#### 设计
```
老板 → 总经理 ──┬→ 技术部 (GroupChat)
                ├→ 运营部 (WriterCritic)
                └→ 质检部 (Sequential)
```

#### 任务清单
- [ ] 定义"部门"抽象（`IDepartment` 接口 + 工厂模式）
- [ ] 实现 3 个部门的 Workflow
- [ ] 部门内的 Critic 智能体（统一 Critic 模板）
- [ ] 总经理级 Critic（汇总验收）
- [ ] 部门 SLA 配置（最大重试次数、超时）
- [ ] 失败上报机制（部门彻底失败 → 总经理 Replan）
- [ ] Ledger 增强：记录所有退回/重做历史
- [ ] 前端：分部门进度展示

#### 验收
- 总经理能根据任务选择合适的部门组合
- 部门内部不达标自动循环（最多 N 次）
- 部门彻底失败时总经理能 Replan
- Ledger 中可以回溯所有重做历史

---

### 阶段 3：工具授权与沙盒（2-3 周）

**目标**：所有工具调用受控。

#### 任务清单
- [ ] 工具风险分级（参见 [05-工具授权与沙盒.md](./05-工具授权与沙盒.md) §4）
- [ ] 给中-高风险工具包装 `ApprovalRequiredAIFunction`
- [ ] 全局加 `ToolApprovalAgent`
- [ ] 引入 `Microsoft.Agents.AI.Tools.Shell`（如需 shell）
- [ ] 实现 `FileAccessProvider` 限制文件路径
- [ ] 前端：审批弹窗 UI
- [ ] WebSocket 实现客户端→服务端的审批响应
- [ ] 审计日志：所有审批入库
- [ ] "Don't ask again" 规则（带过期时间）

#### 可选（高安全场景）
- [ ] 引入 `Microsoft.Agents.AI.Hyperlight` 沙盒
- [ ] LLM 生成代码必须在 Hyperlight 内执行

#### 验收
- 高危工具不会未经批准就执行
- 审批 UI 显示完整上下文（哪个智能体、调用什么工具、参数）
- 审批日志可审计
- 拒绝后智能体能优雅降级

---

### 阶段 4：跨进程集成（2 周）

**目标**：用 A2A 协议整合现有独立进程（视觉/语音）。

#### 任务清单
- [ ] 视觉模型服务暴露 A2A 端点（参考 `samples\04-hosting\A2A`）
- [ ] 语音服务暴露 A2A 端点
- [ ] 主程序用 `A2ACardResolver` 发现远端智能体
- [ ] 把远端智能体作为"部门"加入总经理参与者列表
- [ ] 处理 A2A 长任务（contextId + 任务轮询）

#### 验收
- 主程序能像本地智能体一样调用远端服务
- 远端服务崩溃不影响主程序（错误传播 + 重试）
- 远端任务能在主程序的 Ledger 中追踪

---

### 阶段 5：持久化升级（2-3 周）

**目标**：从文件级持久化升级到数据库/云端。

#### 任务清单
- [ ] 选择存储后端（Cosmos / SQL Server / Redis）
- [ ] 实现 `ICheckpointStore` 数据库版本
- [ ] 实现自定义 `ChatHistoryProvider`（DB 存储 + 截断/压缩策略）
- [ ] 任务历史查询 API（按用户/时间/状态）
- [ ] 任务删除/归档策略
- [ ] 敏感数据加密

#### 可选（云原生）
- [ ] 引入 `Microsoft.Agents.AI.DurableTask`
- [ ] 部署到 Azure Functions
- [ ] 引入 `Microsoft.Agents.AI.CosmosNoSql`

#### 验收
- 任务状态在多实例间共享
- 单实例崩溃不丢任务
- 历史任务可回溯/导出

---

### 阶段 6：业务化扩展（持续）

**目标**：根据具体业务持续添加部门、员工、工具。

#### 持续任务
- [ ] 定义部门"招聘"流程（如何快速加新部门）
- [ ] 员工技能库（提示词 + 工具 + 风险级别）
- [ ] 动态部门组合（声明式 YAML 工作流）
- [ ] 跨项目复用（部门库 / 员工库）
- [ ] 评估体系（`IAgentEvaluator` 自动评分）

---

## 三、关键决策点

### 3.1 部署形态

| 选项 | 优点 | 缺点 | 推荐 |
|------|------|------|------|
| 单体进程 (自托管) | 简单、可控 | 扩展性差 | 阶段 1-3 |
| 多进程 + A2A | 隔离好、可独立扩展 | 复杂度上升 | 阶段 4 后 |
| Azure Functions + Durable | 云原生、自动伸缩 | 锁定 Azure | 阶段 5 可选 |

### 3.2 模型选型

| 角色 | 推荐模型 | 原因 |
|------|---------|------|
| 总经理 | 强模型（GPT-4 / Claude Opus） | 规划复杂、影响大 |
| 部门主管 | 中等模型 | 路由 + 协调 |
| 员工 | 小模型（GPT-4o-mini / 本地模型） | 执行单一任务 |
| Critic | 中等模型 | 需要可靠判断 |

### 3.3 持久化存储

| 选项 | 适用阶段 |
|------|---------|
| 文件 (`FileSystemJsonCheckpointStore`) | 阶段 1-3 |
| SQL Server | 阶段 5（如团队熟悉） |
| Cosmos DB | 阶段 5（生产/高并发） |
| Redis | 短期缓存 + Cosmos 持久 |

### 3.4 前端通信

| 选项 | 用途 |
|------|------|
| SSE (Server-Sent Events) | 单向推送（进度、流式响应） |
| WebSocket | 双向（审批、HITL） |
| HTTP 轮询 | 兼容性兜底 |

---

## 四、风险与对策

| 风险 | 影响 | 对策 |
|------|------|------|
| AF 框架版本不稳定 | 升级困难 | 锁定 v1.6.0；用 Adapter 包裹 |
| Magentic 模式不达预期 | 总经理表现差 | 准备自定义 Manager（继承 `GroupChatManager`） |
| LLM 工具调用错误 | 任务失败 | 强 Critic + 重试 + 严格 schema |
| 检查点过大 | 性能/存储 | 历史摘要化 + 定期归档 |
| 并发任务过多 | 资源耗尽 | 任务队列 + 配额限制 |
| 安全：Prompt Injection | 工具被滥用 | `ApprovalRequiredAIFunction` + 内容审查 |
| 长任务调试困难 | 难以排错 | OpenTelemetry 全链路追踪 + Ledger 详细记录 |

---

## 五、里程碑表

| 里程碑 | 时间 | 交付物 |
|--------|------|--------|
| M0：技术验证完成 | 第 2 周末 | 调研报告 + Demo |
| M1：MVP 总经理上线 | 第 5 周末 | 单部门可用，能跑通流程 |
| M2：多部门体系 | 第 9 周末 | 多部门 + 验收循环 |
| M3：安全门禁 | 第 12 周末 | 工具授权 + 沙盒 |
| M4：跨进程整合 | 第 14 周末 | 视觉/语音通过 A2A |
| M5：生产就绪 | 第 17 周末 | 持久化 + 多实例 + 监控 |

> 总周期：约 4 个月（含调研）。

---

## 六、需要立刻确认的事项

提交给团队 / 老板 / 评审的问题：

1. **总经理的"个性"** — 用哪个模型？什么风格的对话？要不要支持多语言（含中文）？
2. **部门优先级** — 第一批要建哪几个部门？建议从"现有 Cortana 最常用的能力"切入。
3. **数据敏感度** — 用户数据/任务历史是否需要加密？是否必须留在本地（不能上云）？
4. **审批 UI** — 用现有 Cortana 客户端，还是单独 Web 控制台？
5. **失败容忍度** — 任务失败时是否需要人工介入？还是无限重试？
6. **成本预算** — 单任务成本上限？（用于设计 token 预算与降级策略）

---

## 七、必备的脚手架代码（Phase 1 起手）

为最快进入开发，建议先准备以下文件骨架：

```
src/Cortana.Workflow/
├── Agents/
│   ├── ManagerAgent.cs              # 总经理
│   ├── CriticAgent.cs               # Critic 模板
│   └── Prompts/                     # 提示词模板
├── Departments/
│   ├── IDepartmentFactory.cs        # 部门抽象
│   ├── TechDepartmentFactory.cs     # 示例：技术部
│   └── ...
├── Workflows/
│   ├── GeneralManagerWorkflowBuilder.cs
│   └── DepartmentWorkflowBase.cs
├── Models/
│   ├── WorkPlan.cs                  # Plan 数据模型
│   ├── Ledger.cs                    # 进度账本
│   └── WorkflowEvents/              # 自定义事件
├── Persistence/
│   ├── ICortanaCheckpointStore.cs
│   └── FileSystemCheckpointStore.cs
├── Endpoints/
│   ├── TaskEndpoints.cs             # HTTP API
│   └── TaskStreamEndpoint.cs        # SSE
└── DependencyInjection/
    └── CortanaWorkflowExtensions.cs # AddCortanaAgents()
```

---

## 八、推荐学习材料（按优先级）

| 必读 | 内容 | 位置 |
|------|------|------|
| ★★★ | 本目录所有文档 | `Docs\\已完成功能规划\\工作模式方案策划\` |
| ★★★ | Sample 01-06（Get Started 全套） | `samples\01-get-started\` |
| ★★★ | Sample `03_AgentWorkflowPatterns` | `samples\03-workflows\_StartHere\03_AgentWorkflowPatterns\` |
| ★★★ | Sample `07_WriterCriticWorkflow` | `samples\03-workflows\_StartHere\07_WriterCriticWorkflow\` |
| ★★★ | Magentic 完整示例 | `samples\03-workflows\Orchestration\Magentic\` |
| ★★ | Handoff 完整示例 | `samples\03-workflows\Orchestration\Handoff\` |
| ★★ | HumanInTheLoop 系列 | `samples\03-workflows\HumanInTheLoop\` |
| ★★ | DurableAgents 系列 | `samples\04-hosting\DurableAgents\` |
| ★★ | ADR-0009 长任务设计 | `docs\decisions\0009-*.md` |
| ★ | A2A 系列 | `samples\02-agents\A2A\` |
| ★ | DeclarativeAgents (YAML) | `samples\02-agents\DeclarativeAgents\` |

---

## 九、与现有 Cortana 计划的整合

参考已有目录：

| 现有计划 | 整合方式 |
|---------|---------|
| [系统流程与规划](../系统流程与规划/) | 总经理替代/包装现有主流程 |
| [视觉模型功能接入](../视觉模型功能接入/) | 视觉模型作为 A2A "视觉部" |
| [语音服务独立进程拆分](../../已完成功能规划/语音服务独立进程拆分/) | 语音作为 A2A "语音部" |
| [运营平台](../运营平台/) | 任务监控/审计面板的载体 |
| [执行计划(模型能力协议简化与插件元数据读取)](../执行计划(模型能力协议简化与插件元数据读取).md) | 插件即"员工技能" |
| [智能体创建时机与工具上下文刷新](../智能体创建时机与工具上下文刷新问题讨论.md) | 已通过工具上下文版本号和懒重建解决动态上下文刷新 |

---

## 十、下一步行动

1. **本周**：完成阶段 0 任务（跑通关键 sample），输出技术验证报告
2. **本月**：完成阶段 0 + 阶段 1 设计评审
3. **本季度**：完成阶段 1-3，对外可用
4. **下季度**：完成阶段 4-5，准备生产部署
