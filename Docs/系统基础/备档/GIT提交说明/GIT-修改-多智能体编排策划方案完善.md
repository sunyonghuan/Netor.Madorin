# GIT-修改-多智能体编排策划方案完善

## 修改范围

- `Docs/未来版本策划/多智能体编排模式策划/多智能体编排模式策划方案.md`

## 修改内容

- 完善多智能体编排模式策划方案的逻辑边界与落地路线。
- 明确第一阶段采用“UI 只展示最终回复、历史层保留工具调用链”的策略，避免破坏工具调用协议。
- 补充当前轻量子智能体的能力边界，包括不携带历史、长期记忆、技能目录、项目设置和 token 统计包装器。
- 新增 Agent 元数据一致性约束，要求统一 `Build`、`BuildWithSubAgents`、`BuildSubAgent` 的 Id / Name / Description 规则。
- 明确 `IAgentOrchestrator` 与 `AiChatHostedService` 的职责边界，避免编排层绕开现有聊天生命周期。
- 将 `Concurrent` 从主模式定位调整为执行策略，补充 `AgentExecutionStrategy` 概念。
- 新增关键边界与约束章节：
  - 历史保存策略。
  - 子智能体权限边界。
  - 附件与多模态输入传递策略。
  - 取消、超时和失败降级。
  - 参与智能体记录。
- 修正阶段实施路线，区分非流式并行分析和独立流式输出/并发写历史的架构影响。
- 补充风险章节，覆盖子智能体权限扩大、附件并发访问、token 统计状态等风险。

## 影响说明

- 本次仅修改规划文档和提交说明，不涉及代码逻辑变更。
- 不修改数据库结构、UI 协议或 WebSocket 协议。
- 为后续多智能体 Coordinator、Orchestration 层、Concurrent、GroupChat、Magentic 分阶段落地提供更清晰的工程边界。
