namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// Chat 模式下的编排模式（仅服务 Chat 范畴；Workflow 模式由 TaskExecutionEngine 承接）。
/// 详见 docs/未来版本策划/多智能体编排模式策划/03-编排模式与边界约束.md §1。
/// </summary>
public enum AgentOrchestrationMode
{
    /// <summary>无编排：普通单 Agent 对话或单 mention 直接对话。</summary>
    None = 0,

    /// <summary>
    /// 工具委托模式：mentions >= 2 时主 Agent 进入 Coordinator 模式，通过把子 Agent 包装为工具来调用。
    /// 阶段 1 已经实现具体行为（OrchestrationInstructionsProvider + 自定义委托工具签名）。
    /// </summary>
    ToolDelegation = 1,

    /// <summary>
    /// Handoff 客服分流模式：triage 智能体把对话路由给专家智能体。
    /// 阶段 3A 起接入 Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder.CreateHandoffBuilderWith。
    /// </summary>
    HandoffChat = 2,
}
